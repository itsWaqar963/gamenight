// NAudio WASAPI playback sink for decoded Opus PCM (48 kHz mono 16-bit).
// Replaces SIPSorceryMedia.Windows.WindowsAudioEndPoint (net10-only in 10.0.12).
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameNight.Agent.Voice;

public sealed class PcmSpeakerSink : IDisposable
{
    private readonly object _gate = new();
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private bool _started;
    private bool _disposed;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            try
            {
                var format = new WaveFormat(SharedMicSource.SampleRate, 16, 1);
                _buffer = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(400),
                    DiscardOnBufferOverflow = true,
                };
                var device = new MMDeviceEnumerator()
                    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                _out = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 60);
                _out.Init(_buffer);
                _out.Play();
                _started = true;
                AgentLog.Write("voice.log", "PcmSpeakerSink started (48 kHz mono WASAPI)");
            }
            catch (Exception ex)
            {
                Cleanup_NoLock();
                AgentLog.Write("voice.log", $"PcmSpeakerSink start failed: {ex.Message}");
                throw;
            }
        }
    }

    public void WritePcm(short[] pcm)
    {
        if (pcm.Length == 0) return;
        byte[] bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        WritePcmBytes(bytes);
    }

    public void WritePcmBytes(byte[] pcm16le)
    {
        lock (_gate)
        {
            if (_disposed || !_started || _buffer is null || pcm16le.Length == 0) return;
            try { _buffer.AddSamples(pcm16le, 0, pcm16le.Length); }
            catch (Exception ex)
            {
                AgentLog.Write("voice.log", $"PcmSpeakerSink write: {ex.Message}");
            }
        }
    }

    public void WriteSilenceFrame()
    {
        WritePcm(new short[SharedMicSource.FrameSamples]);
    }

    public Task CloseAsync()
    {
        lock (_gate)
        {
            Cleanup_NoLock();
        }
        return Task.CompletedTask;
    }

    private void Cleanup_NoLock()
    {
        _started = false;
        try { _out?.Stop(); } catch { /* ignore */ }
        try { _out?.Dispose(); } catch { /* ignore */ }
        _out = null;
        _buffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) Cleanup_NoLock();
    }
}
