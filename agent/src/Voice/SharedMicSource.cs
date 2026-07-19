// WASAPI shared-mode mic + VAD/PTT gate + SpeexDSP NS + Opus encode @ 48 kHz.
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace GameNight.Agent.Voice;

public sealed class SharedMicSource : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960; // 20 ms mono
    public const int FrameBytes = FrameSamples * 2;

    /// <summary>Opus mono 48 kHz — both peers must negotiate this (no PCMU fallback).</summary>
    public static readonly AudioFormat LockedOpus =
        new(AudioCodecsEnum.OPUS, 111, SampleRate, 1, "useinbandfec=1");

    private readonly AudioEncoder _encoder = new(includeLinearFormats: false, includeOpus: true);
    private readonly object _gate = new();
    private readonly Accumulators _pcmAcc = new();
    private readonly NoiseSuppressor _ns = NoiseSuppressor.CreateOrDisabled();
    private WasapiCapture? _capture;
    private AudioFormat _format = LockedOpus;
    private bool _sharedMode = true;
    private bool _paused = true;
    private bool _disposed;

    public event Action<uint, byte[]>? EncodedSample;
    public event Action<string>? Error;

    /// <summary>
    /// Return false to squelch this frame (zeros PCM before encode).
    /// Must run the same LocalVad used for the Speaking UI when open-mic.
    /// </summary>
    public Func<short[], bool>? ShouldTransmit { get; set; }

    public bool NoiseSuppressionAvailable => _ns.IsAvailable;

    public void SetSharedMode(bool shared) => _sharedMode = shared;

    public List<AudioFormat> GetFormats() => [LockedOpus];

    public void SetFormat(AudioFormat format)
    {
        if (format.Codec != AudioCodecsEnum.OPUS)
        {
            AgentLog.Write("voice.log", $"SharedMicSource ignoring non-Opus format {format.Codec}");
            return;
        }
        _format = format;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _capture is not null) return;
            try
            {
                var device = new MMDeviceEnumerator()
                    .GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                // 20 ms buffer — shared works alongside Discord / other apps.
                _capture = new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: 20);
                _capture.ShareMode = _sharedMode
                    ? AudioClientShareMode.Shared
                    : AudioClientShareMode.Exclusive;
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null)
                        Error?.Invoke($"Mic stopped: {e.Exception.Message}");
                };
                _capture.StartRecording();
                _paused = false;
                AgentLog.Write("voice.log",
                    $"SharedMicSource started format={_capture.WaveFormat} " +
                    $"(WASAPI {(_sharedMode ? "shared" : "exclusive")}, Opus 48 kHz, ns={_ns.IsAvailable})");
            }
            catch (Exception ex)
            {
                CleanupCapture_NoLock();
                string msg = _sharedMode
                    ? $"Could not open microphone in shared mode. Close exclusive apps or uncheck “Share mic”. ({ex.Message})"
                    : $"Could not open microphone exclusively. Enable “Share mic” or close other capture apps. ({ex.Message})";
                AgentLog.Write("voice.log", msg);
                Error?.Invoke(msg);
                throw new InvalidOperationException(msg, ex);
            }
        }
    }

    public void Pause()
    {
        lock (_gate) { _paused = true; }
    }

    public void Resume()
    {
        bool needStart;
        lock (_gate)
        {
            needStart = _capture is null;
            if (!needStart) _paused = false;
        }
        if (needStart) Start();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _paused = true;
            CleanupCapture_NoLock();
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_paused || _disposed || e.BytesRecorded <= 0 || _capture is null) return;

        try
        {
            using var raw = new RawSourceWaveStream(e.Buffer, 0, e.BytesRecorded, _capture.WaveFormat);
            ISampleProvider samples = raw.ToSampleProvider();
            if (_capture.WaveFormat.Channels > 1)
                samples = new StereoToMonoSampleProvider(samples);

            var at48k = samples.WaveFormat.SampleRate == SampleRate
                ? samples
                : new WdlResamplingSampleProvider(samples, SampleRate);
            var pcm16 = new SampleToWaveProvider16(at48k);

            byte[] tmp = new byte[FrameBytes * 4];
            int read;
            while ((read = pcm16.Read(tmp, 0, tmp.Length)) > 0)
                _pcmAcc.Write(tmp, read);

            while (_pcmAcc.Count >= FrameBytes)
            {
                byte[] frame = _pcmAcc.Take(FrameBytes);
                short[] pcm = new short[FrameSamples];
                Buffer.BlockCopy(frame, 0, pcm, 0, frame.Length);

                // 1) Gate (PTT or VAD — same LocalVad as Speaking UI when open-mic)
                bool pass = ShouldTransmit?.Invoke(pcm) ?? true;

                if (pass)
                {
                    // 2) Active NS only while gate is open (incl. hangover)
                    _ns.Process(pcm);
                }
                else
                {
                    // Keep Speex noise model adapted to room hiss while muted/squelched
                    _ns.EstimateNoise(pcm);
                    Array.Clear(pcm, 0, pcm.Length);
                }

                // 3) Encode cleaned (or silence) PCM → Opus
                byte[] encoded = _encoder.EncodeAudio(pcm, _format);
                // RTP clock @ 48 kHz: 20 ms = 960 timestamp units
                uint durationRtp = (uint)(_format.RtpClockRate / 1000 * 20);
                if (durationRtp == 0) durationRtp = FrameSamples;
                try { EncodedSample?.Invoke(durationRtp, encoded); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"SharedMicSource frame: {ex.Message}");
        }
    }

    private void CleanupCapture_NoLock()
    {
        if (_capture is null) return;
        try
        {
            _capture.DataAvailable -= OnData;
            _capture.StopRecording();
            _capture.Dispose();
        }
        catch { /* ignore */ }
        _capture = null;
        _pcmAcc.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _ns.Dispose();
    }

    private sealed class Accumulators
    {
        private readonly List<byte> _buf = new(8192);
        public int Count => _buf.Count;
        public void Clear() => _buf.Clear();
        public void Write(byte[] src, int len)
        {
            for (int i = 0; i < len; i++) _buf.Add(src[i]);
        }
        public byte[] Take(int n)
        {
            var arr = new byte[n];
            _buf.CopyTo(0, arr, 0, n);
            _buf.RemoveRange(0, n);
            return arr;
        }
    }
}
