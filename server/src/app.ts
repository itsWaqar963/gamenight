/**
 * App factory (unchanged philosophy from Phase 0: build, don't listen).
 * Phase 1 additions: cookie support, auth resolution, module routes,
 * and an SPA fallback so React Router owns unknown paths.
 */
import Fastify from 'fastify';
import fastifyStatic from '@fastify/static';
import fastifyCookie from '@fastify/cookie';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import type { Config } from './config.js';
import type { Db } from './db.js';
import { registerAuth } from './plugins/auth.js';
import { registerAuthRoutes } from './modules/auth/routes.js';
import { registerUserRoutes } from './modules/users/routes.js';

export function buildApp(config: Config, db: Db | undefined) {
  const app = Fastify({
    logger: { level: config.nodeEnv === 'production' ? 'info' : 'debug' },
  });

  app.register(fastifyCookie);
  registerAuth(app, db);

  app.get('/healthz', async () => ({ status: 'ok', uptime_s: Math.round(process.uptime()) }));
  app.get('/healthz/db', async (_req, reply) => {
    if (!db) return { status: 'skipped', reason: 'DATABASE_URL not set' };
    try {
      const t0 = performance.now();
      await db.ping();
      return { status: 'ok', rtt_ms: Math.round(performance.now() - t0) };
    } catch (err) {
      app.log.error({ err }, 'db ping failed');
      return reply.code(503).send({ status: 'unavailable' });
    }
  });

  registerAuthRoutes(app, config, db);
  registerUserRoutes(app, db);

  const publicDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'public');
  app.register(fastifyStatic, { root: publicDir });

  // SPA fallback: any path that isn't API/auth/health serves index.html and
  // lets React Router render it. Without this, refreshing /admin would 404 —
  // the classic SPA hosting gotcha.
  app.setNotFoundHandler((req, reply) => {
    if (
      req.raw.url?.startsWith('/api/') ||
      req.raw.url?.startsWith('/auth/') ||
      req.raw.url?.startsWith('/healthz')
    ) {
      return reply.code(404).send({ error: 'not found' });
    }
    return reply.sendFile('index.html');
  });

  return app;
}
