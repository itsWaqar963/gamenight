// SpeexDSP preprocessor noise suppression for 48 kHz PCM frames.
// Native via SpeexDSPSharp — isolated so a load failure never kills share-mic capture.
using SpeexDSPSharp.Core;

namespace GameNight.Agent.Voice;

/// <summary>
/// Active noise suppression (denoise) for 20 ms @ 48 kHz mono PCM (960 samples).
/// </summary>
public sealed class NoiseSuppressor : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960; // 20 ms

    private SpeexDSPPreprocessor? _pre;
    private bool _disposed;

    public bool IsAvailable => _pre is not null;

    /// <summary>Try create Speex denoise. On any failure, IsAvailable stays false (safe no-op).</summary>
    public static NoiseSuppressor CreateOrDisabled()
    {
        var ns = new NoiseSuppressor();
        try
        {
            // frame_size = samples per process call (10–20 ms recommended)
            var pre = new SpeexDSPPreprocessor(FrameSamples, SampleRate);
            int on = 1;
            pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DENOISE, ref on);

            // Speex VAD off — we already have LocalVad for gate/UI.
            int off = 0;
            pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_VAD, ref off);

            // Mild AGC helps level after denoise without pumping noise hard.
            pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC, ref on);
            int agcTarget = 16000;
            pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC_TARGET, ref agcTarget);

            // Stronger suppress (dB, negative). Default is often -15; -25 cleans fans/hiss better.
            int suppressDb = -25;
            pre.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_NOISE_SUPPRESS, ref suppressDb);

            ns._pre = pre;
            AgentLog.Write("voice.log",
                $"NoiseSuppressor: SpeexDSP denoise enabled (48 kHz, {FrameSamples} samples, {suppressDb} dB)");
        }
        catch (Exception ex)
        {
            try { ns._pre?.Dispose(); } catch { /* ignore */ }
            ns._pre = null;
            AgentLog.Write("voice.log",
                $"NoiseSuppressor: SpeexDSP unavailable ({ex.GetType().Name}: {ex.Message}) — NS disabled");
        }

        return ns;
    }

    /// <summary>
    /// Denoise one 48 kHz frame in-place when the transmit gate is open.
    /// </summary>
    public void Process(short[] pcm)
    {
        if (_pre is null || _disposed || pcm.Length == 0) return;
        if (pcm.Length != FrameSamples)
        {
            // Unexpected size — skip rather than crash Speex.
            return;
        }

        try
        {
            _pre.Run(pcm);
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"NoiseSuppressor.Process: {ex.Message}");
            Disable();
        }
    }

    /// <summary>
    /// Update noise estimate from a gated-closed frame without applying output
    /// (keeps the model adapted to fans/hiss during silence).
    /// </summary>
    public void EstimateNoise(short[] pcm)
    {
        if (_pre is null || _disposed || pcm.Length != FrameSamples) return;
        try
        {
            _pre.EstimateUpdate(pcm);
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"NoiseSuppressor.EstimateNoise: {ex.Message}");
            Disable();
        }
    }

    private void Disable()
    {
        try { _pre?.Dispose(); } catch { /* ignore */ }
        _pre = null;
        AgentLog.Write("voice.log", "NoiseSuppressor disabled after runtime error");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pre?.Dispose(); } catch { /* ignore */ }
        _pre = null;
    }
}
