import type { TeamCostMoney, TeamCostOutcome, TeamCostTotals, TeamCostWeek } from '../api/outcomes'
import { outcomeColour } from './outcomeColours'
import { moneyText } from './outcomes'

/**
 * A TEAM'S ESTIMATED EXECUTION COST, BY OUTCOME, WORDED AND STACKED: what the team page's tile, its
 * dialog and the teams list's column read. Pure functions of the cost route's answer (`api/outcomes.ts`).
 *
 * EVERY FIGURE IS THE ROUTE'S, and nothing here adds figures up or measures them against an outcome's stored
 * value (there are no outcome budgets on any screen). Nothing here multiplies hours by a rate: the route prices
 * each run from the snapshot taken at its start, and the "at R/h" line is the route's own weighted agent rate (`totals.agentRate`), never the tenant setting.
 *
 * NO PRICE IS NO DOLLARS, NEVER $0. The route's `cost` is null when no run in the figure was priced;
 * every figure then falls back to agent hours, and nothing here prints a money amount for it.
 */

/**
 * WHAT THESE FIGURES ARE CALLED: each run's hours times the rate it started with is an ESTIMATE of
 * what the execution cost, never money paid or owed. No cost surface titles it Spend, Spent or Cost alone.
 */
export const EstimateTitle = 'Estimated Execution Cost'

/** One stacked series: an outcome (or Unattributed), its colour and its eight weeks. */
export interface CostSeries {
  /** The outcome's id, or `none` for Unattributed. */
  key: string
  name: string
  colour: string
  none: boolean
  weekly: TeamCostWeek[]
}

/** Whether any run in the figure was priced: then figures are money, otherwise agent hours. */
export function priced(cost: { totals: Pick<TeamCostTotals, 'cost'> }): boolean {
  return cost.totals.cost !== null
}

/** Whether any team on the list had a priced run: the whole list is then drawn in money, one unit for every row. */
export function anyPriced(answer: { teams: ReadonlyArray<{ totals: Pick<TeamCostTotals, 'cost'> }> }): boolean {
  return answer.teams.some((team) => priced(team))
}

/**
 * A FIGURE PRICED IN PART FROM THE RATE'S HISTORY SAYS SO: the route's `legacyRates` as a footnote's
 * words - `* Includes reconstructed rates`, `* Includes a fallback rate`, or both - and null when the
 * figure was priced from snapshots alone (or the route did not say).
 */
export function legacyRateNote(rates: readonly string[] | null | undefined): string | null {
  const reconstructed = rates?.includes('reconstructed') ?? false
  const fallback = rates?.includes('fallback-at-upgrade') ?? false
  if (reconstructed && fallback) return '* Includes reconstructed and fallback rates'
  if (reconstructed) return '* Includes reconstructed rates'
  if (fallback) return '* Includes a fallback rate'
  return null
}

/** A money figure with its footnote marker: `$90*` when a reconstructed or fallback rate priced part of it. */
export function markedMoney(text: string, rates: readonly string[] | null | undefined): string {
  return legacyRateNote(rates) === null ? text : `${text}*`
}

/**
 * `12.5 Agent Hours at $60/h`: the hours and the route's weighted agent rate. With no priced agent time
 * - only priced plugin time, say - the rate is left out rather than invented.
 */
export function agentRateLine(totals: Pick<TeamCostTotals, 'agentSeconds' | 'agentRate'>, currency: string): string {
  const rate = totals.agentRate ?? null
  return rate === null
    ? `${hoursFigure(totals.agentSeconds)} Agent Hours`
    : `${hoursFigure(totals.agentSeconds)} Agent Hours at ${moneyText(rate, currency)}/h`
}

/**
 * THE FIXED ORDER: Unattributed first (the bottom of each stack, neutral gray), then each outcome in the
 * route's order. Each colour comes from the shared `outcomeColours` module (the outcome's global rank),
 * never from its position among whatever one team lists: one outcome is one colour on every teams-list
 * row, the tile and the dialog.
 */
export function costSeries(outcomes: readonly TeamCostOutcome[]): CostSeries[] {
  const none = outcomes.filter((o) => o.id === null)
  const linked = outcomes.filter((o) => o.id !== null)

  return [
    ...none.map((o) => ({ key: 'none', name: o.name || 'Unattributed', colour: outcomeColour(o), none: true, weekly: o.weekly })),
    ...linked.map((o) => ({
      key: o.id!,
      name: o.name,
      colour: outcomeColour(o),
      none: false,
      weekly: o.weekly,
    })),
  ]
}

/** A week's figure for the chart: its cost when priced, otherwise its agent hours. */
export function weekValue(week: Pick<TeamCostWeek, 'cost' | 'agentSeconds'>, money: boolean): number {
  return money ? week.cost ?? 0 : week.agentSeconds / 3600
}

/**
 * THE COST DIALOG'S RATE LINE: the tenant default as set now, named as the default - the rate a new agent
 * run inherits - because the figures under it were each priced at the rate their run started with.
 */
export function defaultRateText(money: TeamCostMoney): string {
  return money.agentHourlyRate === null
    ? `No default agent rate set (${money.currency})`
    : `Default agent rate ${moneyText(money.agentHourlyRate, money.currency)}/h (${money.currency})`
}

/** Agent hours, one decimal: `12.5`. */
export function hoursFigure(seconds: number): string {
  const hours = seconds / 3600
  return hours < 100 ? hours.toFixed(1) : Math.round(hours).toLocaleString()
}

/** `12.5 Agent Hours`. */
export function agentHours(seconds: number): string {
  return `${hoursFigure(seconds)} Agent Hours`
}

/** A chart value as its axis or tooltip says it: money in the currency, or hours. */
export function valueText(value: number, money: boolean, currency: string): string {
  return money ? moneyText(value, currency) : `${value < 100 ? value.toFixed(1) : Math.round(value).toLocaleString()} h`
}

/** A figure that is money when priced and hours when not: never $0 for an unpriced one. Money carries its
 * footnote marker when a reconstructed or fallback rate priced part of it. */
export function spendText(totals: Pick<TeamCostTotals, 'cost' | 'agentSeconds' | 'legacyRates'>, currency: string): string {
  return totals.cost === null ? agentHours(totals.agentSeconds) : markedMoney(moneyText(totals.cost, currency), totals.legacyRates)
}

/** Cost per million billable tokens, or null when unpriced or no tokens were measured. */
export function perMillion(totals: Pick<TeamCostTotals, 'cost' | 'billable'>): number | null {
  if (totals.cost === null || totals.billable <= 0) return null
  return totals.cost / (totals.billable / 1_000_000)
}

/** `$4.20`, or `—` when there is no such figure. */
export function perMillionText(totals: Pick<TeamCostTotals, 'cost' | 'billable'>, currency: string): string {
  const figure = perMillion(totals)
  return figure === null ? '—' : moneyText(figure, currency)
}

/** A week's short label: `Sep 29`. The weeks are UTC Mondays, so they are read in UTC. */
export function weekLabel(weekStart: string): string {
  return new Date(weekStart).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' })
}

/** A tidy axis top and its ticks: 0 and three or four steps of 1, 2 or 5 times a power of ten. */
export function axisTicks(max: number): number[] {
  if (!(max > 0)) return [0, 1]
  const rough = max / 4
  const power = 10 ** Math.floor(Math.log10(rough))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * power).find((s) => s >= rough) ?? 10 * power
  const ticks: number[] = []
  for (let tick = 0; tick < max + step * 0.999; tick += step) ticks.push(Number(tick.toPrecision(12)))
  return ticks
}
