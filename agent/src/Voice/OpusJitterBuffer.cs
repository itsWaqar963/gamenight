// Per-peer Opus RTP jitter buffer — ordered pop with target playout delay.
namespace GameNight.Agent.Voice;

/// <summary>
/// Queues Opus payloads by RTP sequence; drops late/dupes; waits ~target delay before first pop.
/// </summary>
public sealed class OpusJitterBuffer
{
    public const int DefaultTargetDelayMs = 80;
    public const int FrameDurationMs = 20;

    private readonly object _gate = new();
    private readonly SortedDictionary<ushort, Packet> _queue = new();
    private readonly int _targetFrames;
    private readonly int _maxFrames;
    private ushort? _nextSeq;
    private bool _primed;
    private int _underruns;

    public OpusJitterBuffer(int targetDelayMs = DefaultTargetDelayMs)
    {
        int delay = Math.Clamp(targetDelayMs, 40, 200);
        _targetFrames = Math.Max(2, delay / FrameDurationMs);
        _maxFrames = _targetFrames * 4;
    }

    public int Count
    {
        get { lock (_gate) return _queue.Count; }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _nextSeq = null;
            _primed = false;
        }
    }

    public void Push(ushort sequenceNumber, uint timestamp, byte[] payload)
    {
        if (payload.Length == 0) return;

        lock (_gate)
        {
            if (_nextSeq is ushort next && IsLate(sequenceNumber, next))
                return; // late — drop

            if (_queue.ContainsKey(sequenceNumber))
                return; // dupe

            _queue[sequenceNumber] = new Packet(sequenceNumber, timestamp, payload);

            while (_queue.Count > _maxFrames)
            {
                ushort oldest = _queue.Keys.First();
                _queue.Remove(oldest);
                if (_nextSeq is ushort n && oldest == n)
                    _nextSeq = (ushort)(n + 1);
            }
        }
    }

    /// <summary>
    /// Pop the next in-order packet once primed to target depth.
    /// On underrun after priming, returns false (caller may insert silence).
    /// </summary>
    public bool TryPop(out ushort sequenceNumber, out uint timestamp, out byte[] payload)
    {
        lock (_gate)
        {
            sequenceNumber = 0;
            timestamp = 0;
            payload = Array.Empty<byte>();

            if (!_primed)
            {
                if (_queue.Count < _targetFrames)
                    return false;
                _primed = true;
                _nextSeq = _queue.Keys.First();
            }

            if (_nextSeq is null)
                return false;

            ushort want = _nextSeq.Value;
            if (_queue.TryGetValue(want, out var pkt))
            {
                _queue.Remove(want);
                _nextSeq = (ushort)(want + 1);
                sequenceNumber = pkt.Seq;
                timestamp = pkt.Timestamp;
                payload = pkt.Payload;
                return true;
            }

            // Gap: skip ahead if we have newer packets (packet loss).
            if (_queue.Count > 0)
            {
                ushort oldest = _queue.Keys.First();
                if (SeqAhead(oldest, want))
                {
                    _nextSeq = oldest;
                    _underruns++;
                    return false;
                }
            }

            _underruns++;
            return false;
        }
    }

    /// <summary>Drain up to <paramref name="max"/> ready packets (after priming).</summary>
    public int Drain(List<(ushort Seq, uint Ts, byte[] Payload)> into, int max = 8)
    {
        int n = 0;
        while (n < max && TryPop(out ushort seq, out uint ts, out byte[] payload))
        {
            into.Add((seq, ts, payload));
            n++;
        }
        return n;
    }

    private static bool IsLate(ushort seq, ushort next)
    {
        // Late if seq is behind next within half the sequence space.
        short delta = (short)(seq - next);
        return delta < 0;
    }

    private static bool SeqAhead(ushort a, ushort b)
    {
        short delta = (short)(a - b);
        return delta > 0;
    }

    private readonly record struct Packet(ushort Seq, uint Timestamp, byte[] Payload);
}
