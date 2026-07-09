/** Tiny typed fetch layer. Cookies ride automatically (same origin). */
export type Me = {
  id: string; displayName: string | null; avatarUrl: string | null; email: string;
  status: 'pending' | 'approved' | 'rejected' | 'banned'; role: 'admin' | 'member'; createdAt: string;
};

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) throw new Error(`${res.status}: ${await res.text()}`);
  return res.json() as Promise<T>;
}

export const api = {
  me: () => fetch('/api/v1/me').then((r) => json<{ user: Me | null }>(r)),
  users: () => fetch('/api/v1/users').then((r) => json<{ users: Me[] }>(r)),
  setStatus: (id: string, action: 'approve' | 'reject' | 'ban') =>
    fetch(`/api/v1/users/${id}/${action}`, { method: 'POST' }).then((r) => json<{ ok: true }>(r)),
  logout: () => fetch('/auth/logout', { method: 'POST' }).then((r) => json<{ ok: true }>(r)),
};
