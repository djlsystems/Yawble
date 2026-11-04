import type { TeamCostMoney, TeamCostPeriod, TeamPeriodFigures, TeamPeriodMember, TeamPeriodOutcome } from '../api/outcomes'
import { outcomeColour } from './outcomeColours'
import { moneyText } from './outcomes'
import { periodLabel } from './spend'
import { type CostSeries, EstimateTitle, agentRateLine, hoursFigure, legacyRateNote, markedMoney } from './teamCost'
import { rangeOfChoice, type TeamsPeriodChoice } from './teamsPeriod'

/**
 * A TEAM PAGE'S PERIOD, WORDED: the Cost tile, its dialog's headline and breakdowns, the Activity tile's executions,
 * Agent Hours and active members, and a workflow's one line. Pure functions of `GET /api/teams/{team}/cost/period`
 * and `GET /api/teams/{team}/cost/workflows` (`api/outcomes.ts`).
 *
 * EVERY FIGURE IS THE ROUTE'S. Nothing here prices, converts or adds money up: each figure a person reads is one the
 * route answered, worded. NO PRICE IS NO MONEY, NEVER $0: an unpriced figure reads `Estimate unavailable`, and a
 * plugin member with no rate of its own reads `Not priced` (a plugin is never given the agent default).
 */

/** A read's currency, as every answer carries it. */
type Priced = { money: TeamCostMoney }

/** What a plugin member with no rate of its own reads: not priced, never the agent default. */
export const NotPriced = 'Not priced'

/** What a figure that ran and was not priced reads. */
export const EstimateUnavailable = 'Estimate unavailable'

/** The period as a tile title names it: `last two months`, a whole month by name, or a first and last day. */
export function periodName(choice: TeamsPeriodChoice, now: Date = new Date()): string {
  return choice.range === null ? 'last two months' : periodLabel(rangeOfChoice(choice, now))
}

/** The Cost tile's title: `Estimated Execution Cost · last two months`. */
export function tileTitle(choice: TeamsPeriodChoice, now: Date = new Date()): string {
  return `${EstimateTitle} · ${periodName(choice, now)}`
}

/** Money with its footnote marker, `Estimate unavailable` when it ran unpriced, `—` when nothing ran. */
export function figureText(
  figures: Pick<TeamPeriodFigures, 'cost' | 'executions' | 'agentSeconds'> & { legacyRates?: readonly string[] },
  currency: string,
): string {
  if (figures.cost !== null) return markedMoney(moneyText(figures.cost, currency), figures.legacyRates)
  return figures.executions > 0 || figures.agentSeconds > 0 ? EstimateUnavailable : '—'
}

/** The Cost tile's and the dialog headline's words: the figure, `{h} Agent Hours at {rate}/h`, and the footnote. */
export interface TeamPeriodTile {
  value: string
  detail: string
  footnote: string | null
}

export function teamPeriodTile(period: TeamCostPeriod): TeamPeriodTile {
  const currency = period.money.currency
  const totals = period.totals
  return {
    value: figureText(totals, currency),
    detail: agentRateLine(totals, currency),
    footnote: totals.cost === null ? null : legacyRateNote(totals.legacyRates),
  }
}

/** Whether the team's chart is in money: any run in the period was priced. Otherwise Agent Hours. */
export function teamPeriodPriced(period: Pick<TeamCostPeriod, 'totals'>): boolean {
  return period.totals.cost !== null
}

/**
 * THE TEAM'S CHART, stacked by outcome as the Teams list's row is: Unattributed first (neutral gray), then each outcome
 * in the route's order, coloured by its rank - one outcome, one colour. The chart draws it on the team's own scale.
 */
export function teamPeriodSeries(period: Pick<TeamCostPeriod, 'outcomes'>): CostSeries[] {
  return period.outcomes.map((o) => ({
    key: o.id ?? 'none',
    name: o.name,
    colour: outcomeColour(o),
    none: o.id === null,
    weekly: o.buckets.map((b) => ({
      weekStart: b.start, agentSeconds: b.agentSeconds, cost: b.cost, billable: 0, measuredRuns: 0, unmeasuredRuns: 0,
    })),
  }))
}

/** One row of a breakdown, worded. */
export interface BreakdownRow {
  key: string
  name: string
  executions: number
  /** Agent Hours, one decimal; `—` for a plugin member, whose time is never Agent Hours. */
  hours: string
  estimate: string
  /** The footnote's words, as the figure's title; null without one. */
  footnote: string | null
}

function row(key: string, name: string, figures: TeamPeriodFigures, currency: string, hours = true): BreakdownRow {
  return {
    key,
    name,
    executions: figures.executions,
    hours: hours ? hoursFigure(figures.agentSeconds) : '—',
    estimate: figureText(figures, currency),
    footnote: figures.cost === null ? null : legacyRateNote(figures.legacyRates),
  }
}

/** A member's row: an agent's Estimated Agent Cost, or a plugin's Estimated Plugin Cost - `Not priced` with none. */
export function memberRow(member: TeamPeriodMember, currency: string): BreakdownRow {
  const plugin = member.kind === 'plugin'
  const worded = row(`${member.kind}:${member.member}`, member.member, member.figures, currency, !plugin)
  return plugin && member.figures.cost === null ? { ...worded, estimate: NotPriced } : worded
}

/** The member breakdown, agent members and plugin members apart, in the route's order. */
export function memberRows(period: Pick<TeamCostPeriod, 'members'> & Priced): { agents: BreakdownRow[]; plugins: BreakdownRow[] } {
  const currency = period.money.currency
  return {
    agents: period.members.filter((m) => m.kind !== 'plugin').map((m) => memberRow(m, currency)),
    plugins: period.members.filter((m) => m.kind === 'plugin').map((m) => memberRow(m, currency)),
  }
}

/** The outcome breakdown, Unattributed first as the route lists it. */
export function outcomeRows(period: Pick<TeamCostPeriod, 'outcomes'> & Priced): (BreakdownRow & { outcome: TeamPeriodOutcome })[] {
  return period.outcomes.map((o) => ({ ...row(o.id ?? 'none', o.name, o.figures, period.money.currency), outcome: o }))
}

/** The workflow breakdown, newest first, each named by its number. */
export function workflowRows(period: Pick<TeamCostPeriod, 'workflows'> & Priced): (BreakdownRow & { workflow: number })[] {
  return period.workflows.map((w) => ({ ...row(String(w.workflow), `#${w.workflow}`, w.figures, period.money.currency), workflow: w.workflow }))
}

/** A workflow's one line: `Agent Hours 1.5 · Estimated Execution Cost $90`. */
export function workflowLine(figures: TeamPeriodFigures, currency: string): string {
  return `Agent Hours ${hoursFigure(figures.agentSeconds)} · ${EstimateTitle} ${figureText(figures, currency)}`
}

/** The line a workflow with no runs reads: nothing ran, so there are no hours and no estimate. */
export const NoRunsLine = `Agent Hours 0.0 · ${EstimateTitle} —`

function counted(count: number, one: string, many: string): string {
  return `${count.toLocaleString()} ${count === 1 ? one : many}`
}

/** The Activity tile's line for the period: `12 executions · 4.5 Agent Hours · 3 active members`. */
export function activityLine(totals: Pick<TeamCostPeriod['totals'], 'executions' | 'agentSeconds' | 'activeMembers'>): string {
  return [
    counted(totals.executions, 'execution', 'executions'),
    `${hoursFigure(totals.agentSeconds)} Agent Hours`,
    counted(totals.activeMembers, 'active member', 'active members'),
  ].join(' · ')
}
