/**
 * WebSocket gateway (SDD §11): one endpoint /ws, two kinds of client.
 * Agents authenticate with a device token in their first message;
 * dashboards authenticate with the session cookie that rode the HTTP
 * Upgrade request (a WebSocket begins life as an HTTP request — the
 * handshake is `GET /ws` + `Upgrade: websocket` headers, and cookies come
 * along like on any other request. After the 101 response the same TCP
 * connection stops being HTTP and becomes a message pipe).
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
import type { AgentToServer, DashboardHello } from '../protocol/messages.js';

const HELLO_TIMEOUT_MS = 10_000;

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

  // We own the upgrade: only /ws becomes a WebSocket; everything else 404s.
  opts.httpServer.on('upgrade', (req, socket, head) => {
    if (!req.url?.startsWith('/ws')) {
      socket.destroy();
      return;
    }
    wss.handleUpgrade(req, socket, head, (ws) => wss.emit('connection', ws, req));
  });

  // Fan-out: every presence change goes to every dashboard. At 20 users this
  // is trivially cheap; the abstraction point exists if it ever isn't.
  presence.onChange((user) => {
    const msg = JSON.stringify({ t: 'presence_delta', user });
    for (const d of dashboards) if (d.sock.readyState === WebSocket.OPEN) d.sock.send(msg);
  });

  // The dead-timer sweep (SDD §7.2): 3 missed heartbeats → reap.
  const reaper = setInterval(
    () => {
      for (const userId of presence.reapStale()) {
        log.info({ userId }, 'presence reaped (heartbeat stale)');
        agentByUser.get(userId)?.sock.close(4000, 'heartbeat timeout');
        agentByUser.delete(userId);
        presence.remove(userId);
      }
    },
    Math.min(REAP_AFTER_MS / 3, 15_000),
  );
  reaper.unref(); // never keep the process alive just to reap

  wss.on('connection', (sock: WebSocket, req: IncomingMessage) => {
    let conn: Conn | null = null;

    // A socket that never authenticates is dead weight — cut it loose.
    const helloTimer = setTimeout(() => {
      if (!conn) sock.close(4001, 'hello timeout');
    }, HELLO_TIMEOUT_MS);

    sock.on('message', (raw) => {
      void (async () => {
        let msg: AgentToServer | DashboardHello;
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
            // Cookie auth: same session machinery as REST.
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
            return;
          }

          if ('token' in msg) {
            // Device token auth: hash the presented token, look up the hash.
            const hash = createHash('sha256').update(msg.token).digest('hex');
            const rows = await db.orm
              .select({ device: devices, user: users })
              .from(devices)
              .innerJoin(users, eq(devices.userId, users.id))
              .where(eq(devices.tokenHash, hash))
              .limit(1);
            const row = rows[0];
            // Token → device → user chain is authoritative (SDD §11): a
            // revoked device or banned user fails HERE, whatever the agent claims.
            if (!row || row.device.revoked || row.user.status !== 'approved') {
              sock.close(4401, 'unauthorized');
              return;
            }
            conn = { sock, kind: 'agent', userId: row.user.id };
            // One agent per user: a reconnect supersedes the old socket.
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
            sock.send(JSON.stringify({ t: 'hello_ok', deviceId: row.device.id }));
            return;
          }

          sock.close(4003, 'bad hello');
          return;
        }

        // ---- post-hello messages ----
        if (conn.kind === 'agent') {
          switch (msg.t) {
            case 'hb':
              presence.heartbeat(conn.userId);
              return;
            case 'state':
              presence.setState(conn.userId, msg.state, msg.radmin);
              return;
            default:
              log.warn({ t: (msg as { t?: string }).t }, 'unknown agent message'); // log, never crash
          }
        }
      })();
    });

    sock.on('close', () => {
      clearTimeout(helloTimer);
      if (!conn) return;
      if (conn.kind === 'dashboard') dashboards.delete(conn);
      else if (agentByUser.get(conn.userId) === conn) {
        // Only clear presence if WE are still the registered socket —
        // a superseding reconnect must not be erased by the old socket's close.
        agentByUser.delete(conn.userId);
        presence.remove(conn.userId);
      }
    });
  });

  return {
    close: () => {
      clearInterval(reaper);
      wss.close();
    },
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
