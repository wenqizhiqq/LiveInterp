using System.Collections.ObjectModel;
using System.IO;
using LiveInterp.Models;

namespace LiveInterp.Core;

public enum SessionPhase
{
    Stopped,
    Listening,
    Capturing,
    Processing,
    Speaking
}

/// <summary>
/// 同传会话：持续采集 → 能量/回声门控分段 → 识别 → 翻译 → 合成 → 朗读。
/// 朗读期间检测到人声立即停播、丢弃过期队列，并继续把新语音走完整条链路。
/// </summary>
public sealed class InterpSession : IDisposable
{
    private const int PreRollSamples = 8000;   // 500 ms，补回 VAD 判定窗口的句首
    private const int MinUtteranceSamples = 6400; // 0.4 s
    private const int MaxUtteranceSamples = 112000; // 7 s，长句强制切片

    private readonly SessionOptions _options;
    private readonly ObservableCollection<InterpSegment> _segments;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _cts = new();
    private readonly BargeInDetector _detector;
    private readonly TtsPlayer _player;
    private readonly MicCapture? _mic;
    private readonly SimulatedSpeechInput? _simulation;
    private readonly VoskSpeechRecognizer? _asrZh;
    private readonly VoskSpeechRecognizer? _asrEn;
    private readonly WhisperRecognizer? _asrOnline;
    private readonly ITranslator? _translatorZhEn;
    private readonly ITranslator? _translatorEnZh;
    private readonly SpeakerDiarizer _diarizer = new();
    private readonly bool _autoLanguage;
    private readonly bool _diarization;
    private readonly Task _playbackWorker;
    private readonly AudioRing _ring = new(16000);
    private readonly List<float> _current = new();
    private readonly Queue<PlayJob> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _queueLock = new();

    private volatile InterpSegment? _currentSegment;
    private volatile PlayJob? _currentJob;
    private volatile float _micRms;
    private volatile float _refRms;
    private volatile SessionPhase _phase = SessionPhase.Stopped;
    private volatile bool _muted;
    private string _lastLanguage;
    private int _generation;
    private int _interruptCount;
    private int _pipelineCount;
    private int _disposed;
    private long _lastBargeInTicks = DateTime.UtcNow.AddMinutes(-10).Ticks;
    private long _lastLatencyMs;
    private long _sumLatencyMs;
    private int _latencyCount;

    public InterpSession(SessionOptions options, ObservableCollection<InterpSegment> segments, Action<Action> post)
    {
        _options = options;
        _segments = segments;
        _post = post;
        _muted = options.Muted;
        _autoLanguage = options.AutoLanguage;
        _lastLanguage = options.SourceCode;

        if (!options.Simulation && options.Mic.Device == null)
            throw new InvalidOperationException("麦克风不可用。");
        if (options.Engine == EngineKind.Online && _autoLanguage)
            throw new InvalidOperationException("自动语种识别请使用离线引擎。");

        string root = AppContext.BaseDirectory;
        string spkDir = Path.Combine(root, "Data", "asr", "vosk-spk");
        _diarization = options.Diarization
                       && options.Engine == EngineKind.Offline
                       && VoskSpeechRecognizer.IsSpkAvailable(spkDir);

        _detector = new BargeInDetector(options.Sensitivity, options.HoldMs, options.EarphoneMode);
        _player = new TtsPlayer(options.Output.Number);
        _player.SetVolume(options.Volume);

        MicCapture? mic = null;
        SimulatedSpeechInput? simulation = null;
        try
        {
            if (options.Engine == EngineKind.Online)
            {
                _asrOnline = new WhisperRecognizer(
                    options.ApiBase, options.ApiKey, options.AsrModel, options.SourceCode);
                var translator = CreateOnlineTranslator(options);
                if (options.Direction == TranslateDirection.ZhToEn) _translatorZhEn = translator;
                else _translatorEnZh = translator;
            }
            else
            {
                string? spk = _diarization ? spkDir : null;
                string zhDir = Path.Combine(root, "Data", "asr", "vosk-zh-cn");
                string enDir = Path.Combine(root, "Data", "asr", "vosk-en-us");

                if (_autoLanguage)
                {
                    _asrZh = CreateVosk(zhDir, spk, "zh");
                    _asrEn = CreateVosk(enDir, spk, "en");
                    _translatorZhEn = CreateHybrid(root, TranslateDirection.ZhToEn);
                    _translatorEnZh = CreateHybrid(root, TranslateDirection.EnToZh);
                }
                else if (options.Direction == TranslateDirection.ZhToEn)
                {
                    _asrZh = CreateVosk(zhDir, spk, "zh");
                    _translatorZhEn = CreateHybrid(root, TranslateDirection.ZhToEn);
                }
                else
                {
                    _asrEn = CreateVosk(enDir, spk, "en");
                    _translatorEnZh = CreateHybrid(root, TranslateDirection.EnToZh);
                }
            }

            if (options.Simulation)
            {
                _simulation = simulation = new SimulatedSpeechInput(
                    OnFrame,
                    () => _player.IsPlaying,
                    options.Direction,
                    options.AutoLanguage,
                    message => Notice?.Invoke(message));
                _simulation.Failed += ex => Notice?.Invoke($"模拟输入异常：{ex.Message}");
                _simulation.Start();
            }
            else
            {
                var captureDevice = options.Mic.Device
                    ?? throw new InvalidOperationException("麦克风不可用。");
                _mic = mic = new MicCapture(OnFrame);
                _mic.Failed += ex => Notice?.Invoke($"麦克风采集异常：{ex.Message}");
                _mic.Start(captureDevice);
            }
            _playbackWorker = Task.Run(PlaybackLoopAsync);
        }
        catch
        {
            mic?.Dispose();
            simulation?.Dispose();
            _asrZh?.Dispose();
            _asrEn?.Dispose();
            _asrOnline?.Dispose();
            _translatorZhEn?.Dispose();
            _translatorEnZh?.Dispose();
            _player.Dispose();
            throw;
        }

        _phase = SessionPhase.Listening;

        _asrZh?.Warmup();
        _asrEn?.Warmup();
        if (_translatorZhEn is HybridOfflineTranslator zhEn) _ = Task.Run(zhEn.Warmup);
        if (_translatorEnZh is HybridOfflineTranslator enZh) _ = Task.Run(enZh.Warmup);
    }

