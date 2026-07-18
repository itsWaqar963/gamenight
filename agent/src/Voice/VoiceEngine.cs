// Native WebRTC mesh voice client (SIPSorcery) + voice-server Socket.IO signaling.
using System.Collections.Concurrent;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

namespace GameNight.Agent.Voice;

public sealed class VoiceEngine : IDisposable
{
    private static readonly RTCIceServer[] IceServers =
    {
        new() { urls = "stun:stun.l.google.com:19302" },
        new() { urls = "stun:stun1.l.google.com:19302" },
    };

    private readonly VoipSignaling _signaling = new();
    private readonly ConcurrentDictionary<string, PeerSlot> _peers = new();
    private readonly object _gate = new();

    private WindowsAudioEndPoint? _mic;
    private LocalVad? _vad;
    private readonly RemoteSpeakingTracker _remoteSpeak = new();
    private bool _connected;
    private bool _muted;
    private bool _pushToTalk;
    private bool _pttActive;
    private bool _disposed;
    private bool _localSpeaking;
    private string _peerId = Guid.NewGuid().ToString("D");
    private string _displayName = "Anonymous";

    public bool IsConnected => _connected;
    public string PeerId => _peerId;
    public string DisplayName => _displayName;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<VoicePeerInfo>? PeerJoined;
    public event Action<VoicePeerInfo>? PeerLeft;
    public event Action<VoicePeerInfo, bool>? PeerSpeaking;
    public event Action<bool>? LocalSpeaking;
    public event Action<string>? Error;
    public event Action<bool>? MutedChanged;

    public async Task ConnectAsync(string serverUrl, string roomId, string displayName, bool pushToTalk, int micSensitivity = 55)
    {
        if (_connected) throw new InvalidOperationException("Already connected. Leave first.");

        if (!VoiceRooms.TryNormalize(roomId, out string room, out string? roomErr))
            throw new InvalidOperationException(roomErr ?? VoiceRooms.Hint);

        _displayName = string.IsNullOrWhiteSpace(displayName) ? "Anonymous" : displayName.Trim();
        _peerId = Guid.NewGuid().ToString("D");
        _pushToTalk = pushToTalk;
        _muted = pushToTalk;
        _pttActive = false;

        _mic = new WindowsAudioEndPoint(new AudioEncoder(), disableSource: false, disableSink: true);
        _mic.RestrictFormats(f => f.Codec == AudioCodecsEnum.PCMU || f.Codec == AudioCodecsEnum.PCMA);

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

        await _signaling.ConnectAsync(serverUrl);
        await _mic.StartAudio();

        if (_muted) await _mic.PauseAudio();

        _vad = new LocalVad(SensitivityToThreshold(micSensitivity));
        _vad.SetEnabled(!_muted);
        _vad.SpeakingChanged += OnLocalVadSpeaking;

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
        AgentLog.Write("voice.log", $"joined room={room} as {_displayName}");
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
        if (_mic is null) { MutedChanged?.Invoke(_muted); return; }
        if (_muted) await _mic.PauseAudio();
        else await _mic.ResumeAudio();
        MutedChanged?.Invoke(_muted);
    }

    public async Task ToggleMuteAsync() => await SetMutedAsync(!_muted);

    public async Task SetPttAsync(bool active)
    {
        if (!_pushToTalk || _mic is null) return;
        _pttActive = active;
        _muted = !active;
        _vad?.SetEnabled(active);
        if (active)
        {
            await _mic.ResumeAudio();
            _vad?.ForceSpeaking(true);
            SetLocalSpeaking(true);
        }
        else
        {
            await _mic.PauseAudio();
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
        // 1 → 0.08 (deaf), 100 → 0.004 (very sensitive)
        return 0.08f - ((s - 1) / 99f) * 0.076f;
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
            _vad.Dispose();
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
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = IceServers.ToList() });

        slot.Speaker = new WindowsAudioEndPoint(new AudioEncoder(), disableSource: true, disableSink: false);
        slot.Speaker.RestrictFormats(f => f.Codec == AudioCodecsEnum.PCMU || f.Codec == AudioCodecsEnum.PCMA);
        slot.Decoder ??= new AudioEncoder();

        var formats = _mic!.GetAudioSourceFormats();
        var track = new MediaStreamTrack(formats, MediaStreamStatusEnum.SendRecv);
        pc.addTrack(track);

