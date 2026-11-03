import type { CostSummary, CostSummaryOutcome } from '../api/outcomes';
import { NoOutcomeColour, outcomeColour } from './outcomeColours';
import { moneyText } from './outcomes';
import { EstimateTitle, agentHours, agentRateLine, percentText, weekLabel, type CostSeries } from './teamCost';

/**
 * THE FOUR TILES ABOVE THE TEAMS LIST, WORDED: Estimated Execution Cost, Budgets, Where it goes and Right now, each a
 * pure function of `GET /api/teams/cost/summary` (`api/outcomes.ts`).
 *
 * EVERY FIGURE IS THE ROUTE'S. The only sums here are over what the route answered: the budgeted
 * outcomes' budgets and estimated cost, the estimate with no budget, and the outcomes past the top three.
 *
 * NO PRICE IS NO DOLLARS, NEVER $0: the route's `cost` is null when no run was priced, and every
 * figure then falls back to agent hours or a dash. Nothing here multiplies hours by a rate: each run is
 * priced by the route from its own snapshot, and "at R/h" is the route's weighted agent rate.
 *
 * COLOURS ARE `outcomeColours`'s, by each outcome's global rank, so an outcome is the same colour here
 * as on every list row and the team page. Other / no outcome is the neutral gray.
 */

/** The Estimated Execution Cost chart's one series: not an outcome, so no outcome's hue. */
export const SpendColour = 'var(--os-ink-muted)';

export const BudgetsNoRateNote = 'Set an agent hourly rate in Outcomes to measure Estimated Execution Cost against budgets.';
export const NoBudgetsNote = 'Give an outcome a value to track it here.';

/** Whether any run behind the summary was priced: in the weeks, or in an outcome's all-time total. */
export function summaryPriced(summary: Pick<CostSummary, 'total' | 'outcomes'>): boolean {
  return summary.total.cost !== null || summary.outcomes.some((o) => o.cost !== null || o.outcomeTotalCost !== null);
}

/** A day of the period, `Sep 7`, read in UTC as the route's dates are. */
function dayLabel(day: string): string {
  return weekLabel(`${day}T00:00:00Z`);
}

/** `Last 8 weeks · Sep 7 – Nov 1`. */
export function periodLine(summary: Pick<CostSummary, 'periodStart' | 'periodEnd'>): string {
  return `Last 8 weeks · ${dayLabel(summary.periodStart)} – ${dayLabel(summary.periodEnd)}`;
}

// ---- Estimated Execution Cost -------------------------------------------------------------------------------------

export interface SpendTile {
  value: string;
  /** `▲ 12% vs prior 8 wk`, or null when the prior weeks had nothing to compare with. */
  change: string | null;
  /** Up, down or flat, for the arrow's reader; null with no change line. */
  direction: 'up' | 'down' | 'flat' | null;
  detail: string;
  /** The weekly chart, as one series. */
  series: CostSeries[];
  weeks: string[];
}

export function spendTile(summary: CostSummary, currency: string): SpendTile {
  const priced = summary.total.cost !== null;
  const now = priced ? summary.total.cost! : summary.total.agentSeconds;
  const before = priced ? summary.prior.cost ?? 0 : summary.prior.agentSeconds;

  let change: string | null = null;
  let direction: SpendTile['direction'] = null;
  if (before > 0) {
    const share = (now - before) / before;
    const percent = Math.round(Math.abs(share) * 100);
    direction = percent === 0 ? 'flat' : share > 0 ? 'up' : 'down';
    change = `${direction === 'up' ? '▲ ' : direction === 'down' ? '▼ ' : ''}${percent}% vs prior 8 wk`;
  }

  return {
    value: priced ? moneyText(summary.total.cost, currency) : agentHours(summary.total.agentSeconds),
    change,
    direction,
    detail: priced
      ? agentRateLine(summary.total, currency)
      : 'no rate set',
    series: [{
      key: 'spend',
      name: priced ? EstimateTitle : 'Agent Hours',
      colour: SpendColour,
      none: false,
      weekly: summary.weeks.map((w) => ({ ...w, billable: 0, measuredRuns: 0, unmeasuredRuns: 0 })),
    }],
    weeks: summary.weeks.map((w) => w.weekStart),
  };
}

// ---- Budgets -----------------------------------------------------------------------------------

/** What an outcome's budget is measured against: its estimated cost across every team. */
function spent(outcome: CostSummaryOutcome): number | null {
  return outcome.outcomeTotalCost ?? outcome.cost;
}

/** Whether an outcome's estimated cost across every team has passed its budget. */
export function summaryOver(outcome: CostSummaryOutcome): boolean {
  const total = spent(outcome);
  return outcome.id !== null && outcome.budget !== null && total !== null && total > outcome.budget;
}

/** The outcomes carrying a budget. No outcome never does. */
export function summaryBudgeted(outcomes: readonly CostSummaryOutcome[]): CostSummaryOutcome[] {
  return outcomes.filter((o) => o.id !== null && o.budget !== null);
}

