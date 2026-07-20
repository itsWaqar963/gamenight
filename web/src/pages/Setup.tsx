import { useQuery } from "@tanstack/react-query";
import type React from "react";
import { api } from "../api";
import { useLiveData } from "../presence";

const STATUS_META = {
  pass: { color: "#22c55e", icon: "✓" },
  warn: { color: "#eab308", icon: "!" },
  fail: { color: "#ef4444", icon: "✕" },
} as const;

export function Setup() {
  const setup = useQuery({ queryKey: ["setup"], queryFn: api.setup });
  const { diagnostics, runDiagnostics } = useLiveData();
  const s = setup.data;

  return (
    <>
      <h1>Get playing in 3 steps</h1>
      <p className="muted">
        New to GameNight? Do these once and you're set. Already playing? Jump to
        "Check my setup" below.
      </p>

      <Step n={1} title="Get Far Cry 2">
        {s?.gameUrl ? (
          <a className="btn" href={s.gameUrl} target="_blank" rel="noreferrer">
            Download the game package
          </a>
        ) : (
          <span className="muted">
            Ask the admin for the game download link.
          </span>
        )}
        <p className="muted" style={{ fontSize: ".85rem" }}>
          Everyone uses the same package so versions match.
        </p>
      </Step>

      <Step n={2} title="Install Radmin VPN & join the network">
        <a
          className="btn secondary"
          href="https://www.radmin-vpn.com/"
          target="_blank"
          rel="noreferrer"
        >
          Get Radmin VPN
        </a>
        <p className="muted" style={{ fontSize: ".85rem" }}>
          After installing: Radmin VPN → Network → Join an existing network, and
          enter:
        </p>
        <div
          style={{
            background: "#0f1420",
            borderRadius: 6,
            padding: ".5rem .8rem",
            fontFamily: "monospace",
          }}
        >
          Network: <strong>{s?.radminNetwork ?? "(ask the admin)"}</strong>
        </div>
        <p className="muted" style={{ fontSize: ".8rem" }}>
          Radmin lets Far Cry 2 see everyone as one LAN. Keep it connected
          (green icon) while playing.
        </p>
      </Step>

      <Step n={3} title="Install the GameNight agent">
        {s?.agent.url ? (
          <a className="btn" href={s.agent.url}>
            Download the agent{s.agent.version ? ` (${s.agent.version})` : ""}
          </a>
        ) : (
          <span className="muted">
            Could not load the latest GitHub release — try again shortly.
          </span>
        )}
        <p className="muted" style={{ fontSize: ".85rem" }}>
          Always the latest release from GitHub. Windows will warn about an
          unrecognized app — click{" "}
          <em>More info → Run anyway</em>. It's safe; it's unsigned. Then
          generate a link code on the Home page and paste it in.
        </p>
      </Step>

      <div style={{ marginTop: "2rem" }}>
        <h3>Check my setup</h3>
        <p className="muted">
          Runs a quick self-check on your PC. Green is good; red comes with a
          fix.
        </p>
        <button className="btn" onClick={runDiagnostics}>
          Run diagnostics
        </button>

        {diagnostics === null ? (
          <p className="muted" style={{ fontSize: ".85rem" }}>
            (Results appear here — make sure your agent is running.)
          </p>
        ) : (
          <div style={{ marginTop: "1rem" }}>
            {diagnostics.map((c) => {
              const meta = STATUS_META[c.status];
              return (
                <div
                  key={c.id}
                  className="card"
                  style={{ borderLeft: `3px solid ${meta.color}` }}
                >
                  <div className="row">
                    <span
                      style={{
                        color: meta.color,
                        fontWeight: 700,
                        marginRight: ".5rem",
                      }}
                    >
                      {meta.icon}
                    </span>
                    <strong className="grow">{c.label}</strong>
                  </div>
                  <div className="muted" style={{ fontSize: ".88rem" }}>
                    {c.detail}
                  </div>
                  {c.fix && (
                    <div
                      style={{
                        fontSize: ".85rem",
                        marginTop: ".3rem",
                        color: "#eab308",
                      }}
                    >
                      → {c.fix}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </>
  );
}

function Step({
  n,
  title,
  children,
}: {
  n: number;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <div className="card">
      <div
        className="row"
        style={{ alignItems: "center", marginBottom: ".5rem" }}
      >
        <span
          style={{
            display: "inline-flex",
            alignItems: "center",
            justifyContent: "center",
            width: 28,
            height: 28,
            borderRadius: "50%",
            background: "#3b82f6",
            color: "white",
            fontWeight: 700,
            marginRight: ".6rem",
          }}
        >
          {n}
        </span>
        <strong style={{ fontSize: "1.1rem" }}>{title}</strong>
      </div>
      {children}
    </div>
  );
}
