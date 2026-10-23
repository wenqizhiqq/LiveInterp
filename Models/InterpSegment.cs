using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace LiveInterp.Models;

public enum SegmentState
{
    Capturing,
    Recognizing,
    Translating,
    Synthesizing,
    Speaking,
    Queued,
    Done,
    Interrupted,
    Skipped,
    Muted,
    Failed
}

public sealed class InterpSegment : INotifyPropertyChanged
{
    private static readonly Brush CapturingFg = Frozen(0x0F, 0x76, 0x6E);
    private static readonly Brush CapturingBg = Frozen(0xE6, 0xF6, 0xF4);
    private static readonly Brush WorkingFg = Frozen(0x1D, 0x4E, 0xD8);
    private static readonly Brush WorkingBg = Frozen(0xE8, 0xEF, 0xFC);
    private static readonly Brush SpeakingFg = Frozen(0x6D, 0x28, 0xD9);
    private static readonly Brush SpeakingBg = Frozen(0xF1, 0xEB, 0xFE);
    private static readonly Brush QueuedFg = Frozen(0x47, 0x55, 0x69);
    private static readonly Brush QueuedBg = Frozen(0xF1, 0xF5, 0xF9);
    private static readonly Brush DoneFg = Frozen(0x04, 0x78, 0x57);
    private static readonly Brush DoneBg = Frozen(0xE7, 0xF7, 0xF1);
    private static readonly Brush InterruptedFg = Frozen(0xB9, 0x1C, 0x1C);
    private static readonly Brush InterruptedBg = Frozen(0xFE, 0xEA, 0xEA);
    private static readonly Brush MutedFg = Frozen(0x64, 0x74, 0x8B);
    private static readonly Brush MutedBg = Frozen(0xF1, 0xF4, 0xF8);
    private static readonly Brush FailedFg = Frozen(0xB4, 0x53, 0x09);
    private static readonly Brush FailedBg = Frozen(0xFF, 0xF7, 0xED);
    private static readonly Brush Ink = Frozen(0x0F, 0x17, 0x2A);
    private static readonly Brush Faded = Frozen(0x8A, 0x96, 0xA8);

    private string _source = "正在收音…";
    private string _translation = "…";
    private string _engineNote = string.Empty;
    private string _directionText = string.Empty;
    private string _speakerName = string.Empty;
    private string? _matchedSource;
    private SegmentState _state = SegmentState.Capturing;
    private int _latencyMs;
    private int _firstAudioMs;

    public DateTime Time { get; } = DateTime.Now;
    public DateTime SpeechEndedAt { get; set; } = DateTime.Now;
    internal int Generation { get; set; }

    public string DirectionText
    {
        get => _directionText;
        set => SetField(ref _directionText, value);
    }

    public string SpeakerName
    {
        get => _speakerName;
        set => SetField(ref _speakerName, value);
    }

    public string TimeText => Time.ToString("HH:mm:ss");

    public string Source
    {
        get => _source;
        set => SetField(ref _source, value);
    }

    public string Translation
    {
        get => _translation;
        set => SetField(ref _translation, value);
    }

    public string EngineNote
    {
        get => _engineNote;
        set => SetField(ref _engineNote, value);
    }

    public string? MatchedSource
    {
        get => _matchedSource;
        set => SetField(ref _matchedSource, value);
    }

    public SegmentState State => _state;

    public string StateText => _state switch
    {
        SegmentState.Capturing => "收音中",
        SegmentState.Recognizing => "识别中",
        SegmentState.Translating => "翻译中",
        SegmentState.Synthesizing => "合成中",
        SegmentState.Speaking => "朗读中",
        SegmentState.Queued => "等待朗读",
        SegmentState.Done => "已完成",
        SegmentState.Interrupted => "被打断",
        SegmentState.Skipped => "已跳过",
        SegmentState.Muted => "已静音",
        SegmentState.Failed => "未完成",
        _ => string.Empty
    };

    public Brush StateBack => _state switch
    {
        SegmentState.Capturing => CapturingBg,
        SegmentState.Recognizing or SegmentState.Translating or SegmentState.Synthesizing => WorkingBg,
        SegmentState.Speaking => SpeakingBg,
        SegmentState.Queued => QueuedBg,
        SegmentState.Done => DoneBg,
        SegmentState.Interrupted => InterruptedBg,
        SegmentState.Failed => FailedBg,
        _ => MutedBg
    };

    public Brush StateForeground => _state switch
    {
        SegmentState.Capturing => CapturingFg,
        SegmentState.Recognizing or SegmentState.Translating or SegmentState.Synthesizing => WorkingFg,
        SegmentState.Speaking => SpeakingFg,
        SegmentState.Queued => QueuedFg,
        SegmentState.Done => DoneFg,
        SegmentState.Interrupted => InterruptedFg,
        SegmentState.Failed => FailedFg,
        _ => MutedFg
    };

    public Brush TranslationBrush =>
        _state is SegmentState.Failed or SegmentState.Skipped or SegmentState.Muted ? Faded : Ink;

    public int LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (_latencyMs == value) return;
            _latencyMs = value;
            OnPropertyChanged(nameof(TimingText));
        }
    }

    public int FirstAudioMs
    {
        get => _firstAudioMs;
        set
        {
            if (_firstAudioMs == value) return;
            _firstAudioMs = value;
            OnPropertyChanged(nameof(TimingText));
        }
    }

    public string TimingText
    {
        get
        {
            var parts = new List<string>(2);
            if (_latencyMs > 0) parts.Add($"字幕 {_latencyMs / 1000.0:0.0}s");
            if (_firstAudioMs > 0) parts.Add($"首声 {_firstAudioMs / 1000.0:0.0}s");
            return string.Join(" · ", parts);
        }
    }

    public void SetState(SegmentState state)
    {
        if (_state == state) return;
        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateBack));
        OnPropertyChanged(nameof(StateForeground));
        OnPropertyChanged(nameof(TranslationBrush));
    }

    public void Fail(string message)
    {
        Source = _source == "正在收音…" ? "（没有听清）" : _source;
        Translation = message;
        SetState(SegmentState.Failed);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
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