export interface BudgetsTile {
  state: 'budgets' | 'over' | 'no-budgets' | 'no-rate';
  /** `3 outcomes`: how many carry a budget. */
  count: string;
  value: string;
  /** `$1,200 of $2,000`, with budgets. */
  line: string | null;
  /** The bar's fill, 0..1, with budgets. */
  bar: number | null;
  /** `All inside budget`, or `⚠ Docs refresh over` in the critical colour. */
  status: string | null;
  /** `· $360 unbudgeted`, when unbudgeted outcomes or no outcome spent. */
  unbudgeted: string | null;
  note: string | null;
}

export function budgetsTile(summary: CostSummary, currency: string): BudgetsTile {
  const withBudget = summaryBudgeted(summary.outcomes);
  const count = `${withBudget.length} outcome${withBudget.length === 1 ? '' : 's'}`;
  const blank = { count, line: null, bar: null, status: null, unbudgeted: null };

  if (!summaryPriced(summary)) return { ...blank, state: 'no-rate', value: '—', note: BudgetsNoRateNote };
  if (withBudget.length === 0) return { ...blank, state: 'no-budgets', value: 'None set', note: NoBudgetsNote };

  const spentSum = withBudget.reduce((sum, o) => sum + (spent(o) ?? 0), 0);
  const budgetSum = withBudget.reduce((sum, o) => sum + o.budget!, 0);
  const share = budgetSum > 0 ? spentSum / budgetSum : spentSum > 0 ? Infinity : 0;
  const over = withBudget.filter(summaryOver);
  const unbudgeted = summary.outcomes
    .filter((o) => o.id === null || o.budget === null)
    .reduce((sum, o) => sum + (o.cost ?? 0), 0);

  return {
    state: over.length > 0 ? 'over' : 'budgets',
    count,
    value: percentText(share),
    line: `${moneyText(spentSum, currency)} of ${moneyText(budgetSum, currency)}`,
    bar: Math.min(1, Number.isFinite(share) ? share : 1),
    status: over.length > 0 ? `⚠ ${over.map((o) => o.name).join(', ')} over` : 'All inside budget',
    unbudgeted: unbudgeted > 0 ? `· ${moneyText(unbudgeted, currency)} unbudgeted` : null,
    note: null,
  };
}

// ---- Where it goes -----------------------------------------------------------------------------

export interface WhereBar {
  /** The outcome's id, or `other` for Other / no outcome. */
  key: string;
  /** The outcome to open, or null for Other / no outcome (the Outcomes list). */
  outcome: string | null;
  name: string;
  colour: string;
  amount: string;
  /** The bar's length on the tile's one scale, 0..1. */
  fill: number;
  /** Where its budget falls on the same scale, 0..1, or null without one. */
  tick: number | null;
  over: boolean;
}

/** The figure an outcome is ranked and drawn by: its cost, or its hours without a rate. */
function share(outcome: CostSummaryOutcome, priced: boolean): number {
  return priced ? outcome.cost ?? 0 : outcome.agentSeconds / 3600;
}

/**
 * THE TOP THREE OUTCOMES BY ESTIMATED COST, THEN OTHER / NO OUTCOME (every other outcome plus unlinked work),
 * on ONE scale across the four bars that a budget tick also fits on.
 */
export function whereBars(summary: CostSummary, currency: string): WhereBar[] {
  const priced = summaryPriced(summary);
  const linked = summary.outcomes
    .filter((o) => o.id !== null && share(o, priced) > 0)
    .sort((a, b) => share(b, priced) - share(a, priced));
  const top = linked.slice(0, 3);
  const rest = [...linked.slice(3), ...summary.outcomes.filter((o) => o.id === null)];
  const restValue = rest.reduce((sum, o) => sum + share(o, priced), 0);
  const restSeconds = rest.reduce((sum, o) => sum + o.agentSeconds, 0);

  const text = (value: number, seconds: number) => (priced ? moneyText(value, currency) : agentHours(seconds));
  const budgetOf = (o: CostSummaryOutcome) => (priced && o.budget !== null ? o.budget : null);
  const scale = Math.max(
    ...top.map((o) => Math.max(share(o, priced), budgetOf(o) ?? 0)),
    restValue,
    0,
  );
  const on = (value: number) => (scale > 0 ? Math.min(1, value / scale) : 0);

  const bars: WhereBar[] = top.map((o) => ({
    key: o.id!,
    outcome: o.id,
    name: o.name,
    colour: outcomeColour(o),
    amount: text(share(o, priced), o.agentSeconds),
    fill: on(share(o, priced)),
    tick: budgetOf(o) === null ? null : on(budgetOf(o)!),
    over: priced && summaryOver(o),
  }));

  if (restValue > 0) {
    bars.push({
      key: 'other',
      outcome: null,
      name: 'Other / no outcome',
      colour: NoOutcomeColour,
      amount: text(restValue, restSeconds),
      fill: on(restValue),
      tick: null,
      over: false,
    });
  }

  return bars;
}
