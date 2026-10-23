using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace LiveInterp.Core;

/// <summary>播放 TTS WAV，并实时给出「此刻正在播出多大声音」的参考包络。</summary>
public sealed class TtsPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly int _deviceNumber;
    private WaveOutEvent? _output;
    private WaveFileReader? _reader;
    private MemoryStream? _stream;
    private VolumeWaveProvider16? _volumeProvider;
    private TtsClip? _clip;
    private Stopwatch? _clock;
    private float _volume = 0.9f;
    private int _disposed;

    public TtsPlayer(int deviceNumber)
    {
        _deviceNumber = deviceNumber;
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return _output?.PlaybackState == PlaybackState.Playing;
        }
    }

    public void SetVolume(float volume)
    {
        lock (_gate)
        {
            _volume = Math.Clamp(volume, 0f, 1f);
            if (_volumeProvider != null) _volumeProvider.Volume = _volume;
        }
    }

    public Task PlayAsync(TtsClip clip)
    {
        lock (_gate)
        {
            CleanupLocked();

            _clip = clip;
            _stream = new MemoryStream(clip.Wav);
            _reader = new WaveFileReader(_stream);
            _volumeProvider = new VolumeWaveProvider16(_reader) { Volume = _volume };

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new WaveOutEvent
            {
                DeviceNumber = _deviceNumber,
                DesiredLatency = 80,
                NumberOfBuffers = 3
            };
            output.PlaybackStopped += (_, _) => completion.TrySetResult(true);

            _output = output;
            _clock = Stopwatch.StartNew();
            output.Init(_volumeProvider);
            output.Play();
            return completion.Task;
        }
    }

    public void Stop()
    {
        WaveOutEvent? output;
        lock (_gate) output = _output;
        if (output == null) return;
        try { output.Stop(); } catch { }
    }

    /// <summary>取当前播出位置附近的包络峰值，作为自播回声的参考电平。</summary>
    public float CurrentReferenceRms()
    {
        lock (_gate)
        {
            if (_output == null || _reader == null || _clip == null || _clock == null) return 0f;
            if (_output.PlaybackState != PlaybackState.Playing) return 0f;
            if (_clip.Envelope.Length == 0) return 0f;

            long frames;
            try
            {
                long bytes = _output.GetPosition();
                frames = bytes / Math.Max(1, _reader.WaveFormat.BlockAlign);
                // 个别驱动 GetPosition 恒为 0，用墙钟兜底
                long byClock = (long)(_clock.Elapsed.TotalSeconds * _reader.WaveFormat.SampleRate);
                if (frames <= 0) frames = byClock;
            }
            catch
            {
                frames = (long)(_clock.Elapsed.TotalSeconds * _reader.WaveFormat.SampleRate);
            }

            int block = Math.Max(1, _clip.EnvelopeBlockFrames);
            int index = (int)(frames / block);
            if (index < 0) return 0f;
            if (index >= _clip.Envelope.Length) return 0f;

            int from = Math.Max(0, index - 10);
            int to = Math.Min(_clip.Envelope.Length - 1, index + 1);
            float peak = 0f;
            for (int i = from; i <= to; i++) peak = Math.Max(peak, _clip.Envelope[i]);
            return peak;
        }
    }

    private void CleanupLocked()
    {
        if (_output != null)
        {
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            _output = null;
        }
        _volumeProvider = null;
        try { _reader?.Dispose(); } catch { }
        _reader = null;
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _clip = null;
        _clock = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (_gate) CleanupLocked();
    }
}