    public event Action<string>? Notice;
    public event Action? BargeIn;

    public SessionPhase Phase => _phase;
    public float MicRms => _micRms;
    public float RefRms => _refRms;
    public float EchoGain => _detector.EchoGain;
    public float NoiseRms => _detector.NoiseRms;
    public int InterruptCount => Volatile.Read(ref _interruptCount);
    public DateTime LastBargeInUtc => new(Interlocked.Read(ref _lastBargeInTicks), DateTimeKind.Utc);
    public long LastLatencyMs => Interlocked.Read(ref _lastLatencyMs);
    public long AverageLatencyMs
    {
        get
        {
            int count = Volatile.Read(ref _latencyCount);
            return count == 0 ? 0 : Interlocked.Read(ref _sumLatencyMs) / count;
        }
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (!muted) return;

        Interlocked.Increment(ref _generation);
        var job = _currentJob;
        _player.Stop();
        if (job != null) Mark(job.Segment, SegmentState.Muted);
        DrainQueue(SegmentState.Muted);
        UpdatePhase();
    }

    public void SetVolume(float volume) => _player.SetVolume(volume);

    // —— 采集与分段 ——

    private void OnFrame(float[] frame, float rms)
    {
        if (_cts.IsCancellationRequested) return;

        _micRms = rms;
        _refRms = _player.CurrentReferenceRms();
        bool playing = _player.IsPlaying;

        _ring.Write(frame);
        var result = _detector.Process(rms, _refRms, playing);

        if (result.BargeIn) HandleBargeIn();

        if (result.SpeechStarted)
        {
            BeginUtterance();
        }
        else if (_currentSegment != null)
        {
            _current.AddRange(frame);
            if (_current.Count >= MaxUtteranceSamples) CutLongUtterance();
        }

        if (result.SpeechEnded) EndUtterance();
    }

    private void BeginUtterance()
    {
        _current.Clear();
        _current.AddRange(_ring.TakeLast(PreRollSamples));

        var segment = new InterpSegment
        {
            DirectionText = _autoLanguage ? "自动识别中…" : _options.DirectionLabel
        };
        _currentSegment = segment;
        _post(() => _segments.Add(segment));
        UpdatePhase();
    }

    private void CutLongUtterance()
    {
        var previous = _currentSegment;
        if (previous == null) return;

        var samples = _current.ToArray();
        _current.Clear();

        var next = new InterpSegment
        {
            DirectionText = _autoLanguage ? "自动识别中…" : _options.DirectionLabel
        };
        _currentSegment = next;
        _post(() => _segments.Add(next));

        StartProcessing(previous, samples, DateTime.Now);
        UpdatePhase();
    }

