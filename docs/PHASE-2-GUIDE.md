# Phase 2 Guide — The Agent: Presence Goes Live

**Goal (Milestone M2):** two real PCs on the real Radmin network show correct live states on the dashboard — including `in_game` flipping when FC2 launches — and agents self-heal after a server restart.

Verified in this package: device linking (single-use human-friendly codes), the WebSocket gateway (token auth for agents, cookie auth for dashboards, heartbeat reaping of silent deaths), the live presence strip, and the **C# agent core compiled and live-tested against the real server** (claim → connect → in_game → power-cut → offline). Only the thin WinForms shell (tray icon, link dialog) couldn't run in my Linux environment — you're its first tester.

---

## Step 1 — Bring the code in (10 min)
Extract the zip over your repo folder (as in Phase 1), then:
```powershell
git checkout -b feat/phase2-agent-presence
git add . && git status    # expect: agent/src/*, ws/, devices/, presence/, protocol/, migration 0001
git commit -m "feat: phase 2 - device linking, ws gateway, presence, c# agent"
git push -u origin feat/phase2-agent-presence
```
Open the PR; keep it unmerged until local testing passes. `npm install` (new `ws` dependency).

## Step 2 — Server + dashboard locally (10 min)
Same five env vars as Phase 1, then `npm run build` and `npm run start -w server`. Boot logs show migration `0001` creating `devices` — against Neon. Sign in at localhost:8080: the Home page is now the presence strip (everyone offline, dimmed) plus a **"Link your PC"** card. Click **Generate link code** — note it and its 2-minute clock.

## Step 3 — Build and link the agent (20 min)
1. Install the **.NET 8 SDK** (dotnet.microsoft.com → SDK 8.0.x, Windows x64). Verify: `dotnet --version`.
2. Build the real Windows binary:
   ```powershell
   cd agent
   dotnet publish -c Release
   ```
   Output: `agent\bin\Release\net8.0-windows\win-x64\publish\GameNightAgent.exe` — one self-contained file; copy it anywhere (e.g. `C:\GameNight\`).
3. Run it. First run shows the **link dialog**: server URL (type `http://localhost:8080` for this test) + the code from Step 2 → **Link this PC**. The dialog closes; a tray icon appears (it uses a generic Windows icon for now — cosmetics later).
4. Look at the dashboard: **you just came online.** Blue dot, your Radmin IP beside your name if Radmin is connected. Launch Far Cry 2 → within ~5 s the dot goes green **IN GAME**. Close FC2 → back to online. Quit the agent from the tray → offline within ~45 s (the reaper) or instantly (clean close).
5. Check the token landed safely: `%LOCALAPPDATA%\GameNight\config.json` — `tokenProtected` is DPAPI ciphertext, not your token.

## Step 4 — Self-heal test (5 min)
With the agent connected: Ctrl+C the server, watch the tray tooltip cycle "reconnecting in Ns" with growing delays (backoff + jitter), restart the server — the agent reconnects and **re-announces its current state** unprompted. That's M2's 90-second self-heal requirement; it should take well under 30.

## Step 5 — Deploy + first real friend (20 min)
Merge the PR (CI green) → Render auto-deploys → verify the live URL's Home page. Then recruit one friend (or your second PC):
they sign in (you approve), generate *their* code, run `GameNightAgent.exe` (send them your built exe — same SmartScreen "More info → Run anyway" ritual as the game zip), enter your **Render URL** + their code. Two people on one dashboard, live, across the internet. If you're both on Radmin, both 26.x IPs show — Phase 3 will draw ping lines between them.

## Step 6 — CCNA Lab 02 (30 min, the payoff)
`docs/labs/02-radmin-tunnel-wireshark.md` — capture the same ping inside and outside the Radmin tunnel. This is the single best half hour of networking education in the whole project. Write your findings into the lab file and commit it.

## M2 checklist
- [ ] CI green; merged; deployed
- [ ] Agent builds with `dotnet publish`, links with a code, tray works
- [ ] Dashboard: online → in_game flip within 5 s; Radmin IP shown
- [ ] Server restart → agent self-heals (< 90 s) and state survives
- [ ] Quit/power-cut → offline within 45 s
- [ ] One other human linked and visible from the Render URL
- [ ] Lab 02 written up and committed

Declare **"Phase 2 complete"** and Phase 3 opens: the probe engine, the N×N quality mesh, and the host recommendation — the feature this project was born for.
