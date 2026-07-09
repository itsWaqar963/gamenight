/**
 * Device linking (SDD §15, "device flow" simplified):
 * logged-in user requests a short-lived 6-char code on the website →
 * types it into the agent → agent claims it → receives a long-lived token.
 * The agent never touches Google credentials; the code is the bridge.
 */
import type { FastifyInstance } from 'fastify';
import { createHash, randomBytes, randomInt } from 'node:crypto';
import { and, eq } from 'drizzle-orm';
import type { Db } from '../../db.js';
import { devices } from '../../db/schema.js';
import { requireApproved } from '../../plugins/auth.js';

const CODE_TTL_MS = 2 * 60 * 1000;
// In-memory is correct here: codes are 2-minute ephemera; a restart merely
// voids outstanding codes (user clicks the button again). Not everything
// deserves a table.
const pendingCodes = new Map<string, { userId: string; expiresAt: number }>();

// No 0/O/1/I — these codes get read off a screen and typed by humans.
const ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
function makeCode(): string {
  let s = '';
  for (let i = 0; i < 6; i++) s += ALPHABET[randomInt(ALPHABET.length)];
  return s;
}

export function registerDeviceRoutes(app: FastifyInstance, db: Db | undefined) {
  app.post('/api/v1/devices/link', { preHandler: requireApproved }, async (req) => {
    // Sweep expired codes opportunistically
    const now = Date.now();
    for (const [c, v] of pendingCodes) if (v.expiresAt < now) pendingCodes.delete(c);

    const code = makeCode();
    pendingCodes.set(code, { userId: req.user!.id, expiresAt: now + CODE_TTL_MS });
    return { code, expiresInSec: CODE_TTL_MS / 1000 };
  });

  // Unauthenticated by design: the agent has no session. The code IS the
  // credential — 6 chars from a 32-char alphabet ≈ 1 billion combinations,
  // valid 2 minutes. Rate limiting arrives with real exposure (SDD §24 notes
  // 5/min makes guessing infeasible; at 20 trusted users we start simpler).
  app.post<{ Body: { code?: string; name?: string } }>(
    '/api/v1/devices/claim',
    async (req, reply) => {
      if (!db) return reply.code(503).send({ error: 'no database' });
      const code = (req.body?.code ?? '').toUpperCase().trim();
      const entry = pendingCodes.get(code);
      if (!entry || entry.expiresAt < Date.now())
        return reply.code(400).send({ error: 'invalid or expired code' });
      pendingCodes.delete(code); // single-use, burn immediately

      const token = randomBytes(32).toString('hex');
      const inserted = await db.orm
        .insert(devices)
        .values({
          userId: entry.userId,
          name: (req.body?.name ?? 'unnamed-pc').slice(0, 64),
          tokenHash: createHash('sha256').update(token).digest('hex'),
        })
        .returning();
      // The ONLY moment the raw token exists server-side. Shown once, like a
      // GitHub personal access token.
      return { deviceId: inserted[0]!.id, token };
    },
  );

  app.get('/api/v1/devices', { preHandler: requireApproved }, async (req) => {
    if (!db) return { devices: [] };
    const list = await db.orm
      .select({
        id: devices.id,
        name: devices.name,
        agentVersion: devices.agentVersion,
        lastSeen: devices.lastSeen,
        revoked: devices.revoked,
      })
      .from(devices)
      .where(eq(devices.userId, req.user!.id));
    return { devices: list };
  });

  app.delete<{ Params: { id: string } }>(
    '/api/v1/devices/:id',
    { preHandler: requireApproved },
    async (req, reply) => {
      if (!db) return reply.code(503).send({ error: 'no database' });
      // Owner-only: the WHERE clause carries the authorization (id AND userId).
      await db.orm
        .update(devices)
        .set({ revoked: true })
        .where(and(eq(devices.id, req.params.id), eq(devices.userId, req.user!.id)));
      return { ok: true };
    },
  );
}
