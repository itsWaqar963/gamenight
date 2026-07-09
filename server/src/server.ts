import { loadConfig } from './config.js';
import { createDb } from './db.js';
import { buildApp } from './app.js';
import { purgeExpired } from './modules/auth/sessions.js';

const config = loadConfig();
const db = createDb(config);

// Migrations run BEFORE we accept traffic: the process must never serve
// requests against a schema it doesn't expect. Boot order is a contract.
if (db) {
  await db.migrate();
  await purgeExpired(db);
}

const app = buildApp(config, db);
await app.listen({ port: config.port, host: config.host });

for (const signal of ['SIGTERM', 'SIGINT'] as const) {
  process.on(signal, async () => {
    app.log.info({ signal }, 'shutting down');
    await app.close();
    await db?.close();
    process.exit(0);
  });
}
