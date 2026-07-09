import { defineConfig } from 'drizzle-kit';
// Offline config: `db:generate` diffs schema.ts against ./drizzle and writes
// plain SQL migration files — readable, reviewable in PRs, no DB needed.
export default defineConfig({
  dialect: 'postgresql',
  schema: './src/db/schema.ts',
  out: './drizzle',
});
