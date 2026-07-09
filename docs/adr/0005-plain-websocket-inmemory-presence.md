# ADR-0005: Plain WebSocket protocol; presence lives in memory

**Status:** accepted · **Date:** 2026-07-09

## Context
Phase 2 needs bidirectional real-time between C# agents, browsers, and one
Node server, on a 512MB free tier (SDD §5–7, §11).

## Options considered
- Socket.IO: rooms/fallbacks for 2012 browser problems; proprietary framing
  needs a special C# client; hides the protocol we want to learn.
- Server-Sent Events: server→client only; agents must talk upward.
- Presence in Postgres: every state flip = a write; presence is derived state
  that dies with the connection — persisting it is write amplification for
  data nobody queries historically.
- **Plain `ws` + JSON discriminated messages; presence in a registry class (chosen).**

## Decision
One /ws endpoint. Agents auth via device token in the first message;
dashboards via the session cookie on the HTTP Upgrade. 15s heartbeats,
45s dead-timer reaping (OSPF-style 3×). Canonical types in
server/src/protocol/messages.ts, mirrored in agent/src/Dto.cs.

## Consequences
We own framing/versioning rules (t discriminator, ignore unknown fields) —
and we understand every byte. A server restart wipes presence; agents
re-announce on reconnect by design. Verified by integration test including
silent-death reaping and a live C#-core-to-server session.
