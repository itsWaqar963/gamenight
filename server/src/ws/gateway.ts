/**
 * WebSocket gateway (SDD §11, §20): one endpoint /ws, two kinds of client.
 * Agents authenticate with a device token in their first message; dashboards
 * with the session cookie on the HTTP Upgrade.
 *
 * Phase 3 additions: the server tells each agent WHO to probe (peer list of
 * online agents with Radmin IPs), ingests their metric summaries into the
 * matrix, and broadcasts the matrix + host recommendation to dashboards.
 */
import { WebSocketServer, WebSocket } from 'ws';
import type { Server, IncomingMessage } from 'node:http';
import type { FastifyBaseLogger } from 'fastify';
import { createHash } from 'node:crypto';
import { eq } from 'drizzle-orm';
import type { Db } from '../db.js';
import { devices, users } from '../db/schema.js';
import { getUserBySession } from '../modules/auth/sessions.js';
import { PresenceRegistry, REAP_AFTER_MS } from '../modules/presence/registry.js';
import { MetricStore } from '../modules/metrics/store.js';
import { recommendHost, type Candidate } from '../modules/recommend/engine.js';
import type { AgentToServer, DashboardToServer, Peer } from '../protocol/messages.js';

const HELLO_TIMEOUT_MS = 10_000;
const MATRIX_BROADCAST_MS = 5_000; // recompute + push matrix at most this often

type Conn = {
  sock: WebSocket;
  kind: 'agent' | 'dashboard';
  userId: string;
};

