import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../api';

export function Admin() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['users'], queryFn: api.users });
  const act = useMutation({
    mutationFn: ({ id, action }: { id: string; action: 'approve' | 'reject' | 'ban' }) =>
      api.setStatus(id, action),
    // After any change, refetch the roster — cache invalidation as a one-liner.
    onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }),
  });

  const users = q.data?.users ?? [];
  const pending = users.filter((u) => u.status === 'pending');
  const rest = users.filter((u) => u.status !== 'pending');

  return (
    <>
      <h1>Admin</h1>
      <h3>Pending approval ({pending.length})</h3>
      {pending.length === 0 && <p className="muted">Nobody waiting. Peace.</p>}
      {pending.map((u) => (
        <div className="card row" key={u.id}>
          {u.avatarUrl && <img className="avatar" src={u.avatarUrl} alt="" referrerPolicy="no-referrer" />}
          <div className="grow">
            <div>{u.displayName}</div>
            <div className="muted" style={{ fontSize: '.85rem' }}>{u.email}</div>
          </div>
          <button className="btn" onClick={() => act.mutate({ id: u.id, action: 'approve' })}>Approve</button>
          <button className="btn danger" onClick={() => act.mutate({ id: u.id, action: 'reject' })}>Reject</button>
        </div>
      ))}
      <h3>Everyone</h3>
      {rest.map((u) => (
        <div className="card row" key={u.id}>
          <div className="grow">
            <div>{u.displayName} <span className={`tag ${u.status}`}>{u.status}</span>{' '}
              {u.role === 'admin' && <span className="tag admin">admin</span>}</div>
            <div className="muted" style={{ fontSize: '.85rem' }}>{u.email}</div>
          </div>
          {u.status === 'approved' && u.role !== 'admin' && (
            <button className="btn danger" onClick={() => act.mutate({ id: u.id, action: 'ban' })}>Ban</button>
          )}
          {(u.status === 'banned' || u.status === 'rejected') && (
            <button className="btn secondary" onClick={() => act.mutate({ id: u.id, action: 'approve' })}>Re-approve</button>
          )}
        </div>
      ))}
    </>
  );
}
