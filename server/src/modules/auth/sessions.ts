/**
 * Server-side sessions (SDD §15): the cookie carries an opaque random token;
 * the DB stores its hash. Opaque beats JWT here because revocation is a hard
 * requirement (FR-04: banning must kill access instantly) — you can delete a
 * row; you cannot un-sign a JWT.
 */
import { createHash, randomBytes } from 'node:crypto';
import { eq, lt } from 'drizzle-orm';
import type { Db } from '../../db.js';
import { sessions, users, type User } from '../../db/schema.js';

const SESSION_TTL_DAYS = 30;

export function hashToken(token: string): string {
  return createHash('sha256').update(token).digest('hex');
}

/** Creates a session row; returns the RAW token (only moment it exists in plaintext). */
export async function createSession(db: Db, userId: string): Promise<string> {
  const token = randomBytes(32).toString('hex'); // 256 bits of entropy
  const expiresAt = new Date(Date.now() + SESSION_TTL_DAYS * 24 * 60 * 60 * 1000);
  await db.orm.insert(sessions).values({ tokenHash: hashToken(token), userId, expiresAt });
  return token;
}

/** Resolves a raw cookie token to its user, or null (missing/expired/revoked). */
export async function getUserBySession(db: Db, token: string): Promise<User | null> {
  const rows = await db.orm
    .select({ user: users, expiresAt: sessions.expiresAt })
    .from(sessions)
    .innerJoin(users, eq(sessions.userId, users.id))
    .where(eq(sessions.tokenHash, hashToken(token)))
    .limit(1);
  const row = rows[0];
  if (!row) return null;
  if (row.expiresAt < new Date()) {
    await db.orm.delete(sessions).where(eq(sessions.tokenHash, hashToken(token)));
    return null;
  }
  return row.user;
}

export async function deleteSession(db: Db, token: string): Promise<void> {
  await db.orm.delete(sessions).where(eq(sessions.tokenHash, hashToken(token)));
}

/** Revocation cascade (SDD §16): banning a user kills all their sessions NOW. */
export async function deleteAllUserSessions(db: Db, userId: string): Promise<void> {
  await db.orm.delete(sessions).where(eq(sessions.userId, userId));
}

/** Housekeeping: called opportunistically at boot. */
export async function purgeExpired(db: Db): Promise<void> {
  await db.orm.delete(sessions).where(lt(sessions.expiresAt, new Date()));
}
