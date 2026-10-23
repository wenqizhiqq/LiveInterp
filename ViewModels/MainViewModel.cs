using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiveInterp.Core;
using LiveInterp.Models;

namespace LiveInterp.ViewModels;

public sealed class VoiceOption
{
    public required string Name { get; init; }
    public required string Culture { get; init; }
    public string Label => $"{Name}  ·  {Culture}";
    public override string ToString() => Label;
}

public sealed class ToastMessage
{
    public required string Text { get; init; }
    public required Brush Background { get; init; }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Brush Teal = Frozen(0x0D, 0x94, 0x88);
    private static readonly Brush DeepTeal = Frozen(0x0F, 0x76, 0x6E);
    private static readonly Brush Blue = Frozen(0x25, 0x63, 0xEB);
    private static readonly Brush Violet = Frozen(0x6D, 0x28, 0xD9);
    private static readonly Brush Red = Frozen(0xDC, 0x26, 0x26);
    private static readonly Brush Slate = Frozen(0x94, 0xA3, 0xB8);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _meterTimer;

    private InterpSession? _session;
    private bool _isRunning;
    private bool _isStopping;
    private bool _isMuted;
    private double _volume = 0.9;
    private double _micLevel;
    private double _playbackLevel;
    private string _micDbText = "—";
    private string _playbackDbText = "—";
    private string _echoText = "回声门控 — · 噪声底 —";
    private string _statsText = "打断 0 次 · 等待开始";
    private string _stateText = "已停止";
    private Brush _stateColor = Slate;

    private AudioDevice? _selectedMic;
    private OutputDevice? _selectedOutput;
    private VoiceOption? _selectedVoice;

    private int _engineIndex;
    private int _directionIndex;
    private int _audioRouteIndex = 1;
    private int _sensitivityIndex = 1;
    private double _holdMs = 180;
    private double _sapiRate;
    private bool _dropPending = true;
    private bool _pauseWhileSpeaking = true;
    private bool _simulationMode;
    private bool _autoLanguage = true;
    private bool _diarization = true;

    private string _apiBase = "https://api.openai.com/v1";
    private string _apiKey = string.Empty;
    private string _chatModel = "gpt-4o-mini";
    private string _asrModel = "whisper-1";
    private string _offlineInfo = "离线资产检查中…";
    private int _kbPairs;
    private int _kbTerms;
    private bool _nmtZhEnReady;
    private bool _nmtEnZhReady;
    private bool _voskZhReady;
    private bool _voskEnReady;
    private bool _voskSpkReady;
    private DateTime _sessionStartedUtc;
    private bool _micWarningShown;
    private DateTime _lastBargeToast = DateTime.UtcNow.AddMinutes(-1);

    public MainViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        MicDevices = new ObservableCollection<AudioDevice>();
        OutputDevices = new ObservableCollection<OutputDevice>();
        Voices = new ObservableCollection<VoiceOption>();
        Segments = new ObservableCollection<InterpSegment>();
        Toasts = new ObservableCollection<ToastMessage>();

        _simulationMode = Environment.GetEnvironmentVariable("LIVEINTERP_SIM") == "1";
        if (Environment.GetEnvironmentVariable("LIVEINTERP_DIRECTION") == "1")
            _directionIndex = 1;

        Segments.CollectionChanged += (_, _) => CommandManager.InvalidateRequerySuggested();

