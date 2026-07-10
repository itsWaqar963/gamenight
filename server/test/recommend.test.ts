/**
 * Tests for the host recommendation engine (SDD §31: pure logic gets the
 * densest tests). Each scenario mirrors a real situation from discovery.
 */
import { describe, it, expect } from 'vitest';
import { recommendHost, effectiveRtt, type Candidate } from '../src/modules/recommend/engine.js';
import type { MatrixCell } from '../src/protocol/messages.js';

const cell = (from: string, to: string, avgRtt: number, jitter = 0, lossPct = 0): MatrixCell => ({
  fromUserId: from,
  toUserId: to,
  avgRtt,
  jitter,
  lossPct,
  samples: 30,
  ageMs: 0,
});

describe('recommendHost', () => {
  it('returns null with fewer than 2 candidates', () => {
    expect(recommendHost([], [])).toBeNull();
    expect(recommendHost([{ userId: 'a', name: 'A' }], [])).toBeNull();
  });

  it('picks the host with the best worst-case ping (min-max)', () => {
    // 3 players. If A hosts, worst peer ping is 120 (C->A). If B hosts, worst
    // is 60. B should win despite A maybe having a good average elsewhere.
    const cands: Candidate[] = [
      { userId: 'a', name: 'Ahmed' },
      { userId: 'b', name: 'Bilal' },
      { userId: 'c', name: 'Chan' },
    ];
    const cells = [
      cell('b', 'a', 40),
      cell('c', 'a', 120), // C reaching A is bad
      cell('a', 'b', 50),
      cell('c', 'b', 60),
      cell('a', 'c', 55),
      cell('b', 'c', 65),
    ];
    const rec = recommendHost(cands, cells);
    expect(rec?.hostUserId).toBe('b');
    expect(rec?.worstCaseRtt).toBe(60);
    expect(rec?.reasons.length).toBeGreaterThan(0);
  });

  it('penalizes packet loss heavily (UDP games have no retransmit)', () => {
    // A has lower raw ping but 5% loss; B is a bit slower but clean.
    // effectiveRtt: A = 30 + 0 + 5*30 = 180; B = 50. B should win.
    const cands: Candidate[] = [
      { userId: 'a', name: 'A' },
      { userId: 'b', name: 'B' },
    ];
    const cells = [cell('b', 'a', 30, 0, 5), cell('a', 'b', 50, 0, 0)];
    const rec = recommendHost(cands, cells);
    expect(rec?.hostUserId).toBe('b');
  });

  it('penalizes jitter (stable beats fast-but-swingy)', () => {
    // A: 40ms but 30ms jitter → eff 100. B: 55ms, 2ms jitter → eff 59. B wins.
    const cands: Candidate[] = [
      { userId: 'a', name: 'A' },
      { userId: 'b', name: 'B' },
    ];
    const cells = [cell('b', 'a', 40, 30, 0), cell('a', 'b', 55, 2, 0)];
    const rec = recommendHost(cands, cells);
    expect(rec?.hostUserId).toBe('b');
  });

  it('prefers a fully-measured host over one with missing links', () => {
    // B has a GREAT known link but a missing one; A is fully measured and fine.
    // Even though B's single known link (10ms) beats A's worst (70ms), we can't
    // trust B because we haven't measured everyone reaching it — so A wins.
    const cands: Candidate[] = [
      { userId: 'a', name: 'A' },
      { userId: 'b', name: 'B' },
      { userId: 'c', name: 'C' },
    ];
    const cells = [
      cell('b', 'a', 50),
      cell('c', 'a', 70), // A fully measured, worst 70
      cell('a', 'b', 10), // B has one great link...
      // no c->b  (B's link from C is MISSING — untrustworthy)
      cell('a', 'c', 200), // C fully measured but terrible (worst 200)
      cell('b', 'c', 210),
    ];
    const rec = recommendHost(cands, cells);
    expect(rec?.hostUserId).toBe('a'); // fully-measured A beats unmeasurable B and bad C
  });

  it('ignores stale measurements beyond maxAge', () => {
    const cands: Candidate[] = [
      { userId: 'a', name: 'A' },
      { userId: 'b', name: 'B' },
    ];
    const stale = { ...cell('b', 'a', 20), ageMs: 999_999 };
    const rec = recommendHost(cands, [stale, cell('a', 'b', 60)], 60_000);
    // b->a is stale so A has no measured inbound link; B is fully measured (a->b).
    expect(rec?.hostUserId).toBe('b');
  });

  it('effectiveRtt weights jitter x2 and loss x30', () => {
    expect(effectiveRtt({ avgRtt: 40, jitter: 5, lossPct: 1 })).toBe(40 + 10 + 30);
  });
});
