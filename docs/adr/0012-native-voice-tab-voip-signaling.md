# ADR-0012: Native Voice tab in the agent; dedicated signaling server

**Status:** accepted · **Date:** 2026-07-18

## Context
GameNight’s desktop surface is the C# tray agent. Voice should live as a
**tab** in that window (not Electron / Chromium). Signaling stays a small
Socket.IO process (`voice-server/`) that never carries media.

## Decision
- Agent **Voice** tab: native WebRTC (SIPSorcery) + Socket.IO signaling.
- Room codes: **UFLL**, **APR**, or **Custom** (any code, everyone).
- Signaling lives in `gamenight/voice-server/` (not an Electron voip-app).

## Consequences
One exe, one tray. Deploy `voice-server` separately; paste its URL into the
Voice tab. PTT = hold **2**.
