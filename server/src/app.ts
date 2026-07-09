/**
 * App factory: builds and returns the Fastify instance WITHOUT listening.
 * Why a factory instead of listening at import time? Tests can build the app
 * and fire requests at it in-memory (app.inject) — no port, no network, fast.
 * This one decision is most of what makes a server testable.
 */
import Fastify from 'fastify';
import fastifyStatic from '@fastify/static';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import type { Config } from './config.js';
import type { Db } from './db.js';

export function buildApp(config: Config, db: Db | undefined) {
  const app = Fastify({
    // Pino structured logging (SDD §28). In dev, `npm run dev` pipes are readable enough;
    // in production these JSON lines are what Render's log stream shows.
    logger: { level: config.nodeEnv === 'production' ? 'info' : 'debug' },
  });

  // ---- health (SDD §10) ----
  // Liveness: "is the process up?" — UptimeRobot and Render's health check target this.
  app.get('/healthz', async () => ({ status: 'ok', uptime_s: Math.round(process.uptime()) }));

  // Readiness: "can we serve real traffic?" — includes the DB round-trip.
  // Teaching: liveness vs readiness are different questions. A process can be
  // alive but useless (DB down). Conflating them causes restart loops in prod.
  app.get('/healthz/db', async (_req, reply) => {
    if (!db) return { status: 'skipped', reason: 'DATABASE_URL not set (Phase 0 allows this)' };
    try {
      const t0 = performance.now();
      await db.ping();
      return { status: 'ok', rtt_ms: Math.round(performance.now() - t0) };
    } catch (err) {
      app.log.error({ err }, 'db ping failed');
      return reply.code(503).send({ status: 'unavailable' });
    }
  });

  // ---- api v1 (versioned prefix from day one, SDD §10) ----
  app.get('/api/v1/hello', async () => ({
    message: 'Hello Daku! GameNight server is alive',
    phase: 0,
  }));

  // ---- static SPA placeholder (real React app arrives in Phase 1) ----
  const publicDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'public');
  app.register(fastifyStatic, { root: publicDir });

  return app;
}
