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
export type AgentToServer = AgentHello | AgentStateMsg | AgentHeartbeat;

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
