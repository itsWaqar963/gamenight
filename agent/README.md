# GameNight Agent (Phase 2+)

C#/.NET 8 Windows tray application (SDD §7.3, §12).

## Tabs

| Tab | Purpose |
|-----|---------|
| **Status** | Connection, presence, Radmin, updates, pause |
| **Voice** | Squad voice — UFLL / APR / Custom rooms, native WebRTC. See ADR-0012. |

### Voice server path (dropdown)

| Option | Behavior |
|--------|----------|
| **GameNight VoIP** | Signaling + media via `https://voip-app-production-fc3c.up.railway.app` (STUN) |
| **Radmin (P2P)** | Same signaling URL; RTP over Radmin `26.x` host ICE |
| **Custom…** | Paste any Socket.IO voice signaling URL |

PTT: hold your bound key (default **2** / numpad 2) only while joined with push-to-talk on.
Bind any key from the Voice tab (“Press to bind…”). The key is not captured — you can still type it in other apps.

**Codec:** Opus **48 kHz** mono (20 ms). Both peers must run this Opus build — there is no PCMU fallback.

**Share mic** (default on): WASAPI shared capture so Discord and other apps can use the mic at the same time.
A VAD noise gate (same threshold as the mic sensitivity / Speaking UI, 500 ms hangover) zeros PCM before Opus encode when you’re not speaking (open-mic). PTT mode transmits only while the key is held.
While the gate is open, **SpeexDSP** denoise at 48 kHz cleans fans/keyboard/hiss before encode (falls back safely if the native DLL is missing).
Receive path uses a short Opus jitter buffer (~80 ms) before decode/playback.

## Build

```bash
cd agent
dotnet publish -c Release
```

Output: `bin/Release/net8.0-windows10.0.17763.0/win-x64/publish/GameNightAgent.exe`
