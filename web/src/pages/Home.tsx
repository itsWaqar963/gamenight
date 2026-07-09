import { useQuery } from '@tanstack/react-query';
import { api, type Me } from '../api';

export function Home({ me }: { me: Me }) {
  const q = useQuery({ queryKey: ['users'], queryFn: api.users });
  const members = (q.data?.users ?? []).filter((u) => u.status === 'approved');
  return (
    <>
      <h1>Welcome, {me.displayName} 👋</h1>
      <p className="muted">
        Phase 1: identity works. Live presence and the ping matrix arrive in Phase 2 —
        this roster will light up.
      </p>
      <h3>Members ({members.length})</h3>
      {members.map((u) => (
        <div className="card row" key={u.id}>
          {u.avatarUrl && <img className="avatar" src={u.avatarUrl} alt="" referrerPolicy="no-referrer" />}
          <div className="grow">
            <div>{u.displayName}</div>
            <div className="muted" style={{ fontSize: '.85rem' }}>{u.email}</div>
          </div>
          {u.role === 'admin' && <span className="tag admin">admin</span>}
        </div>
      ))}
    </>
  );
}
