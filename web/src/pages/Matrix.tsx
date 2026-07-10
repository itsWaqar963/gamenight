import type {
  PresenceUser,
  MatrixCell,
  HostRecommendation,
} from "../../../server/src/protocol/messages";

// Composite quality → color. Mirrors the engine's intuition: green good,
// yellow marginal, red bad. Loss and jitter drag a cell toward red.
function cellColor(c: MatrixCell): string {
  const eff = c.avgRtt + c.jitter * 2 + c.lossPct * 30;
  if (c.lossPct >= 5 || eff >= 150) return "#7f1d1d"; // red
  if (eff >= 80) return "#78350f"; // amber
  return "#14532d"; // green
}

export function MeshMatrix({
  presence,
  matrix,
  recommendation,
}: {
  presence: Map<string, PresenceUser>;
  matrix: MatrixCell[];
  recommendation: HostRecommendation;
}) {
  // Only players with a live agent + Radmin IP participate in the mesh.
  const players = [...presence.values()].filter(
    (u) => u.radmin?.connected && u.radmin.ip,
  );
  if (players.length < 2) {
    return (
      <div className="card">
        <p className="muted" style={{ margin: 0 }}>
          The ping matrix appears when at least two players are online with
          Radmin connected. Right now {players.length}{" "}
          {players.length === 1 ? "is" : "are"} ready.
        </p>
      </div>
    );
  }

  const byPair = new Map(
    matrix.map((c) => [`${c.fromUserId}->${c.toUserId}`, c]),
  );
  const name = (id: string) => presence.get(id)?.displayName ?? "?";

  return (
    <div className="card">
      <h3 style={{ marginTop: 0 }}>Ping matrix</h3>
      {recommendation?.hostUserId ? (
        <div
          style={{
            background: "#14532d",
            borderRadius: 8,
            padding: ".7rem 1rem",
            marginBottom: "1rem",
          }}
        >
          <strong>🏆 Recommended host: {recommendation.hostName}</strong>
          <ul style={{ margin: ".4rem 0 0", paddingLeft: "1.2rem" }}>
            {recommendation.reasons.map((r, i) => (
              <li key={i} className="muted" style={{ fontSize: ".9rem" }}>
                {r}
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <p className="muted" style={{ fontSize: ".9rem" }}>
          Gathering measurements… the recommendation appears once links are
          probed (~30s).
        </p>
      )}

      <div style={{ overflowX: "auto" }}>
        <table style={{ borderCollapse: "collapse", fontSize: ".85rem" }}>
          <thead>
            <tr>
              <th style={{ padding: ".3rem .5rem", textAlign: "left" }}>
                <span className="muted">from ↓ / to →</span>
              </th>
              {players.map((p) => (
                <th key={p.userId} style={{ padding: ".3rem .5rem" }}>
                  {name(p.userId)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {players.map((from) => (
              <tr key={from.userId}>
                <td style={{ padding: ".3rem .5rem", fontWeight: 600 }}>
                  {name(from.userId)}
                </td>
                {players.map((to) => {
                  if (from.userId === to.userId)
                    return (
                      <td
                        key={to.userId}
                        style={{ textAlign: "center", color: "#4b5563" }}
                      >
                        —
                      </td>
                    );
                  const c = byPair.get(`${from.userId}->${to.userId}`);
                  return (
                    <td
                      key={to.userId}
                      title={
                        c
                          ? `${c.avgRtt}ms · jitter ${c.jitter}ms · loss ${c.lossPct}%`
                          : "not measured yet"
                      }
                      style={{
                        textAlign: "center",
                        padding: ".3rem .5rem",
                        background: c ? cellColor(c) : "#1b2233",
                        borderRadius: 4,
                      }}
                    >
                      {c ? `${Math.round(c.avgRtt)}` : "·"}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="muted" style={{ fontSize: ".78rem", marginBottom: 0 }}>
        Numbers are round-trip ms. Hover a cell for jitter and loss. Colors
        weight loss and jitter, not just latency — a stable 50ms beats a jittery
        40ms.
      </p>
    </div>
  );
}
