/**
 * Agent release metadata — served to the download button AND the agent
 * self-updater (both read GET /api/v1/agent/latest, response shape unchanged).
 *
 * Source of truth is the GitHub Releases API, not env vars: the contributor's
 * CI publishes a release (with the exe + a GitHub-computed SHA-256 digest) on
 * every version, so fetching from GitHub means zero manual env-var edits and
 * one source of truth — no drift (the failure mode that bit APP_URL).
 *
 * Resilience (three tiers, best to worst):
 *   1. Fresh GitHub value  — cached up to CACHE_TTL_MS (60 min).
 *   2. Stale cached value  — if a refresh fails but we have a prior good value,
 *      serve it regardless of age. A working link an hour old beats an error.
 *   3. "unavailable"        — only if GitHub is unreachable AND cache is cold
 *      (e.g. first request right after a restart during a GitHub outage).
 * No env-var fallback by design: one source of truth, never stale wrong data.
 */
import type { FastifyInstance } from 'fastify';
import type { Config } from '../../config.js';

export type ReleaseInfo = { version: string; url: string; sha256: string | null };

const CACHE_TTL_MS = 60 * 60 * 1000; // 60 minutes

// Module-level cache (single server instance; fine for our scale).
let cached: ReleaseInfo | null = null;
let cachedAt = 0;
let inFlight: Promise<ReleaseInfo | null> | null = null;

type GhAsset = { name: string; browser_download_url: string; digest?: string | null };
type GhRelease = { tag_name?: string; body?: string; assets?: GhAsset[] };

// tag "agent-v0.7.6" → "0.7.6"; also strips a bare leading v.
function versionFromTag(tag: string): string {
  let v = tag.trim();
  if (v.toLowerCase().startsWith('agent-')) v = v.slice(6);
  return v.replace(/^v/i, '');
}

// Prefer GitHub's own asset digest ("sha256:abc…"); fall back to parsing the
// AGENT_SHA256=… line the CI writes into the release notes.
function extractSha(asset: GhAsset | undefined, body: string | undefined): string | null {
  if (asset?.digest) {
    const d = asset.digest.trim();
    return d.toLowerCase().startsWith('sha256:') ? d.slice(7).toLowerCase() : d.toLowerCase();
  }
  const m = body?.match(/AGENT_SHA256=([a-fA-F0-9]{64})/);
  return m ? m[1]!.toLowerCase() : null;
}

async function fetchLatestFromGitHub(config: Config): Promise<ReleaseInfo | null> {
  const { owner, repo } = config.github;
  const res = await fetch(`https://api.github.com/repos/${owner}/${repo}/releases/latest`, {
    headers: {
      Accept: 'application/vnd.github+json',
      'User-Agent': 'gamenight-server',
    },
    signal: AbortSignal.timeout(8000),
  });
  if (!res.ok) throw new Error(`GitHub API ${res.status}`);
  const rel = (await res.json()) as GhRelease;
  if (!rel.tag_name || !rel.assets?.length) throw new Error('release missing tag or assets');

  // Find the .exe asset (the agent binary).
  const exe = rel.assets.find((a) => a.name.toLowerCase().endsWith('.exe'));
  if (!exe) throw new Error('no .exe asset in latest release');

  return {
    version: versionFromTag(rel.tag_name),
    url: exe.browser_download_url,
    sha256: extractSha(exe, rel.body),
  };
}

/**
 * Returns the latest release, honoring the cache and the stale-on-failure
 * policy. Never throws — returns null only when cache is cold AND GitHub fails.
 */
export async function getLatestRelease(
  config: Config,
  log: FastifyInstance['log'],
): Promise<ReleaseInfo | null> {
  const fresh = cached && Date.now() - cachedAt < CACHE_TTL_MS;
  if (fresh) return cached;

  // De-dupe concurrent refreshes: many visitors shouldn't each hit GitHub.
  if (!inFlight) {
    inFlight = (async () => {
      try {
        const info = await fetchLatestFromGitHub(config);
        cached = info;
        cachedAt = Date.now();
        return info;
      } catch (err) {
        // Refresh failed. Tier 2: serve stale cache if we have one.
        log.warn({ err: String(err) }, 'agent/latest: GitHub refresh failed');
        return cached; // may be a stale value, or null if cache is cold
      } finally {
        inFlight = null;
      }
    })();
  }
  return inFlight;
}

export function registerReleaseRoutes(app: FastifyInstance, config: Config) {
  app.get('/api/v1/agent/latest', async (_req, reply) => {
    const info = await getLatestRelease(config, app.log);
    if (!info) {
      // Tier 3: GitHub unreachable and cache cold. Honest, self-healing error.
      return reply.code(503).send({
        version: null,
        url: null,
        sha256: null,
        error: 'Release info temporarily unavailable — please try again shortly.',
      });
    }
    return { version: info.version, url: info.url, sha256: info.sha256 };
  });
}
