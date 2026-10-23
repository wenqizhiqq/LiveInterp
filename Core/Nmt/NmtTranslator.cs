using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SentencePieceTokenizer;

namespace LiveInterp.Core;

/// <summary>OPUS-MT zh→en 的离线 ONNX 推理，CPU 贪心解码。</summary>
public sealed class NmtTranslator : IDisposable
{
    private const int MaxSourceTokens = 500;
    private const int MaxNewTokens = 80;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly MarianTokenizer _tokenizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _decoderStart;
    private readonly long _eos;
    private readonly long _pad;
    private int _disposed;

    public NmtTranslator(string modelDir)
    {
        string encoderPath = Path.Combine(modelDir, "encoder_model_quantized.onnx");
        string decoderPath = Path.Combine(modelDir, "decoder_model_quantized.onnx");
        string sourceSpm = Path.Combine(modelDir, "source.spm");
        string vocabPath = Path.Combine(modelDir, "vocab.json");
        string configPath = Path.Combine(modelDir, "config.json");

        foreach (string path in new[] { encoderPath, decoderPath, sourceSpm, vocabPath, configPath })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("离线翻译模型文件缺失", path);
        }

        using (var config = JsonDocument.Parse(File.ReadAllText(configPath)))
        {
            var root = config.RootElement;
            _decoderStart = root.TryGetProperty("decoder_start_token_id", out var start) ? start.GetInt64() : 65000;
            _eos = root.TryGetProperty("eos_token_id", out var eos) ? eos.GetInt64() : 0;
            _pad = root.TryGetProperty("pad_token_id", out var pad) ? pad.GetInt64() : 65000;
        }

        _tokenizer = new MarianTokenizer(sourceSpm, vocabPath);

        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)),
            InterOpNumThreads = 1
        };
        _encoder = new InferenceSession(encoderPath, options);
        _decoder = new InferenceSession(decoderPath, options);
    }

    public static bool IsAvailable(string modelDir)
        => File.Exists(Path.Combine(modelDir, "encoder_model_quantized.onnx"))
           && File.Exists(Path.Combine(modelDir, "decoder_model_quantized.onnx"))
           && File.Exists(Path.Combine(modelDir, "source.spm"))
           && File.Exists(Path.Combine(modelDir, "vocab.json"));

    public async Task<string?> TranslateAsync(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => TranslateCore(text, token), token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? TranslateCore(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        long[] encoded = _tokenizer.EncodeToIds(text.AsSpan(), default, new[] { _eos });
        if (encoded.Length > MaxSourceTokens)
        {
            encoded = encoded.AsSpan(0, MaxSourceTokens - 1).ToArray()
                .Concat(new[] { _eos }).ToArray();
        }

        var sourceIds = new DenseTensor<long>(new[] { 1, encoded.Length });
        var mask = new DenseTensor<long>(new[] { 1, encoded.Length });
        sourceIds.Buffer.Span.Fill(0);
        mask.Buffer.Span.Fill(1);
        for (int i = 0; i < encoded.Length; i++) sourceIds.Buffer.Span[i] = encoded[i];

        using var encoderResults = _encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", sourceIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask)
        });

        var hidden = encoderResults.First(v => v.Name == "last_hidden_state").AsTensor<float>();
        var decoderIds = new List<long>(MaxNewTokens) { _decoderStart };

        for (int step = 0; step < MaxNewTokens; step++)
        {
            token.ThrowIfCancellationRequested();

            var decoderInput = new DenseTensor<long>(new[] { 1, decoderIds.Count });
            for (int i = 0; i < decoderIds.Count; i++)
                decoderInput.Buffer.Span[i] = decoderIds[i];

            using var decoderResults = _decoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", decoderInput),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden)
            }, new[] { "logits" });

            var logits = decoderResults.First().AsTensor<float>();
            if (logits is not DenseTensor<float> dense)
                throw new InvalidOperationException("ONNX logits 类型异常。");

            int sequence = logits.Dimensions[1];
            int vocabulary = logits.Dimensions[2];
            var values = dense.Buffer.Span;
            int offset = (sequence - 1) * vocabulary;

            long next = _eos;
            float bestScore = float.NegativeInfinity;
            for (int id = 0; id < vocabulary; id++)
            {
                float score = values[offset + id];
                if (score > bestScore)
                {
                    bestScore = score;
                    next = id;
                }
            }

            if (next == _eos || next == _pad) break;
            decoderIds.Add(next);
        }

        if (decoderIds.Count <= 1) return null;

        var outputIds = new long[decoderIds.Count - 1];
        decoderIds.CopyTo(1, outputIds, 0, outputIds.Length);
        string decoded = _tokenizer.Decode(outputIds.AsSpan());
        return CleanOutput(decoded);
    }

    private static string CleanOutput(string text)
    {
        text = text.Replace("<unk>", string.Empty, StringComparison.Ordinal)
                   .Replace("<pad>", string.Empty, StringComparison.Ordinal)
                   .Replace("</s>", string.Empty, StringComparison.Ordinal)
                   .Trim();
        text = Regex.Replace(text, @"\s+([,.!?;:])", "$1");
        text = Regex.Replace(text, @"\s{2,}", " ");
        return text;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _gate.Dispose();
        try { _encoder.Dispose(); } catch { }
        try { _decoder.Dispose(); } catch { }
        try { _tokenizer.Dispose(); } catch { }
    }
}
