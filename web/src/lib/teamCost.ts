import type { TeamCost, TeamCostMoney, TeamCostOutcome, TeamCostTotals, TeamCostWeek } from '../api/outcomes'
import { outcomeColour } from './outcomeColours'
import { moneyText } from './outcomes'

/**
 * A TEAM'S ESTIMATED EXECUTION COST, BY OUTCOME, WORDED AND STACKED: what the team page's tile, its
 * dialog and the teams list's column read. Pure functions of the cost route's answer (`api/outcomes.ts`).
 *
 * EVERY FIGURE IS THE ROUTE'S, and the only sums here are over what the route answered: the budgets
 * and their outcomes' all-team totals, and the team's estimated cost of unbudgeted work. Nothing here
 * multiplies hours by a rate: the route prices each run from the snapshot taken at its start, and the
 * "at R/h" line is the route's own weighted agent rate (`totals.agentRate`), never the tenant setting.
 *
 * NO PRICE IS NO DOLLARS, NEVER $0. The route's `cost` is null when no run in the figure was priced;
 * every figure then falls back to agent hours, and nothing here prints a money amount for it.
 */

/**
 * WHAT THESE FIGURES ARE CALLED: each run's hours times the rate it started with is an ESTIMATE of
 * what the execution cost, never money paid or owed. No cost surface titles it Spend, Spent or Cost alone.
 */
export const EstimateTitle = 'Estimated Execution Cost'

/** The critical colour: an outcome over its budget. */
export const CriticalColour = 'var(--os-error)'

/** One stacked series: an outcome (or No outcome), its colour and its eight weeks. */
export interface CostSeries {
  /** The outcome's id, or `none` for No outcome. */
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
 * THE FIXED ORDER: No outcome first (the bottom of each stack, neutral gray), then each outcome in the
 * route's order. Each colour comes from the shared `outcomeColours` module (the outcome's global rank),
 * never from its position among whatever one team lists: one outcome is one colour on every teams-list
 * row, the tile and the dialog.
 */
export function costSeries(outcomes: readonly TeamCostOutcome[]): CostSeries[] {
  const none = outcomes.filter((o) => o.id === null)
  const linked = outcomes.filter((o) => o.id !== null)

  return [
    ...none.map((o) => ({ key: 'none', name: o.name || 'No outcome', colour: outcomeColour(o), none: true, weekly: o.weekly })),
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

/** A figure that is money when priced and hours when not: never $0 for an unpriced one. */
export function spendText(totals: Pick<TeamCostTotals, 'cost' | 'agentSeconds'>, currency: string): string {
  return totals.cost === null ? agentHours(totals.agentSeconds) : moneyText(totals.cost, currency)
}

/** Whether one outcome's spend across every team has passed its budget. */
export function overBudget(outcome: Pick<TeamCostOutcome, 'id' | 'value' | 'outcomeTotalCost'>): boolean {
  return outcome.id !== null && outcome.value !== null && outcome.outcomeTotalCost !== null && outcome.outcomeTotalCost > outcome.value
}

/** The outcomes this team serves that carry a budget. No outcome never does. */
export function budgeted(outcomes: readonly TeamCostOutcome[]): TeamCostOutcome[] {
  return outcomes.filter((o) => o.id !== null && o.value !== null)
}

/** How many of the team's outcomes are over budget. */
export function overCount(outcomes: readonly TeamCostOutcome[]): number {
  return outcomes.filter(overBudget).length
}

/**
 * SPENT AGAINST BUDGET, over the budgeted outcomes only, each outcome's spent its TOTAL ACROSS ALL
 * TEAMS: a budget is shared by every team serving the outcome. Null without a budget or a rate.
 */
export function budgetUse(cost: TeamCost): { spent: number; budget: number; share: number; over: number } | null {
  if (!priced(cost)) return null
  const withBudget = budgeted(cost.outcomes)
  if (withBudget.length === 0) return null

  const spent = withBudget.reduce((sum, o) => sum + (o.outcomeTotalCost ?? 0), 0)
  const budget = withBudget.reduce((sum, o) => sum + o.value!, 0)

  return { spent, budget, share: budget > 0 ? spent / budget : spent > 0 ? Infinity : 0, over: overCount(cost.outcomes) }
}

/** What this team spent on work with no budget: unbudgeted outcomes and No outcome. Null unpriced. */
export function unbudgetedSpend(cost: TeamCost): number | null {
  if (!priced(cost)) return null
  return cost.outcomes
    .filter((o) => o.id === null || o.value === null)
    .reduce((sum, o) => sum + (o.totals.cost ?? 0), 0)
}

/** A share as a whole percent: `42%`, `<1%`. */
export function percentText(share: number): string {
  if (!Number.isFinite(share)) return '—'
  const percent = share * 100
  return percent > 0 && percent < 1 ? '<1%' : `${Math.round(percent)}%`
}

/** The tile's words, in one of its four states: budgets, over budget, no budgets, no rate. */
export interface CostTile {
  state: 'budgets' | 'over' | 'no-budgets' | 'no-rate'
  value: string
  detail: string
  /** The budget bar's fill, 0..1, or null without one. */
  bar: number | null
  budgetLine: string | null
  unbudgetedLine: string | null
  overLine: string | null
  note: string | null
}

export const NoRateNote = 'Set an agent hourly rate in Outcomes to see dollars'
export const NoBudgetNote = 'No outcome budget set'

export function costTile(cost: TeamCost): CostTile {
  const currency = cost.money.currency

  if (cost.totals.cost === null) {
    return {
      state: 'no-rate',
      value: agentHours(cost.totals.agentSeconds),
      detail: '',
      bar: null,
      budgetLine: null,
      unbudgetedLine: null,
      overLine: null,
      note: NoRateNote,
    }
  }

  const value = moneyText(cost.totals.cost, currency)
  const detail = agentRateLine(cost.totals, currency)
  const use = budgetUse(cost)

  if (!use) {
    return { state: 'no-budgets', value, detail, bar: null, budgetLine: null, unbudgetedLine: null, overLine: null, note: NoBudgetNote }
  }

  const unbudgeted = unbudgetedSpend(cost) ?? 0

  return {
    state: use.over > 0 ? 'over' : 'budgets',
    value,
    detail,
    bar: Math.min(1, Number.isFinite(use.share) ? use.share : 1),
    budgetLine: `${moneyText(use.spent, currency)} of ${moneyText(use.budget, currency)} budget (${percentText(use.share)})`,
    unbudgetedLine: unbudgeted > 0 ? `+ ${moneyText(unbudgeted, currency)} on work with no budget` : null,
    overLine: use.over > 0 ? `⚠ ${use.over} over` : null,
    note: null,
  }
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
