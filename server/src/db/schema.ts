/**
 * Database schema (Drizzle). SDD §9 — Phase 2 slice: users, sessions, devices.
 * Teaching: constraints ARE business rules — `unique` on google_sub means
 * "one account per Google identity" is enforced by Postgres itself.
 */
import { pgTable, uuid, text, timestamp, boolean } from 'drizzle-orm/pg-core';

export const users = pgTable('users', {
  id: uuid('id').primaryKey().defaultRandom(),
  googleSub: text('google_sub').notNull().unique(),
  email: text('email').notNull(),
  displayName: text('display_name'),
  avatarUrl: text('avatar_url'),
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
  tokenHash: text('token_hash').notNull().unique(),
  userId: uuid('user_id')
    .notNull()
    .references(() => users.id, { onDelete: 'cascade' }),
  createdAt: timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
  expiresAt: timestamp('expires_at', { withTimezone: true }).notNull(),
});

export const devices = pgTable('devices', {
  id: uuid('id').primaryKey().defaultRandom(),
  userId: uuid('user_id')
    .notNull()
    .references(() => users.id, { onDelete: 'cascade' }),
  name: text('name').notNull(), // e.g. "Zahid-PC" — user-visible machine name
  // Same rule as sessions: the DB holds a SHA-256; the raw token exists only
  // on the agent's disk (DPAPI-encrypted) and in transit over TLS.
  tokenHash: text('token_hash').notNull().unique(),
  agentVersion: text('agent_version'),
  lastSeen: timestamp('last_seen', { withTimezone: true }),
  revoked: boolean('revoked').notNull().default(false),
  createdAt: timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
});

export type User = typeof users.$inferSelect;
export type Device = typeof devices.$inferSelect;
