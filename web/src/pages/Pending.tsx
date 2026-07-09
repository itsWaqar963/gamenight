import type { Me } from "../api";
import { api } from "../api";

export function Pending({ user }: { user: Me }) {
  return (
    <div className="center">
      <main>
        <h1>
          {user.status === "pending"
            ? "⏳ Waiting for approval"
            : "🚫 Access denied"}
        </h1>
        <p className="muted">
          {user.status === "pending"
            ? `Signed in as ${user.email}. An admin needs to approve your account — ping the group!`
            : `Your account (${user.email}) is ${user.status}.`}
        </p>
        <button
          className="btn secondary"
          onClick={() => api.logout().then(() => location.assign("/"))}
        >
          Sign out
        </button>
      </main>
    </div>
  );
}
