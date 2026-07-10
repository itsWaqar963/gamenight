/**
 * Host recommendation engine (SDD §22). PURE function — no I/O, no clock,
 * no randomness — so it's exhaustively unit-testable and its output is a
 * deterministic function of its input. This is where the project's core
 * value lives: turning a mesh of measurements into "who should host, and why."
 *
 * The problem is min-max: the best host is NOT the one with the best average
 * ping, but the one whose WORST peer still gets an acceptable ping. Hosting a
 * P2P game, every client talks to the host, so the host's worst link decides
 * whether anyone rubber-bands. (SDD §22, and the reasoning we discussed in
 * discovery: "the guy with good ping" is often a bad host because of upload.)
 */
import type { HostRecommendation, MatrixCell } from '../../protocol/messages.js';

export type Candidate = { userId: string; name: string | null };

// Quality penalty: jitter and loss are weighted into an "effective RTT" so a
// stable 50ms link beats a 40ms link that swings or drops packets. Loss is
// brutal for UDP games (no retransmit), so it's penalized hard.
export function effectiveRtt(cell: { avgRtt: number; jitter: number; lossPct: number }): number {
  return cell.avgRtt + cell.jitter * 2 + cell.lossPct * 30;
}

/**
 * @param candidates online players who could host (have a Radmin IP)
 * @param cells the measured mesh (directed edges)
 * @param maxAgeMs measurements older than this are ignored as stale
 */
export function recommendHost(
  candidates: Candidate[],
  cells: MatrixCell[],
  maxAgeMs = 60_000,
): HostRecommendation {
  if (candidates.length < 2) return null; // need at least 2 players to have a "host" question

  // Index fresh cells by "from->to" for O(1) lookup.
  const edge = new Map<string, MatrixCell>();
  for (const c of cells) {
    if (c.ageMs > maxAgeMs) continue;
    edge.set(`${c.fromUserId}->${c.toUserId}`, c);
  }

  type Eval = {
    host: Candidate;
    worstEff: number;
    worstPeer: string | null;
    worstRtt: number;
    missing: number;
  };
  const evals: Eval[] = [];

  for (const host of candidates) {
    let worstEff = 0;
    let worstRtt = 0;
    let worstPeer: string | null = null;
    let missing = 0;
    for (const peer of candidates) {
      if (peer.userId === host.userId) continue;
      // A hosts P2P: every OTHER player connects TO the host. We use the
      // peer->host measurement (what the client experiences reaching the host).
      const c = edge.get(`${peer.userId}->${host.userId}`);
      if (!c) {
        missing++;
        continue;
      }
      const eff = effectiveRtt(c);
      if (eff > worstEff) {
        worstEff = eff;
        worstRtt = c.avgRtt;
        worstPeer = peer.userId;
      }
    }
    evals.push({ host, worstEff, worstPeer, worstRtt, missing });
  }

  // Prefer hosts we could fully measure; among those, the smallest worst-case.
  const fullyMeasured = evals.filter((e) => e.missing === 0);
  const pool = fullyMeasured.length > 0 ? fullyMeasured : evals;
  pool.sort((a, b) => a.worstEff - b.worstEff);
  const best = pool[0];
  if (!best) return null;

  const reasons: string[] = [];
  if (best.worstPeer) {
    reasons.push(
      `Worst-case ping to ${best.host.name ?? 'host'} is ${Math.round(best.worstRtt)}ms.`,
    );
  }
  // Show the contrast with the runner-up, so the pick is defensible, not magic.
  const runnerUp = pool[1];
  if (runnerUp && runnerUp.worstPeer) {
    reasons.push(
      `If ${runnerUp.host.name ?? 'someone else'} hosted, worst-case would be ${Math.round(runnerUp.worstRtt)}ms.`,
    );
  }
  if (best.missing > 0) {
    reasons.push(`Note: ${best.missing} link(s) not yet measured — recommendation may improve.`);
  }

  return {
    hostUserId: best.host.userId,
    hostName: best.host.name,
    worstCaseRtt: best.worstPeer ? Math.round(best.worstRtt) : null,
    reasons,
  };
}
