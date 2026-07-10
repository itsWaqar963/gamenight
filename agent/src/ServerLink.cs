// The agent's lifeline (SDD §7.3, §12 state machine): connect, hello,
// heartbeat every 15s, push state on change, reconnect forever with
// exponential backoff + jitter. Fail-quiet: errors log to Debug and retry;
// the agent must NEVER interrupt gameplay with a dialog.
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GameNight.Agent;

public sealed class ServerLink : IDisposable
{
    private readonly Uri _wsUri;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _sock;
    private string _lastState = "";
    private RadminInfo _lastRadmin = new(false, null);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<string>? StatusChanged; // for the tray tooltip
    public event Action<List<Peer>>? PeersReceived; // Phase 3: probe targets from server

    public ServerLink(string serverUrl, string token)
    {
        var b = new UriBuilder(serverUrl);
        b.Scheme = b.Scheme == "https" ? "wss" : "ws";
        b.Path = "/ws";
        _wsUri = b.Uri;
        _token = token;
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Called by detectors; sends only on actual change (delta discipline).</summary>
    public void ReportState(string state, RadminInfo radmin)
    {
        lock (_gate)
        {
            if (state == _lastState && radmin == _lastRadmin)
            {
                Log($"SKIP no-change state={state} ip={radmin.Ip}");
                return;
            }
            _lastState = state;
            _lastRadmin = radmin;
        }
        Log($"SEND state={state} ip={radmin.Ip} socket={_sock?.State}");
        _ = SendAsync(StateMsg.Create(state, radmin));
    }

    private static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameNight");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "link.log"), $"{DateTime.Now:HH:mm:ss} {msg}\r\n");
        }
        catch { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke("connecting…");
                _sock = new ClientWebSocket();
                await _sock.ConnectAsync(_wsUri, ct);
                await SendAsync(HelloMsg.Create(_token));
                attempt = 0;
                StatusChanged?.Invoke("connected");

                // re-announce current state after every (re)connect —
                // the server's registry died with the old socket
                // On every (re)connect, forget prior state and force a fresh
                // detect-and-send AFTER the socket is confirmed open — our
                // first pre-connect send may have been dropped (the IP bug).
                lock (_gate) { _lastState = ""; _lastRadmin = new RadminInfo(false, null); }
                ReportState(GameDetector.IsFarCry2Running() ? "in_game" : "online", RadminDetector.Detect());

                using var hb = new PeriodicTimer(TimeSpan.FromSeconds(15));
                var recv = ReceiveLoopAsync(_sock, ct);
                while (await hb.WaitForNextTickAsync(ct))
                {
                    if (_sock.State != WebSocketState.Open) break;
                    await SendAsync(HeartbeatMsg.Instance);
                    if (recv.IsCompleted) break; // server closed on us
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ServerLink] {ex.Message}");
            }

            // Exponential backoff with ±20% jitter (SDD §7.3): 1,2,4…60s.
            // Jitter prevents 20 agents reconnecting in lockstep after a
            // server restart — same reasoning as Ethernet's random backoff.
            double baseDelay = Math.Min(Math.Pow(2, attempt++), 60);
            double jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            StatusChanged?.Invoke($"reconnecting in {(int)(baseDelay * jitter)}s");
            try { await Task.Delay(TimeSpan.FromSeconds(baseDelay * jitter), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket sock, CancellationToken ct)
    {
        // WebSocket messages can span multiple frames; accumulate until EndOfMessage.
        var buf = new byte[8192];
        var acc = new System.IO.MemoryStream();
        try
        {
            while (sock.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult r = await sock.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                acc.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;

                var json = System.Text.Encoding.UTF8.GetString(acc.ToArray());
                acc.SetLength(0);
                HandleServerMessage(json);
            }
        }
        catch { /* socket died; outer loop reconnects */ }
    }

    private void HandleServerMessage(string json)
    {
        try
        {
            // Peek at the discriminator without a full typed parse.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("t", out var tEl)) return;
            switch (tEl.GetString())
            {
                case "peers":
                    var msg = System.Text.Json.JsonSerializer.Deserialize<PeersMsg>(json);
                    if (msg?.List != null) PeersReceived?.Invoke(msg.List);
                    break;
                // hello_ok, toasts, updates handled in later phases; unknown types ignored.
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[ServerLink.recv] {ex.Message}"); }
    }

    /// <summary>Send a metrics summary to the server (Phase 3).</summary>
    public void ReportMetrics(List<MetricSample> samples)
    {
        if (samples.Count == 0) return;
        _ = SendAsync(MetricsMsg.Create(samples));
    }

   private async Task SendAsync<T>(T msg)
    {
        var s = _sock;
        if (s is null || s.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
            await s.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception ex) { Debug.WriteLine($"[ServerLink.send] {ex.Message}"); }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _sock?.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "quit", CancellationToken.None).Wait(1000); }
        catch { /* best effort */ }
        _sock?.Dispose();
        _cts.Dispose();
    }
}
