/**
 * In-memory presence (SDD §7.2). Deliberately NOT in the database: presence
 * is derived, ephemeral state that dies with the connection. The registry is
 * the single writer; everyone else reads or subscribes.
 *
 * Heartbeat/reaping teaching note: TCP will not reliably tell you the other
 * side vanished (power cut, sleep, Wi-Fi drop) — connections sit "half-open".
 * Application heartbeats + a dead timer (3 × interval, like OSPF hello 10s /
 * dead 40s) are the standard fix.
 */
import type { AgentState, PresenceUser, RadminInfo } from '../../protocol/messages.js';

export type PresenceEntry = {
  userId: string;
  deviceId: string;
  displayName: string | null;
  avatarUrl: string | null;
  state: AgentState;
  radmin: RadminInfo | null;
  agentVersion: string;
  lastHeartbeat: number; // epoch ms
};

export const HEARTBEAT_INTERVAL_MS = 15_000;
export const REAP_AFTER_MS = Number(process.env.PRESENCE_REAP_MS ?? 45_000); // 3 missed beats

type Listener = (user: PresenceUser) => void;

export class PresenceRegistry {
  private byUser = new Map<string, PresenceEntry>();
  private listeners = new Set<Listener>();

  upsert(entry: PresenceEntry): void {
    this.byUser.set(entry.userId, entry);
    this.emit(this.toPublic(entry));
  }

  heartbeat(userId: string): void {
    const e = this.byUser.get(userId);
    if (e) e.lastHeartbeat = Date.now();
  }

  setState(userId: string, state: AgentState, radmin: RadminInfo): void {
    const e = this.byUser.get(userId);
    if (!e) return;
    e.state = state;
    e.radmin = radmin;
    e.lastHeartbeat = Date.now();
    this.emit(this.toPublic(e));
  }

  remove(userId: string): void {
    const e = this.byUser.get(userId);
    if (!e) return;
    this.byUser.delete(userId);
    this.emit({
      userId: e.userId,
      displayName: e.displayName,
      avatarUrl: e.avatarUrl,
      state: 'offline',
      radmin: null,
      agentVersion: null,
    });
  }

  /** Returns userIds whose heartbeat went stale — caller closes their sockets. */
  reapStale(): string[] {
    const cutoff = Date.now() - REAP_AFTER_MS;
    const stale: string[] = [];
    for (const [userId, e] of this.byUser) if (e.lastHeartbeat < cutoff) stale.push(userId);
    return stale;
  }

  snapshot(): PresenceUser[] {
    return [...this.byUser.values()].map((e) => this.toPublic(e));
  }

  get(userId: string): PresenceEntry | undefined {
    return this.byUser.get(userId);
  }

  onChange(fn: Listener): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  private emit(u: PresenceUser): void {
    for (const fn of this.listeners) fn(u);
  }

  private toPublic(e: PresenceEntry): PresenceUser {
    return {
      userId: e.userId,
      displayName: e.displayName,
      avatarUrl: e.avatarUrl,
      state: e.state,
      radmin: e.radmin,
      agentVersion: e.agentVersion,
    };
  }
}
