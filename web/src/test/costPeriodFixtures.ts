import type { CostPeriodTeam, CostPeriodTeamOutcome, CostSummaryOutcome, TeamsCostPeriod } from '../api/outcomes';

/**
 * EVERY TEAM'S FIGURES OVER THE TEAMS LIST'S PERIOD AS THE PINNED ROUTE ANSWERS THEM (`GET /api/teams/cost/period`),
 * for the Teams list's mount tests: three days, by day.
 *
 * At $60/h a day: Alpha's Unattributed 1 h, Ship billing 2 h, Docs refresh 1 h and Research 0.5 h = $270 a day,
 * $810 and 13.5 Agent Hours over the period; Beta twice that ($1,620, 27 h); Gamma ran nothing. The instance:
 * $2,430 and 40.5 Agent Hours, $810 a day; the period before: $1,215.
 */
export const PeriodBuckets = ['2026-10-01', '2026-10-02', '2026-10-03'];

export interface PeriodOptions {
  /** The rate every run was snapshotted at; null is no priced run, and every cost is null. */
  rate?: number | null;
  /** Alpha's figure was priced in part from a reconstructed rate. */
  legacy?: boolean;
  bucket?: 'day' | 'month';
}

const Outcomes: Array<[string | null, string, number | null, number]> = [
  [null, 'Unattributed', null, 1],
  ['o-billing', 'Ship billing', 1, 2],
  ['o-docs', 'Docs refresh', 2, 1],
  ['o-research', 'Research', 3, 0.5],
];

function team(name: string, by: number, rate: number | null, legacy: boolean): CostPeriodTeam {
  const outcomes: CostPeriodTeamOutcome[] = Outcomes.map(([id, label, rank, hours]) => {
    const seconds = hours * by * 3600;
    const cost = rate === null ? null : hours * by * rate;
    return {
      id,
      name: label,
      rank,
      totals: { agentSeconds: seconds * 3, cost: cost === null ? null : cost * 3, agentRate: rate, legacyRates: [] },
      buckets: PeriodBuckets.map((start) => ({ start, agentSeconds: seconds, cost })),
    };
  });
  const seconds = outcomes.reduce((sum, o) => sum + o.totals.agentSeconds, 0);
  const daySeconds = seconds / 3;
  return {
    team: name,
    totals: {
      agentSeconds: seconds,
      cost: rate === null ? null : (seconds / 3600) * rate,
      agentRate: rate,
      legacyRates: legacy ? ['reconstructed'] : [],
    },
    buckets: PeriodBuckets.map((start) => ({ start, agentSeconds: daySeconds, cost: rate === null ? null : (daySeconds / 3600) * rate })),
    outcomes,
  };
}

export function teamsCostPeriod(options: PeriodOptions = {}): TeamsCostPeriod {
  const rate = options.rate === undefined ? 60 : options.rate;
  const alpha = team('alpha', 1, rate, options.legacy ?? false);
  const beta = team('beta', 2, rate, false);
  const gamma: CostPeriodTeam = { team: 'gamma', totals: { agentSeconds: 0, cost: null, agentRate: null, legacyRates: [] }, buckets: PeriodBuckets.map((start) => ({ start, agentSeconds: 0, cost: null })), outcomes: [] };
  const seconds = alpha.totals.agentSeconds + beta.totals.agentSeconds;

  const outcomes: CostSummaryOutcome[] = Outcomes.map(([id, name, rank, hours]) => ({
    id,
    name,
    rank,
    agentSeconds: hours * 3 * 3 * 3600,
    cost: rate === null ? null : hours * 3 * 3 * rate,
    budget: id === 'o-billing' ? 2000 : null,
    outcomeTotalCost: rate === null || id === null ? null : hours * 3 * 3 * rate,
    legacyRates: [],
  }));

  return {
    money: { currency: 'USD', agentHourlyRate: rate },
    period: { from: '2026-10-01', to: '2026-10-04', bucket: options.bucket ?? 'day' },
    buckets: PeriodBuckets,
    teams: [alpha, beta, gamma],
    total: { agentSeconds: seconds, cost: rate === null ? null : (seconds / 3600) * rate, agentRate: rate, legacyRates: [] },
    totalBuckets: PeriodBuckets.map((start) => ({ start, agentSeconds: seconds / 3, cost: rate === null ? null : (seconds / 3 / 3600) * rate })),
    prior: { agentSeconds: seconds / 2, cost: rate === null ? null : (seconds / 2 / 3600) * rate, agentRate: rate },
    outcomes,
  };
}
