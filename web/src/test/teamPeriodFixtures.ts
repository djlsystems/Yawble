import type { TeamCostPeriod, TeamCostWorkflows, TeamPeriodFigures } from '../api/outcomes';

/**
 * ONE TEAM'S FIGURES OVER A PERIOD AS THE PINNED ROUTE ANSWERS THEM (`GET /api/teams/{team}/cost/period`), for the team
 * page's mount tests: three days, by day (or one month), at $60/h.
 *
 * Unattributed: Dev 3 Agent Hours ($180, 2 executions) and the plugin Tool's own $30 (1 execution); the plugin Bare
 * ran once with no rate (not priced). Ship billing: Ana 6 h ($360, 3 executions). Docs refresh: Dev 3 h ($180, 1).
 * The team: $750, 12 Agent Hours at $60/h, 8 executions, 4 active members, 4,500 billable tokens. Every breakdown adds
 * up to it.
 */
export const TeamBuckets = ['2026-10-01', '2026-10-02', '2026-10-03'];

export interface TeamPeriodOptions {
  /** No run was priced: every cost is null. */
  unpriced?: boolean;
  /** The team ran nothing in the period. */
  empty?: boolean;
  /** Part of the figure was priced from a reconstructed rate. */
  legacy?: boolean;
  /** Ship billing's all-time total is over its budget. */
  over?: boolean;
  bucket?: 'day' | 'month';
  team?: string;
}

function figures(agentHours: number, money: number | null, executions: number, billable: number, legacy: string[] = []): TeamPeriodFigures {
  return {
    agentSeconds: agentHours * 3600,
    cost: money,
    agentRate: money === null || agentHours === 0 ? null : 60,
    legacyRates: money === null ? [] : legacy,
    executions,
    billable,
    measuredRuns: executions,
    unmeasuredRuns: 0,
  };
}

export function teamCostPeriod(options: TeamPeriodOptions = {}): TeamCostPeriod {
  const priced = !options.unpriced;
  const m = (amount: number) => (priced ? amount : null);
  const legacy = options.legacy ? ['reconstructed'] : [];
  const buckets = options.bucket === 'month' ? ['2026-10-01'] : TeamBuckets;
  const share = 1 / buckets.length;
  const series = (hours: number, money: number) =>
    buckets.map((start) => ({ start, agentSeconds: hours * 3600 * share, cost: m(money * share) }));

  if (options.empty) {
    return {
      team: options.team ?? 'alpha',
      money: { currency: 'USD', agentHourlyRate: 60 },
      period: { from: '2026-10-01', to: '2026-10-04', bucket: options.bucket ?? 'day' },
      buckets,
      totals: { agentSeconds: 0, cost: null, agentRate: null, legacyRates: [], billable: 0, measuredRuns: 0, unmeasuredRuns: 0, executions: 0, activeMembers: 0 },
      totalBuckets: buckets.map((start) => ({ start, agentSeconds: 0, cost: null })),
      outcomes: [],
      members: [],
      workflows: [],
    };
  }

  return {
    team: options.team ?? 'alpha',
    money: { currency: 'USD', agentHourlyRate: 60 },
    period: { from: '2026-10-01', to: '2026-10-04', bucket: options.bucket ?? 'day' },
    buckets,
    totals: {
      agentSeconds: 12 * 3600, cost: m(750), agentRate: priced ? 60 : null, legacyRates: priced ? legacy : [],
      billable: 4500, measuredRuns: 8, unmeasuredRuns: 0, executions: 8, activeMembers: 4,
    },
    totalBuckets: series(12, 750),
    outcomes: [
      {
        id: null, name: 'Unattributed', rank: null,
        totals: { agentSeconds: 3 * 3600, cost: m(210), agentRate: priced ? 60 : null, legacyRates: priced ? legacy : [] },
        buckets: series(3, 210), value: null, outcomeTotalCost: null, figures: figures(3, m(210), 4, 1000, legacy),
      },
      {
        id: 'o-billing', name: 'Ship billing', rank: 1,
        totals: { agentSeconds: 6 * 3600, cost: m(360), agentRate: priced ? 60 : null, legacyRates: [] },
        buckets: series(6, 360), value: 2000, outcomeTotalCost: m(options.over ? 2400 : 900), figures: figures(6, m(360), 3, 3000),
      },
      {
        id: 'o-docs', name: 'Docs refresh', rank: 2,
        totals: { agentSeconds: 3 * 3600, cost: m(180), agentRate: priced ? 60 : null, legacyRates: [] },
        buckets: series(3, 180), value: null, outcomeTotalCost: m(180), figures: figures(3, m(180), 1, 500),
      },
    ],
    members: [
      { member: 'Ana', kind: 'agent', figures: figures(6, m(360), 3, 3000) },
      { member: 'Dev', kind: 'agent', figures: figures(6, m(360), 3, 1500, legacy) },
      { member: 'Bare', kind: 'plugin', figures: figures(0, null, 1, 0), unpriced: 'not-priced' },
      { member: 'Tool', kind: 'plugin', figures: figures(0, m(30), 1, 0), ...(priced ? {} : { unpriced: 'not-priced' as const }) },
    ],
    workflows: [
      { workflow: 42, figures: figures(6, m(360), 3, 3000) },
      { workflow: 41, figures: figures(3, m(180), 2, 1000, legacy) },
      { workflow: 40, figures: figures(3, m(180), 1, 500) },
      { workflow: 39, figures: figures(0, m(30), 2, 0) },
    ],
  };
}

/** `GET /api/teams/{team}/cost/workflows` for the same team, all time: #42 and #41 as above, #40 a little more. */
export function teamCostWorkflows(options: { unpriced?: boolean } = {}): TeamCostWorkflows {
  const m = (amount: number) => (options.unpriced ? null : amount);
  return {
    team: 'alpha',
    money: { currency: 'USD', agentHourlyRate: 60 },
    workflows: [
      { workflow: 42, figures: figures(6, m(360), 3, 3000) },
      { workflow: 41, figures: figures(1.5, m(90), 1, 1000, ['reconstructed']) },
      { workflow: 40, figures: figures(4, m(240), 2, 500) },
    ],
  };
}
