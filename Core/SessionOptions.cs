namespace LiveInterp.Core;

public enum EngineKind
{
    Offline,
    Online
}

public enum TranslateDirection
{
    ZhToEn,
    EnToZh
}

public sealed class SessionOptions
{
    public required AudioDevice Mic { get; init; }
    public required OutputDevice Output { get; init; }

    public EngineKind Engine { get; init; } = EngineKind.Offline;
    public TranslateDirection Direction { get; init; } = TranslateDirection.ZhToEn;

    public string ApiBase { get; init; } = "https://api.openai.com/v1";
    public string ApiKey { get; init; } = string.Empty;
    public string ChatModel { get; init; } = "gpt-4o-mini";
    public string AsrModel { get; init; } = "whisper-1";

    public string VoiceName { get; init; } = string.Empty;
    public string VoiceZh { get; init; } = string.Empty;
    public string VoiceEn { get; init; } = string.Empty;
    public int SapiRate { get; init; }

    public bool AutoLanguage { get; init; }
    public bool Diarization { get; init; }

    public int Sensitivity { get; init; } = 1;
    public int HoldMs { get; init; } = 180;
    public bool EarphoneMode { get; init; }
    public bool DropPendingOnBargeIn { get; init; } = true;
    public bool WaitForSilence { get; init; } = true;

    public bool Muted { get; init; }
    public float Volume { get; init; } = 0.9f;
    public bool Simulation { get; init; }

    public string SourceCode => Direction == TranslateDirection.ZhToEn ? "zh" : "en";
    public string TargetCode => Direction == TranslateDirection.ZhToEn ? "en" : "zh";
    public string SourceLanguage => Direction == TranslateDirection.ZhToEn ? "Chinese (Mandarin)" : "English";
    public string TargetLanguage => Direction == TranslateDirection.ZhToEn ? "English" : "Chinese (Mandarin)";
    public string DirectionLabel => Direction == TranslateDirection.ZhToEn ? "中文 → English" : "English → 中文";
}
