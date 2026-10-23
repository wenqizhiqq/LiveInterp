using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveInterp.Core;

/// <summary>
/// 无麦克风演示：把合成的源语言语音按 20 ms 帧注入采集链路，
/// 不经过扬声器。第二句在检测到译文开始朗读后注入，用于验证打断。
/// </summary>
public sealed class SimulatedSpeechInput : IDisposable
{
    private const int SampleRate = 16000;
    private const int FrameSamples = 320;

    private readonly Action<float[], float> _onFrame;
    private readonly Func<bool> _isPlaying;
    private readonly TranslateDirection _direction;
    private readonly bool _autoLanguage;
    private readonly Action<string> _notice;
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private int _disposed;

    public SimulatedSpeechInput(
        Action<float[], float> onFrame,
        Func<bool> isPlaying,
        TranslateDirection direction,
        bool autoLanguage,
        Action<string> notice)
    {
        _onFrame = onFrame;
        _isPlaying = isPlaying;
        _direction = direction;
        _autoLanguage = autoLanguage;
        _notice = notice;
    }

    public event Action<Exception>? Failed;

    public void Start()
    {
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var (firstText, firstVoice, secondText, secondVoice) = Script();
            _notice("模拟输入：内置语音演示，不使用麦克风，也不发出声音。");
            await Task.Delay(500, token);

            var first = await TtsSynthesizer.SynthesizeAsync(firstText, firstVoice, 0, token)
                .ConfigureAwait(false);
            await FeedAsync(first.Wav, token).ConfigureAwait(false);

            await Task.Delay(800, token);
            var waiting = Stopwatch.StartNew();
            while (!_isPlaying() && waiting.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(100, token).ConfigureAwait(false);

            await Task.Delay(500, token).ConfigureAwait(false);
            var second = await TtsSynthesizer.SynthesizeAsync(secondText, secondVoice, 0, token)
                .ConfigureAwait(false);
            await FeedAsync(second.Wav, token).ConfigureAwait(false);
            _notice("模拟：第二句已注入，用于验证朗读打断。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    private (string First, string FirstVoice, string Second, string SecondVoice) Script()
    {
        if (_autoLanguage)
        {
            return (
                "欢迎参加今天的会议。", "Microsoft Huihui Desktop",
                "Please stop for a moment.", "Microsoft Zira Desktop");
        }

        return _direction == TranslateDirection.ZhToEn
            ? ("欢迎参加今天的会议。", "Microsoft Huihui Desktop",
               "会议到此结束。", "Microsoft Huihui Desktop")
            : ("Hello everyone, welcome to today's meeting.", "Microsoft Zira Desktop",
               "Please stop for a moment.", "Microsoft Zira Desktop");
    }

    private async Task FeedAsync(byte[] wav, CancellationToken token)
    {
        float[] samples = DecodeTo16kMono(wav);
        var frame = new float[FrameSamples];

        for (int offset = 0; offset < samples.Length; offset += FrameSamples)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(frame, 0, frame.Length);
            int count = Math.Min(FrameSamples, samples.Length - offset);
            Array.Copy(samples, offset, frame, 0, count);

            double sum = 0;
            for (int i = 0; i < FrameSamples; i++) sum += frame[i] * frame[i];
            float rms = (float)Math.Sqrt(sum / FrameSamples);

            _onFrame((float[])frame.Clone(), rms);
            await Task.Delay(20, token).ConfigureAwait(false);
        }

        // 补 600ms 静音，让 VAD 正常收尾、电平回落
        var silence = new float[FrameSamples];
        for (int i = 0; i < 30; i++)
        {
            token.ThrowIfCancellationRequested();
            _onFrame(silence, 0);
            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    private static float[] DecodeTo16kMono(byte[] wav)
    {
        using var stream = new MemoryStream(wav);
        using var reader = new WaveFileReader(stream);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels > 2)
            throw new InvalidOperationException("模拟语音只支持 1~2 声道。");
        if (samples.WaveFormat.Channels == 2)
            samples = new StereoToMonoSampleProvider(samples);
        if (samples.WaveFormat.SampleRate != SampleRate)
            samples = new WdlResamplingSampleProvider(samples, SampleRate);

        var output = new List<float>(SampleRate * 8);
        var buffer = new float[1600];
        while (true)
        {
            int read = samples.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (int i = 0; i < read; i++) output.Add(buffer[i]);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _cts.Cancel(); } catch { }
        try { _worker?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
