using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LiveInterp.Core;

/// <summary>OpenAI 兼容的 Whisper 识别（在线引擎）。</summary>
public sealed class WhisperRecognizer : ISpeechRecognizer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _language;

    public WhisperRecognizer(string apiBase, string apiKey, string model, string language)
    {
        _endpoint = ApiEndpoints.Build(apiBase, "/audio/transcriptions");
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "whisper-1" : model;
        _language = language;
    }

    public async Task<AsrResult> RecognizeAsync(byte[] wav16kMono, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav16kMono);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "segment.wav");
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent(_language), "language");
        form.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = form };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"识别接口 HTTP {(int)response.StatusCode}：{Shorten(body)}");

        using var doc = JsonDocument.Parse(body);
        string text = doc.RootElement.TryGetProperty("text", out var value)
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
        return new AsrResult(text, _language, string.IsNullOrWhiteSpace(text) ? 0 : 0.85);
    }

    internal static string Shorten(string text, int max = 160)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose() { }
}
