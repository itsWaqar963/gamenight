/**
 * Database wiring: postgres.js client + Drizzle + boot-time migrations.
 * Migrations run at startup (SDD §27): additive-only by policy, so a deploy
 * can always run them safely; destructive changes require the manual
 * two-step in docs/runbook.md.
 */
import postgres from 'postgres';
import { drizzle, type PostgresJsDatabase } from 'drizzle-orm/postgres-js';
import { migrate } from 'drizzle-orm/postgres-js/migrator';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Config } from './config.js';
import * as schema from './db/schema.js';

export type Db = {
  orm: PostgresJsDatabase<typeof schema>;
  ping(): Promise<void>;
  migrate(): Promise<void>;
  close(): Promise<void>;
};

export function createDb(config: Config): Db | undefined {
  if (!config.databaseUrl) return undefined;
  const sql = postgres(config.databaseUrl, { max: 3, connect_timeout: 5 });
  const orm = drizzle(sql, { schema });
  const migrationsFolder = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'drizzle');
  return {
    orm,
    async ping() {
      await sql`SELECT 1`;
    },
    async migrate() {
      await migrate(orm, { migrationsFolder });
    },
    async close() {
      await sql.end();
    },
  };
}
