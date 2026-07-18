# GameNight Agent (Phase 2+)

C#/.NET 8 Windows tray application (SDD §7.3, §12).

## Tabs

| Tab | Purpose |
|-----|---------|
| **Status** | Connection, presence, Radmin, updates, pause |
| **Voice** | Squad voice — UFLL / APR / Custom rooms, native WebRTC. See ADR-0012. |

Point the Voice tab at your `voice-server` URL (local or Railway).

PTT: hold **2** (or numpad 2) only while joined with push-to-talk on.
The key is not captured — you can still type `2` in other apps.

## Build

```bash
cd agent
dotnet publish -c Release
```

Output: `bin/Release/net8.0-windows10.0.17763.0/win-x64/publish/GameNightAgent.exe`
