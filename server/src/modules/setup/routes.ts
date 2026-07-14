/**
 * Setup/onboarding info (SDD §21, FR-50). Game + Radmin come from env; the agent
 * download now comes from the SAME GitHub-backed source as /agent/latest (shared
 * cache), so the Setup page's step-3 button always shows the real latest release
 * — no env-var drift.
 */
import type { FastifyInstance } from 'fastify';
import type { Config } from '../../config.js';
import { getLatestRelease } from '../releases/routes.js';

export function registerSetupRoutes(app: FastifyInstance, config: Config) {
  app.get('/api/v1/setup', async () => {
    const agent = await getLatestRelease(config, app.log);
    return {
      gameUrl: config.setup.gameUrl,
      radminNetwork: config.setup.radminNetwork,
      agent: agent
        ? { version: agent.version, url: agent.url, sha256: agent.sha256 }
        : { version: null, url: null, sha256: null },
    };
  });
}
