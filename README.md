# GameNight

Private LAN/VPN gaming monitor & community platform for Far Cry 2 over Radmin VPN.

- **Design:** see `docs/SDD.md` (approved architecture baseline)
- **Decisions:** see `docs/adr/`
- **Run locally:** `npm install && npm run dev` → http://localhost:8080
- **Run like production:** `docker compose -f infra/docker-compose.yml up --build`

| Folder | What |
|--------|------|
| `server/` | Node.js + TypeScript + Fastify API (modular monolith) |
| `web/` | React SPA (Phase 1) |
| `agent/` | C#/.NET Windows tray agent (Phase 2) |
| `shared/` | Protocol types shared across components |
| `infra/` | Dockerfile, compose, Render blueprint |
| `docs/` | SDD, ADRs, runbook, CCNA lab writeups |

Agent Voice signaling uses the hosted Railway Socket.IO server (not shipped in this repo).
