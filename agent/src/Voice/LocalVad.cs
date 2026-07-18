// Local mic voice-activity detection (RMS). Broadcast via VoiceEngine → peer:speaking.
using NAudio.Wave;

namespace GameNight.Agent.Voice;

public sealed class LocalVad : IDisposable
{
    private readonly WaveInEvent? _waveIn;
    private float _threshold;
    private readonly int _hangoverMs;
    private bool _speaking;
    private bool _enabled = true;
    private DateTime _lastVoiceUtc = DateTime.MinValue;
    private bool _disposed;

    public event Action<bool>? SpeakingChanged;

    public bool IsSpeaking => _speaking;
    public float Threshold => _threshold;

    public LocalVad(float threshold = 0.02f, int hangoverMs = 350)
    {
        _threshold = Math.Clamp(threshold, 0.001f, 0.25f);
        _hangoverMs = hangoverMs;

        try
        {
            if (WaveInEvent.DeviceCount <= 0)
            {
                AgentLog.Write("voice.log", "VAD: no capture devices");
                return;
            }

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 40,
                NumberOfBuffers = 2,
            };
            _waveIn.DataAvailable += OnData;
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"VAD start failed: {ex.Message}");
            try { _waveIn?.Dispose(); } catch { /* ignore */ }
            _waveIn = null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) SetSpeaking(false);
    }

    /// <summary>Lower threshold = more sensitive (picks up quieter speech).</summary>
    public void SetThreshold(float threshold)
    {
        _threshold = Math.Clamp(threshold, 0.001f, 0.25f);
    }

    /// <summary>Force speaking on (e.g. while PTT is held).</summary>
    public void ForceSpeaking(bool speaking)
    {
        if (speaking)
        {
            _lastVoiceUtc = DateTime.UtcNow;
            SetSpeaking(true);
        }
        else if (!_enabled)
        {
            SetSpeaking(false);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (!_enabled || e.BytesRecorded < 4) return;

        int samples = e.BytesRecorded / 2;
        double sum = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            double v = s / 32768.0;
            sum += v * v;
        }
        float rms = (float)Math.Sqrt(sum / Math.Max(1, samples));

        if (rms >= _threshold)
        {
            _lastVoiceUtc = DateTime.UtcNow;
            SetSpeaking(true);
        }
        else if (_speaking
                 && (DateTime.UtcNow - _lastVoiceUtc).TotalMilliseconds > _hangoverMs)
        {
            SetSpeaking(false);
        }
    }

    private void SetSpeaking(bool speaking)
    {
        if (_speaking == speaking) return;
        _speaking = speaking;
        try { SpeakingChanged?.Invoke(speaking); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_waveIn is not null)
        {
            try
            {
                _waveIn.DataAvailable -= OnData;
                _waveIn.StopRecording();
                _waveIn.Dispose();
            }
            catch { /* ignore */ }
        }
        SetSpeaking(false);
    }
}
