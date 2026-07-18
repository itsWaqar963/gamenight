# GameNight Voice Signaling Server

Lightweight Socket.IO broker for the agent **Voice** tab (WebRTC mesh).  
Does **not** carry audio — only room join + SDP/ICE relay.

```bash
cd voice-server
npm install
npm start
```

Default: `http://127.0.0.1:3001` (`/health` for probes).

Room codes: **UFLL**, **APR** (normalized uppercase), or any **Custom** code (max 32 chars).
