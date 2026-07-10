# Phase 3 Guide — The Mesh: Ping Matrix & Host Recommendation

**Goal (Milestone M3):** with 2+ agents online on Radmin, the dashboard shows a
live ping matrix between all players and recommends the best host with visible
reasoning — the feature this whole project exists for.

Built and tested in this package: the ICMP probe engine (agent pings every peer
over Radmin, 10s interval, rolling stats), server-side peer distribution, the
in-memory metric matrix, a pure min-max recommendation engine (7 unit tests),
matrix + recommendation broadcast to dashboards, and the matrix UI. Verified end
to end with a 3-agent integration test — the server correctly recommended the
central host with the right "why."

---

## Step 1 — Bring the code in (10 min)
Extract the zip over your repo. New/changed files: agent probe engine + DTOs +
ServerLink/Program wiring; server metrics store, recommend engine + tests,
gateway mesh wiring, protocol; web live-data hook + Matrix UI + Home.
```powershell
cd "C:\Users\Sudo\Documents\NETWORKING PROJECT\gamenight"
git checkout main; git pull
git checkout -b feat/phase3-mesh
git add . ; git status    # review; expect the files above, nothing unexpected
git commit -m "feat: phase 3 - ping mesh, metric matrix, host recommendation engine"
git push -u origin feat/phase3-mesh
npm install   # (no new deps, but safe)
```
Open the PR; keep unmerged until local test passes.

## Step 2 — Server + dashboard locally (5 min)
```powershell
. .\dev-env.ps1
npm run build
npm run dev -w server
```
Sign in at localhost:8080. The Home page now shows a **Ping matrix** card below
the presence strip — currently saying "appears when 2+ players are online",
since only you are on.

## Step 3 — Rebuild + relaunch your agent (5 min)
The agent gained the probe engine, so rebuild it:
```powershell
cd agent
dotnet publish -c Release
taskkill /IM GameNightAgent.exe /F 2>$null
Copy-Item bin\Release\net8.0-windows\win-x64\publish\GameNightAgent.exe C:\GameNight\ -Force
C:\GameNight\GameNightAgent.exe
```
(It relinks automatically from the saved token — no dialog.)

## Step 4 — The real test needs a second agent (20 min)
The matrix needs **two players on Radmin**. Either:
- **A friend** on your Radmin network runs the (new) agent pointed at your Render
  URL, OR
- **A second PC** of yours on Radmin.

Once two agents are online with Radmin connected:
- Within ~30s the matrix populates with real ping numbers between you.
- The **Recommended host** banner appears with reasoning: *"Worst-case ping to X
  is Nms; if Y hosted, worst-case would be Mms."*
- Hover any cell to see jitter and loss, not just latency.

To deploy for the friend test: merge the PR (CI green) → Render auto-deploys →
publish a **new agent release** (v0.3.0) to GitHub so the download button and your
friend get the probe-capable build. (Bump `<Version>` in the .csproj to 0.3.0,
rebuild, create release `agent-v0.3.0`, update the 3 Render AGENT_* env vars.)

## Step 5 — CCNA Lab 02 (Wireshark) pairs perfectly here
With a friend on Radmin, capture the probe pings inside vs outside the tunnel
(docs/labs/02). You'll literally see your agent's ICMP probes as plaintext inside
Radmin and encrypted UDP outside — the exact traffic driving the matrix.

## M3 checklist
- [ ] CI green; merged; deployed; agent v0.3.0 released
- [ ] Matrix card shows on dashboard
- [ ] With 2 agents: real ping numbers appear in the grid within ~30s
- [ ] Host recommendation shows with reasoning
- [ ] Cell hover shows jitter + loss
- [ ] (bonus) Lab 02 written up

Declare **"Phase 3 complete"** and Phase 4 (scheduling + WhatsApp + notifications)
begins — turning the monitor into a community platform.
