/**
 * The WebSocket wire protocol (SDD §11) — the single source of truth for
 * every message that crosses the socket. The C# agent mirrors these shapes
 * in Dto.cs; if you change something here, change it there.
 * Rules: every message has a `t` discriminator; unknown fields are ignored
 * (forward compat); unknown types are logged, never crash.
 */

export type AgentState = 'online' | 'idle' | 'in_game';

export type RadminInfo = { connected: boolean; ip?: string };

// ---- agent → server ----
export type AgentHello = { t: 'hello'; token: string; agentVersion: string };
export type AgentStateMsg = { t: 'state'; state: AgentState; radmin: RadminInfo };
export type AgentHeartbeat = { t: 'hb' };
export type AgentToServer = AgentHello | AgentStateMsg | AgentHeartbeat | MetricsMsg;

// ---- dashboard → server ----
export type DashboardHello = { t: 'hello'; role: 'dashboard' };

// ---- server → agent ----
export type HelloOk = { t: 'hello_ok'; deviceId: string };
export type ServerError = { t: 'error'; message: string };

// ---- server → dashboard ----
export type PresenceUser = {
  userId: string;
  displayName: string | null;
  avatarUrl: string | null;
  state: AgentState | 'offline';
  radmin: RadminInfo | null;
  agentVersion: string | null;
};
export type PresenceFull = { t: 'presence'; users: PresenceUser[] };
export type PresenceDelta = { t: 'presence_delta'; user: PresenceUser };

// ===== Phase 3: the mesh (SDD §20, §22) =====

// ---- server → agent: who to probe ----
export type Peer = { userId: string; radminIp: string };
export type PeersMsg = { t: 'peers'; list: Peer[] };

// ---- agent → server: probe results ----
// One entry per peer the agent measured over the Radmin tunnel.
export type MetricSample = {
  peerUserId: string;
  avgRtt: number; // ms, average round-trip over the window
  jitter: number; // ms, mean absolute deviation of consecutive RTTs
  lossPct: number; // 0..100
  samples: number; // how many probes this summary is based on
};
export type MetricsMsg = { t: 'metrics'; peers: MetricSample[] };

// ---- server → dashboard: the live matrix + recommendation ----
export type MatrixCell = {
  fromUserId: string;
  toUserId: string;
  avgRtt: number;
  jitter: number;
  lossPct: number;
  samples: number;
  ageMs: number; // how stale this measurement is
};
export type HostRecommendation = {
  hostUserId: string | null;
  hostName: string | null;
  worstCaseRtt: number | null; // the worst ping any player would have to this host
  reasons: string[]; // human-readable "why", shown on the dashboard
} | null;
export type MatrixMsg = {
  t: 'matrix';
  cells: MatrixCell[];
  recommendation: HostRecommendation;
};
