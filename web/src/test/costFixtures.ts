import type { TeamCost, TeamCostOutcome, TeamCostWeek } from '../api/outcomes';

/**
 * A TEAM'S COST AS THE PINNED ROUTE ANSWERS IT, for the Cost tile, dialog and list column's
 * mount tests: eight Monday-start UTC weeks, No outcome first, two budgeted outcomes (one shared with
 * another team, so its all-team total is more than this team's part), one unbudgeted outcome.
 *
 * At $60/h: No outcome 4 h = $240, Ship billing 8 h = $480 of $700 all-team against $1,000, Docs
 * refresh 4 h = $240 against $500 ($300 all-team), Research 2 h = $120 with no budget.
 */
export const Weeks = Array.from({ length: 8 }, (_, i) => new Date(Date.UTC(2026, 8, 7 + 7 * i)).toISOString());

export interface CostOptions {
  /** The rate every run was snapshotted at; null is no priced run, and every cost is null. */
  rate?: number | null;
  /** The tenant default rate as set now (`money.agentHourlyRate`); the run rate unless given. No figure uses it. */
  tenantRate?: number | null;
  /** Whether the budgeted outcomes keep their budgets. */
  budgets?: boolean;
  /** Docs refresh's all-team total passes its $500 budget. */
  over?: boolean;
  team?: string;
}

function outcome(id: string | null, name: string, hoursPerWeek: number, billablePerWeek: number, rate: number | null,
  value: number | null, outcomeTotal: number | null, rank: number | null = null): TeamCostOutcome {
  const weekly: TeamCostWeek[] = Weeks.map((weekStart) => ({
    weekStart,
    agentSeconds: hoursPerWeek * 3600,
    billable: billablePerWeek,
    measuredRuns: 2,
    unmeasuredRuns: 0,
    cost: rate === null ? null : hoursPerWeek * rate,
  }));
  const agentSeconds = weekly.reduce((sum, w) => sum + w.agentSeconds, 0);

  return {
    id,
    name,
    value,
    weekly,
    totals: {
      agentSeconds,
      billable: billablePerWeek * 8,
      measuredRuns: 16,
      unmeasuredRuns: 0,
      cost: rate === null ? null : (agentSeconds / 3600) * rate,
      agentRate: rate,
    },
    outcomeTotalCost: id === null || rate === null ? null : outcomeTotal,
    rank: id === null ? null : rank,
  };
}

export function teamCost(options: CostOptions = {}): TeamCost {
  const rate = options.rate === undefined ? 60 : options.rate;
  const budgets = options.budgets ?? true;

  const outcomes = [
    outcome(null, 'No outcome', 0.5, 100_000, rate, null, null),
    outcome('o-billing', 'Ship billing', 1, 250_000, rate, budgets ? 1000 : null, 700, 1),
    outcome('o-docs', 'Docs refresh', 0.5, 50_000, rate, budgets ? 500 : null, options.over ? 650 : 300, 2),
    outcome('o-research', 'Research', 0.25, 25_000, rate, null, 120, 3),
  ];

  const agentSeconds = outcomes.reduce((sum, o) => sum + o.totals.agentSeconds, 0);

  return {
    team: options.team ?? 'alpha',
    money: { currency: 'USD', agentHourlyRate: options.tenantRate === undefined ? rate : options.tenantRate },
    weeks: Weeks,
    outcomes,
    totals: {
      agentSeconds,
      billable: outcomes.reduce((sum, o) => sum + o.totals.billable, 0),
      measuredRuns: 64,
      unmeasuredRuns: 0,
      cost: rate === null ? null : (agentSeconds / 3600) * rate,
      agentRate: rate,
    },
  };
}
