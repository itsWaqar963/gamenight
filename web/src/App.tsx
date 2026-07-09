import { Routes, Route, Link, Navigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from './api';
import { Login } from './pages/Login';
import { Pending } from './pages/Pending';
import { Home } from './pages/Home';
import { Admin } from './pages/Admin';

export function useMe() {
  return useQuery({ queryKey: ['me'], queryFn: api.me });
}

export function App() {
  const me = useMe();
  if (me.isLoading) return <div className="center"><p className="muted">loading…</p></div>;
  const user = me.data?.user ?? null;

  // The routing IS the authorization UX: not signed in → Login;
  // signed in but not approved → Pending; approved → the app.
  if (!user) return <Login />;
  if (user.status !== 'approved') return <Pending user={user} />;

  return (
    <div className="shell">
      <nav>
        <strong>🎮 GameNight</strong>
        <Link to="/">Home</Link>
        {user.role === 'admin' && <Link to="/admin">Admin</Link>}
        <span className="grow" />
        <span className="muted">{user.displayName}</span>
        <button className="btn secondary" onClick={() => api.logout().then(() => location.assign('/'))}>
          Sign out
        </button>
      </nav>
      <Routes>
        <Route path="/" element={<Home me={user} />} />
        <Route path="/admin" element={user.role === 'admin' ? <Admin /> : <Navigate to="/" />} />
        <Route path="*" element={<Navigate to="/" />} />
      </Routes>
    </div>
  );
}
