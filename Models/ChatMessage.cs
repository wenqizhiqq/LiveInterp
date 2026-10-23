using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace LiveInterp.Models;

/// <summary>聊天时间线：系统提示，或一段“对方说 + 机器人译”的对话。</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    public static readonly Brush WorkingBack = Frozen(0xE8, 0xEF, 0xFC);
    public static readonly Brush WorkingForeground = Frozen(0x1D, 0x4E, 0xD8);
    public static readonly Brush DoneBack = Frozen(0xE7, 0xF7, 0xF1);
    public static readonly Brush DoneForeground = Frozen(0x04, 0x78, 0x57);
    public static readonly Brush FailedBack = Frozen(0xFF, 0xF7, 0xED);
    public static readonly Brush FailedForeground = Frozen(0xB4, 0x53, 0x09);
    public static readonly Brush DefaultText = Frozen(0x0F, 0x17, 0x2A);

    private string _text = string.Empty;
    private string _source = string.Empty;
    private string _translation = "…";
    private string _speaker = string.Empty;
    private string _direction = string.Empty;
    private string _engineNote = string.Empty;
    private string _stateText = string.Empty;
    private string _timingText = string.Empty;
    private string? _matchedSource;
    private Brush _stateBack = Brushes.Transparent;
    private Brush _stateForeground = Brushes.Gray;
    private Brush _translationForeground = DefaultText;

    public bool IsSystem { get; init; }
    public string TimeText { get; init; } = DateTime.Now.ToString("HH:mm:ss");

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

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

    public string Speaker
    {
        get => _speaker;
        set
        {
            if (!SetField(ref _speaker, value)) return;
            OnPropertyChanged(nameof(UserLabel));
        }
    }

    public string Direction
    {
        get => _direction;
        set
        {
            if (!SetField(ref _direction, value)) return;
            OnPropertyChanged(nameof(UserLabel));
        }
    }

    public string EngineNote
    {
        get => _engineNote;
        set => SetField(ref _engineNote, value);
    }

    public string StateText
    {
        get => _stateText;
        set => SetField(ref _stateText, value);
    }

    public string TimingText
    {
        get => _timingText;
        set => SetField(ref _timingText, value);
    }

    public string? MatchedSource
    {
        get => _matchedSource;
        set => SetField(ref _matchedSource, value);
    }

    public Brush StateBack
    {
        get => _stateBack;
        set => SetField(ref _stateBack, value);
    }

    public Brush StateForeground
    {
        get => _stateForeground;
        set => SetField(ref _stateForeground, value);
    }

    public Brush TranslationForeground
    {
        get => _translationForeground;
        set => SetField(ref _translationForeground, value);
    }

    public string UserLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Speaker)) return Direction;
            return string.IsNullOrWhiteSpace(Direction) ? Speaker : $"{Speaker} · {Direction}";
        }
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
