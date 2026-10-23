namespace LiveInterp.Core;

public readonly record struct DetectorFrame(
    bool SpeechStarted,
    bool SpeechEnded,
    bool BargeIn,
    bool SpeechActive,
    float NoiseRms,
    float EchoGain,
    float ThresholdRms);

/// <summary>
/// 能量 VAD + 自适应回声门控。
/// 朗读时用「本机实际播出的 TTS 包络 × 自适应泄漏增益」估计自播声，
/// 麦克风能量只有明显超过该估计（且持续 N 毫秒）才被当成人声，触发打断。
/// </summary>
public sealed class BargeInDetector
{
    private const int FrameMs = 20;
    public const int HangoverMs = 450;
    private const int CalibrationMs = 300;
    private const float MinThresholdRms = 0.0025f;

    private readonly float _noiseMultiplier;
    private readonly float _echoMultiplier;
    private readonly int _onsetMs;

    private float _noiseRms = 0.004f;
    private float _leak;
    private bool _speaking;
    private int _aboveMs;
    private int _silenceMs;
    private bool _bargedThisPlayback;
    private bool _wasPlaying;
    private int _calibrationLeftMs;
    private float _calibMin;
    private int _calibSamples;

    public BargeInDetector(int sensitivity, int holdMs, bool earphoneMode)
    {
        _noiseMultiplier = sensitivity switch { 0 => 4.0f, 1 => 2.8f, _ => 2.0f };
        _echoMultiplier = earphoneMode ? 1.35f : 1.7f;
        _onsetMs = Math.Clamp(holdMs, 80, 400);
        _leak = earphoneMode ? 0.04f : 1.5f;
    }

    public bool IsSpeechActive => _speaking;
    public float NoiseRms => _noiseRms;
    public float EchoGain => _leak;

    public DetectorFrame Process(float micRms, float refRms, bool playing)
    {
        if (!_wasPlaying && playing)
        {
            _bargedThisPlayback = false;
            _aboveMs = 0;
            _calibrationLeftMs = CalibrationMs;
            _calibMin = float.MaxValue;
            _calibSamples = 0;
        }
        else if (!playing)
        {
            _bargedThisPlayback = false;
            _calibrationLeftMs = 0;
        }
        _wasPlaying = playing;

        if (playing && _calibrationLeftMs > 0)
        {
            if (refRms > 0.008f)
            {
                float ratio = micRms / refRms;
                if (ratio < _calibMin) _calibMin = ratio;
                _calibSamples++;
            }
            _calibrationLeftMs -= FrameMs;
            if (_calibrationLeftMs <= 0 && _calibSamples >= 4)
                _leak = Math.Clamp(_calibMin * 1.15f + 0.01f, 0.01f, 3.0f);

            // 校准期间不判定人声，也不让自播声推进 VAD 计时
            if (_calibrationLeftMs > 0)
            {
                return new DetectorFrame(false, false, false, _speaking,
                    _noiseRms, _leak, _noiseRms * _noiseMultiplier);
            }
        }

        float echo = playing ? _leak * refRms : 0f;
        float threshold = Math.Max(_noiseRms * _noiseMultiplier, echo * _echoMultiplier);
        threshold = Math.Max(threshold, MinThresholdRms);

        bool above = micRms >= threshold;
        if (above)
        {
            _aboveMs += FrameMs;
            _silenceMs = 0;
        }
        else
        {
            _aboveMs = 0;
            if (_speaking) _silenceMs += FrameMs;
        }

        bool started = false;
        bool ended = false;

        if (!_speaking)
        {
            if (_aboveMs >= _onsetMs)
            {
                _speaking = true;
                started = true;
                _silenceMs = 0;
            }
        }
        else if (_silenceMs >= HangoverMs)
        {
            _speaking = false;
            ended = true;
            _aboveMs = 0;
        }

        bool barge = playing && _speaking && !_bargedThisPlayback;
        if (barge) _bargedThisPlayback = true;

        if (!playing && !_speaking)
        {
            float rate = micRms < _noiseRms ? 0.15f : 0.0035f;
            _noiseRms += (micRms - _noiseRms) * rate;
            _noiseRms = Math.Clamp(_noiseRms, 0.0004f, 0.05f);
        }
        else if (playing && !_speaking && !above && refRms > 0.002f)
        {
            float measured = Math.Clamp(micRms / Math.Max(refRms, 1e-4f), 0.005f, 1.5f);
            float rate = measured < _leak ? 0.25f : 0.002f;
            _leak = Math.Clamp(_leak + (measured - _leak) * rate, 0.01f, 1.5f);
        }

        return new DetectorFrame(started, ended, barge, _speaking, _noiseRms, _leak, threshold);
    }
}
