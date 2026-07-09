# Runbook

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
