/**
 * GameNight wire protocol (SDD §11). This package is the single source of
 * truth for every WebSocket message. The C# agent mirrors these shapes in
 * Protocol.cs — if you change anything here, change it there.
 *
 * Rules: every message has a `t` discriminator; unknown `t` is ignored
 * (never crash); unknown FIELDS are ignored (old agents survive new servers).
 */

export type AgentState = 'online' | 'idle' | 'in_game';
export type RadminInfo = { connected: boolean; ip?: string };

// ---- agent → server ----
export type AgentHello = { t: 'hello'; token: string; agentVersion: string };
export type AgentStateMsg = { t: 'state'; state: AgentState; radmin: RadminInfo };
export type AgentHeartbeat = { t: 'hb' };
export type AgentToServer = AgentHello | AgentStateMsg | AgentHeartbeat;

// ---- server → agent ----
export type ServerHelloOk = { t: 'hello_ok'; deviceId: string; userId: string };
export type ServerError = { t: 'error'; code: 'bad_token' | 'revoked' | 'not_approved' | 'bad_hello'; message: string };
export type ServerToAgent = ServerHelloOk | ServerError;

// ---- server → dashboard ----
export type PresenceUser = {
  userId: string;
  displayName: string | null;
  avatarUrl: string | null;
  state: AgentState | 'offline';
  radmin: RadminInfo;
  agentVersion?: string;
};
export type PresenceFull = { t: 'presence_full'; users: PresenceUser[] };
export type PresenceDelta = { t: 'presence'; user: PresenceUser };
export type ServerToDashboard = PresenceFull | PresenceDelta;
