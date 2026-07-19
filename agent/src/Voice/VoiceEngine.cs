// Native WebRTC mesh voice client (SIPSorcery) + voice-server Socket.IO signaling.
// v0.10: Opus 48 kHz mono, receive jitter buffer, NAudio speaker sink (no Media.Windows).
using System.Collections.Concurrent;
using System.Net;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace GameNight.Agent.Voice;

public enum PeerMediaLinkState
{
    Linking,
    Linked,
    Failed,
}

public sealed class VoiceEngine : IDisposable
{
    private static readonly RTCIceServer[] StunServers =
    {
        new() { urls = "stun:stun.l.google.com:19302" },
        new() { urls = "stun:stun1.l.google.com:19302" },
        new() { urls = "stun:stun2.l.google.com:19302" },
        new() { urls = "stun:stun.cloudflare.com:3478" },
    };

    private static readonly AudioFormat LockedOpus = SharedMicSource.LockedOpus;

    private readonly VoipSignaling _signaling = new();
    private readonly ConcurrentDictionary<string, PeerSlot> _peers = new();
    private readonly object _gate = new();
    private readonly List<(ushort Seq, uint Ts, byte[] Payload)> _drainScratch = new(8);

    private SharedMicSource? _sharedMic;
    private LocalVad? _vad;
    private readonly RemoteSpeakingTracker _remoteSpeak = new();
    private bool _talkViaRadmin;
    private IPAddress? _radminBind;
    private bool _connected;
    private bool _muted;
    private bool _pushToTalk;
    private bool _pttActive;
    private bool _disposed;
    private bool _localSpeaking;
    private string _peerId = Guid.NewGuid().ToString("D");
    private string _displayName = "Anonymous";

    public bool IsConnected => _connected;
    public bool TalkViaRadmin => _talkViaRadmin;
    public string PeerId => _peerId;
    public string DisplayName => _displayName;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<VoicePeerInfo>? PeerJoined;
    public event Action<VoicePeerInfo>? PeerLeft;
    public event Action<VoicePeerInfo, bool>? PeerSpeaking;
    public event Action<VoicePeerInfo, PeerMediaLinkState>? PeerMediaState;
    public event Action<bool>? LocalSpeaking;
    public event Action<string>? Error;
    public event Action<bool>? MutedChanged;

    public async Task ConnectAsync(
        string serverUrl,
        string roomId,
        string displayName,
        bool pushToTalk,
        int micSensitivity = 55,
        bool shareMicWithOtherApps = true,
        bool talkViaRadmin = false)
    {
        if (_connected) throw new InvalidOperationException("Already connected. Leave first.");

        if (!VoiceRooms.TryNormalize(roomId, out string room, out string? roomErr))
            throw new InvalidOperationException(roomErr ?? VoiceRooms.Hint);

        _talkViaRadmin = talkViaRadmin;
        _radminBind = null;
        if (_talkViaRadmin)
        {
            if (!RadminIce.TryGetBindAddress(out IPAddress bind))
                throw new InvalidOperationException(
                    "Radmin VPN not connected (no 26.x address). Open Radmin VPN, then join again.");
            _radminBind = bind;
            AgentLog.Write("voice.log", $"ICE: Radmin P2P bind={bind}");
        }
        else
        {
            AgentLog.Write("voice.log", "ICE: VoIP (STUN)");
        }

        _displayName = string.IsNullOrWhiteSpace(displayName) ? "Anonymous" : displayName.Trim();
        _peerId = Guid.NewGuid().ToString("D");
        _pushToTalk = pushToTalk;
        _muted = pushToTalk;
        _pttActive = false;

        _signaling.PeerJoined += OnRemotePeerJoined;
        _signaling.PeerLeft += OnRemotePeerLeft;
        _signaling.OfferReceived += OnOffer;
        _signaling.AnswerReceived += OnAnswer;
        _signaling.IceReceived += OnIce;
        _signaling.PeerSpeaking += (p, s) => PeerSpeaking?.Invoke(p, s);
        _signaling.Disconnected += reason =>
        {
            AgentLog.Write("voice.log", $"signaling disconnected: {reason}");
            Error?.Invoke($"Voice server disconnected ({reason})");
        };

        await _signaling.ConnectAsync(serverUrl).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // Create VAD before mic start so gate + Speaking UI share one instance/threshold.
        _vad = new LocalVad(SensitivityToThreshold(micSensitivity), LocalVad.DefaultHangoverMs);
        _vad.SetEnabled(!_muted);
        _vad.SpeakingChanged += OnLocalVadSpeaking;

        var shared = new SharedMicSource();
        shared.SetSharedMode(shareMicWithOtherApps);
        shared.Error += msg => Error?.Invoke(msg);
        shared.SetFormat(LockedOpus);
        shared.ShouldTransmit = pcm =>
        {
            if (_pushToTalk)
            {
                // PTT-only transmit gate; still feed VAD for Speaking UI while held.
                if (_pttActive)
                    _vad?.ObservePcm(pcm);
                return _pttActive;
            }

            return _vad!.ShouldTransmitPcm(pcm);
        };
        shared.EncodedSample += OnMicEncoded;
        _sharedMic = shared;
        // Mic open/init off the UI thread (WASAPI activate must not touch WinForms SyncContext).
        if (!_muted)
            await Task.Run(() => shared.Start()).ConfigureAwait(false);
        AgentLog.Write("voice.log",
            $"mic: SharedMicSource Opus 48 kHz ({(shareMicWithOtherApps ? "shared" : "exclusive")} WASAPI)");

        var ack = await _signaling.JoinRoomAsync(room, _peerId, _displayName);
        if (!string.IsNullOrEmpty(ack.Error))
            throw new InvalidOperationException(ack.Error);

        foreach (var existing in ack.Peers ?? [])
        {
            PeerJoined?.Invoke(existing);
            await CreateOfferAsync(existing);
        }

        _connected = true;
        Connected?.Invoke();
        MutedChanged?.Invoke(_muted);
        AgentLog.Write("voice.log", $"joined room={room} as {_displayName} (Opus-only SDP)");
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;

        _muted = true;
        bool wasConnected = _connected;
        _connected = false;

        try
        {
            await _signaling.LeaveAndDisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"leave/disconnect: {ex.Message}");
        }

