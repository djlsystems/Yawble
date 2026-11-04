import type { CostBucket, CostPeriodRange, CostPeriodTeam, TeamsCostPeriod } from '../api/outcomes'
import { outcomeColour } from './outcomeColours'
import { moneyText } from './outcomes'
import { periodLabel } from './spend'
import { type CostSeries, hoursFigure, legacyRateNote, markedMoney, weekValue } from './teamCost'

/**
 * THE TEAMS LIST'S PERIOD: one control above the list - Daily or Monthly, over the last two months or a custom
 * range - that the Estimated Execution Cost column, the Agent Hours column and the first and third summary tiles
 * follow. Pure functions of `GET /api/teams/cost/period` (`api/outcomes.ts`).
 *
 * EVERY FIGURE IS THE ROUTE'S. Nothing here prices, converts or adds money up into a figure a person reads: a
 * row's total and Agent Hours are the route's `totals`, and the only sums are the chart's stacked segments, for
 * its one relative scale.
 */

/** Where the person's choice is remembered, in this browser, beside `harness.teamSort` and `harness.teamsShown`. */
export const TeamsPeriodKey = 'harness.teamsCostPeriod'

/** The person's choice: the bucket, and a custom range or null for the last two months (which move with the clock). */
export interface TeamsPeriodChoice {
  bucket: CostBucket
  range: CostPeriodRange | null
}

/** Daily, over the last two months. */
export const DefaultTeamsPeriod: TeamsPeriodChoice = { bucket: 'day', range: null }

const Day = /^\d{4}-\d{2}-\d{2}$/

function iso(date: Date): string {
  return date.toISOString().slice(0, 10)
}

/** The last two months: the previous UTC month and the current one, `to` exclusive - the route's own default. */
export function lastTwoMonths(now: Date = new Date()): CostPeriodRange {
  return {
    from: iso(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1))),
    to: iso(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1))),
  }
}

/** The range a choice reads. */
export function rangeOfChoice(choice: TeamsPeriodChoice, now: Date = new Date()): CostPeriodRange {
  return choice.range ?? lastTwoMonths(now)
}

/** Read defensively, as the sort is: anything but a stored bucket and a null or well-formed range is the default. */
export function readTeamsPeriod(raw: string | null): TeamsPeriodChoice {
  if (!raw) return DefaultTeamsPeriod

  try {
    const parsed = JSON.parse(raw) as Partial<TeamsPeriodChoice> | null
    if (!parsed || (parsed.bucket !== 'day' && parsed.bucket !== 'month')) return DefaultTeamsPeriod
    if (parsed.range === null || parsed.range === undefined) return { bucket: parsed.bucket, range: null }
    const { from, to } = parsed.range
    if (typeof from === 'string' && typeof to === 'string' && Day.test(from) && Day.test(to) && to > from) {
      return { bucket: parsed.bucket, range: { from, to } }
    }
  } catch {
    // Not JSON: the default.
  }

  return DefaultTeamsPeriod
}

export function writeTeamsPeriod(choice: TeamsPeriodChoice): string {
  return JSON.stringify(choice)
}

/** `Last two months · Oct 1, 2026 – Nov 30, 2026`, or a custom range by its first and last day. UTC. */
export function periodHeading(choice: TeamsPeriodChoice, now: Date = new Date()): string {
  const range = rangeOfChoice(choice, now)
  return choice.range === null ? `Last two months · ${periodLabel(range)}` : periodLabel(range)
}

/** A bucket's short label: `Nov 3` by day, `Nov 2026` by month. UTC. */
export function bucketLabel(start: string, bucket: CostBucket): string {
  const date = new Date(`${start}T00:00:00Z`)
  return bucket === 'day'
    ? date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' })
    : date.toLocaleDateString(undefined, { month: 'short', year: 'numeric', timeZone: 'UTC' })
}

/** Whether any team on the list had a priced run in the period: the whole list is then drawn in money. */
export function periodPriced(answer: Pick<TeamsCostPeriod, 'teams'>): boolean {
  return answer.teams.some((team) => team.totals.cost !== null)
}

/**
 * A ROW'S STACKED SERIES, as the eight-week chart takes them: Unattributed first (neutral gray), then each
 * outcome in the route's order, coloured by its rank in the period - one outcome, one colour, on every row.
 */
export function periodSeries(team: CostPeriodTeam): CostSeries[] {
  return team.outcomes.map((o) => ({
    key: o.id ?? 'none',
    name: o.name,
    colour: outcomeColour(o),
    none: o.id === null,
    weekly: o.buckets.map((b) => ({
      weekStart: b.start, agentSeconds: b.agentSeconds, cost: b.cost, billable: 0, measuredRuns: 0, unmeasuredRuns: 0,
    })),
  }))
}

/**
 * ONE RELATIVE SCALE ACROSS EVERY ROW: the tallest stacked bucket of any team, in the list's unit (money when any
 * team was priced, Agent Hours otherwise). Every row's chart is drawn against it.
 */
export function periodTop(answer: Pick<TeamsCostPeriod, 'teams' | 'buckets'>, money: boolean): number {
  let top = 0
  for (const team of answer.teams) {
    answer.buckets.forEach((_, index) => {
      const stacked = team.outcomes.reduce(
        (sum, o) => sum + weekValue(o.buckets[index] ?? { cost: null, agentSeconds: 0 }, money), 0)
      top = Math.max(top, stacked)
    })
  }
  return top
}

/**
 * A ROW'S ESTIMATED EXECUTION COST: the route's total, with its footnote marker when a reconstructed or fallback
 * rate priced part of it; `Estimate unavailable` when the team ran and nothing was priced; `—` with no runs.
 */
export function estimateText(team: CostPeriodTeam, currency: string): string {
  if (team.totals.cost !== null) return markedMoney(moneyText(team.totals.cost, currency), team.totals.legacyRates)
  return team.outcomes.length > 0 ? 'Estimate unavailable' : '—'
}

/** The total's footnote words, as its title; null when the figure was priced from snapshots alone, or unpriced. */
export function estimateFootnote(team: CostPeriodTeam): string | null {
  return team.totals.cost === null ? null : legacyRateNote(team.totals.legacyRates)
}

/** A row's Agent Hours: the route's agent seconds, one decimal. */
export function hoursText(team: CostPeriodTeam): string {
  return hoursFigure(team.totals.agentSeconds)
}
