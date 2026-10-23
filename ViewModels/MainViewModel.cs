using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text;
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

    private readonly Dictionary<InterpSegment, ChatMessage> _chatBySegment = new();
    private string _commandText = string.Empty;
    private bool _restarting;
    private bool _autoStarted;
    private bool _simulationTestRunning;
    private bool _testingSimulation;

    public MainViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        MicDevices = new ObservableCollection<AudioDevice>();
        OutputDevices = new ObservableCollection<OutputDevice>();
        Voices = new ObservableCollection<VoiceOption>();
        Segments = new ObservableCollection<InterpSegment>();
        Toasts = new ObservableCollection<ToastMessage>();
        ChatMessages = new ObservableCollection<ChatMessage>();

        _simulationMode = false;

        Segments.CollectionChanged += OnSegmentsChanged;

        StartCommand = new RelayCommand(Start, () => !IsRunning && !IsStopping);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        ClearCommand = new RelayCommand(() => Segments.Clear(), () => Segments.Count > 0);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => !IsRunning && !IsStopping);
        SendCommand = new RelayCommand(() => _ = SendAsync(),
            () => !string.IsNullOrWhiteSpace(CommandText));
        SimulationTestCommand = new RelayCommand(() => _ = RunSimulationTestAsync(),
            () => !_simulationTestRunning && !IsStopping && !_restarting);

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
    public ObservableCollection<ChatMessage> ChatMessages { get; }

    // —— 命令 ——
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand SimulationTestCommand { get; }

    public bool IsSimulationTestRunning => _simulationTestRunning;

    public string SimulationTestButtonText => _simulationTestRunning ? "测试中…" : "模拟测试";

    private bool IsSimulatedSession => SimulationMode || _testingSimulation;

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (!SetField(ref _commandText, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

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
            if (!IsSimulatedSession) _session?.SetVolume((float)value);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!SetField(ref _isMuted, value)) return;
            if (!IsSimulatedSession) _session?.SetMuted(value);
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
            string sim = _simulationTestRunning
                ? " · 模拟测试"
                : SimulationMode ? " · 模拟输入" : string.Empty;
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

    // —— 聊天与命令 ——
    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (InterpSegment segment in e.NewItems) AttachSegment(segment);
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            foreach (InterpSegment segment in e.OldItems) DetachSegment(segment);
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (InterpSegment segment in _chatBySegment.Keys)
                segment.PropertyChanged -= OnSegmentPropertyChanged;
            _chatBySegment.Clear();
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void AttachSegment(InterpSegment segment)
    {
        var chat = new ChatMessage
        {
            Source = segment.Source,
            Translation = segment.Translation,
            Speaker = segment.SpeakerName,
            Direction = segment.DirectionText,
            EngineNote = segment.EngineNote,
            StateText = segment.StateText,
            StateBack = segment.StateBack,
            StateForeground = segment.StateForeground,
            TranslationForeground = segment.TranslationBrush,
            TimingText = segment.TimingText,
            MatchedSource = segment.MatchedSource
        };
        _chatBySegment[segment] = chat;
        ChatMessages.Add(chat);
        segment.PropertyChanged += OnSegmentPropertyChanged;
    }

    private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not InterpSegment segment || !_chatBySegment.TryGetValue(segment, out var chat))
            return;

        switch (e.PropertyName)
        {
            case nameof(InterpSegment.Source): chat.Source = segment.Source; break;
            case nameof(InterpSegment.Translation): chat.Translation = segment.Translation; break;
            case nameof(InterpSegment.SpeakerName): chat.Speaker = segment.SpeakerName; break;
            case nameof(InterpSegment.DirectionText): chat.Direction = segment.DirectionText; break;
            case nameof(InterpSegment.EngineNote): chat.EngineNote = segment.EngineNote; break;
            case nameof(InterpSegment.StateText): chat.StateText = segment.StateText; break;
            case nameof(InterpSegment.StateBack): chat.StateBack = segment.StateBack; break;
            case nameof(InterpSegment.StateForeground): chat.StateForeground = segment.StateForeground; break;
            case nameof(InterpSegment.TranslationBrush): chat.TranslationForeground = segment.TranslationBrush; break;
            case nameof(InterpSegment.TimingText): chat.TimingText = segment.TimingText; break;
            case nameof(InterpSegment.MatchedSource): chat.MatchedSource = segment.MatchedSource; break;
        }
    }

    private void DetachSegment(InterpSegment segment)
    {
        segment.PropertyChanged -= OnSegmentPropertyChanged;
        if (_chatBySegment.Remove(segment, out var chat)) ChatMessages.Remove(chat);
    }

    private void AddSystem(string text)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => AddSystem(text)));
            return;
        }
        ChatMessages.Add(new ChatMessage { IsSystem = true, Text = text });
    }

    public void AutoStart()
    {
        if (_autoStarted) return;
        _autoStarted = true;
        AddSystem("离线同传机器人已启动。直接说话即可；输入 /help 查看命令，右上角可做无声模拟测试。");
        Start();
    }

    private async Task RunSimulationTestAsync()
    {
        if (_simulationTestRunning || _restarting || IsStopping) return;

        _simulationTestRunning = true;
        _testingSimulation = true;
        NotifySimulationTestChanged();
        bool wasRunning = IsRunning;

        try
        {
            AddSystem("开始模拟测试：内置语音，不占麦克风、不发声，结束后自动恢复真实输入。");
            await RestartAsync("正在启动模拟测试。");
            await Task.Delay(TimeSpan.FromSeconds(20));
        }
        finally
        {
            _testingSimulation = false;

            if (wasRunning)
                await RestartAsync("模拟测试结束，恢复真实输入。");
            else
                AddSystem("模拟测试结束。");

            _simulationTestRunning = false;
            NotifySimulationTestChanged();
        }
    }

    private void NotifySimulationTestChanged()
    {
        OnPropertyChanged(nameof(IsSimulationTestRunning));
        OnPropertyChanged(nameof(SimulationTestButtonText));
        OnPropertyChanged(nameof(SubtitleMeta));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task SendAsync()
    {
        string text = CommandText.Trim();
        if (text.Length == 0) return;
        CommandText = string.Empty;

        if (!text.StartsWith('/'))
        {
            await TranslateTypedAsync(text);
            return;
        }
        await ExecuteCommandAsync(text);
    }

    private async Task TranslateTypedAsync(string text)
    {
        var session = _session;
        if (session == null)
        {
            AddSystem("当前未运行，输入 /start 启动。");
            return;
        }

        var chat = new ChatMessage
        {
            Source = text,
            Speaker = "你（输入）",
            Direction = AutoLanguage ? "自动中/英" : DirectionLabel,
            Translation = "…",
            StateText = "翻译中",
            StateBack = ChatMessage.WorkingBack,
            StateForeground = ChatMessage.WorkingForeground,
            TranslationForeground = ChatMessage.DefaultText
        };
        ChatMessages.Add(chat);

        try
        {
            var (result, language) = await session.TranslateTextAsync(text, CancellationToken.None);
            chat.Direction = language == "zh" ? "中文 → English" : "English → 中文";
            if (result == null)
            {
                chat.Translation = "（没有翻译结果）";
                chat.StateText = "未完成";
                chat.StateBack = ChatMessage.FailedBack;
                chat.StateForeground = ChatMessage.FailedForeground;
                chat.TranslationForeground = ChatMessage.FailedForeground;
                return;
            }

            chat.Translation = result.Text;
            chat.EngineNote = result.MatchScore > 0
                ? $"{result.Engine} {result.MatchScore:P0}"
                : result.Engine;
            chat.MatchedSource = result.MatchedSource;
            chat.StateText = "已完成";
            chat.StateBack = ChatMessage.DoneBack;
            chat.StateForeground = ChatMessage.DoneForeground;
        }
        catch (Exception ex)
        {
            chat.Translation = "翻译失败：" + ex.Message;
            chat.StateText = "未完成";
            chat.StateBack = ChatMessage.FailedBack;
            chat.StateForeground = ChatMessage.FailedForeground;
            chat.TranslationForeground = ChatMessage.FailedForeground;
        }
    }

    private async Task ExecuteCommandAsync(string line)
    {
        string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1] : string.Empty;
        bool? onOff = ParseOnOff(arg);

        switch (command)
        {
            case "/help":
                AddSystem(HelpText);
                break;
            case "/status":
                AddSystem(BuildStatus());
                break;
            case "/devices":
                AddSystem(BuildDevices());
                break;
            case "/lang":
                await ChangeLanguageAsync(arg);
                break;
            case "/speaker":
                if (onOff == null) { AddSystem("用法：/speaker on|off"); break; }
                Diarization = onOff.Value;
                await RestartAsync($"声纹分人：{(Diarization ? "开" : "关")}，正在重启。");
                break;
            case "/sim":
                if (onOff == null) { AddSystem("用法：/sim on|off"); break; }
                SimulationMode = onOff.Value;
                await RestartAsync(SimulationMode ? "模拟输入开启：无声演示，正在重启。" : "模拟输入关闭，正在重启。");
                break;
            case "/tts":
                if (onOff == null) { AddSystem("用法：/tts on|off"); break; }
                IsMuted = !onOff.Value;
                AddSystem(SimulationMode
                    ? "模拟输入始终无声，TTS 开关不影响演示。"
                    : $"朗读已{(IsMuted ? "关闭" : "开启")}。");
                break;
            case "/volume":
                if (!int.TryParse(arg, out int volume) || volume < 0 || volume > 100)
                {
                    AddSystem("用法：/volume 0-100");
                    break;
                }
                Volume = volume / 100.0;
                AddSystem(SimulationMode ? "模拟输入音量固定为 0。" : $"音量 {volume}%。");
                break;
            case "/rate":
                if (!int.TryParse(arg, out int rate) || rate < -5 || rate > 5)
                {
                    AddSystem("用法：/rate -5..5");
                    break;
                }
                SapiRate = rate;
                await RestartAsync($"语速 {rate}，正在重启。");
                break;
            case "/sens":
                int? sensitivity = arg.ToLowerInvariant() switch
                {
                    "low" or "低" => 0,
                    "mid" or "中" => 1,
                    "high" or "高" => 2,
                    _ => null
                };
                if (sensitivity == null) { AddSystem("用法：/sens low|mid|high"); break; }
                SensitivityIndex = sensitivity.Value;
                await RestartAsync($"打断灵敏度：{arg}，正在重启。");
                break;
            case "/hold":
                if (!int.TryParse(arg, out int hold) || hold < 80 || hold > 400)
                {
                    AddSystem("用法：/hold 80-400");
                    break;
                }
                HoldMs = hold;
                await RestartAsync($"判定窗口 {hold} ms，正在重启。");
                break;
            case "/drop":
                if (onOff == null) { AddSystem("用法：/drop on|off"); break; }
                DropPending = onOff.Value;
                await RestartAsync($"被打断后丢弃未读译文：{(DropPending ? "开" : "关")}，正在重启。");
                break;
            case "/wait":
                if (onOff == null) { AddSystem("用法：/wait on|off"); break; }
                PauseWhileSpeaking = onOff.Value;
                await RestartAsync($"对方说话时等停顿再朗读：{(PauseWhileSpeaking ? "开" : "关")}，正在重启。");
                break;
            case "/mic":
                await ChangeDeviceAsync(arg, input: true);
                break;
            case "/out":
                await ChangeDeviceAsync(arg, input: false);
                break;
            case "/restart":
                await RestartAsync("正在重启。");
                break;
            case "/stop":
                if (!IsRunning) { AddSystem("当前未运行。"); break; }
                Stop();
                AddSystem("已暂停。输入 /start 继续。");
                break;
            case "/start":
                if (IsRunning) { AddSystem("已在运行。"); break; }
                Start();
                break;
            case "/clear":
                ChatMessages.Clear();
                _chatBySegment.Clear();
                AddSystem("聊天已清空。");
                break;
            default:
                AddSystem($"未知命令 {command}。输入 /help 查看命令。");
                break;
        }
    }

    private async Task ChangeLanguageAsync(string arg)
    {
        string value = arg.ToLowerInvariant();
        switch (value)
        {
            case "auto":
                AutoLanguage = true;
                await RestartAsync("方向：自动中/英，正在重启。");
                break;
            case "zh-en":
            case "zh":
                AutoLanguage = false;
                DirectionIndex = 0;
                await RestartAsync("方向：中文 → English，正在重启。");
                break;
            case "en-zh":
            case "en":
                AutoLanguage = false;
                DirectionIndex = 1;
                await RestartAsync("方向：English → 中文，正在重启。");
                break;
            default:
                AddSystem("用法：/lang auto|zh-en|en-zh");
                break;
        }
    }

    private async Task ChangeDeviceAsync(string arg, bool input)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            AddSystem(input ? "用法：/mic 名称片段" : "用法：/out 名称片段");
            return;
        }

        if (input)
        {
            var device = MicDevices.FirstOrDefault(d =>
                d.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
            if (device == null) { AddSystem($"找不到输入设备：{arg}。输入 /devices 查看列表。"); return; }
            SelectedMic = device;
            await RestartAsync($"输入切换为：{device.Name}，正在重启。");
        }
        else
        {
            var device = OutputDevices.FirstOrDefault(d =>
                d.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
            if (device == null) { AddSystem($"找不到输出设备：{arg}。输入 /devices 查看列表。"); return; }
            SelectedOutput = device;
            await RestartAsync($"输出切换为：{device.Name}，正在重启。");
        }
    }

    private async Task RestartAsync(string reason)
    {
        if (_restarting) { AddSystem("正在重启，请稍候。"); return; }
        _restarting = true;
        AddSystem(reason);
        try
        {
            if (IsRunning)
            {
                Stop();
                while (IsStopping) await Task.Delay(120);
            }
            await Task.Delay(250);
            Start();
        }
        finally
        {
            _restarting = false;
        }
    }

    private static bool? ParseOnOff(string value)
        => value.ToLowerInvariant() switch
        {
            "on" or "1" or "开" => true,
            "off" or "0" or "关" => false,
            _ => null
        };

    private string BuildStatus()
    {
        var lines = new List<string>
        {
            $"运行：{(IsRunning ? StateText : (IsStopping ? "停止中" : "已暂停"))}",
            $"方向：{(AutoLanguage ? "自动中/英" : DirectionLabel)}",
            $"声纹分人：{(Diarization ? "开" : "关")}",
            $"输入：{SelectedMic?.Name ?? "—"}",
            $"输出：{SelectedOutput?.Name ?? "—"}",
            $"朗读：{(IsMuted ? "关" : $"开 · {Volume:P0}")}",
            $"模拟输入：{(SimulationMode ? "开（无声）" : "关")}",
            $"灵敏度：{SensitivityIndex} · 判定窗口：{HoldMs:0} ms",
            $"知识库：{_kbPairs:N0} 句对 / {_kbTerms:N0} 术语",
            $"离线资产：{OfflineInfo}",
            $"统计：{StatsText}"
        };
        return string.Join("\n", lines);
    }

    private string BuildDevices()
    {
        var lines = new List<string> { "输入设备：" };
        lines.AddRange(MicDevices.Select(d => "· " + d.Name));
        lines.Add("输出设备：");
        lines.AddRange(OutputDevices.Select(d => "· " + d.Name));
        lines.Add("用法：/mic 名称片段，/out 名称片段");
        return string.Join("\n", lines);
    }

    private const string HelpText = """
        /help  命令列表
        /lang auto|zh-en|en-zh  语种与方向
        /speaker on|off  声纹分人
        /sim on|off  模拟输入（无声、不用麦克风）
        /tts on|off  朗读开关
        /volume 0-100  音量
        /rate -5..5  语速
        /sens low|mid|high  打断灵敏度
        /hold 80-400  人声判定窗口(ms)
        /drop on|off  被打断后丢弃未读译文
        /wait on|off  对方说话时等停顿再朗读
        /mic 名称片段  切换输入
        /out 名称片段  切换输出
        /devices  列出设备
        /status  当前状态
        /start /stop /restart
        /clear  清空聊天
        直接输入文字：离线翻译
        """;

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

        bool simulate = SimulationMode || _testingSimulation;

        var options = new SessionOptions
        {
            Mic = SelectedMic ?? new AudioDevice { Name = "模拟输入", Id = "simulation" },
            Output = SelectedOutput,
            Engine = EngineKind.Offline,
            Direction = DirectionIndex == 0 ? TranslateDirection.ZhToEn : TranslateDirection.EnToZh,
            ApiBase = ApiBase.Trim(),
            ApiKey = ApiKey.Trim(),
            ChatModel = string.IsNullOrWhiteSpace(ChatModel) ? "gpt-4o-mini" : ChatModel.Trim(),
            AsrModel = string.IsNullOrWhiteSpace(AsrModel) ? "whisper-1" : AsrModel.Trim(),
            VoiceName = selectedName,
            VoiceZh = voiceZh,
            VoiceEn = voiceEn,
            AutoLanguage = AutoLanguage,
            Diarization = Diarization,
            SapiRate = (int)Math.Round(SapiRate),
            Sensitivity = SensitivityIndex,
            HoldMs = (int)Math.Round(HoldMs),
            EarphoneMode = AudioRouteIndex == 0,
            DropPendingOnBargeIn = DropPending,
            WaitForSilence = PauseWhileSpeaking,
            Muted = simulate ? false : IsMuted,
            Volume = simulate ? 0f : (float)Volume,
            Simulation = simulate
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
                try { _dispatcher.BeginInvoke(new Action(() => AddSystem(text))); }
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
            AddSystem(_testingSimulation
                ? "已启动：模拟测试（无声）。"
                : simulate
                    ? "已启动：模拟输入（无声）。"
                    : $"已启动：{(AutoLanguage ? "自动中/英" : DirectionLabel)} · 声纹{(Diarization ? "开" : "关")} · 朗读{(IsMuted ? "关" : "开")}");
        }
        catch (Exception ex)
        {
            _session = null;
            AddSystem($"启动失败：{ex.Message}");
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

        if (!IsSimulatedSession && !_micWarningShown
            && (DateTime.UtcNow - _sessionStartedUtc).TotalSeconds > 2
            && session.MicRms < 1e-5f)
        {
            _micWarningShown = true;
            AddSystem("麦克风没有信号。输入 /devices 查看设备，用 /mic 名称片段切换，或 /sim on 无声演示。");
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
