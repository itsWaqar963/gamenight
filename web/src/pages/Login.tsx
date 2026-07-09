export function Login() {
  return (
    <div className="center">
      <main>
        <h1>🎮 GameNight</h1>
        <p className="muted">Far Cry 2 nights, organized. Members only.</p>
        {/* A plain link, not fetch(): OAuth is a full-page redirect dance —
            the browser must physically travel to Google and back. */}
        <a className="btn" href="/auth/google">
          Sign in with Google
        </a>
      </main>
    </div>
  );
}
