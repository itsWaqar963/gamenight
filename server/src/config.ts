/**
 * All environment access lives HERE and nowhere else (12-factor, factor III).
 */
export type Config = {
  port: number;
  host: string;
  nodeEnv: 'development' | 'production' | 'test';
  databaseUrl: string | undefined;
  /** Public base URL of this app, no trailing slash — used to build the OAuth redirect_uri. */
  appUrl: string;
  google: { clientId: string; clientSecret: string } | undefined;
  /** First login with this email is auto-approved as admin (bootstrap problem: someone must approve the approver). */
  adminEmail: string | undefined;
};

export function loadConfig(env: NodeJS.ProcessEnv = process.env): Config {
  const nodeEnv =
    env.NODE_ENV === 'production' ? 'production' : env.NODE_ENV === 'test' ? 'test' : 'development';
  const port = env.PORT ? Number(env.PORT) : 8080;
  return {
    port,
    host: env.HOST ?? '0.0.0.0',
    nodeEnv,
    databaseUrl: env.DATABASE_URL,
    appUrl: (env.APP_URL ?? `http://localhost:${port}`).replace(/\/$/, ''),
    google:
      env.GOOGLE_CLIENT_ID && env.GOOGLE_CLIENT_SECRET
        ? { clientId: env.GOOGLE_CLIENT_ID, clientSecret: env.GOOGLE_CLIENT_SECRET }
        : undefined,
    adminEmail: env.ADMIN_EMAIL?.toLowerCase(),
  };
}
