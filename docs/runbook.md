# Runbook

## Publish a new agent build

Preferred path (CI):

1. Bump `<Version>` in `agent/GameNight.Agent.csproj` **and** `AgentInfo.Version` in `agent/src/Dto.cs` (keep them identical).
2. Merge to `main`. The **release-agent** workflow runs on that push and creates GitHub Release `agent-vX.Y.Z` with `GameNightAgent.exe` **if that tag does not already exist**.
3. You can also force a release without waiting for a merge:
   - push tag `agent-vX.Y.Z`, or
   - Actions → **release-agent** → Run workflow → enter `X.Y.Z` (repo admins)
4. Open the release notes and copy the three `AGENT_*` lines into Render → Environment → Save (redeploy).
5. Existing agents poll `/api/v1/agent/latest` within ~6 hours (or tray → **Check for updates**) and self-swap after verifying the hash. See ADR-0009.

Notes:
- Merging the workflow alone does **not** create a release unless `main` still has an unpublished agent version (then a later `main` push / this auto path will publish it).
- Tag / manual dispatch **refuse** to overwrite an existing release. A plain `main` push **skips** quietly if the release already exists.

### Local / offline fallback

1. Build:
   ```powershell
   cd agent
   dotnet publish -c Release
   ```
   Output: `agent\bin\Release\net8.0-windows\win-x64\publish\GameNightAgent.exe`
2. SHA-256:
   ```powershell
   (Get-FileHash .\bin\Release\net8.0-windows\win-x64\publish\GameNightAgent.exe -Algorithm SHA256).Hash.ToLower()
   ```
3. Create a GitHub Release tagged `agent-vX.Y.Z`, upload the exe, then set Render:
   - `AGENT_VERSION` = `X.Y.Z`
   - `AGENT_DOWNLOAD_URL` = the release asset URL
   - `AGENT_SHA256` = the lowercase hex from step 2


## Deploy
Push to `main` → CI green → Render auto-deploys from the Blueprint. Rollback: Render dashboard → previous deploy → "Rollback".

## Secrets
### Rotating GOOGLE_CLIENT_SECRET
Do this if the secret ever leaks (pasted somewhere public, committed, laptop stolen) or annually as hygiene.
1. console.cloud.google.com → APIs & Services → Credentials → OAuth client `gamenight-web`.
2. Under "Client secrets": **Add secret** → copy the new value. (Old one keeps working — no downtime yet.)
3. Render → gamenight service → Environment → update `GOOGLE_CLIENT_SECRET` → Save (auto-redeploys).
4. Verify: sign out and sign in on the live URL.
5. Back in Google Console: **delete the old secret**. Now the leaked one is dead.
Note: client_id is NOT secret (it's visible in the login redirect URL) — only the secret rotates.
Existing user sessions survive rotation — the secret is only used during the login handshake.

## Restore drill (quarterly, SDD §29)
TBD Phase 1 when the first real table exists: restore latest `pg_dump` artifact into a scratch Neon branch, run smoke queries, record time taken.

## Known operational facts
- Free instance sleeps after ~15 min idle; first request pays 30–60 s.
- `/healthz` = liveness (UptimeRobot target). `/healthz/db` = readiness.
