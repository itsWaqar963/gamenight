// Local speaking detection + transmit gate (energy VAD).
// Same threshold drives UI "Speaking" and the open-mic Opus noise gate.
namespace GameNight.Agent.Voice;

public sealed class LocalVad
{
    public const int DefaultHangoverMs = 500;

    private float _threshold;
    private readonly int _hangoverMs;
    private readonly int _attackFrames;
    private int _hotStreak;
    private bool _speaking;
    private bool _enabled = true;
    private DateTime _lastVoiceUtc = DateTime.MinValue;

    public event Action<bool>? SpeakingChanged;

    public bool IsSpeaking => _speaking;
    public float Threshold => _threshold;

    public LocalVad(float threshold = 0.012f, int hangoverMs = DefaultHangoverMs, int attackFrames = 2)
    {
        _threshold = Math.Clamp(threshold, 0.0005f, 0.25f);
        _hangoverMs = Math.Clamp(hangoverMs, 50, 2000);
        _attackFrames = Math.Clamp(attackFrames, 1, 8);
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _hotStreak = 0;
            SetSpeaking(false);
        }
    }

    public void SetThreshold(float threshold) =>
        _threshold = Math.Clamp(threshold, 0.0005f, 0.25f);

    public void ForceSpeaking(bool speaking)
    {
        if (speaking)
        {
            _lastVoiceUtc = DateTime.UtcNow;
            _hotStreak = _attackFrames;
            SetSpeaking(true);
        }
        else if (!_enabled)
        {
            _hotStreak = 0;
            SetSpeaking(false);
        }
    }

    /// <summary>
    /// Update speaking from PCM and return whether this frame should be transmitted.
    /// Hangover keeps the gate open after energy drops (same flag as UI Speaking).
    /// </summary>
    public bool ShouldTransmitPcm(short[] pcm)
    {
        if (!_enabled) return false;
        ObservePcm(pcm);
        return _speaking;
    }

    public void ObservePcm(short[] pcm)
    {
        if (!_enabled || pcm.Length == 0) return;

        double sum = 0;
        foreach (short s in pcm)
        {
            double v = s / 32768.0;
            sum += v * v;
        }
        float rms = (float)Math.Sqrt(sum / pcm.Length);
        ObserveLevel(rms);
    }

    private void ObserveLevel(float level)
    {
        if (level >= _threshold)
        {
            _hotStreak++;
            _lastVoiceUtc = DateTime.UtcNow;
            if (_hotStreak >= _attackFrames)
                SetSpeaking(true);
        }
        else
        {
            _hotStreak = 0;
            if (_speaking
                && (DateTime.UtcNow - _lastVoiceUtc).TotalMilliseconds > _hangoverMs)
            {
                SetSpeaking(false);
            }
        }
    }

    private void SetSpeaking(bool speaking)
    {
        if (_speaking == speaking) return;
        _speaking = speaking;
        try { SpeakingChanged?.Invoke(speaking); } catch { /* ignore */ }
    }

    public void Reset()
    {
        _hotStreak = 0;
        SetSpeaking(false);
    }
}
