using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveInterp.Core;

/// <summary>
/// WASAPI 共享模式采集，统一重采样成 16 kHz 单声道，并按 20 ms 一帧抛出。
/// </summary>
public sealed class MicCapture : IDisposable
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 320;

    private readonly Action<float[], float> _onFrame;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _chain;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private volatile bool _running;
    private int _disposed;

    public MicCapture(Action<float[], float> onFrame)
    {
        _onFrame = onFrame;
    }

    public event Action<Exception>? Failed;

    public void Start(MMDevice device)
    {
        _capture = new WasapiCapture(device);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(600),
            DiscardOnBufferOverflow = true
        };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _chain = new WdlResamplingSampleProvider(new RawMonoSampleProvider(_buffer), SampleRate);

        _running = true;
        _capture.StartRecording();
        _cts = new CancellationTokenSource();
        _pump = Task.Factory.StartNew(
            () => Pump(_cts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Failed?.Invoke(e.Exception);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || _buffer == null) return;
        try { _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); }
        catch { /* 设备被拔出时直接丢弃这一块 */ }
    }

    private void Pump(CancellationToken token)
    {
        var frame = new float[FrameSamples];
        int filled = 0;
        var chain = _chain;
        var buffer = _buffer;
        if (chain == null || buffer == null) return;

        var sourceFormat = buffer.WaveFormat;
        int frameBytes = Math.Max(1, sourceFormat.Channels * Math.Max(1, sourceFormat.BitsPerSample / 8));
        int inputFramesPerFrame = Math.Max(1, (int)Math.Round(sourceFormat.SampleRate / (double)SampleRate));
        int requiredBytes = frameBytes * inputFramesPerFrame * FrameSamples;

        while (!token.IsCancellationRequested)
        {
            if (buffer.BufferedBytes < requiredBytes)
            {
                Thread.Sleep(5);
                continue;
            }

            int read;
            try { read = chain.Read(frame, filled, FrameSamples - filled); }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
                break;
            }

            if (read <= 0)
            {
                Thread.Sleep(5);
                continue;
            }

            filled += read;
            if (filled < FrameSamples) continue;

            double sum = 0;
            for (int i = 0; i < FrameSamples; i++) sum += frame[i] * frame[i];
            float rms = (float)Math.Sqrt(sum / FrameSamples);
            if (!float.IsFinite(rms)) rms = 0f;

            var copy = new float[FrameSamples];
            Array.Copy(frame, copy, FrameSamples);
            try { _onFrame(copy, rms); }
            catch { /* 处理帧异常不能终止采集线程 */ }
            filled = 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _pump?.Wait(800); } catch { }
        _cts?.Dispose();
        _cts = null;
        _capture = null;
        _buffer = null;
        _chain = null;
    }

    /// <summary>按实际采集格式把原始字节直接转成单声道 float，交给 WDL 重采样。</summary>
    private sealed class RawMonoSampleProvider : ISampleProvider
    {
        private readonly BufferedWaveProvider _source;
        private readonly WaveFormat _format;
        private readonly int _channels;
        private readonly int _bytesPerSample;
        private byte[] _bytes = Array.Empty<byte>();

        public RawMonoSampleProvider(BufferedWaveProvider source)
        {
            _source = source;
            var waveFormat = source.WaveFormat;
            _channels = Math.Max(1, waveFormat.Channels);
            _bytesPerSample = Math.Max(1, waveFormat.BitsPerSample / 8);
            _format = WaveFormat.CreateIeeeFloatWaveFormat(waveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat => _format;

        public int Read(float[] buffer, int offset, int count)
        {
            int frameBytes = _channels * _bytesPerSample;
            if (frameBytes <= 0 || count <= 0) return 0;

            int needed = count * frameBytes;
            if (_bytes.Length < needed) _bytes = new byte[needed];

            int read = _source.Read(_bytes, 0, needed);
            int frames = read / frameBytes;
            var sourceFormat = _source.WaveFormat;

            for (int frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                int baseOffset = frameIndex * frameBytes;
                float sum = 0;
                for (int channel = 0; channel < _channels; channel++)
                {
                    int offsetBytes = baseOffset + channel * _bytesPerSample;
                    sum += ReadSample(_bytes, offsetBytes, sourceFormat);
                }
                buffer[offset + frameIndex] = sum / _channels;
            }
            return frames;
        }

        private float ReadSample(byte[] data, int offset, WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && _bytesPerSample == 4)
                return BitConverter.ToSingle(data, offset);
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && _bytesPerSample == 8)
                return (float)BitConverter.ToDouble(data, offset);

            if (format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.Extensible)
            {
                switch (_bytesPerSample)
                {
                    case 1:
                        return (data[offset] - 128) / 128f;
                    case 2:
                        return BitConverter.ToInt16(data, offset) / 32768f;
                    case 3:
                        int value24 = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
                        if ((value24 & 0x800000) != 0) value24 |= unchecked((int)0xFF000000);
                        return value24 / 8388608f;
                    case 4:
                        return BitConverter.ToInt32(data, offset) / 2147483648f;
                }
            }

            return 0f;
        }
    }
}