    private void EndUtterance()
    {
        var segment = _currentSegment;
        if (segment == null) return;
        _currentSegment = null;

        var samples = _current.ToArray();
        _current.Clear();
        if (samples.Length < MinUtteranceSamples)
        {
            _post(() => _segments.Remove(segment));
            UpdatePhase();
            return;
        }

        StartProcessing(segment, samples, DateTime.Now.AddMilliseconds(-BargeInDetector.HangoverMs));
        UpdatePhase();
    }

    private void StartProcessing(InterpSegment segment, float[] samples, DateTime speechEndedAt)
    {
        segment.SpeechEndedAt = speechEndedAt;
        segment.Generation = Volatile.Read(ref _generation);
        _ = ProcessAsync(segment, samples);
    }

    // —— 识别 → 翻译 → 合成 ——

    private async Task<(AsrResult Result, string Language)> RecognizeAsync(
        byte[] wav, CancellationToken token)
    {
        if (_asrOnline != null)
        {
            var online = await _asrOnline.RecognizeAsync(wav, token).ConfigureAwait(false);
            return (online, _options.SourceCode);
        }

        if (!_autoLanguage)
        {
            var asr = _options.Direction == TranslateDirection.ZhToEn ? _asrZh : _asrEn;
            if (asr == null) throw new InvalidOperationException("离线识别器未初始化。");
            var single = await asr.RecognizeAsync(wav, token).ConfigureAwait(false);
            return (single, single.Language);
        }

        Task<AsrResult> zhTask = _asrZh!.RecognizeAsync(wav, token);
        Task<AsrResult> enTask = _asrEn!.RecognizeAsync(wav, token);
        await Task.WhenAll(zhTask, enTask).ConfigureAwait(false);
        AsrResult zh = await zhTask.ConfigureAwait(false);
        AsrResult en = await enTask.ConfigureAwait(false);

        string zhText = TextNormalizer.CleanChinese(zh.Text);
        string enText = en.Text.Trim();
        double zhScore = zh.Confidence + (zhText.Length == 0 ? -1 : 0);
        double enScore = en.Confidence + (enText.Length == 0 ? -1 : 0);
        if (_lastLanguage == "zh") zhScore += 0.02;
        else enScore += 0.02;

        string language;
        if (enScore > zhScore + 0.08) language = "en";
        else if (zhScore > enScore + 0.08) language = "zh";
        else language = _lastLanguage;

        if (language == "zh" && zhText.Length == 0 && enText.Length > 0) language = "en";
        else if (language == "en" && enText.Length == 0 && zhText.Length > 0) language = "zh";

        _lastLanguage = language;
        return language == "zh"
            ? (zh with { Text = zhText }, "zh")
            : (en with { Text = enText }, "en");
    }

