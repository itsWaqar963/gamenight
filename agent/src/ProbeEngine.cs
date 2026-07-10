// Probe engine (SDD §20, §18.4): pings each peer's Radmin IP over the tunnel,
// keeps a rolling window per peer, and produces 30s summaries. Uses the Windows
// ICMP API via System.Net.NetworkInformation.Ping — no admin rights required
// (unlike raw sockets), a deliberate choice so the agent installs unelevated.
//
// Metrics (CCNA QoS territory):
//  - avgRtt : mean round-trip time
//  - jitter : mean absolute deviation of consecutive RTTs (the RTP/VoIP defn)
//  - loss   : timeouts / attempts, as a percentage
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace GameNight.Agent;

public sealed class ProbeEngine : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    // peerUserId -> its Radmin IP (updated whenever the server sends a peer list)
    private volatile Dictionary<string, string> _peers = new();
    // peerUserId -> rolling window of recent results (RTT ms, or null for timeout)
    private readonly ConcurrentDictionary<string, Queue<double?>> _windows = new();
    private const int WindowSize = 30; // last 30 probes (~5 min at 10s interval)

    /// <summary>Called when the server sends a fresh peer list.</summary>
    public void SetPeers(IEnumerable<Peer> peers)
    {
        var map = new Dictionary<string, string>();
        foreach (var p in peers) map[p.UserId] = p.RadminIp;
        _peers = map;
        // Forget windows for peers no longer in the list.
        foreach (var key in _windows.Keys)
            if (!map.ContainsKey(key)) _windows.TryRemove(key, out _);
    }

    public void Start() => _ = Task.Run(() => ProbeLoopAsync(_cts.Token));

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var peers = _peers; // snapshot the reference (volatile)
            var tasks = new List<Task>();
            foreach (var (userId, ip) in peers)
                tasks.Add(ProbeOneAsync(userId, ip, ct));
            try { await Task.WhenAll(tasks); } catch { /* individual failures handled inside */ }

            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ProbeOneAsync(string userId, string ip, CancellationToken ct)
    {
        double? result = null;
        try
        {
            using var ping = new Ping();
            // 32-byte payload, 2s timeout — same shape a game's keepalive uses.
            PingReply reply = await ping.SendPingAsync(ip, 2000, new byte[32]);
            if (reply.Status == IPStatus.Success) result = reply.RoundtripTime;
        }
        catch (Exception ex) { Debug.WriteLine($"[Probe] {ip}: {ex.Message}"); }

        var window = _windows.GetOrAdd(userId, _ => new Queue<double?>());
        lock (window)
        {
            window.Enqueue(result);
            while (window.Count > WindowSize) window.Dequeue();
        }
    }

    /// <summary>Snapshot current stats as a metrics payload (called every 30s).</summary>
    public List<MetricSample> Summarize()
    {
        var peers = _peers;
        var samples = new List<MetricSample>();
        foreach (var (userId, _) in peers)
        {
            if (!_windows.TryGetValue(userId, out var window)) continue;
            double?[] snapshot;
            lock (window) { snapshot = window.ToArray(); }
            if (snapshot.Length == 0) continue;

            var rtts = snapshot.Where(r => r.HasValue).Select(r => r!.Value).ToArray();
            int total = snapshot.Length;
            double lossPct = total == 0 ? 0 : 100.0 * (total - rtts.Length) / total;
            double avg = rtts.Length == 0 ? 0 : rtts.Average();

            // Jitter = mean absolute difference between consecutive successful RTTs.
            double jitter = 0;
            if (rtts.Length > 1)
            {
                double sum = 0;
                for (int i = 1; i < rtts.Length; i++) sum += Math.Abs(rtts[i] - rtts[i - 1]);
                jitter = sum / (rtts.Length - 1);
            }

            samples.Add(new MetricSample(userId, Math.Round(avg, 1), Math.Round(jitter, 1),
                                         Math.Round(lossPct, 1), total));
        }
        return samples;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
