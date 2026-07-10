# ADR-0006: Full-mesh agent-measured metrics; pure recommendation engine

**Status:** accepted · **Date:** 2026-07-10

## Context
The core feature (SDD §20–22): measure network quality between all players over
Radmin and recommend the best host. Needs to work at 20 users on a free tier.

## Decisions
- **Full mesh, agent-measured, server-aggregated.** Each agent pings every peer
  (ICMP, no admin rights) every 10s, keeps a 30-sample window, sends a 30s
  summary {avgRtt, jitter, lossPct}. Server holds the live N×N matrix in memory;
  raw probes are NEVER stored or sent (only summaries) — the difference between a
  healthy free-tier DB and a dead one.
- **Server is the source of truth for peer lists.** Agents never discover peers;
  the server sends "who to probe" (online agents with Radmin IPs), so paused/
  banned/offline users are excluded centrally.
- **Recommendation is a PURE function** (no I/O/clock/random) → exhaustively
  unit-tested (7 tests). Min-max: best host minimizes the WORST peer's effective
  ping. Effective ping penalizes jitter (×2) and loss (×30) because a stable
  50ms beats a jittery 40ms, and UDP games have no retransmit for lost packets.
- **Fully-measured hosts preferred** over hosts with unmeasured links (can't
  recommend what we can't verify).

## Consequences
O(N²) probe traffic — trivial at 20 (~38 pkt/s), documented ceiling at ~100
(sample a subset). Matrix + recommendation broadcast to dashboards every 5s.
Verified by a 3-agent integration test: correct peer distribution, matrix
aggregation, and recommendation with reasoning.