        _mic.OnAudioSourceEncodedSample += (duration, sample) =>
        {
            if (_muted) return;
            try { pc.SendAudio(duration, sample); } catch { /* peer may be closing */ }
        };

        pc.OnAudioFormatsNegotiated += formatsNegotiated =>
        {
            var fmt = formatsNegotiated.First();
            slot.AudioFormat = fmt;
            _mic.SetAudioSourceFormat(fmt);
            slot.Speaker.SetAudioSinkFormat(fmt);
        };

        pc.OnRtpPacketReceived += (ep, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio || slot.Speaker is null) return;

            float vol = slot.Volume;
            if (vol < 0.01f)
            {
                // Muted for this peer — still track speaking for UI.
            }
            else if (vol >= 0.99f || slot.AudioFormat.Codec == AudioCodecsEnum.Unknown)
            {
                slot.Speaker.GotAudioRtp(
                    ep,
                    pkt.Header.SyncSource,
                    pkt.Header.SequenceNumber,
                    pkt.Header.Timestamp,
                    pkt.Header.PayloadType,
                    pkt.Header.MarkerBit == 1,
                    pkt.Payload);
            }
            else
            {
                try
                {
                    short[] pcm = slot.Decoder!.DecodeAudio(pkt.Payload, slot.AudioFormat);
                    for (int i = 0; i < pcm.Length; i++)
                        pcm[i] = (short)Math.Clamp((int)(pcm[i] * vol), short.MinValue, short.MaxValue);
                    byte[] pcmBytes = new byte[pcm.Length * 2];
                    Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
                    slot.Speaker.GotAudioSample(pcmBytes);
                }
                catch
                {
                    slot.Speaker.GotAudioRtp(
                        ep,
                        pkt.Header.SyncSource,
                        pkt.Header.SequenceNumber,
                        pkt.Header.Timestamp,
                        pkt.Header.PayloadType,
                        pkt.Header.MarkerBit == 1,
                        pkt.Payload);
                }
            }

            // Audio-energy speaking indicator (works even if peer VAD broadcast is late).
            if (_remoteSpeak.ObservePcmu(slot.Info.SocketId, pkt.Payload, out bool changed, out bool speaking)
                && changed)
            {
                PeerSpeaking?.Invoke(slot.Info, speaking);
            }
        };

        pc.onicecandidate += async cand =>
        {
            if (cand is null || string.IsNullOrEmpty(cand.candidate)) return;
            try
            {
                await _signaling.SendIceAsync(slot.Info.SocketId, new IceCandidatePayload
                {
                    Candidate = cand.candidate,
                    SdpMid = cand.sdpMid,
                    SdpMLineIndex = cand.sdpMLineIndex,
                });
            }
            catch (Exception ex) { AgentLog.Write("voice.log", $"send ice: {ex.Message}"); }
        };

        pc.onconnectionstatechange += async state =>
        {
            AgentLog.Write("voice.log", $"{slot.Info.DisplayName} → {state}");
            if (state == RTCPeerConnectionState.connected)
                await slot.Speaker.StartAudioSink();
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
                await slot.Speaker.CloseAudioSink();
        };

        return pc;
    }

    private async Task ClosePeerAsync(string socketId)
    {
        if (!_peers.TryRemove(socketId, out var slot)) return;
        try { slot.Pc?.close(); } catch { /* ignore */ }
        try
        {
            if (slot.Speaker is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await slot.Speaker.CloseAudioSink().WaitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* ignore */ }
    }

    private async Task CleanupMediaAsync()
    {
        WindowsAudioEndPoint? mic;
        lock (_gate)
        {
            mic = _mic;
            _mic = null;
        }
        if (mic is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await mic.CloseAudio().WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* ignore */ }
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
                    _ = Task.Run(async () => { try { await speaker.CloseAudioSink(); } catch { /* ignore */ } });
            }
        }
        WindowsAudioEndPoint? mic;
        lock (_gate)
        {
            mic = _mic;
            _mic = null;
        }
        if (mic is not null)
            _ = Task.Run(async () => { try { await mic.CloseAudio(); } catch { /* ignore */ } });
    }

    private sealed class PeerSlot
    {
        public VoicePeerInfo Info;
        public RTCPeerConnection? Pc;
        public WindowsAudioEndPoint? Speaker;
        public AudioEncoder? Decoder;
        public AudioFormat AudioFormat;
        public float Volume = 1f;

        public PeerSlot(VoicePeerInfo info) => Info = info;
    }
}
