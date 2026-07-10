/**
 * Agent release metadata (SDD §17, §22). The platform stores the LINK, not the
 * binary — the exe lives on GitHub Releases (free, unlimited bandwidth, and the
 * same place the Phase 5 auto-updater will look). Free-tier disk is ephemeral,
 * so hosting a 60MB exe on the server was never the plan.
 *
 * The values come from env so publishing a new agent version is a one-line
 * config change on Render, not a redeploy — and every download button + future
 * self-update follows automatically.
 */
import type { FastifyInstance } from 'fastify';
import type { Config } from '../../config.js';

export function registerReleaseRoutes(app: FastifyInstance, config: Config) {
  // Public endpoint (no auth): the download button and, later, the agent's
  // self-updater both read this. Returns null fields if not yet configured.
  app.get('/api/v1/agent/latest', async () => ({
    version: config.agent.version,
    url: config.agent.url,
    sha256: config.agent.sha256,
  }));
}
