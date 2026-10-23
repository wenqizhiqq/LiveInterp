using System.Globalization;
using System.Text;

namespace LiveInterp.Core;

public sealed record TranslationResult(
    string Text,
    string Engine,
    double MatchScore = 0,
    string? MatchedSource = null);

public sealed record AsrResult(
    string Text,
    string Language,
    double Confidence,
    float[]? SpeakerEmbedding = null);

public interface ISpeechRecognizer : IDisposable
{
    Task<AsrResult> RecognizeAsync(byte[] wav16kMono, CancellationToken token);
}

public interface ITranslator : IDisposable
{
    Task<TranslationResult?> TranslateAsync(string text, CancellationToken token);
}

public static class TextNormalizer
{
    /// <summary>SAPI 中文结果常带字间空格，去掉后再交给翻译。</summary>
    public static string CleanChinese(string text)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", string.Empty);
        return cleaned.Trim();
    }

    /// <summary>与建库脚本一致的检索键：全角转半角、去标点空白、小写。</summary>
    public static string NormalizeCjk(string text) => NormalizeForMatch(text);

    public static string NormalizeLatin(string text) => NormalizeForMatch(text);

    private static string NormalizeForMatch(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (char ch in normalized)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }
}

public static class ApiEndpoints
{
    public static string Build(string apiBase, string suffix)
    {
        string root = (apiBase ?? string.Empty).Trim().TrimEnd('/');
        foreach (var tail in new[] { "/chat/completions", "/audio/transcriptions" })
        {
            if (root.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                root = root[..^tail.Length];
        }
        if (root.Length == 0) root = "https://api.openai.com/v1";
        return root + suffix;
    }
}
