// Debounced speaking state from remote RTP (backup when peer:speaking is late/missing).
namespace GameNight.Agent.Voice;

internal sealed class RemoteSpeakingTracker
{
    private readonly int _hangoverMs;
    private readonly Dictionary<string, (bool Speaking, DateTime LastVoiceUtc)> _state = new();

    public RemoteSpeakingTracker(int hangoverMs = 400) => _hangoverMs = hangoverMs;

    public bool ObservePcm(string socketId, short[] pcm, out bool changed, out bool speaking)
    {
        changed = false;
        speaking = false;
        if (pcm.Length == 0) return false;

        double sum = 0;
        foreach (short s in pcm)
        {
            double v = s / 32768.0;
            sum += v * v;
        }
        float rms = (float)Math.Sqrt(sum / pcm.Length);
        bool voice = rms > 0.008f;

        DateTime now = DateTime.UtcNow;
        _state.TryGetValue(socketId, out var prev);

        if (voice)
        {
            speaking = true;
            if (!prev.Speaking) changed = true;
            _state[socketId] = (true, now);
            return changed;
        }

        if (prev.Speaking && (now - prev.LastVoiceUtc).TotalMilliseconds > _hangoverMs)
        {
            speaking = false;
            changed = true;
            _state[socketId] = (false, prev.LastVoiceUtc);
            return true;
        }

        speaking = prev.Speaking;
        return false;
    }

    public void Remove(string socketId) => _state.Remove(socketId);
    public void Clear() => _state.Clear();
}
