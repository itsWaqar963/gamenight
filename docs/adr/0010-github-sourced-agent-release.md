# ADR-0010: Serve agent release info from the GitHub API, not env vars

**Status:** accepted · **Date:** 2026-07-14

## Context
The download button and the agent self-updater both read /api/v1/agent/latest,
which sourced {version, url, sha256} from Render env vars (AGENT_VERSION,
AGENT_DOWNLOAD_URL, AGENT_SHA256). Every release required editing those by hand
— manual, easy to forget, and a drift risk (the same class of failure that broke
sign-in when APP_URL got a bad value).

## Decision
Source release info from the GitHub Releases API
(`/repos/{owner}/{repo}/releases/latest`), the single source of truth the
contributor's CI already publishes to. SHA-256 comes from GitHub's own asset
`digest` field (authoritative), falling back to parsing the `AGENT_SHA256=` line
the CI writes into the release notes. Response shape is unchanged, so the agent
self-updater is unaffected.

**Resilience (three tiers):**
1. Fresh GitHub value — cached 60 min (well under the 60 req/hr unauthenticated
   limit; concurrent refreshes de-duped into one in-flight fetch).
2. Stale cache — if a refresh fails but a prior value exists, serve it regardless
   of age. A working link an hour old beats an error.
3. 503 "temporarily unavailable" — only when GitHub is unreachable AND the cache
   is cold (first request after a restart during a GitHub outage). Self-heals on
   the next successful fetch.

No env-var fallback by design: one source of truth, never stale/wrong data.
Both /agent/latest and /setup share one getLatestRelease() and one cache.

## Consequences
Zero manual env-var edits per release — publish a release, the webapp and the
self-updater pick it up within the cache window. AGENT_* env vars are retired
for this purpose (GITHUB_OWNER/GITHUB_REPO configurable, default to the canonical
repo). Verified: parsing against the live GitHub response (both SHA sources
agree), caching, and the stale/error tiers.
