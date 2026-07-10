import { useQuery } from "@tanstack/react-query";
import { api, type Me } from "../api";
import { usePresence } from "../presence";

const STATE_META = {
  in_game: { dot: "#22c55e", label: "IN GAME 🎮" },
  online: { dot: "#3b82f6", label: "online" },
  idle: { dot: "#eab308", label: "idle" },
  offline: { dot: "#4b5563", label: "offline" },
} as const;

export function Home({ me }: { me: Me }) {
  const roster = useQuery({ queryKey: ["users"], queryFn: api.users });
  const presence = usePresence();

  const members = (roster.data?.users ?? []).filter(
    (u) => u.status === "approved",
  );
  // Merge persistent roster with ephemeral presence: everyone appears;
  // those with a live agent get their real state, the rest show offline.
  const rows = members
    .map((u) => {
      const p = presence.get(u.id);
      return {
        user: u,
        state: (p?.state ?? "offline") as keyof typeof STATE_META,
        radmin: p?.radmin ?? null,
      };
    })
    .sort((a, b) => {
      const rank = { in_game: 0, online: 1, idle: 2, offline: 3 };
      return rank[a.state] - rank[b.state];
    });

  const liveCount = rows.filter((r) => r.state !== "offline").length;

  return (
    <>
      <h1>Welcome, {me.displayName} 👋</h1>
      <p className="muted">
        {liveCount === 0
          ? "Nobody online right now. The strip lights up when agents connect."
          : `${liveCount} ${liveCount === 1 ? "person" : "people"} online.`}
      </p>
      {rows.map(({ user: u, state, radmin }) => (
        <div
          className="card row"
          key={u.id}
          style={{ opacity: state === "offline" ? 0.55 : 1 }}
        >
          <span
            style={{
              width: 10,
              height: 10,
              borderRadius: "50%",
              background: STATE_META[state].dot,
            }}
          />
          {u.avatarUrl && (
            <img
              className="avatar"
              src={u.avatarUrl}
              alt=""
              referrerPolicy="no-referrer"
            />
          )}
          <div className="grow">
            <div>
              {u.displayName}{" "}
              {u.role === "admin" && <span className="tag admin">admin</span>}
            </div>
            <div className="muted" style={{ fontSize: ".85rem" }}>
              {STATE_META[state].label}
              {radmin?.connected && radmin.ip && <> · Radmin {radmin.ip}</>}
              {state !== "offline" && radmin && !radmin.connected && (
                <> · ⚠ Radmin disconnected</>
              )}
            </div>
          </div>
        </div>
      ))}
      <h3 style={{ marginTop: "2rem" }}>Link your PC</h3>
      <LinkDevice />
    </>
  );
}

function LinkDevice() {
  // Agent download metadata comes from the server (which reads it from env,
  // pointing at a GitHub Release). If unconfigured, the download step hides
  // itself gracefully rather than showing a dead button.
  const release = useQuery({
    queryKey: ["agent-latest"],
    queryFn: () =>
      fetch("/api/v1/agent/latest").then((r) => r.json()) as Promise<{
        version: string | null;
        url: string | null;
        sha256: string | null;
      }>,
  });

  const link = useQuery({
    queryKey: ["linkcode"],
    queryFn: () =>
      fetch("/api/v1/devices/link", { method: "POST" }).then((r) =>
        r.json(),
      ) as Promise<{ code: string; expiresInSec: number }>,
    enabled: false, // only on click
    gcTime: 0,
  });

  const downloadUrl = release.data?.url ?? null;

  return (
    <div className="card">
      <p className="muted" style={{ marginTop: 0 }}>
        Three steps: download the agent, run it, then paste a link code into it.
      </p>

      {/* Step 1 — download */}
      <p>
        <strong>1. Download the agent</strong>
        <br />
        {downloadUrl ? (
          <>
            <a className="btn" href={downloadUrl}>
              Download GameNight agent
              {release.data?.version ? ` (${release.data.version})` : ""}
            </a>
            <br />
            <span className="muted" style={{ fontSize: ".85rem" }}>
              Windows will warn about an unrecognized app — click{" "}
              <em>More info → Run anyway</em>. It's safe; it's just unsigned.
            </span>
          </>
        ) : (
          <span className="muted">
            Download link not configured yet — ask the admin for the agent file.
          </span>
        )}
      </p>

      {/* Step 2 — generate code */}
      <p style={{ marginTop: "1.2rem" }}>
        <strong>2. Generate a link code</strong> and type it into the agent
        along with this site's address.
      </p>
      {link.data ? (
        <p>
          Code:{" "}
          <strong style={{ fontSize: "1.6rem", letterSpacing: ".2rem" }}>
            {link.data.code}
          </strong>{" "}
          <span className="muted">
            (valid {Math.round(link.data.expiresInSec / 60)} min, single use)
          </span>
        </p>
      ) : (
        <button className="btn" onClick={() => link.refetch()}>
          Generate link code
        </button>
      )}
    </div>
  );
}
