# Phase 1 Guide — Identity: Google Sign-In, Approval, Admin

**Goal (Milestone M1):** a friend's Gmail sign-in lands in *pending*; you approve from any browser and they're in; a banned account is locked out mid-session.

Built and tested in this package (integration-tested against a real Postgres):
users + sessions schema with generated SQL migration (runs automatically at boot), full Google OIDC code flow (state/nonce/JWKS verification), hashed session tokens in HttpOnly cookies, `requireApproved`/`requireAdmin` guards, ban revocation cascade, and a React SPA (login → pending → home/roster → admin approvals) built into `server/public` and served by the same server.

---

## Step 1 — Bring the code into your repo (15 min)

1. Extract the zip **over** your existing `gamenight` folder (overwrite when asked). Your `.git` folder is untouched — the history stays.
2. The old placeholder page is now build output, so untrack it:
   ```powershell
   git rm -r --cached server/public
   ```
3. New branch, review, commit:
   ```powershell
   git checkout -b feat/phase1-identity
   git add .
   git status        # READ it: new modules, web/, drizzle/, no node_modules, no server/public
   git commit -m "feat: phase 1 identity - google oidc, sessions, approval workflow, react spa"
   git push -u origin feat/phase1-identity
   ```
4. Open the PR but **don't merge yet** — first make it work locally (Steps 2–4), pushing any fixes to the same branch. CI should be green throughout.
5. Run `npm install` (new dependencies in all workspaces).

## Step 2 — Google Cloud setup (20 min, one-time)

This creates the `client_id`/`client_secret` — Google's half of the SDD §15 sequence diagram.

1. **console.cloud.google.com** → sign in with your Gmail → top bar → **New Project** → name `gamenight` → Create → make sure it's selected.
2. **APIs & Services → OAuth consent screen** (may appear as "Google Auth Platform"):
   - User type: **External** → Create.
   - App name `GameNight`, support email = your Gmail, developer contact = your Gmail. Save through the steps; **add no extra scopes** (the basics — openid/email/profile — are included and are exactly the minimal identity data we designed for).
   - After creating, **Publish app** (move from *Testing* to *In production*). With only basic scopes this needs **no Google review**, and it means friends can sign in without you whitelisting each one as a "test user."
3. **APIs & Services → Credentials → + Create Credentials → OAuth client ID**:
   - Application type: **Web application**, name `gamenight-web`.
   - **Authorized redirect URIs** — add both now (exact strings, no trailing slash):
     - `http://localhost:8080/auth/google/callback`
     - `https://<your-app>.onrender.com/auth/google/callback`
   - Create → copy the **Client ID** and **Client Secret**. The secret is a real secret: env vars only, never git, never chat.

Why redirect URIs are locked down: Google will *only* send the authorization code to URIs on this exact list — it's the reason a phishing site can't impersonate your callback. Any mismatch gives the infamous `redirect_uri_mismatch` error; the fix is always "make the strings identical."

## Step 3 — Local login test (15 min)

In the `gamenight` folder, one PowerShell window:

```powershell
$env:DATABASE_URL   = "<your Neon connection string>"
$env:GOOGLE_CLIENT_ID     = "<client id>"
$env:GOOGLE_CLIENT_SECRET = "<client secret>"
$env:APP_URL        = "http://localhost:8080"
$env:ADMIN_EMAIL    = "<your gmail, lowercase>"
npm run build
npm run start -w server
```

Watch the boot logs: the migrator creates `users` and `sessions` **in your real Neon database** before the server listens (boot order is a contract: never serve traffic against a schema you don't have).

Open **http://localhost:8080** → *Sign in with Google* → pick your Gmail → you should land on Home, already approved, with the Admin link visible. That's the `ADMIN_EMAIL` bootstrap solving "who approves the first approver."

**Now the real test — be your own second user:** open an Incognito window (or another browser), sign in with a *different* Google account. It lands on "Waiting for approval." In your normal window → Admin → the account appears under Pending → **Approve** → refresh incognito → they're in. Then try **Ban** and refresh incognito again: thrown out mid-session. That's the revocation cascade you just verified with your own eyes.

*(Daily dev workflow, once this works: terminal 1 `npm run dev` (server), terminal 2 `npm run dev -w web` (Vite on :5173 with hot reload, proxying api/auth to :8080). For login on :5173, set `APP_URL=http://localhost:5173` and add that third redirect URI in Google. Optional — building once and testing on :8080 is fine for now.)*

## Step 4 — Merge and deploy (15 min)

1. Merge the PR (CI green), `git checkout main`, `git pull`.
2. Render → your service → **Environment** tab → add the four new variables: `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `APP_URL` = `https://<your-app>.onrender.com` (no trailing slash — it must match the redirect URI you registered), `ADMIN_EMAIL`. Save → Render redeploys.
3. When Live: open the public URL → sign in → approve someone → the loop works on the internet. Note the login now happens over three parties and two continents: your browser (Pakistan) ↔ Google ↔ your server (Singapore) — and the session cookie that comes back is `Secure`, so it will never travel over plain HTTP.

## M1 checklist

- [ ] CI green on the PR; merged to main
- [ ] Local: your Gmail boots as approved admin (bootstrap works)
- [ ] Local: second account → pending → approved via Admin panel → in
- [ ] Local: ban kicks the second account mid-session
- [ ] Deployed: same three behaviors on the Render URL
- [ ] `docs/runbook.md`: note added for rotating GOOGLE_CLIENT_SECRET (Credentials → your client → reset secret → update Render env)

Tick everything, declare **"Phase 1 complete"**, and Phase 2 begins: the C# agent, WebSockets, presence — and your first Wireshark session inside the Radmin tunnel.
