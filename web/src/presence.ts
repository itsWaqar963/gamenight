/**
 * Dashboard WebSocket client. Connects to /ws, authenticates via the session
 * cookie (sent automatically on the upgrade request), receives a full
 * presence snapshot then deltas. Reconnects with backoff — the same
 * resilience pattern the C# agent uses, in miniature.
 */
import { useEffect, useState } from "react";
import type { PresenceUser } from "../../server/src/protocol/messages";

export function usePresence(): Map<string, PresenceUser> {
  const [byUser, setByUser] = useState<Map<string, PresenceUser>>(new Map());

  useEffect(() => {
    let sock: WebSocket | null = null;
    let closed = false;
    let attempt = 0;

    const connect = () => {
      const proto = location.protocol === "https:" ? "wss" : "ws";
      sock = new WebSocket(`${proto}://${location.host}/ws`);

      sock.onopen = () => {
        attempt = 0;
        sock?.send(JSON.stringify({ t: "hello", role: "dashboard" }));
      };

      sock.onmessage = (ev) => {
        const msg = JSON.parse(String(ev.data)) as
          | { t: "presence"; users: PresenceUser[] }
          | { t: "presence_delta"; user: PresenceUser };
        if (msg.t === "presence") {
          setByUser(new Map(msg.users.map((u) => [u.userId, u])));
        } else if (msg.t === "presence_delta") {
          setByUser((prev) => {
            const next = new Map(prev);
            if (msg.user.state === "offline") next.delete(msg.user.userId);
            else next.set(msg.user.userId, msg.user);
            return next;
          });
        }
      };

      sock.onclose = () => {
        if (closed) return;
        // Exponential backoff with jitter — never hammer a restarting server.
        const delay =
          Math.min(1000 * 2 ** attempt++, 30_000) * (0.8 + Math.random() * 0.4);
        setTimeout(connect, delay);
      };
    };

    connect();
    return () => {
      closed = true;
      sock?.close();
    };
  }, []);

  return byUser;
}
