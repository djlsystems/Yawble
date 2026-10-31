import type { CostSummary, CostSummaryOutcome } from '../api/outcomes';

/**
 * THE INSTANCE'S COST SUMMARY AS THE PINNED ROUTE ANSWERS IT, for the teams summary's mount tests.
 *
 * At $60/h over the eight weeks: Ship billing 20 h = $1,200 (all-team $1,200) against $2,000, Docs
 * refresh 10 h = $600 against $500 when over (else $400 and 6.67 h), Research 5 h = $300 with no
 * budget, Ops 2 h = $120 with no budget, No outcome 3 h = $180. The prior eight weeks: 30 h = $1,800.
 */
export interface SummaryOptions {
  rate?: number | null;
  budgets?: boolean;
  over?: boolean;
  waiting?: number;
}

export function costSummary(options: SummaryOptions = {}): CostSummary {
  const rate = options.rate === undefined ? 60 : options.rate;
  const budgets = options.budgets ?? true;
  const docsHours = options.over ? 10 : 400 / 60;

  const outcome = (id: string | null, name: string, rank: number | null, hours: number, budget: number | null): CostSummaryOutcome => ({
    id,
    name,
    rank,
    agentSeconds: hours * 3600,
    cost: rate === null ? null : hours * rate,
    budget: budgets ? budget : null,
    spentTotal: rate === null || id === null ? null : hours * rate,
  });

  const outcomes = [
    outcome(null, 'No outcome', null, 3, null),
    outcome('o-billing', 'Ship billing', 1, 20, 2000),
    outcome('o-docs', 'Docs refresh', 2, docsHours, 500),
    outcome('o-research', 'Research', 3, 5, null),
    outcome('o-ops', 'Ops', 4, 2, null),
  ];
  const seconds = outcomes.reduce((sum, o) => sum + o.agentSeconds, 0);
  const weeks = Array.from({ length: 8 }, (_, i) => {
    const weekSeconds = seconds / 8;
    return {
      weekStart: new Date(Date.UTC(2026, 8, 7 + 7 * i)).toISOString().slice(0, 10),
      agentSeconds: weekSeconds,
      cost: rate === null ? null : (weekSeconds / 3600) * rate,
    };
  });

  return {
    rate,
    weeks,
    periodStart: '2026-09-07',
    periodEnd: '2026-11-01',
    total: { agentSeconds: seconds, cost: rate === null ? null : (seconds / 3600) * rate },
    prior: { agentSeconds: 30 * 3600, cost: rate === null ? null : 30 * rate },
    outcomes,
    rightNow: { openWorkflows: 4, membersRunning: 2, waitingOnYou: options.waiting ?? 3 },
  };
}
