/**
 * Database schema (Drizzle). SDD §9 — Phase 1 slice: users + sessions.
 * Teaching: constraints ARE business rules. `unique` on google_sub means
 * "one account per Google identity" is enforced by Postgres itself —
 * application bugs cannot violate it.
 */
import { pgTable, uuid, text, timestamp } from 'drizzle-orm/pg-core';

export const users = pgTable('users', {
  id: uuid('id').primaryKey().defaultRandom(),
  // Google's `sub` claim: the STABLE identity key. Emails can change; sub never does.
  googleSub: text('google_sub').notNull().unique(),
  email: text('email').notNull(),
  displayName: text('display_name'),
  avatarUrl: text('avatar_url'),
  // The entire authorization model (SDD §16) lives in these two columns:
  status: text('status', { enum: ['pending', 'approved', 'rejected', 'banned'] })
    .notNull()
    .default('pending'),
  role: text('role', { enum: ['admin', 'member'] })
    .notNull()
    .default('member'),
  createdAt: timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
  approvedAt: timestamp('approved_at', { withTimezone: true }),
  approvedBy: uuid('approved_by'),
});

export const sessions = pgTable('sessions', {
  id: uuid('id').primaryKey().defaultRandom(),
  // We store a SHA-256 of the session token, never the token itself.
  // If the DB ever leaks, the attacker holds hashes — useless for login.
  // Same principle as hashing passwords, applied to bearer tokens (SDD §24).
  tokenHash: text('token_hash').notNull().unique(),
  userId: uuid('user_id')
    .notNull()
    .references(() => users.id, { onDelete: 'cascade' }),
  createdAt: timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
  expiresAt: timestamp('expires_at', { withTimezone: true }).notNull(),
});

export type User = typeof users.$inferSelect;
