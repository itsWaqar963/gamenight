// Socket.IO client for gamenight/voice-server signaling.
using System.Text.Json;
using SocketIOClient;
using SocketIOClient.Transport;

namespace GameNight.Agent.Voice;

public sealed class VoipSignaling : IDisposable
{
    private SocketIOClient.SocketIO? _socket;
    private bool _disposed;

    public bool IsConnected => _socket?.Connected == true;
    public string? SocketId => _socket?.Id;

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<VoicePeerInfo>? PeerJoined;
    public event Action<VoicePeerInfo>? PeerLeft;
    public event Action<string, string, string, SdpPayload>? OfferReceived;   // from, peerId, displayName, offer
    public event Action<string, SdpPayload>? AnswerReceived;                  // from, answer
    public event Action<string, IceCandidatePayload>? IceReceived;            // from, candidate
    public event Action<VoicePeerInfo, bool>? PeerSpeaking;                   // peer, speaking

    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        DisposeSocket();

        string url = NormalizeUrl(serverUrl);
        _socket = new SocketIOClient.SocketIO(url, new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            Reconnection = true,
            ReconnectionAttempts = 5,
            ConnectionTimeout = TimeSpan.FromSeconds(8),
        });

        _socket.OnConnected += (_, _) => Connected?.Invoke();
        _socket.OnDisconnected += (_, reason) => Disconnected?.Invoke(reason);

        _socket.On("peer:joined", response =>
        {
            var peer = response.GetValue<VoicePeerInfo>();
            if (peer is not null) PeerJoined?.Invoke(peer);
        });
        _socket.On("peer:left", response =>
        {
            var peer = response.GetValue<VoicePeerInfo>();
            if (peer is not null) PeerLeft?.Invoke(peer);
        });
        BindSignalHandlers();

        await _socket.ConnectAsync().WaitAsync(ct);
    }

    private void BindSignalHandlers()
    {
        if (_socket is null) return;

        _socket.On("signal:offer", response =>
        {
            try
            {
                var root = response.GetValue<JsonElement>();
                string from = root.GetProperty("from").GetString() ?? "";
                string peerId = root.TryGetProperty("peerId", out var p) ? p.GetString() ?? "" : "";
                string name = root.TryGetProperty("displayName", out var n) ? n.GetString() ?? peerId : peerId;
                var offerEl = root.GetProperty("offer");
                var offer = new SdpPayload
                {
                    Type = offerEl.GetProperty("type").GetString() ?? "offer",
                    Sdp = offerEl.GetProperty("sdp").GetString() ?? "",
                };
                OfferReceived?.Invoke(from, peerId, name, offer);
            }
            catch (Exception ex) { AgentLog.Write("voice.log", $"offer parse: {ex.Message}"); }
        });

        _socket.On("signal:answer", response =>
        {
            try
            {
                var root = response.GetValue<JsonElement>();
                string from = root.GetProperty("from").GetString() ?? "";
                var ansEl = root.GetProperty("answer");
                var answer = new SdpPayload
                {
                    Type = ansEl.GetProperty("type").GetString() ?? "answer",
                    Sdp = ansEl.GetProperty("sdp").GetString() ?? "",
                };
                AnswerReceived?.Invoke(from, answer);
            }
            catch (Exception ex) { AgentLog.Write("voice.log", $"answer parse: {ex.Message}"); }
        });

        _socket.On("signal:ice-candidate", response =>
        {
            try
            {
                var root = response.GetValue<JsonElement>();
                string from = root.GetProperty("from").GetString() ?? "";
                if (!root.TryGetProperty("candidate", out var candEl) || candEl.ValueKind == JsonValueKind.Null)
                    return;
                var ice = new IceCandidatePayload
                {
                    Candidate = candEl.TryGetProperty("candidate", out var c) ? c.GetString() : candEl.GetString(),
                    SdpMid = candEl.TryGetProperty("sdpMid", out var m) ? m.GetString() : null,
                    SdpMLineIndex = candEl.TryGetProperty("sdpMLineIndex", out var i) && i.ValueKind == JsonValueKind.Number
                        ? i.GetInt32() : null,
                };
                if (!string.IsNullOrEmpty(ice.Candidate))
                    IceReceived?.Invoke(from, ice);
            }
            catch (Exception ex) { AgentLog.Write("voice.log", $"ice parse: {ex.Message}"); }
        });

        _socket.On("peer:speaking", response =>
        {
            try
            {
                var root = response.GetValue<JsonElement>();
                var peer = new VoicePeerInfo
                {
                    SocketId = root.TryGetProperty("socketId", out var s) ? s.GetString() ?? "" : "",
                    PeerId = root.TryGetProperty("peerId", out var p) ? p.GetString() ?? "" : "",
                    DisplayName = root.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
                };
                bool speaking = root.TryGetProperty("speaking", out var sp) && sp.GetBoolean();
                PeerSpeaking?.Invoke(peer, speaking);
            }
            catch (Exception ex) { AgentLog.Write("voice.log", $"speaking parse: {ex.Message}"); }
        });
    }

    public async Task<RoomJoinAck> JoinRoomAsync(string roomId, string peerId, string displayName)
    {
        if (_socket is null || !_socket.Connected)
            throw new InvalidOperationException("Not connected to voice server.");

        var tcs = new TaskCompletionSource<RoomJoinAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = new { roomId, peerId, displayName };

        await _socket.EmitAsync("room:join", response =>
        {
            try
            {
                var ack = response.GetValue<RoomJoinAck>() ?? new RoomJoinAck { Error = "empty ack" };
                tcs.TrySetResult(ack);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new RoomJoinAck { Error = ex.Message });
            }
        }, payload);

        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }

    public async Task LeaveRoomAsync()
    {
        if (_socket?.Connected == true)
            await _socket.EmitAsync("room:leave");
    }

    public async Task SendOfferAsync(string toSocketId, SdpPayload offer) =>
        await EmitAsync("signal:offer", new { to = toSocketId, offer });

    public async Task SendAnswerAsync(string toSocketId, SdpPayload answer) =>
        await EmitAsync("signal:answer", new { to = toSocketId, answer });

    public async Task SendIceAsync(string toSocketId, IceCandidatePayload candidate) =>
        await EmitAsync("signal:ice-candidate", new { to = toSocketId, candidate });

    public async Task SendSpeakingAsync(bool speaking) =>
        await EmitAsync("peer:speaking", new { speaking });

    private async Task EmitAsync(string eventName, object payload)
    {
        if (_socket?.Connected != true) return;
        await _socket.EmitAsync(eventName, payload);
    }

    private static string NormalizeUrl(string serverUrl)
    {
        var u = serverUrl.Trim().TrimEnd('/');
        if (u.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return "http://" + u[5..];
        if (u.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return "https://" + u[6..];
        return u;
    }

    private void DisposeSocket()
    {
        var socket = _socket;
        _socket = null;
        if (socket is null) return;

        // Never sync-block here — calling GetResult() on the UI thread deadlocks Leave.
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.DisconnectAsync().WaitAsync(cts.Token);
            }
            catch { /* ignore */ }
            try { socket.Dispose(); } catch { /* ignore */ }
        });
    }

    public async Task LeaveAndDisconnectAsync()
    {
        try
        {
            if (_socket?.Connected == true)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.EmitAsync("room:leave").WaitAsync(cts.Token);
            }
        }
        catch { /* ignore */ }

        var socket = _socket;
        _socket = null;
        if (socket is null) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.DisconnectAsync().WaitAsync(cts.Token);
        }
        catch { /* ignore */ }
        try { socket.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeSocket();
    }
}
