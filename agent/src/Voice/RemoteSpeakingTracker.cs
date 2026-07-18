// Debounced speaking state from remote RTP (backup when peer:speaking is late/missing).
namespace GameNight.Agent.Voice;

internal sealed class RemoteSpeakingTracker
{
    private readonly int _hangoverMs;
    private readonly Dictionary<string, (bool Speaking, DateTime LastVoiceUtc)> _state = new();

    public RemoteSpeakingTracker(int hangoverMs = 400) => _hangoverMs = hangoverMs;

    public bool ObservePcmu(string socketId, byte[] payload, out bool changed, out bool speaking)
    {
        changed = false;
        speaking = false;
        if (payload.Length == 0) return false;

        int hot = 0;
        foreach (byte b in payload)
        {
            // μ-law idle is typically 0xFF / 0x7F
            if (b is not (0xFF or 0x7F)) hot++;
        }
        bool voice = hot > payload.Length / 10;

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
