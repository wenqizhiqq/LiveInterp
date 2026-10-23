using System.IO;
using System.Speech.Synthesis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveInterp.Core;

public sealed record TtsClip(byte[] Wav, float[] Envelope, int EnvelopeBlockFrames);

/// <summary>用本机 SAPI 声音合成 WAV，同时算出 20 ms 包络供回声门控参考。</summary>
public static class TtsSynthesizer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task<TtsClip> SynthesizeAsync(string text, string voiceName, int rate, CancellationToken token)
        => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            Gate.Wait(token);
            try
            {
                token.ThrowIfCancellationRequested();
                return Synthesize(text, voiceName, rate);
            }
            finally
            {
                Gate.Release();
            }
        }, token);

    private static TtsClip Synthesize(string text, string voiceName, int rate)
    {
        using var stream = new MemoryStream();
        using (var synth = new SpeechSynthesizer())
        {
            synth.Rate = Math.Clamp(rate, -10, 10);
            if (!string.IsNullOrWhiteSpace(voiceName))
            {
                try { synth.SelectVoice(voiceName); } catch { /* 声音不存在时用默认声音 */ }
            }
            synth.SetOutputToWaveStream(stream);
            synth.Speak(text);
            synth.SetOutputToNull();
        }

        var wav = stream.ToArray();
        var (envelope, blockFrames) = BuildEnvelope(wav);
        return new TtsClip(wav, envelope, blockFrames);
    }

    private static (float[] Envelope, int BlockFrames) BuildEnvelope(byte[] wav)
    {
        using var stream = new MemoryStream(wav);
        using var reader = new WaveFileReader(stream);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
            samples = new NaudioStereoToMono(samples);
        if (samples.WaveFormat.SampleRate <= 0)
            return (Array.Empty<float>(), 1);

        int block = Math.Max(1, samples.WaveFormat.SampleRate / 50);
        var envelope = new List<float>(samples.WaveFormat.SampleRate / 10);
        var buffer = new float[block];

        while (true)
        {
            int read = samples.Read(buffer, 0, block);
            if (read <= 0) break;
            double sum = 0;
            for (int i = 0; i < read; i++) sum += buffer[i] * buffer[i];
            envelope.Add((float)Math.Sqrt(sum / read));
            if (read < block) break;
        }
        return (envelope.ToArray(), block);
    }

    private sealed class NaudioStereoToMono : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly WaveFormat _format;
        private float[] _temp = Array.Empty<float>();

        public NaudioStereoToMono(ISampleProvider source)
        {
            _source = source;
            _format = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat => _format;

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = _source.WaveFormat.Channels;
            int needed = count * channels;
            if (_temp.Length < needed) _temp = new float[needed + 1024];
            int read = _source.Read(_temp, 0, needed);
            int frames = read / channels;
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                int baseIndex = frame * channels;
                for (int c = 0; c < channels; c++) sum += _temp[baseIndex + c];
                buffer[offset + frame] = sum / channels;
            }
            return frames;
        }
    }
}
