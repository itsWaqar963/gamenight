/**
 * Live metric matrix (SDD §20). In-memory, like presence — raw per-probe
 * samples are NEVER stored; agents pre-aggregate into 30s summaries, and we
 * keep only the latest summary per directed pair. This is the difference
 * between a healthy free-tier server and one drowning in telemetry writes.
 *
 * Directed edges: from->to. We store what the FROM agent measured reaching TO.
 */
import type { MatrixCell, MetricSample } from '../../protocol/messages.js';

type Edge = {
  avgRtt: number;
  jitter: number;
  lossPct: number;
  samples: number;
  updatedAt: number; // epoch ms — for staleness/age
};

export class MetricStore {
  // key: `${fromUserId}->${toUserId}`
  private edges = new Map<string, Edge>();

  ingest(fromUserId: string, samples: MetricSample[]): void {
    const now = Date.now();
    for (const s of samples) {
      this.edges.set(`${fromUserId}->${s.peerUserId}`, {
        avgRtt: s.avgRtt,
        jitter: s.jitter,
        lossPct: s.lossPct,
        samples: s.samples,
        updatedAt: now,
      });
    }
  }

  /** Drop everything involving a user who went offline. */
  dropUser(userId: string): void {
    for (const key of this.edges.keys()) {
      if (key.startsWith(`${userId}->`) || key.endsWith(`->${userId}`)) this.edges.delete(key);
    }
  }

  cells(): MatrixCell[] {
    const now = Date.now();
    const out: MatrixCell[] = [];
    for (const [key, e] of this.edges) {
      const [fromUserId, toUserId] = key.split('->') as [string, string];
      out.push({
        fromUserId,
        toUserId,
        avgRtt: e.avgRtt,
        jitter: e.jitter,
        lossPct: e.lossPct,
        samples: e.samples,
        ageMs: now - e.updatedAt,
      });
    }
    return out;
  }
}