    private async Task ProcessAsync(InterpSegment segment, float[] samples)
    {
        Interlocked.Increment(ref _pipelineCount);
        UpdatePhase();
        var token = _cts.Token;
        try
        {
            _post(() => segment.SetState(SegmentState.Recognizing));
            AsrResult asr;
            string language;
            try
            {
                (asr, language) = await RecognizeAsync(WavUtil.Build16kMono(samples), token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _post(() => segment.Fail($"识别失败：{ex.Message}"));
                Notice?.Invoke($"识别失败：{ex.Message}");
                return;
            }

            if (token.IsCancellationRequested) return;
            string text = language == "zh" ? TextNormalizer.CleanChinese(asr.Text) : asr.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                _post(() => segment.Fail("没有识别到内容，再说一次试试。"));
                return;
            }

            int asrMs = (int)(DateTime.Now - segment.SpeechEndedAt).TotalMilliseconds;
            string speaker = string.Empty;
            if (_diarization && asr.SpeakerEmbedding is { Length: > 0 } embedding)
                speaker = _diarizer.Assign(embedding);

            _post(() =>
            {
                segment.Source = text;
                segment.DirectionText = language == "zh" ? "中文 → English" : "English → 中文";
                segment.SpeakerName = speaker;
                segment.SetState(SegmentState.Translating);
            });

            ITranslator? translator = language == "zh" ? _translatorZhEn : _translatorEnZh;
            if (translator == null)
            {
                _post(() => segment.Fail("对应方向的翻译器未初始化。"));
                return;
            }

            string? translation;
            try
            {
                var result = await translator.TranslateAsync(text, token).ConfigureAwait(false);
                translation = result?.Text;
                if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                {
                    _post(() =>
                    {
                        segment.Translation = result.Text;
                        segment.EngineNote = result.MatchScore > 0
                            ? $"{result.Engine} {result.MatchScore:P0}"
                            : result.Engine;
                        segment.MatchedSource = result.MatchedSource;
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _post(() => segment.Fail($"翻译失败：{ex.Message}"));
                Notice?.Invoke($"翻译失败：{ex.Message}");
                return;
            }

            if (token.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(translation))
            {
                _post(() =>
                {
                    segment.LatencyMs = (int)(DateTime.Now - segment.SpeechEndedAt).TotalMilliseconds;
                    segment.Fail("离线知识库未命中，NMT 也没有返回结果。");
                });
                Notice?.Invoke("离线翻译没有结果，可切换到在线引擎。");
                return;
            }

            int subtitleMs = (int)(DateTime.Now - segment.SpeechEndedAt).TotalMilliseconds;
            _post(() =>
            {
                segment.LatencyMs = subtitleMs;
                segment.SetState(SegmentState.Synthesizing);
            });

            if (_muted)
            {
                _post(() => segment.SetState(SegmentState.Muted));
                return;
            }

            TtsClip clip;
            try
            {
                string target = language == "zh" ? "en" : "zh";
                string voice = target == "zh" ? _options.VoiceZh : _options.VoiceEn;
                if (string.IsNullOrWhiteSpace(voice)) voice = _options.VoiceName;
                clip = await TtsSynthesizer.SynthesizeAsync(
                    translation, voice, _options.SapiRate, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _post(() => segment.Fail($"语音合成失败：{ex.Message}"));
                Notice?.Invoke($"语音合成失败：{ex.Message}");
                return;
            }

            if (token.IsCancellationRequested) return;
            _post(() => segment.SetState(SegmentState.Queued));
            Enqueue(new PlayJob(segment, clip, segment.Generation));
        }
        finally
        {
            Interlocked.Decrement(ref _pipelineCount);
            UpdatePhase();
        }
    }

    // —— 朗读队列 ——

    private void Enqueue(PlayJob job)
    {
        lock (_queueLock)
        {
            if (_queue.Count >= 4)
            {
                var dropped = _queue.Dequeue();
                Mark(dropped.Segment, SegmentState.Skipped);
            }
            _queue.Enqueue(job);
        }
        try { _queueSignal.Release(); } catch (ObjectDisposedException) { }
    }

    private async Task PlaybackLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { await _queueSignal.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            PlayJob? job = null;
            lock (_queueLock)
            {
                if (_queue.Count > 0) job = _queue.Dequeue();
            }
            if (job == null) continue;

            if (_muted) { Mark(job.Segment, SegmentState.Muted); continue; }
            if (_options.DropPendingOnBargeIn && job.Generation != Volatile.Read(ref _generation))
            {
                Mark(job.Segment, SegmentState.Skipped);
                continue;
            }

            try
            {
                if (_options.WaitForSilence)
                {
                    while (_detector.IsSpeechActive && !token.IsCancellationRequested)
                        await Task.Delay(40, token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested) break;
                if (_muted) { Mark(job.Segment, SegmentState.Muted); continue; }
                if (_options.DropPendingOnBargeIn && job.Generation != Volatile.Read(ref _generation))
                {
                    Mark(job.Segment, SegmentState.Skipped);
                    continue;
                }

                _currentJob = job;
                var startedAt = DateTime.Now;
                int firstAudioMs = (int)(startedAt - job.Segment.SpeechEndedAt).TotalMilliseconds;
                _post(() =>
                {
                    job.Segment.FirstAudioMs = firstAudioMs;
                    job.Segment.SetState(SegmentState.Speaking);
                });
                RecordLatency(firstAudioMs);
                UpdatePhase();

                await _player.PlayAsync(job.Clip).ConfigureAwait(false);

                if (ReferenceEquals(_currentJob, job)) _currentJob = null;
                bool stale = _options.DropPendingOnBargeIn && job.Generation != Volatile.Read(ref _generation);
                if (!stale)
                {
                    _post(() =>
                    {
                        if (job.Segment.State == SegmentState.Speaking)
                            job.Segment.SetState(SegmentState.Done);
                    });
                }
                UpdatePhase();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_currentJob, job)) _currentJob = null;
                Notice?.Invoke($"播放失败：{ex.Message}");
                _post(() => job.Segment.Fail($"播放失败：{ex.Message}"));
            }
        }
        UpdatePhase();
    }

    private void HandleBargeIn()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _interruptCount);
        Interlocked.Exchange(ref _lastBargeInTicks, DateTime.UtcNow.Ticks);

        var job = _currentJob;
        _player.Stop();
        if (job != null) Mark(job.Segment, SegmentState.Interrupted);
        if (_options.DropPendingOnBargeIn) DrainQueue(SegmentState.Skipped);

        _post(() => BargeIn?.Invoke());
        UpdatePhase();
    }

