/**
 * Google OIDC, authorization-code flow (SDD §15) — implemented against
 * Google's raw endpoints on purpose: seeing every moving part once is the
 * whole point. The jose library handles only what MUST NOT be hand-rolled:
 * JWT signature verification against Google's published keys (JWKS).
 */
import { createRemoteJWKSet, jwtVerify } from 'jose';

const AUTH_ENDPOINT = 'https://accounts.google.com/o/oauth2/v2/auth';
const TOKEN_ENDPOINT = 'https://oauth2.googleapis.com/token';
// Google rotates signing keys; createRemoteJWKSet fetches + caches them.
const GOOGLE_JWKS = createRemoteJWKSet(new URL('https://www.googleapis.com/oauth2/v3/certs'));

export function buildAuthUrl(opts: {
  clientId: string;
  redirectUri: string;
  state: string; // CSRF binding: ties the callback to a flow WE started
  nonce: string; // replay binding: ties the ID token to THIS login attempt
}): string {
  const p = new URLSearchParams({
    client_id: opts.clientId,
    redirect_uri: opts.redirectUri,
    response_type: 'code',
    scope: 'openid email profile', // minimal scopes — data minimization applies to identity too
    state: opts.state,
    nonce: opts.nonce,
    prompt: 'select_account',
  });
  return `${AUTH_ENDPOINT}?${p.toString()}`;
}

export type GoogleIdentity = {
  sub: string;
  email: string;
  emailVerified: boolean;
  name: string | undefined;
  picture: string | undefined;
};

/** Exchanges the one-time code for tokens, verifies the ID token, returns identity claims. */
export async function exchangeAndVerify(opts: {
  code: string;
  clientId: string;
  clientSecret: string;
  redirectUri: string;
  expectedNonce: string;
}): Promise<GoogleIdentity> {
  // Server-to-server exchange: the client_secret and tokens never touch the browser.
  const res = await fetch(TOKEN_ENDPOINT, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      code: opts.code,
      client_id: opts.clientId,
      client_secret: opts.clientSecret,
      redirect_uri: opts.redirectUri,
      grant_type: 'authorization_code',
    }),
  });
  if (!res.ok) throw new Error(`token exchange failed: ${res.status} ${await res.text()}`);
  const body = (await res.json()) as { id_token?: string };
  if (!body.id_token) throw new Error('no id_token in token response');

  // Verify: signature (Google's keys), issuer, audience (OUR client id —
  // rejects tokens minted for some other app), expiry (jose checks exp).
  const { payload } = await jwtVerify(body.id_token, GOOGLE_JWKS, {
    issuer: ['https://accounts.google.com', 'accounts.google.com'],
    audience: opts.clientId,
  });
  if (payload.nonce !== opts.expectedNonce) throw new Error('nonce mismatch (possible replay)');
  if (typeof payload.sub !== 'string' || typeof payload.email !== 'string')
    throw new Error('id token missing sub/email');

  return {
    sub: payload.sub,
    email: payload.email,
    emailVerified: payload.email_verified === true,
    name: typeof payload.name === 'string' ? payload.name : undefined,
    picture: typeof payload.picture === 'string' ? payload.picture : undefined,
  };
}
