using System.IO;
using System.Text.Json;
using Vosk;

namespace LiveInterp.Core;

/// <summary>Vosk 离线识别：模型可复用，可选加载说话人向量模型。</summary>
public sealed class VoskSpeechRecognizer : ISpeechRecognizer
{
    private static readonly object ModelGate = new();
    private static readonly Dictionary<string, Model> Models = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SpkModel> SpkModels = new(StringComparer.Ordinal);
    private static int _logConfigured;

    private readonly string _modelDirectory;
    private readonly string? _spkDirectory;
    private readonly string _language;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public VoskSpeechRecognizer(string modelDirectory, string? spkModelDirectory, string language)
    {
        _modelDirectory = modelDirectory;
        _spkDirectory = spkModelDirectory;
        _language = language;
        if (!IsAvailable(modelDirectory))
            throw new InvalidOperationException($"Vosk 模型不存在：{modelDirectory}");
    }

    public static bool IsAvailable(string modelDirectory)
        => Directory.Exists(modelDirectory)
           && File.Exists(Path.Combine(modelDirectory, "am", "final.mdl"))
           && File.Exists(Path.Combine(modelDirectory, "conf", "model.conf"));

    public static bool IsSpkAvailable(string? spkModelDirectory)
        => !string.IsNullOrWhiteSpace(spkModelDirectory)
           && File.Exists(Path.Combine(spkModelDirectory, "final.ext.raw"))
           && File.Exists(Path.Combine(spkModelDirectory, "mean.vec"));

    public void Warmup()
    {
        try { _ = Task.Run(() => { _ = GetModel(); _ = GetSpkModel(); }); }
        catch { }
    }

    public async Task<AsrResult> RecognizeAsync(byte[] wav16kMono, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RecognizeCore(wav16kMono, token), token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AsrResult RecognizeCore(byte[] wav, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (dataOffset, dataLength) = FindDataChunk(wav);
        if (dataLength <= 0) return new AsrResult(string.Empty, _language, 0);

        var pcm = new byte[dataLength];
        Buffer.BlockCopy(wav, dataOffset, pcm, 0, dataLength);

        using var recognizer = new VoskRecognizer(GetModel(), 16000.0f);
        var spkModel = GetSpkModel();
        if (spkModel != null) recognizer.SetSpkModel(spkModel);
        recognizer.SetWords(true);
        recognizer.AcceptWaveform(pcm, pcm.Length);
        string json = recognizer.FinalResult();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string text = root.TryGetProperty("text", out var textValue)
            ? (textValue.GetString() ?? string.Empty).Trim()
            : string.Empty;

        double confidence = 0;
        if (root.TryGetProperty("result", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            double sum = 0;
            int count = 0;
            foreach (var word in words.EnumerateArray())
            {
                if (word.TryGetProperty("conf", out var conf) && conf.ValueKind == JsonValueKind.Number)
                {
                    sum += conf.GetDouble();
                    count++;
                }
            }
            confidence = count > 0 ? sum / count : 0;
        }
        if (string.IsNullOrWhiteSpace(text)) confidence = 0;

        float[]? embedding = null;
        if (root.TryGetProperty("spk", out var spk) && spk.ValueKind == JsonValueKind.Array)
        {
            var values = new List<float>(spk.GetArrayLength());
            foreach (var item in spk.EnumerateArray()) values.Add(item.GetSingle());
            if (values.Count > 0) embedding = values.ToArray();
        }

        return new AsrResult(text, _language, confidence, embedding);
    }

    private Model GetModel()
    {
        lock (ModelGate)
        {
            ConfigureLog();
            if (!Models.TryGetValue(_modelDirectory, out var model))
            {
                model = new Model(_modelDirectory);
                Models[_modelDirectory] = model;
            }
            return model;
        }
    }

    private SpkModel? GetSpkModel()
    {
        if (!IsSpkAvailable(_spkDirectory)) return null;
        lock (ModelGate)
        {
            ConfigureLog();
            string directory = _spkDirectory!;
            if (!SpkModels.TryGetValue(directory, out var model))
            {
                model = new SpkModel(directory);
                SpkModels[directory] = model;
            }
            return model;
        }
    }

    private static void ConfigureLog()
    {
        if (Volatile.Read(ref _logConfigured) != 0) return;
        try { Vosk.Vosk.SetLogLevel(-1); } catch { }
        Volatile.Write(ref _logConfigured, 1);
    }

    private static (int Offset, int Length) FindDataChunk(byte[] wav)
    {
        for (int index = 12; index + 8 <= wav.Length; index++)
        {
            if (wav[index] != (byte)'d' || wav[index + 1] != (byte)'a'
                || wav[index + 2] != (byte)'t' || wav[index + 3] != (byte)'a')
            {
                continue;
            }
            int size = BitConverter.ToInt32(wav, index + 4);
            int offset = index + 8;
            int length = Math.Min(size, wav.Length - offset);
            if (length > 0) return (offset, length);
        }
        return (Math.Min(44, wav.Length), Math.Max(0, wav.Length - 44));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _gate.Dispose();
    }
}