    private void DrainQueue(SegmentState state)
    {
        List<PlayJob> drained = new();
        lock (_queueLock)
        {
            while (_queue.Count > 0) drained.Add(_queue.Dequeue());
        }
        foreach (var job in drained) Mark(job.Segment, state);
    }

    private void Mark(InterpSegment segment, SegmentState state)
        => _post(() => segment.SetState(state));

    private void RecordLatency(long milliseconds)
    {
        if (milliseconds <= 0) return;
        Interlocked.Exchange(ref _lastLatencyMs, milliseconds);
        Interlocked.Add(ref _sumLatencyMs, milliseconds);
        Interlocked.Increment(ref _latencyCount);
    }

    private void UpdatePhase()
    {
        if (_disposed == 1)
        {
            _phase = SessionPhase.Stopped;
            return;
        }
        if (_player.IsPlaying) _phase = SessionPhase.Speaking;
        else if (_currentSegment != null) _phase = SessionPhase.Capturing;
        else if (Volatile.Read(ref _pipelineCount) > 0) _phase = SessionPhase.Processing;
        else _phase = SessionPhase.Listening;
    }

    private static VoskSpeechRecognizer CreateVosk(string modelDir, string? spkDir, string language)
    {
        if (!VoskSpeechRecognizer.IsAvailable(modelDir))
            throw new InvalidOperationException($"离线识别模型缺失：{modelDir}");
        return new VoskSpeechRecognizer(modelDir, spkDir, language);
    }

    private ITranslator CreateHybrid(string root, TranslateDirection direction)
        => new HybridOfflineTranslator(
            Path.Combine(root, "Data", "knowledge", "liveinterp-kb.db"),
            Path.Combine(root, "Data", "opus-mt-zh-en"),
            Path.Combine(root, "Data", "opus-mt-en-zh"),
            direction,
            message => Notice?.Invoke(message));

    private static OpenAiTranslator CreateOnlineTranslator(SessionOptions options)
        => new OpenAiTranslator(
            options.ApiBase, options.ApiKey, options.ChatModel,
            options.SourceLanguage, options.TargetLanguage);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _phase = SessionPhase.Stopped;

        try { _cts.Cancel(); } catch { }
        try { _mic?.Dispose(); } catch { }
        try { _simulation?.Dispose(); } catch { }
        try { _player.Stop(); } catch { }
        try { _queueSignal.Release(); } catch { }
        try { _playbackWorker?.Wait(1200); } catch { }
        lock (_queueLock) _queue.Clear();
        try { _player.Dispose(); } catch { }
        try { _asrZh?.Dispose(); } catch { }
        try { _asrEn?.Dispose(); } catch { }
        try { _asrOnline?.Dispose(); } catch { }
        try { _translatorZhEn?.Dispose(); } catch { }
        try { _translatorEnZh?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _queueSignal.Dispose(); } catch { }
    }

    private sealed record PlayJob(InterpSegment Segment, TtsClip Clip, int Generation);

    /// <summary>1 秒环形缓冲，用于补回 VAD 判定窗口内的句首音频。</summary>
    private sealed class AudioRing
    {
        private readonly float[] _buffer;
        private int _position;
        private int _count;

        public AudioRing(int capacity) => _buffer = new float[capacity];

        public void Write(float[] samples)
        {
            foreach (float sample in samples)
            {
                _buffer[_position] = sample;
                _position = (_position + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
            }
        }

        public float[] TakeLast(int wanted)
        {
            int take = Math.Min(wanted, _count);
            var result = new float[take];
            int start = (_position - take + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < take; i++)
                result[i] = _buffer[(start + i) % _buffer.Length];
            return result;
        }
    }
}
