/**
 * /api/v1/me + roster + the approval workflow (FR-02..04).
 */
import type { FastifyInstance } from 'fastify';
import { eq } from 'drizzle-orm';
import type { Db } from '../../db.js';
import { users, type User } from '../../db/schema.js';
import { requireAdmin, requireApproved } from '../../plugins/auth.js';
import { deleteAllUserSessions } from '../auth/sessions.js';

/** Shape sent to clients — NEVER the raw row (googleSub is internal). */
function publicUser(u: User) {
  return {
    id: u.id,
    displayName: u.displayName,
    avatarUrl: u.avatarUrl,
    email: u.email,
    status: u.status,
    role: u.role,
    createdAt: u.createdAt,
  };
}

export function registerUserRoutes(app: FastifyInstance, db: Db | undefined) {
  // /me deliberately allows pending/banned callers: the SPA needs to know
  // WHO you are to show the right page, even when you're not approved yet.
  app.get('/api/v1/me', async (req) => ({ user: req.user ? publicUser(req.user) : null }));

  app.get('/api/v1/users', { preHandler: requireApproved }, async () => {
    if (!db) return { users: [] };
    const all = await db.orm.select().from(users).orderBy(users.createdAt);
    return { users: all.map(publicUser) };
  });

  const setStatus = (status: 'approved' | 'rejected' | 'banned') =>
    async function handler(req: { params: { id: string }; user: User | null }) {
      if (!db) throw new Error('no db');
      const patch: Partial<typeof users.$inferInsert> = { status };
      if (status === 'approved') {
        patch.approvedAt = new Date();
        patch.approvedBy = req.user?.id ?? null;
      }
      await db.orm.update(users).set(patch).where(eq(users.id, req.params.id));
      // Revocation cascade: a ban must bite immediately, not at next login.
      if (status === 'banned' || status === 'rejected')
        await deleteAllUserSessions(db, req.params.id);
      return { ok: true };
    };

  app.post<{ Params: { id: string } }>(
    '/api/v1/users/:id/approve',
    { preHandler: requireAdmin },
    setStatus('approved'),
  );
  app.post<{ Params: { id: string } }>(
    '/api/v1/users/:id/reject',
    { preHandler: requireAdmin },
    setStatus('rejected'),
  );
  app.post<{ Params: { id: string } }>(
    '/api/v1/users/:id/ban',
    { preHandler: requireAdmin },
    setStatus('banned'),
  );
}
