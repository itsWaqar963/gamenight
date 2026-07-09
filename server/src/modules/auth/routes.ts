/**
 * /auth/* — the OIDC dance, SDD §15. Read alongside the sequence diagram.
 */
import type { FastifyInstance } from 'fastify';
import { randomBytes } from 'node:crypto';
import { eq } from 'drizzle-orm';
import type { Config } from '../../config.js';
import type { Db } from '../../db.js';
import { users } from '../../db/schema.js';
import { buildAuthUrl, exchangeAndVerify } from './google.js';
import { createSession, deleteSession } from './sessions.js';

const OAUTH_COOKIE = 'oauth_flow'; // short-lived: carries state+nonce across the redirect
const SESSION_COOKIE = 'sid';

export function registerAuthRoutes(app: FastifyInstance, config: Config, db: Db | undefined) {
  const redirectUri = `${config.appUrl}/auth/google/callback`;

  app.get('/auth/google', async (_req, reply) => {
    if (!config.google || !db)
      return reply
        .code(503)
        .send({ error: 'auth not configured (GOOGLE_CLIENT_ID/SECRET, DATABASE_URL)' });

    const state = randomBytes(16).toString('hex');
    const nonce = randomBytes(16).toString('hex');
    // The browser carries state+nonce in a cookie only WE set; the callback
    // must present matching values. An attacker can forge a callback URL,
    // but not this cookie — that asymmetry is the CSRF defense.
    reply.setCookie(OAUTH_COOKIE, JSON.stringify({ state, nonce }), {
      httpOnly: true,
      secure: config.nodeEnv === 'production',
      sameSite: 'lax',
      path: '/auth',
      maxAge: 600,
    });
    return reply.redirect(
      buildAuthUrl({ clientId: config.google.clientId, redirectUri, state, nonce }),
    );
  });

  app.get<{ Querystring: { code?: string; state?: string; error?: string } }>(
    '/auth/google/callback',
    async (req, reply) => {
      if (!config.google || !db) return reply.code(503).send({ error: 'auth not configured' });
      if (req.query.error) return reply.redirect('/?login=denied'); // user clicked Cancel at Google

      const raw = req.cookies[OAUTH_COOKIE];
      reply.clearCookie(OAUTH_COOKIE, { path: '/auth' });
      if (!raw || !req.query.code || !req.query.state)
        return reply.code(400).send({ error: 'missing oauth flow state' });
      const flow = JSON.parse(raw) as { state: string; nonce: string };
      if (flow.state !== req.query.state) return reply.code(400).send({ error: 'state mismatch' });

      const who = await exchangeAndVerify({
        code: req.query.code,
        clientId: config.google.clientId,
        clientSecret: config.google.clientSecret,
        redirectUri,
        expectedNonce: flow.nonce,
      });
      if (!who.emailVerified) return reply.code(403).send({ error: 'google email not verified' });

      // Upsert by google_sub (SDD §9: sub is the identity key, email is display data).
      const existing = await db.orm
        .select()
        .from(users)
        .where(eq(users.googleSub, who.sub))
        .limit(1);
      let user = existing[0];
      if (!user) {
        // Bootstrap: the ADMIN_EMAIL's first login is born approved+admin —
        // solves "who approves the first approver" without a magic script.
        const isBootstrapAdmin =
          config.adminEmail !== undefined && who.email.toLowerCase() === config.adminEmail;
        const inserted = await db.orm
          .insert(users)
          .values({
            googleSub: who.sub,
            email: who.email,
            displayName: who.name ?? who.email.split('@')[0] ?? 'player',
            avatarUrl: who.picture ?? null,
            status: isBootstrapAdmin ? 'approved' : 'pending',
            role: isBootstrapAdmin ? 'admin' : 'member',
            approvedAt: isBootstrapAdmin ? new Date() : null,
          })
          .returning();
        user = inserted[0];
        req.log.info({ email: who.email, status: user?.status }, 'new user signed up');
      }
      if (!user) return reply.code(500).send({ error: 'user upsert failed' });
      if (user.status === 'banned') return reply.code(403).send({ error: 'account banned' });

      const token = await createSession(db, user.id);
      reply.setCookie(SESSION_COOKIE, token, {
        httpOnly: true, // JS cannot read it → XSS cannot steal it
        secure: config.nodeEnv === 'production', // HTTPS-only in prod
        sameSite: 'lax', // cross-site POSTs won't carry it → CSRF baseline
        path: '/',
        maxAge: 30 * 24 * 60 * 60,
      });
      return reply.redirect('/');
    },
  );

  app.post('/auth/logout', async (req, reply) => {
    const token = req.cookies[SESSION_COOKIE];
    if (token && db) await deleteSession(db, token);
    reply.clearCookie(SESSION_COOKIE, { path: '/' });
    return { ok: true };
  });
}