        foreach (var id in _peers.Keys.ToList())
            await ClosePeerAsync(id).ConfigureAwait(false);

        _remoteSpeak.Clear();
        StopVad();
        await CleanupMediaAsync().ConfigureAwait(false);

        if (wasConnected)
        {
            try { Disconnected?.Invoke(); } catch { /* ignore */ }
        }
    }

    public async Task SetMutedAsync(bool muted)
    {
        if (_pushToTalk) return;
        _muted = muted;
        _vad?.SetEnabled(!_muted);
        if (_muted) SetLocalSpeaking(false);
        if (_sharedMic is null)
        {
            MutedChanged?.Invoke(_muted);
            return;
        }
        if (_muted) await PauseMicAsync();
        else await ResumeMicAsync();
        MutedChanged?.Invoke(_muted);
    }

    public async Task ToggleMuteAsync() => await SetMutedAsync(!_muted);

    public async Task SetPttAsync(bool active)
    {
        if (!_pushToTalk) return;
        if (_sharedMic is null) return;
        _pttActive = active;
        _muted = !active;
        _vad?.SetEnabled(active);
        if (active)
        {
            await ResumeMicAsync();
            _vad?.ForceSpeaking(true);
            SetLocalSpeaking(true);
        }
        else
        {
            await PauseMicAsync();
            _vad?.ForceSpeaking(false);
            SetLocalSpeaking(false);
        }
        MutedChanged?.Invoke(_muted);
    }

    /// <summary>1–100 UI sensitivity → VAD threshold (higher sensitivity = quieter speech triggers).</summary>
    public void SetMicSensitivity(int sensitivity)
    {
        _vad?.SetThreshold(SensitivityToThreshold(sensitivity));
    }

    /// <summary>Per-peer playback volume 0.0–1.0.</summary>
    public void SetPeerVolume(string socketId, float volume)
    {
        if (!_peers.TryGetValue(socketId, out var slot)) return;
        slot.Volume = Math.Clamp(volume, 0f, 1f);
    }

    public static float SensitivityToThreshold(int sensitivity)
    {
        int s = Math.Clamp(sensitivity, 1, 100);
        // More sensitive overall so speech opens Speaking + gate reliably.
        // 1 → 0.04 (deaf), 100 → 0.002 (very sensitive); default 55 ≈ 0.019
        return 0.04f - ((s - 1) / 99f) * 0.038f;
    }

    private void OnMicEncoded(uint duration, byte[] sample)
    {
        if (_muted) return;

        foreach (var slot in _peers.Values)
        {
            if (slot.Pc is null) continue;
            // Skip only hard failures — allow send while Linking so RTP can start as soon as DTLS is up.
            if (slot.LinkState == PeerMediaLinkState.Failed) continue;
            try { slot.Pc.SendAudio(duration, sample); } catch { /* peer may be closing / not ready */ }
        }
    }

    private void SetPeerLink(PeerSlot slot, PeerMediaLinkState state)
    {
        if (slot.LinkState == state) return;
        slot.LinkState = state;
        try { PeerMediaState?.Invoke(slot.Info, state); } catch { /* ignore */ }
    }

    private async Task PauseMicAsync()
    {
        _sharedMic?.Pause();
        await Task.CompletedTask;
    }

    private async Task ResumeMicAsync()
    {
        if (_sharedMic is null) return;
        // Start() may open WASAPI — never do that on the UI thread.
        await Task.Run(() => _sharedMic.Resume()).ConfigureAwait(false);
    }

    private void OnLocalVadSpeaking(bool speaking)
    {
        if (_muted && !_pttActive)
        {
            SetLocalSpeaking(false);
            return;
        }
        SetLocalSpeaking(speaking);
    }

    private void SetLocalSpeaking(bool speaking)
    {
        if (_localSpeaking == speaking) return;
        _localSpeaking = speaking;
        try { LocalSpeaking?.Invoke(speaking); } catch { /* ignore */ }
        _ = _signaling.SendSpeakingAsync(speaking);
    }

    private void StopVad()
    {
        if (_vad is null) return;
        try
        {
            _vad.SpeakingChanged -= OnLocalVadSpeaking;
            _vad.Reset();
        }
        catch { /* ignore */ }
        _vad = null;
        SetLocalSpeaking(false);
    }

    private async void OnRemotePeerJoined(VoicePeerInfo peer)
    {
        // Existing peers send offers to us; prepare slot only.
        _peers.TryAdd(peer.SocketId, new PeerSlot(peer));
        PeerJoined?.Invoke(peer);
        await Task.CompletedTask;
    }

    private void OnRemotePeerLeft(VoicePeerInfo peer)
    {
        _remoteSpeak.Remove(peer.SocketId);
        _ = ClosePeerAsync(peer.SocketId);
        PeerLeft?.Invoke(peer);
    }

    private async void OnOffer(string from, string peerId, string displayName, SdpPayload offer)
    {
        try
        {
            var info = new VoicePeerInfo { SocketId = from, PeerId = peerId, DisplayName = displayName };
            var slot = _peers.GetOrAdd(from, _ => new PeerSlot(info));
            slot.Info = info;

            if (slot.Pc is null)
                slot.Pc = CreatePeerConnection(slot);

            var result = slot.Pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offer.Sdp,
            });
            if (result != SetDescriptionResultEnum.OK)
            {
                Error?.Invoke($"Bad remote offer from {displayName}: {result}");
                return;
            }

            var answer = slot.Pc.createAnswer();
            await slot.Pc.setLocalDescription(answer);
            await _signaling.SendAnswerAsync(from, new SdpPayload { Type = "answer", Sdp = answer.sdp });
            PeerJoined?.Invoke(info);
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"handle offer: {ex.Message}");
            Error?.Invoke(ex.Message);
        }
    }

    private async void OnAnswer(string from, SdpPayload answer)
    {
        try
        {
            if (!_peers.TryGetValue(from, out var slot) || slot.Pc is null) return;
            var result = slot.Pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answer.Sdp,
            });
            if (result != SetDescriptionResultEnum.OK)
                Error?.Invoke($"Bad answer: {result}");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"handle answer: {ex.Message}");
        }
    }

    private async void OnIce(string from, IceCandidatePayload ice)
    {
        try
        {
            if (!_peers.TryGetValue(from, out var slot) || slot.Pc is null) return;
            if (string.IsNullOrEmpty(ice.Candidate)) return;
            if (_talkViaRadmin && !RadminIce.IsRadminCandidateString(ice.Candidate))
                return;
            slot.Pc.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = ice.Candidate,
                sdpMid = ice.SdpMid,
                sdpMLineIndex = (ushort)(ice.SdpMLineIndex ?? 0),
            });
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"handle ice: {ex.Message}");
        }
    }

    private async Task CreateOfferAsync(VoicePeerInfo peer)
    {
        var slot = _peers.GetOrAdd(peer.SocketId, _ => new PeerSlot(peer));
        slot.Info = peer;
        slot.Pc ??= CreatePeerConnection(slot);

        var offer = slot.Pc.createOffer();
        await slot.Pc.setLocalDescription(offer);
        await _signaling.SendOfferAsync(peer.SocketId, new SdpPayload { Type = "offer", Sdp = offer.sdp });
    }

    private RTCPeerConnection CreatePeerConnection(PeerSlot slot)
    {
        var cfg = new RTCConfiguration
        {
            iceServers = _talkViaRadmin ? [] : StunServers.ToList(),
        };
        if (_talkViaRadmin && _radminBind is not null)
        {
            cfg.X_BindAddress = _radminBind;
        }
        else
        {
            // VoIP: gather host candidates on every NIC (including Radmin 26.x when present).
            cfg.X_ICEIncludeAllInterfaceAddresses = true;
        }

        var pc = new RTCPeerConnection(cfg);
        SetPeerLink(slot, PeerMediaLinkState.Linking);

        slot.Speaker = new PcmSpeakerSink();
        slot.Decoder ??= new AudioEncoder(includeLinearFormats: false, includeOpus: true);
        slot.Jitter ??= new OpusJitterBuffer();

        var track = new MediaStreamTrack([LockedOpus], MediaStreamStatusEnum.SendRecv);
        pc.addTrack(track);

        pc.OnAudioFormatsNegotiated += formatsNegotiated =>
        {
            var fmt = formatsNegotiated.FirstOrDefault(f => f.Codec == AudioCodecsEnum.OPUS);
            if (fmt.Codec == AudioCodecsEnum.Unknown)
                fmt = LockedOpus;
            slot.AudioFormat = fmt;
            _sharedMic?.SetFormat(fmt);
            AgentLog.Write("voice.log",
                $"negotiated audio with {slot.Info.DisplayName}: {fmt.Codec} {fmt.ClockRate}Hz ch={fmt.ChannelCount} pt={fmt.FormatID}");
        };

        pc.OnRtpPacketReceived += (ep, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio || slot.Speaker is null) return;
            _ = ep;

            slot.Jitter!.Push(pkt.Header.SequenceNumber, pkt.Header.Timestamp, pkt.Payload);

            _drainScratch.Clear();
            slot.Jitter.Drain(_drainScratch);

            float vol = slot.Volume;
            foreach (var item in _drainScratch)
            {
                byte[] payload = item.Payload;
                try
                {
                    short[] pcm = slot.Decoder!.DecodeAudio(payload, slot.AudioFormat.Codec == AudioCodecsEnum.Unknown
                        ? LockedOpus
                        : slot.AudioFormat);

                    if (_remoteSpeak.ObservePcm(slot.Info.SocketId, pcm, out bool changed, out bool speaking)
                        && changed)
                    {
                        PeerSpeaking?.Invoke(slot.Info, speaking);
                    }

                    if (vol < 0.01f)
                        continue;

                    if (vol < 0.99f)
                    {
                        for (int i = 0; i < pcm.Length; i++)
                            pcm[i] = (short)Math.Clamp((int)(pcm[i] * vol), short.MinValue, short.MaxValue);
                    }

                    slot.Speaker.WritePcm(pcm);
                }
                catch (Exception ex)
                {
                    AgentLog.Write("voice.log", $"decode/play: {ex.Message}");
                }
            }
        };

        pc.onicecandidate += async cand =>
        {
            if (cand is null || string.IsNullOrEmpty(cand.candidate)) return;
            if (_talkViaRadmin && !RadminIce.IsRadminHostCandidate(cand))
            {
                AgentLog.Write("voice.log", "skip trickle non-Radmin candidate");
                return;
            }
            try
            {
                AgentLog.Write("voice.log",
                    $"ICE local → {slot.Info.DisplayName}: {TruncateCand(cand.candidate)}");
                await _signaling.SendIceAsync(slot.Info.SocketId, new IceCandidatePayload
                {
                    Candidate = cand.candidate,
                    SdpMid = cand.sdpMid,
                    SdpMLineIndex = cand.sdpMLineIndex,
                });
            }
            catch (Exception ex) { AgentLog.Write("voice.log", $"send ice: {ex.Message}"); }
        };

        // Mark Linked as soon as ICE is up (often before full PC "connected") so RTP can flow.
        pc.oniceconnectionstatechange += state =>
        {
            AgentLog.Write("voice.log",
                $"{slot.Info.DisplayName} ICE → {state} (mode={(_talkViaRadmin ? "radmin" : "voip")})");
            if (state == RTCIceConnectionState.connected)
            {
                _ = OnPeerMediaReadyAsync(slot);
            }
            else if (state == RTCIceConnectionState.failed)
            {
                SetPeerLink(slot, PeerMediaLinkState.Failed);
                AgentLog.Write("voice.log",
                    $"media FAILED (ICE) with {slot.Info.DisplayName} — both peers should use the same path " +
                    "(VoIP+VoIP or Radmin+Radmin). Prefer Radmin if STUN is blocked.");
                Error?.Invoke(
                    $"Link failed with {slot.Info.DisplayName}. Use matching Voice path " +
                    "(both VoIP or both Radmin).");
            }
        };

        pc.onconnectionstatechange += async state =>
        {
            AgentLog.Write("voice.log",
                $"{slot.Info.DisplayName} → {state} (ice={(_talkViaRadmin ? "radmin" : "voip")})");
            if (state == RTCPeerConnectionState.connected)
            {
                await OnPeerMediaReadyAsync(slot);
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                SetPeerLink(slot, PeerMediaLinkState.Failed);
                if (slot.Speaker is not null)
                    await slot.Speaker.CloseAsync();
            }
            else if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
            {
                if (state == RTCPeerConnectionState.disconnected)
                    SetPeerLink(slot, PeerMediaLinkState.Linking);
                if (slot.Speaker is not null)
                    await slot.Speaker.CloseAsync();
            }
        };

        return pc;
    }

    private async Task OnPeerMediaReadyAsync(PeerSlot slot)
    {
        bool becameLinked = slot.LinkState != PeerMediaLinkState.Linked;
        SetPeerLink(slot, PeerMediaLinkState.Linked);
        if (becameLinked)
            AgentLog.Write("voice.log", $"media Linked with {slot.Info.DisplayName} — RTP send enabled");
        try
        {
            slot.Speaker?.Start();
            if (!_muted)
                await ResumeMicAsync();
        }
        catch (Exception ex)
        {
            AgentLog.Write("voice.log", $"media ready sink: {ex.Message}");
        }
    }

    private static string TruncateCand(string? c)
    {
        if (string.IsNullOrEmpty(c)) return "";
        return c.Length <= 96 ? c : c[..96] + "…";
    }

    private async Task ClosePeerAsync(string socketId)
    {
        if (!_peers.TryRemove(socketId, out var slot)) return;
        try { slot.Pc?.close(); } catch { /* ignore */ }
        try
        {
            slot.Jitter?.Clear();
            if (slot.Speaker is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await slot.Speaker.CloseAsync().WaitAsync(cts.Token).ConfigureAwait(false);
                slot.Speaker.Dispose();
            }
        }
        catch { /* ignore */ }
    }

    private async Task CleanupMediaAsync()
    {
        SharedMicSource? shared;
        lock (_gate)
        {
            shared = _sharedMic;
            _sharedMic = null;
        }

        if (shared is not null)
        {
            try { shared.EncodedSample -= OnMicEncoded; } catch { /* ignore */ }
            try { shared.Dispose(); } catch { /* ignore */ }
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _muted = true;
        _connected = false;
        StopVad();

        // Non-blocking: never GetResult() — that froze Leave on the UI thread.
        _signaling.Dispose();
        foreach (var id in _peers.Keys.ToList())
        {
            if (_peers.TryRemove(id, out var slot))
            {
                try { slot.Pc?.close(); } catch { /* ignore */ }
                var speaker = slot.Speaker;
                if (speaker is not null)
                    _ = Task.Run(() => { try { speaker.Dispose(); } catch { /* ignore */ } });
            }
        }

        SharedMicSource? shared;
        lock (_gate)
        {
            shared = _sharedMic;
            _sharedMic = null;
        }
        if (shared is not null)
            _ = Task.Run(() => { try { shared.Dispose(); } catch { /* ignore */ } });
    }

    private sealed class PeerSlot
    {
        public VoicePeerInfo Info;
        public RTCPeerConnection? Pc;
        public PcmSpeakerSink? Speaker;
        public AudioEncoder? Decoder;
        public OpusJitterBuffer? Jitter;
        public AudioFormat AudioFormat;
        public float Volume = 1f;
        public PeerMediaLinkState LinkState = PeerMediaLinkState.Linking;

        public PeerSlot(VoicePeerInfo info)
        {
            Info = info;
            AudioFormat = LockedOpus;
        }
    }
}
