import type { TeamCost, TeamCostMoney, TeamCostOutcome, TeamCostTotals, TeamCostWeek } from '../api/outcomes'
import { moneyText } from './outcomes'

/**
 * A TEAM'S COST, BY OUTCOME, WORDED AND STACKED: what the Cost tile, the Cost dialog and the teams
 * list's Cost column read. Pure functions of the cost route's answer (`api/outcomes.ts`).
 *
 * EVERY FIGURE IS THE ROUTE'S, and the only sums here are over what the route answered: the budgets
 * and their outcomes' all-team totals, and the team's spend on unbudgeted work.
 *
 * NO RATE IS NO DOLLARS, NEVER $0. The route's `cost` is null when no agent hourly rate is set; every
 * figure then falls back to agent hours, and nothing here prints a money amount for it.
 */

/** The categorical palette's slots (`--os-series-1..8`). An outcome past the last folds into Other. */
export const CostSeriesSlots = 8

/** "No outcome": neutral gray, stacked first (at the bottom). Stepped per mode by the theme. */
export const NoOutcomeColour = 'var(--os-ink-faint)'

/** Outcomes past the palette's last slot share one recessive colour: a hue is never generated or cycled. */
export const OtherOutcomeColour = 'var(--os-rule-strong)'

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

/** Whether the instance prices agent time: then figures are money, otherwise agent hours. */
export function priced(cost: { money: TeamCostMoney }): boolean {
  return cost.money.agentHourlyRate !== null
}

/**
 * THE FIXED ORDER: No outcome first (the bottom of each stack, neutral gray), then each outcome in the
 * route's order. AN OUTCOME'S COLOUR IS ITS GLOBAL RANK'S PALETTE SLOT (`rank`, the same on every
 * team), never its position among whatever one team lists: one outcome is one colour on every teams-list
 * row, the tile and the dialog. A rank past the last slot folds into Other.
 */
export function costSeries(outcomes: readonly TeamCostOutcome[]): CostSeries[] {
  const none = outcomes.filter((o) => o.id === null)
  const linked = outcomes.filter((o) => o.id !== null)

  return [
    ...none.map((o) => ({ key: 'none', name: o.name || 'No outcome', colour: NoOutcomeColour, none: true, weekly: o.weekly })),
    ...linked.map((o) => ({
      key: o.id!,
      name: o.name,
      colour: outcomeColour(o.rank),
      none: false,
      weekly: o.weekly,
    })),
  ]
}

/** An outcome's colour from its global rank: palette slots 1..8, then Other. */
export function outcomeColour(rank: number | null): string {
  return rank !== null && rank >= 1 && rank <= CostSeriesSlots ? `var(--os-series-${rank})` : OtherOutcomeColour
}

/** A week's figure for the chart: its cost when priced, otherwise its agent hours. */
export function weekValue(week: Pick<TeamCostWeek, 'cost' | 'agentSeconds'>, money: boolean): number {
  return money ? week.cost ?? 0 : week.agentSeconds / 3600
}

/** Agent hours, one decimal: `12.5`. */
export function hoursFigure(seconds: number): string {
  const hours = seconds / 3600
  return hours < 100 ? hours.toFixed(1) : Math.round(hours).toLocaleString()
}

/** `12.5 agent h`. */
export function agentHours(seconds: number): string {
  return `${hoursFigure(seconds)} agent h`
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
  const rate = cost.money.agentHourlyRate

  if (rate === null || cost.totals.cost === null) {
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
  const detail = `${hoursFigure(cost.totals.agentSeconds)} agent h at ${moneyText(rate, currency)}/h`
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