export function attachGateway(opts: {
  httpServer: Server;
  db: Db;
  log: FastifyBaseLogger;
  presence: PresenceRegistry;
}) {
  const { db, log, presence } = opts;
  const wss = new WebSocketServer({ noServer: true });
  const agentByUser = new Map<string, Conn>();
  const dashboards = new Set<Conn>();
  const metrics = new MetricStore();

  opts.httpServer.on('upgrade', (req, socket, head) => {
    if (!req.url?.startsWith('/ws')) {
      socket.destroy();
      return;
    }
    wss.handleUpgrade(req, socket, head, (ws) => wss.emit('connection', ws, req));
  });

  // ---- peer list: who each agent should probe ----
  // The list is "all OTHER online agents that have a Radmin IP." The server is
  // the source of truth (SDD §7.4) — agents never discover peers themselves,
  // so paused/banned/offline users are simply excluded here.
  function currentPeers(): Peer[] {
    const peers: Peer[] = [];
    for (const u of presence.snapshot()) {
      if (u.radmin?.connected && u.radmin.ip)
        peers.push({ userId: u.userId, radminIp: u.radmin.ip });
    }
    return peers;
  }

  function sendPeersToAgents(): void {
    const all = currentPeers();
    for (const [userId, conn] of agentByUser) {
      if (conn.sock.readyState !== WebSocket.OPEN) continue;
      // Each agent gets everyone EXCEPT itself.
      const list = all.filter((p) => p.userId !== userId);
      conn.sock.send(JSON.stringify({ t: 'peers', list }));
    }
  }

  // ---- matrix + recommendation broadcast to dashboards ----
  function broadcastMatrix(): void {
    if (dashboards.size === 0) return;
    const cells = metrics.cells();
    const candidates: Candidate[] = currentPeers().map((p) => ({
      userId: p.userId,
      name: presence.get(p.userId)?.displayName ?? null,
    }));
    const recommendation = recommendHost(candidates, cells);
    const msg = JSON.stringify({ t: 'matrix', cells, recommendation });
    for (const d of dashboards) if (d.sock.readyState === WebSocket.OPEN) d.sock.send(msg);
  }

  const matrixTimer = setInterval(broadcastMatrix, MATRIX_BROADCAST_MS);
  matrixTimer.unref();

  // When presence changes (someone connects, gets a Radmin IP, goes offline),
  // the peer topology changed — re-distribute probe targets.
  presence.onChange((user) => {
    const msg = JSON.stringify({ t: 'presence_delta', user });
    for (const d of dashboards) if (d.sock.readyState === WebSocket.OPEN) d.sock.send(msg);
    sendPeersToAgents();
  });

  const reaper = setInterval(
    () => {
      for (const userId of presence.reapStale()) {
        log.info({ userId }, 'presence reaped (heartbeat stale)');
        agentByUser.get(userId)?.sock.close(4000, 'heartbeat timeout');
        agentByUser.delete(userId);
        presence.remove(userId);
        metrics.dropUser(userId);
      }
    },
    Math.min(REAP_AFTER_MS / 3, 15_000),
  );
  reaper.unref();

  wss.on('connection', (sock: WebSocket, req: IncomingMessage) => {
    let conn: Conn | null = null;

    const helloTimer = setTimeout(() => {
      if (!conn) sock.close(4001, 'hello timeout');
    }, HELLO_TIMEOUT_MS);

    sock.on('message', (raw) => {
      void (async () => {
        let msg: AgentToServer | DashboardToServer;
        try {
          msg = JSON.parse(String(raw));
        } catch {
          sock.close(4002, 'not json');
          return;
        }

        // ---- first message must be hello ----
        if (!conn) {
          if (msg.t !== 'hello') {
            sock.close(4003, 'hello first');
            return;
          }
          clearTimeout(helloTimer);

          if ('role' in msg && msg.role === 'dashboard') {
            const cookies = parseCookies(req.headers.cookie ?? '');
            const sid = cookies['sid'];
            const user = sid ? await getUserBySession(db, sid) : null;
            if (!user || user.status !== 'approved') {
              sock.close(4401, 'unauthorized');
              return;
            }
            conn = { sock, kind: 'dashboard', userId: user.id };
            dashboards.add(conn);
            sock.send(JSON.stringify({ t: 'presence', users: presence.snapshot() }));
            broadcastMatrix(); // give the new dashboard the current matrix immediately
            return;
          }

          if ('token' in msg) {
            const hash = createHash('sha256').update(msg.token).digest('hex');
            const rows = await db.orm
              .select({ device: devices, user: users })
              .from(devices)
              .innerJoin(users, eq(devices.userId, users.id))
              .where(eq(devices.tokenHash, hash))
              .limit(1);
            const row = rows[0];
            if (!row || row.device.revoked || row.user.status !== 'approved') {
              sock.close(4401, 'unauthorized');
              return;
            }
            conn = { sock, kind: 'agent', userId: row.user.id };
            agentByUser.get(row.user.id)?.sock.close(4004, 'superseded');
            agentByUser.set(row.user.id, conn);

            presence.upsert({
              userId: row.user.id,
              deviceId: row.device.id,
              displayName: row.user.displayName,
              avatarUrl: row.user.avatarUrl,
              state: 'online',
              radmin: null,
              agentVersion: msg.agentVersion ?? 'unknown',
              lastHeartbeat: Date.now(),
            });
            await db.orm
              .update(devices)
              .set({ lastSeen: new Date(), agentVersion: msg.agentVersion ?? null })
              .where(eq(devices.id, row.device.id));
            sock.send(
              JSON.stringify({
                t: 'hello_ok',
                deviceId: row.device.id,
                userId: row.user.id,
                role: row.user.role === 'admin' ? 'admin' : 'member',
              }),
            );
            sendPeersToAgents(); // tell this agent (and refresh others) who to probe
            return;
          }

          sock.close(4003, 'bad hello');
          return;
        }

        // ---- post-hello messages ----
        if (conn.kind === 'dashboard') {
          if (msg.t === 'run_diagnostics') {
            const agent = agentByUser.get(conn.userId);
            if (agent && agent.sock.readyState === WebSocket.OPEN) {
              agent.sock.send(JSON.stringify({ t: 'diagnose' }));
            } else {
              conn.sock.send(
                JSON.stringify({
                  t: 'diagnostics_result',
                  userId: conn.userId,
                  checks: [
                    {
                      id: 'agent',
                      label: 'Agent connected',
                      status: 'fail',
                      detail: 'Your agent is not connected.',
                      fix: 'Start the GameNight agent on your gaming PC, then run diagnostics again.',
                    },
                  ],
                }),
              );
            }
          }
          return;
        }

        if (conn.kind === 'agent') {
          switch (msg.t) {
            case 'hb':
              presence.heartbeat(conn.userId);
              return;
            case 'state': {
              const prev = presence.get(conn.userId)?.state;
              presence.setState(conn.userId, msg.state, msg.radmin);
              // Transition into the game → nudge everyone else to jump in.
              if (msg.state === 'in_game' && prev !== 'in_game') {
                const who = presence.get(conn.userId)?.displayName ?? 'Someone';
                for (const [uid, c] of agentByUser) {
                  if (uid === conn.userId) continue;
                  if (c.sock.readyState === WebSocket.OPEN)
                    c.sock.send(
                      JSON.stringify({
                        t: 'toast',
                        title: `${who} started Far Cry 2`,
                        body: 'Jump in!',
                      }),
                    );
                }
              }
              return;
            }
            case 'metrics':
              metrics.ingest(conn.userId, msg.peers);
              return;
            case 'diagnostics_result': {
              const out = JSON.stringify(msg);
              for (const d of dashboards)
                if (d.sock.readyState === WebSocket.OPEN) d.sock.send(out);
              return;
            }
            default:
              log.warn({ t: (msg as { t?: string }).t }, 'unknown agent message');
          }
        }
      })();
    });

    sock.on('close', () => {
      clearTimeout(helloTimer);
      if (!conn) return;
      if (conn.kind === 'dashboard') dashboards.delete(conn);
      else if (agentByUser.get(conn.userId) === conn) {
        agentByUser.delete(conn.userId);
        presence.remove(conn.userId);
        metrics.dropUser(conn.userId);
        sendPeersToAgents();
      }
    });
  });

  // Push a toast to a specific user's agent (if connected). Used by the
  // reminder scheduler and by "someone started hosting" events.
  function sendToast(userId: string, title: string, body: string): boolean {
    const conn = agentByUser.get(userId);
    if (!conn || conn.sock.readyState !== WebSocket.OPEN) return false;
    conn.sock.send(JSON.stringify({ t: 'toast', title, body }));
    return true;
  }

  // Broadcast a toast to ALL connected agents (e.g. "match starting").
  function broadcastToast(title: string, body: string): void {
    for (const [, conn] of agentByUser) {
      if (conn.sock.readyState === WebSocket.OPEN)
        conn.sock.send(JSON.stringify({ t: 'toast', title, body }));
    }
  }

  return {
    close: () => {
      clearInterval(reaper);
      clearInterval(matrixTimer);
      wss.close();
    },
    sendToast,
    broadcastToast,
  };
}

function parseCookies(header: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const part of header.split(';')) {
    const i = part.indexOf('=');
    if (i > 0) out[part.slice(0, i).trim()] = decodeURIComponent(part.slice(i + 1).trim());
  }
  return out;
}
