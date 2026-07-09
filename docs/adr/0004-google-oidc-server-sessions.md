# ADR-0004: Google OIDC (hand-rolled code flow) + opaque server-side sessions

**Status:** accepted · **Date:** 2026-07-09

## Context
Phase 1 needs sign-in with zero stored passwords (SDD §15) and instant
revocation on ban (FR-04). Learning value of seeing the OAuth machinery is a
project goal.

## Options considered
- Auth SaaS (Auth0/Clerk/Supabase Auth): fastest, but hides the exact thing we
  want to learn, adds a vendor, free tiers churn.
- Passport.js / auth framework: middleware magic obscures the flow.
- **Hand-rolled code flow + jose for JWT verification (chosen):** every step
  visible (state, nonce, code exchange, JWKS verification); jose covers the
  only part that must never be hand-rolled (crypto).
- JWT sessions: stateless, but revocation requires denylists — rebuilding what
  server-side sessions give for free.

## Decision
Authorization-code flow against Google's raw endpoints; ID token verified via
jose + Google JWKS (issuer, audience, nonce). Sessions are 256-bit opaque
tokens in an HttpOnly/Lax cookie, stored as SHA-256 hashes in Postgres.
ADMIN_EMAIL bootstraps the first admin.

## Consequences
~150 lines we own and must maintain; in exchange, full understanding, zero
auth vendors, and bans that bite mid-session (verified by integration test).
Adding another IdP later means writing another small module, not swapping a
framework.
