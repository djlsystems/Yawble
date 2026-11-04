import type { CostBucket, CostSummary, CostSummaryOutcome, TeamsCostPeriod } from '../api/outcomes';
import { NoOutcomeColour, outcomeColour } from './outcomeColours';
import { moneyText } from './outcomes';
import { EstimateTitle, agentHours, agentRateLine, legacyRateNote, markedMoney, weekLabel, type CostSeries } from './teamCost';

/**
 * THE TILES ABOVE THE TEAMS LIST, WORDED: Estimated Execution Cost, Where it goes and Right now, each a pure
 * function of what the route answered (`api/outcomes.ts`). There is no outcome budget tile: an outcome's stored
 * `value` is kept and readable, but no screen measures an estimate against it.
 *
 * EVERY FIGURE IS THE ROUTE'S. The only sum here is over what the route answered: the outcomes past the top three.
 *
 * NO PRICE IS NO DOLLARS, NEVER $0: the route's `cost` is null when no run was priced, and every
 * figure then falls back to agent hours or a dash. Nothing here multiplies hours by a rate: each run is
 * priced by the route from its own snapshot, and "at R/h" is the route's weighted agent rate.
 *
 * COLOURS ARE `outcomeColours`'s, by each outcome's global rank, so an outcome is the same colour here
 * as on every list row and the team page. Other outcomes and Unattributed are the neutral gray.
 */

/** The Estimated Execution Cost chart's one series: not an outcome, so no outcome's hue. */
export const SpendColour = 'var(--os-ink-muted)';

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
  /** The footnote when a reconstructed or fallback rate priced part of the figure; null otherwise. */
  footnote: string | null;
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
    value: priced ? markedMoney(moneyText(summary.total.cost, currency), summary.total.legacyRates) : agentHours(summary.total.agentSeconds),
    change,
    direction,
    detail: priced
      ? agentRateLine(summary.total, currency)
      : 'no rate set',
    footnote: priced ? legacyRateNote(summary.total.legacyRates) : null,
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

/**
 * THE FIRST TILE OVER THE TEAMS LIST'S PERIOD (`GET /api/teams/cost/period`): the period's Estimated Execution Cost
 * with its Agent Hours, its change on the same length of time just before, and the period's buckets as columns.
 * Every figure is the route's `total`, `prior` and `totalBuckets`.
 */
export function periodTile(period: Pick<TeamsCostPeriod, 'total' | 'prior' | 'totalBuckets' | 'outcomes'>, currency: string): SpendTile {
  const priced = period.total.cost !== null;
  const now = priced ? period.total.cost! : period.total.agentSeconds;
  const before = priced ? period.prior.cost ?? 0 : period.prior.agentSeconds;

  let change: string | null = null;
  let direction: SpendTile['direction'] = null;
  if (before > 0) {
    const share = (now - before) / before;
    const percent = Math.round(Math.abs(share) * 100);
    direction = percent === 0 ? 'flat' : share > 0 ? 'up' : 'down';
    change = `${direction === 'up' ? '▲ ' : direction === 'down' ? '▼ ' : ''}${percent}% vs prior period`;
  }

  const ran = period.total.agentSeconds > 0 || period.outcomes.length > 0;

  return {
    value: priced
      ? markedMoney(moneyText(period.total.cost, currency), period.total.legacyRates)
      : ran ? 'Estimate unavailable' : '—',
    change,
    direction,
    detail: priced ? agentRateLine(period.total, currency) : agentHours(period.total.agentSeconds),
    footnote: priced ? legacyRateNote(period.total.legacyRates) : null,
    series: [{
      key: 'spend',
      name: priced ? EstimateTitle : 'Agent Hours',
      colour: SpendColour,
      none: false,
      weekly: period.totalBuckets.map((b) => ({
        weekStart: b.start, agentSeconds: b.agentSeconds, cost: b.cost, billable: 0, measuredRuns: 0, unmeasuredRuns: 0,
      })),
    }],
    weeks: period.totalBuckets.map((b) => b.start),
  };
}

/** The tile's aside: how the period is cut. */
export function bucketAside(bucket: CostBucket): string {
  return bucket === 'day' ? 'by day' : 'by month';
}

// ---- Where it goes -----------------------------------------------------------------------------

export interface WhereBar {
  /** The outcome's id, `other` for the outcomes past the top three, or `unattributed` for work serving none. */
  key: string;
  /** The outcome to open, or null for Other outcomes and Unattributed (the Outcomes list). */
  outcome: string | null;
  name: string;
  colour: string;
  amount: string;
  /** The bar's length on the tile's one scale, 0..1. */
  fill: number;
}

/** The figure an outcome is ranked and drawn by: its cost, or its hours without a rate. */
function share(outcome: CostSummaryOutcome, priced: boolean): number {
  return priced ? outcome.cost ?? 0 : outcome.agentSeconds / 3600;
}

/** The bucket for work that serves no outcome - no link, an unlink, a deleted outcome - as the route names it. */
export const UnattributedName = 'Unattributed';

/**
 * THE TOP THREE OUTCOMES BY ESTIMATED COST, THEN OTHER OUTCOMES (every outcome past the top three), THEN
 * UNATTRIBUTED (the work serving none, computed by the route, never an outcome), on ONE scale across the bars.
 * Every run is in exactly one of them, so they add up to the period's figure.
 */
export function whereBars(summary: Pick<CostSummary, 'total' | 'outcomes'>, currency: string): WhereBar[] {
  const priced = summaryPriced(summary);
  const linked = summary.outcomes
    .filter((o) => o.id !== null && share(o, priced) > 0)
    .sort((a, b) => share(b, priced) - share(a, priced));
  const top = linked.slice(0, 3);
  const rest = linked.slice(3);
  const none = summary.outcomes.filter((o) => o.id === null);
  const sumOf = (list: CostSummaryOutcome[]) => ({
    value: list.reduce((sum, o) => sum + share(o, priced), 0),
    seconds: list.reduce((sum, o) => sum + o.agentSeconds, 0),
  });
  const other = sumOf(rest);
  const unattributed = sumOf(none);

  const text = (value: number, seconds: number) => (priced ? moneyText(value, currency) : agentHours(seconds));
  const scale = Math.max(
    ...top.map((o) => share(o, priced)),
    other.value,
    unattributed.value,
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
  }));

  const grouped = (key: string, name: string, part: { value: number; seconds: number }): WhereBar => ({
    key,
    outcome: null,
    name,
    colour: NoOutcomeColour,
    amount: text(part.value, part.seconds),
    fill: on(part.value),
  });

  if (other.value > 0) bars.push(grouped('other', 'Other outcomes', other));
  if (unattributed.value > 0) bars.push(grouped('unattributed', none[0]?.name || UnattributedName, unattributed));

  return bars;
}
