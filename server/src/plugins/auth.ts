/**
 * Resolves the session cookie to request.user on every request, and provides
 * the two guards that ARE the authorization model (SDD §16). All checks live
 * here — never scattered ad-hoc through route handlers.
 */
import type { FastifyInstance, FastifyReply, FastifyRequest } from 'fastify';
import type { Db } from '../db.js';
import type { User } from '../db/schema.js';
import { getUserBySession } from '../modules/auth/sessions.js';

declare module 'fastify' {
  interface FastifyRequest {
    user: User | null;
  }
}

export function registerAuth(app: FastifyInstance, db: Db | undefined) {
  app.decorateRequest('user', null);
  app.addHook('preHandler', async (req) => {
    const token = req.cookies['sid'];
    if (token && db) req.user = await getUserBySession(db, token);
  });
}

export async function requireApproved(req: FastifyRequest, reply: FastifyReply) {
  if (!req.user) return reply.code(401).send({ error: 'not signed in' });
  if (req.user.status !== 'approved')
    return reply.code(403).send({ error: `account ${req.user.status}` });
}

export async function requireAdmin(req: FastifyRequest, reply: FastifyReply) {
  if (!req.user) return reply.code(401).send({ error: 'not signed in' });
  if (req.user.status !== 'approved' || req.user.role !== 'admin')
    return reply.code(403).send({ error: 'admin only' });
}