        StartCommand = new RelayCommand(Start, () => !IsRunning && !IsStopping);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        ClearCommand = new RelayCommand(() => Segments.Clear(), () => Segments.Count > 0);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => !IsRunning && !IsStopping);

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _meterTimer.Tick += OnMeterTick;

        LoadOfflineAssets();
        LoadDevices();
        LoadVoices();
    }

    // —— 集合 ——
    public ObservableCollection<InterpSegment> Segments { get; }
    public ObservableCollection<AudioDevice> MicDevices { get; }
    public ObservableCollection<OutputDevice> OutputDevices { get; }
    public ObservableCollection<VoiceOption> Voices { get; }
    public ObservableCollection<ToastMessage> Toasts { get; }

    // —— 命令 ——
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RefreshDevicesCommand { get; }

    // —— 运行状态 ——
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsStopping
    {
        get => _isStopping;
        private set
        {
            if (!SetField(ref _isStopping, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !IsRunning && !IsStopping;

    public string StateText
    {
        get => _stateText;
        private set => SetField(ref _stateText, value);
    }

    public Brush StateColor
    {
        get => _stateColor;
        private set => SetField(ref _stateColor, value);
    }

    public double MicLevel
    {
        get => _micLevel;
        private set => SetField(ref _micLevel, value);
    }

    public double PlaybackLevel
    {
        get => _playbackLevel;
        private set => SetField(ref _playbackLevel, value);
    }

    public string MicDbText
    {
        get => _micDbText;
        private set => SetField(ref _micDbText, value);
    }

    public string PlaybackDbText
    {
        get => _playbackDbText;
        private set => SetField(ref _playbackDbText, value);
    }

    public string EchoText
    {
        get => _echoText;
        private set => SetField(ref _echoText, value);
    }

    public string StatsText
    {
        get => _statsText;
        private set => SetField(ref _statsText, value);
    }

    // —— 设置 ——
    public bool IsOnline => EngineIndex == 1;

    public int EngineIndex
    {
        get => _engineIndex;
        set
        {
            if (!SetField(ref _engineIndex, value)) return;
            RefreshMeta();
            if (IsOnline)
            {
                if (AutoLanguage) AutoLanguage = false;
                if (Diarization) Diarization = false;
            }
        }
    }

    public int DirectionIndex
    {
        get => _directionIndex;
        set
        {
            if (!SetField(ref _directionIndex, value)) return;
            PickVoiceForDirection();
            RefreshMeta();
        }
    }

    public int AudioRouteIndex
    {
        get => _audioRouteIndex;
        set => SetField(ref _audioRouteIndex, value);
    }

    public int SensitivityIndex
    {
        get => _sensitivityIndex;
        set => SetField(ref _sensitivityIndex, value);
    }

    public double HoldMs
    {
        get => _holdMs;
        set => SetField(ref _holdMs, value);
    }

    public double SapiRate
    {
        get => _sapiRate;
        set => SetField(ref _sapiRate, value);
    }

    public bool DropPending
    {
        get => _dropPending;
        set => SetField(ref _dropPending, value);
    }

    public bool PauseWhileSpeaking
    {
        get => _pauseWhileSpeaking;
        set => SetField(ref _pauseWhileSpeaking, value);
    }

    public string ApiBase
    {
        get => _apiBase;
        set
        {
            if (!SetField(ref _apiBase, value)) return;
            RefreshMeta();
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetField(ref _apiKey, value);
    }

    public string ChatModel
    {
        get => _chatModel;
        set
        {
            if (!SetField(ref _chatModel, value)) return;
            RefreshMeta();
        }
    }

    public string AsrModel
    {
        get => _asrModel;
        set
        {
            if (!SetField(ref _asrModel, value)) return;
            RefreshMeta();
        }
    }

    public AudioDevice? SelectedMic
    {
        get => _selectedMic;
        set => SetField(ref _selectedMic, value);
    }

    public OutputDevice? SelectedOutput
    {
        get => _selectedOutput;
        set => SetField(ref _selectedOutput, value);
    }

    public VoiceOption? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (!SetField(ref _selectedVoice, value)) return;
            OnPropertyChanged(nameof(VoiceHint));
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetField(ref _volume, value)) return;
            if (!SimulationMode) _session?.SetVolume((float)value);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!SetField(ref _isMuted, value)) return;
            if (!SimulationMode) _session?.SetMuted(value);
        }
    }

    public bool SimulationMode
    {
        get => _simulationMode;
        set
        {
            if (!SetField(ref _simulationMode, value)) return;
            OnPropertyChanged(nameof(SubtitleMeta));
            OnPropertyChanged(nameof(EngineHint));
        }
    }

    public bool AutoLanguage
    {
        get => _autoLanguage;
        set
        {
            if (!SetField(ref _autoLanguage, value)) return;
            OnPropertyChanged(nameof(DirectionManualEnabled));
            OnPropertyChanged(nameof(SubtitleMeta));
            OnPropertyChanged(nameof(EngineHint));
        }
    }

    public bool Diarization
    {
        get => _diarization;
        set
        {
            if (!SetField(ref _diarization, value)) return;
            OnPropertyChanged(nameof(SubtitleMeta));
            OnPropertyChanged(nameof(EngineHint));
        }
    }

    public bool IsOffline => !IsOnline;

    public bool DirectionManualEnabled => !AutoLanguage;

    // —— 派生文案 ——
    public string DirectionLabel => DirectionIndex == 0 ? "中文 → English" : "English → 中文";

    public string EngineChip => IsOnline ? "在线 · Whisper" : "离线 · NMT+知识库";

    public string OfflineInfo
    {
        get => _offlineInfo;
        private set => SetField(ref _offlineInfo, value);
    }

    public string OfflineSources =>
        "数据：Tatoeba (CC-BY 2.0 FR) · CC-CEDICT (CC-BY-SA 3.0) · 模型：OPUS-MT (CC-BY 4.0) · ASR：Vosk (Apache 2.0)";

    public string SubtitleMeta
    {
        get
        {
            string mode = AutoLanguage && !IsOnline ? "自动中/英" : DirectionLabel;
            string diar = Diarization && !IsOnline ? " · 声纹分人" : string.Empty;
            string sim = SimulationMode ? " · 模拟输入" : string.Empty;
            return $"{mode} · {EngineChip} · 人声打断{diar}{sim}";
        }
    }

    public string VoiceHint => SelectedVoice == null ? "使用系统默认声音" : SelectedVoice.Label;

    public string EngineHint
    {
        get
        {
            if (IsOnline)
                return "Whisper 转写 + Chat Completions 翻译；朗读仍使用本机 SAPI 声音。";
            string knowledge = _kbPairs > 0
                ? $"{_kbPairs:N0} 句对 / {_kbTerms:N0} 术语"
                : "内置词表";
            if (AutoLanguage)
            {
                string diar = Diarization ? "声纹自动分人；" : string.Empty;
                return $"逐句用 Vosk 中/英双模型判语种，自动切换方向；{knowledge} + 双向 OPUS-MT；{diar}首次翻译会加载模型。";
            }
            if (DirectionIndex == 0)
                return $"中文识别 → 知识库（{knowledge}）/ OPUS-MT zh→en → 英文朗读；首次翻译会加载模型。";
            return $"英文识别（Vosk）→ 知识库（{knowledge}）/ OPUS-MT en→zh → 中文朗读；首次翻译会加载模型。";
        }
    }

    private void RefreshMeta()
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(DirectionLabel));
        OnPropertyChanged(nameof(DirectionManualEnabled));
        OnPropertyChanged(nameof(EngineChip));
        OnPropertyChanged(nameof(SubtitleMeta));
        OnPropertyChanged(nameof(EngineHint));
        OnPropertyChanged(nameof(OfflineInfo));
    }

    // —— 设备 / 声音 ——
    private void LoadDevices(string? preferredMicId = null, int? preferredOutputNumber = null)
    {
        try
        {
            MicDevices.Clear();
            foreach (var device in AudioDevices.GetCaptureDevices()) MicDevices.Add(device);
            string? micOverride = Environment.GetEnvironmentVariable("LIVEINTERP_MIC");
            SelectedMic = MicDevices.FirstOrDefault(d => d.Id == preferredMicId)
                ?? MicDevices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(micOverride)
                    && d.Name.Contains(micOverride, StringComparison.OrdinalIgnoreCase))
                ?? MicDevices.FirstOrDefault(d => !IsVirtualAudio(d.Name)
                    && (d.Name.Contains("麦克风", StringComparison.OrdinalIgnoreCase)
                        || d.Name.Contains("micro", StringComparison.OrdinalIgnoreCase)))
                ?? MicDevices.FirstOrDefault(d => !IsVirtualAudio(d.Name))
                ?? MicDevices.FirstOrDefault();

            OutputDevices.Clear();
            foreach (var device in AudioDevices.GetOutputDevices()) OutputDevices.Add(device);
            string? outputOverride = Environment.GetEnvironmentVariable("LIVEINTERP_OUTPUT");
            string? defaultOutput = AudioDevices.GetDefaultRenderName();
            SelectedOutput = OutputDevices.FirstOrDefault(d => d.Number == preferredOutputNumber)
                ?? OutputDevices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(outputOverride)
                    && d.Name.Contains(outputOverride, StringComparison.OrdinalIgnoreCase))
                ?? OutputDevices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(defaultOutput)
                    && (d.Name.Contains(defaultOutput, StringComparison.OrdinalIgnoreCase)
                        || defaultOutput.Contains(d.Name, StringComparison.OrdinalIgnoreCase)))
                ?? OutputDevices.FirstOrDefault(d => !IsVirtualAudio(d.Name)
                    && (d.Name.Contains("扬声器", StringComparison.OrdinalIgnoreCase)
                        || d.Name.Contains("speaker", StringComparison.OrdinalIgnoreCase)))
                ?? OutputDevices.FirstOrDefault(d => !IsVirtualAudio(d.Name))
                ?? OutputDevices.FirstOrDefault();

            if (MicDevices.Count == 0) ShowToast("没有找到录音设备，请检查麦克风。", "warn");
            if (OutputDevices.Count == 0) ShowToast("没有找到播放设备。", "warn");
        }
        catch (Exception ex)
        {
            ShowToast($"枚举音频设备失败：{ex.Message}", "error");
        }
    }

    private void LoadOfflineAssets()
    {
        try
        {
            string root = AppContext.BaseDirectory;
            var meta = KnowledgeBase.ReadMeta(Path.Combine(root, "Data", "knowledge", "liveinterp-kb.db"));
            _kbPairs = meta.Pairs;
            _kbTerms = meta.Terms;

            static bool ModelReady(string root, string name)
            {
                string dir = Path.Combine(root, "Data", name);
                return File.Exists(Path.Combine(dir, "encoder_model_quantized.onnx"))
                       && File.Exists(Path.Combine(dir, "decoder_model_quantized.onnx"))
                       && File.Exists(Path.Combine(dir, "source.spm"))
                       && File.Exists(Path.Combine(dir, "vocab.json"));
            }

            _nmtZhEnReady = ModelReady(root, "opus-mt-zh-en");
            _nmtEnZhReady = ModelReady(root, "opus-mt-en-zh");
            string zhVosk = Path.Combine(root, "Data", "asr", "vosk-zh-cn");
            string enVosk = Path.Combine(root, "Data", "asr", "vosk-en-us");
            string spkVosk = Path.Combine(root, "Data", "asr", "vosk-spk");
            _voskZhReady = VoskSpeechRecognizer.IsAvailable(zhVosk);
            _voskEnReady = VoskSpeechRecognizer.IsAvailable(enVosk);
            _voskSpkReady = VoskSpeechRecognizer.IsSpkAvailable(spkVosk);

            OfflineInfo = meta.Available
                ? $"知识库 {meta.Pairs:N0} 句对 · {meta.Terms:N0} 术语 · " +
                  $"NMT zh→en {(_nmtZhEnReady ? "就绪" : "缺失")} · " +
                  $"NMT en→zh {(_nmtEnZhReady ? "就绪" : "缺失")} · " +
                  $"Vosk 中{(_voskZhReady ? "✓" : "×")} 英{(_voskEnReady ? "✓" : "×")} · " +
                  $"声纹{(_voskSpkReady ? "✓" : "×")}"
                : "知识库未安装，仅使用内置词表。";
            OnPropertyChanged(nameof(EngineHint));
        }
        catch (Exception ex)
        {
            OfflineInfo = $"离线资产检查失败：{ex.Message}";
        }
    }

    private void LoadVoices()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            Voices.Clear();
            foreach (var voice in synth.GetInstalledVoices())
            {
                if (!voice.Enabled) continue;
                Voices.Add(new VoiceOption
                {
                    Name = voice.VoiceInfo.Name,
                    Culture = voice.VoiceInfo.Culture.Name
                });
            }
            PickVoiceForDirection();
        }
        catch (Exception ex)
        {
            ShowToast($"读取本机语音失败：{ex.Message}", "error");
        }
    }

    private void PickVoiceForDirection()
    {
        if (Voices.Count == 0) return;
        string target = DirectionIndex == 0 ? "en" : "zh";
        SelectedVoice = Voices.FirstOrDefault(v => v.Culture.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            ?? SelectedVoice
            ?? Voices[0];
    }

    private static bool IsVirtualAudio(string name)
        => name.Contains("virtual", StringComparison.OrdinalIgnoreCase)
           || name.Contains("ToDesk", StringComparison.OrdinalIgnoreCase)
           || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
           || name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase)
           || name.Contains("OBS", StringComparison.OrdinalIgnoreCase);

    private void RefreshDevices()
    {
        LoadDevices(SelectedMic?.Id, SelectedOutput?.Number);
        ShowToast("设备列表已刷新。", "ok");
    }

    // —— 启停 ——
    private void Start()
    {
        if (IsRunning || IsStopping) return;

        if (!SimulationMode && (SelectedMic == null || SelectedMic.Device == null))
        {
            ShowToast("没有可用的麦克风。", "error");
            return;
        }
        if (SelectedOutput == null)
        {
            ShowToast("没有可用的播放设备。", "error");
            return;
        }
        if (IsOnline && string.IsNullOrWhiteSpace(ApiKey)
            && ApiBase.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("使用 OpenAI 官方接口需要 API Key。", "warn");
            return;
        }
        if (IsOnline && string.IsNullOrWhiteSpace(ApiBase))
        {
            ShowToast("请填写在线接口地址。", "warn");
            return;
        }
        if (!IsOnline)
        {
            if (AutoLanguage)
            {
                if (!_voskZhReady || !_voskEnReady)
                {
                    ShowToast("中/英离线识别模型缺失，请检查 Data/asr。", "error");
                    return;
                }
            }
            else if (DirectionIndex == 0 && !_voskZhReady)
            {
                ShowToast("中文离线识别模型缺失（Data/asr/vosk-zh-cn）。", "error");
                return;
            }
            else if (DirectionIndex == 1 && !_voskEnReady)
            {
                ShowToast("英文离线识别模型缺失（Data/asr/vosk-en-us）。", "error");
                return;
            }

            if (Diarization && !_voskSpkReady)
            {
                ShowToast("声纹模型缺失，已关闭声纹分人。", "warn");
                Diarization = false;
            }
        }

        string selectedName = SelectedVoice?.Name ?? string.Empty;
        string defaultZh = Voices.FirstOrDefault(v =>
            v.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))?.Name ?? selectedName;
        string defaultEn = Voices.FirstOrDefault(v =>
            v.Culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))?.Name ?? selectedName;
        string voiceZh = SelectedVoice?.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
            ? selectedName : defaultZh;
        string voiceEn = SelectedVoice?.Culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true
            ? selectedName : defaultEn;

        var options = new SessionOptions
        {
            Mic = SelectedMic ?? new AudioDevice { Name = "模拟输入", Id = "simulation" },
            Output = SelectedOutput,
            Engine = IsOnline ? EngineKind.Online : EngineKind.Offline,
            Direction = DirectionIndex == 0 ? TranslateDirection.ZhToEn : TranslateDirection.EnToZh,
            ApiBase = ApiBase.Trim(),
            ApiKey = ApiKey.Trim(),
            ChatModel = string.IsNullOrWhiteSpace(ChatModel) ? "gpt-4o-mini" : ChatModel.Trim(),
            AsrModel = string.IsNullOrWhiteSpace(AsrModel) ? "whisper-1" : AsrModel.Trim(),
            VoiceName = selectedName,
            VoiceZh = voiceZh,
            VoiceEn = voiceEn,
            AutoLanguage = !IsOnline && AutoLanguage,
            Diarization = !IsOnline && Diarization,
            SapiRate = (int)Math.Round(SapiRate),
            Sensitivity = SensitivityIndex,
            HoldMs = (int)Math.Round(HoldMs),
            EarphoneMode = AudioRouteIndex == 0,
            DropPendingOnBargeIn = DropPending,
            WaitForSilence = PauseWhileSpeaking,
            Muted = SimulationMode ? false : IsMuted,
            Volume = SimulationMode ? 0f : (float)Volume,
            Simulation = SimulationMode
        };

        Segments.Clear();
        try
        {
            var session = new InterpSession(
                options,
                Segments,
                action =>
                {
                    try { _dispatcher.BeginInvoke(DispatcherPriority.Background, action); }
                    catch { }
                });
            session.Notice += text =>
            {
                try { _dispatcher.BeginInvoke(new Action(() => ShowToast(text, "error"))); }
                catch { }
            };
            session.BargeIn += () =>
            {
                try { _dispatcher.BeginInvoke(new Action(OnBargeInDetected)); }
                catch { }
            };
            _session = session;

            IsRunning = true;
            _meterTimer.Start();
            _sessionStartedUtc = DateTime.UtcNow;
            _micWarningShown = false;
            SetState("监听中 · 等待说话", DeepTeal);
            ShowToast(SimulationMode
                ? "模拟输入已启动：内置语音演示，不发出声音。"
                : (IsOnline ? "已开始同传（在线引擎）。" : "已开始同传（离线演示）。"), "ok");
        }
        catch (Exception ex)
        {
            _session = null;
            ShowToast($"启动失败：{ex.Message}", "error");
            SetState("已停止", Slate);
        }
    }

    private void Stop()
    {
        var session = _session;
        _session = null;
        _meterTimer.Stop();
        IsRunning = false;
        ResetMeters();

        if (session == null)
        {
            SetState("已停止", Slate);
            return;
        }

        IsStopping = true;
        SetState("正在停止…", Slate);
        foreach (var segment in Segments)
        {
            if (segment.State is SegmentState.Capturing or SegmentState.Recognizing
                or SegmentState.Translating or SegmentState.Synthesizing
                or SegmentState.Queued or SegmentState.Speaking)
            {
                segment.SetState(SegmentState.Skipped);
            }
        }
        Task.Run(() =>
        {
            try { session.Dispose(); } catch { }
            try
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    IsStopping = false;
                    SetState("已停止", Slate);
                    StatsText = $"打断 {session.InterruptCount} 次 · 会话已结束";
                }));
            }
            catch { }
        });
    }

    private void OnBargeInDetected()
    {
        SetState("已打断 · 继续聆听", Red);
        var now = DateTime.UtcNow;
        if ((now - _lastBargeToast).TotalMilliseconds < 1500) return;
        _lastBargeToast = now;
        ShowToast("检测到人声，已停止朗读。", "info");
    }

    private void OnMeterTick(object? sender, EventArgs e)
    {
        var session = _session;
        if (session == null) return;

        if (!SimulationMode && !_micWarningShown
            && (DateTime.UtcNow - _sessionStartedUtc).TotalSeconds > 2
            && session.MicRms < 1e-5f)
        {
            _micWarningShown = true;
            ShowToast("麦克风没有信号：请在右侧换一个输入设备，或勾选「模拟输入」。", "warn");
        }

        MicLevel = Smooth(MicLevel, LevelOf(session.MicRms));
        PlaybackLevel = Smooth(PlaybackLevel, LevelOf(session.RefRms));
        MicDbText = Decibels(session.MicRms);
        PlaybackDbText = Decibels(session.RefRms);
        EchoText = $"回声门控 {session.EchoGain:0.00} · 噪声底 {Decibels(session.NoiseRms)}";
        string last = session.LastLatencyMs > 0 ? $"{session.LastLatencyMs / 1000.0:0.0}s" : "—";
        string average = session.AverageLatencyMs > 0 ? $"{session.AverageLatencyMs / 1000.0:0.0}s" : "—";
        StatsText = $"打断 {session.InterruptCount} 次 · 首声 {last} · 平均 {average}";

        bool barged = (DateTime.UtcNow - session.LastBargeInUtc).TotalMilliseconds < 1300;
        if (barged)
        {
            SetState("已打断 · 继续聆听", Red);
            return;
        }

        switch (session.Phase)
        {
            case SessionPhase.Speaking:
                SetState("朗读中", Violet);
                break;
            case SessionPhase.Capturing:
                SetState("正在收音", Teal);
                break;
            case SessionPhase.Processing:
                SetState("识别 / 翻译中", Blue);
                break;
            case SessionPhase.Listening:
                SetState("监听中 · 等待说话", DeepTeal);
                break;
            default:
                SetState("已停止", Slate);
                break;
        }
    }

    private void ResetMeters()
    {
        MicLevel = 0;
        PlaybackLevel = 0;
        MicDbText = "—";
        PlaybackDbText = "—";
        EchoText = "回声门控 — · 噪声底 —";
    }

    private void SetState(string text, Brush color)
    {
        StateText = text;
        StateColor = color;
    }

    private static double LevelOf(float rms)
        => Math.Sqrt(Math.Clamp(rms * 6.0, 0.0, 1.0));

    private static double Smooth(double current, double target)
        => target >= current ? target : current * 0.72 + target * 0.28;

    private static string Decibels(float rms)
        => rms <= 1e-6f ? "—" : $"{20 * Math.Log10(rms):0} dB";

    // —— 气泡提示 ——
    private void ShowToast(string text, string kind)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => ShowToast(text, kind)));
            return;
        }

        var brush = kind switch
        {
            "ok" => Frozen(0x0F, 0x76, 0x6E),
            "warn" => Frozen(0xB4, 0x53, 0x09),
            "error" => Frozen(0xB9, 0x1C, 0x1C),
            _ => Frozen(0x33, 0x41, 0x55)
        };
        var toast = new ToastMessage { Text = text, Background = brush };
        if (Toasts.Count >= 4) Toasts.RemoveAt(0);
        Toasts.Add(toast);

        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
        {
            try { _dispatcher.BeginInvoke(new Action(() => Toasts.Remove(toast))); }
            catch { }
        });
    }

    public void Shutdown()
    {
        _meterTimer.Stop();
        var session = _session;
        _session = null;
        if (session != null)
        {
            try { session.Dispose(); } catch { }
        }
        AudioDevices.Shutdown();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
