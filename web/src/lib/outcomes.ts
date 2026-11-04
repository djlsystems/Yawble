import type {
  HumanFigures,
  Outcome,
  OutcomeCumulativeWeek,
  OutcomeFigures,
  OutcomePresentation,
  OutcomeStatus,
  OutcomeThroughputWeek,
  OutcomeWorkflowLine,
} from '../api/outcomes'
import { localTime, wireInstant } from './localTime'

/**
 * THE MANAGE OUTCOMES DIALOG'S WORDING, kept out of the component so each rule is one function.
 *
 * NOTHING HERE COMPUTES A FIGURE. Each function formats one number the route answered - it never
 * adds two, averages, or fills a gap - because the dialog's promise is that every figure is the
 * ledger's. The one sum on screen, agent time, is the route's sum and is labelled as one.
 */

/** Seconds as a person reads a duration: `45s`, `12m`, `3h 5m`, `2d 4h`. */
export function duration(seconds: number): string {
  const s = Math.round(seconds)
  if (s < 60) return `${s}s`

  const minutes = Math.floor(s / 60)
  if (minutes < 60) return `${minutes}m`

  const hours = Math.floor(minutes / 60)
  if (hours < 48) return minutes % 60 ? `${hours}h ${minutes % 60}m` : `${hours}h`

  const days = Math.floor(hours / 24)
  return hours % 24 ? `${days}d ${hours % 24}h` : `${days}d`
}

/** A token count at a glance: `950`, `12.4K`, `1.2M`. */
export function compactTokens(tokens: number): string {
  if (tokens < 1000) return `${tokens}`
  if (tokens < 1_000_000) return `${(tokens / 1000).toFixed(tokens < 10_000 ? 1 : 0)}K`
  return `${(tokens / 1_000_000).toFixed(1)}M`
}

/** `+ 3 unmeasured runs`, or nothing when every run reported its usage. */
export function unmeasuredText(unmeasuredRuns: number): string {
  if (unmeasuredRuns <= 0) return ''
  return `+ ${unmeasuredRuns} unmeasured run${unmeasuredRuns === 1 ? '' : 's'}`
}

/**
 * A workflow line's Tokens figure, from the route's own counts: with no measured run there is no
 * number ("not measured"), and a measured 0 is a 0 however many runs went unmeasured beside it.
 * The unmeasured count follows it as `unmeasuredText`.
 */
export function lineTokensFigure(line: Pick<OutcomeWorkflowLine, 'billableTokens' | 'measuredRuns' | 'unmeasuredRuns'>): string {
  if (line.measuredRuns === 0) return line.unmeasuredRuns > 0 ? 'not measured' : '—'
  return line.billableTokens.toLocaleString()
}

function teamName(team: OutcomeFigures['teams'][number]): string {
  return team.deleted ? `${team.name} (deleted)` : team.name
}

/** Every team, a gone one named deleted: the Teams cell's tooltip. */
export function teamsText(teams: OutcomeFigures['teams']): string {
  return teams.map(teamName).join(', ')
}

export function firstLine(text: string | null | undefined): string {
  return (text ?? '').split(/\r?\n/, 1)[0]?.trim() ?? ''
}

/** How many workflows a proposed outcome's links name, as Needs You reads it: the route's total. */
export function linkedWorkflowsText(total: number): string {
  return `${total} workflow${total === 1 ? '' : 's'} linked`
}

export function when(at: string | null | undefined): string {
  return at ? localTime(wireInstant(at), { date: true }) : '—'
}

// --- The period --------------------------------------------------------------------------------

export type PeriodKind = 'all' | 'last30' | 'month' | 'custom'

export const PeriodOptions: { label: string; value: PeriodKind }[] = [
  { label: 'All time', value: 'all' },
  { label: 'Last 30 days', value: 'last30' },
  { label: 'This month', value: 'month' },
  { label: 'Custom', value: 'custom' },
]

export interface Period {
  kind: PeriodKind
  /** Custom only: `YYYY-MM-DD`, local days, both inclusive. */
  customFrom?: string
  customTo?: string
}

/**
 * The route's `from` and `to` (UTC instants) for a period. `null` is no bound. A custom day is the
 * person's local day: from its midnight to the next day's.
 */
export function periodRange(period: Period, now = new Date()): { from: string | null; to: string | null } {
  switch (period.kind) {
    case 'all':
      return { from: null, to: null }
    case 'last30':
      return { from: new Date(now.getTime() - 30 * 86_400_000).toISOString(), to: null }
    case 'month':
      return { from: new Date(now.getFullYear(), now.getMonth(), 1).toISOString(), to: null }
    case 'custom': {
      const from = localDay(period.customFrom)
      const to = localDay(period.customTo)
      if (to) to.setDate(to.getDate() + 1)
      return { from: from?.toISOString() ?? null, to: to?.toISOString() ?? null }
    }
  }
}

function localDay(text: string | undefined): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text ?? '')
  if (!match) return null
  return new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
}

/**
 * Whether the "Accounting since" note shows: the period reaches before the ledger began, so runs a
 * Reset deleted before then are missing from it. No lower bound always reaches before it.
 */
export function reachesBeforeLedger(from: string | null, ledgerStartedAt: string | null): boolean {
  if (!ledgerStartedAt) return false
  if (!from) return true
  return wireInstant(from) < wireInstant(ledgerStartedAt)
}

export function accountingSinceText(ledgerStartedAt: string): string {
  return `Accounting since ${when(ledgerStartedAt)}; runs deleted by a Reset before then are not included.`
}

// --- Status and actions ------------------------------------------------------------------------

export type OutcomeAction = 'confirm' | 'merge' | 'reject' | 'retire' | 'reactivate'

/**
 * What a person may do to an outcome in each status. REJECT ONLY WITH NOTHING HOLDING IT: the route
 * refuses it (409) while any link names the outcome or another outcome is merged into it, so the
 * button is not offered then. `links` counts both.
 */
export function actionsFor(status: OutcomeStatus, links: number): OutcomeAction[] {
  switch (status) {
    case 'proposed':
      return links === 0 ? ['confirm', 'merge', 'reject'] : ['confirm', 'merge']
    case 'active':
      return ['merge', 'retire']
    case 'retired':
      return ['reactivate']
    case 'merged':
      return []
  }
}

export const StatusLabel: Record<OutcomeStatus, string> = {
  proposed: 'Proposed',
  active: 'Active',
  retired: 'Retired',
  merged: 'Merged',
}

/** How a link was made, as a person reads it. */
export const LinkHowLabel: Record<string, string> = {
  dispatch: 'backlog dispatch',
  trigger: 'trigger',
  tell: 'instruction',
  manager: 'Manager',
  person: 'person',
}

/** The merge preview's sentence: what moves, all figures the route's. */
export function mergePreviewText(preview: {
  into: { name: string }
  moves: { links: number; figures: OutcomeFigures }
}): string {
  const figures = preview.moves.figures
  const teams = figures.teams.length
  const parts = [
    `${figures.workflows.total} workflow${figures.workflows.total === 1 ? '' : 's'}`,
    `${teams} team${teams === 1 ? '' : 's'}`,
    figures.tokens.measuredRuns > 0 ? `${compactTokens(figures.tokens.billable)} tokens` : 'no measured tokens',
    `${duration(figures.agentSeconds)} agent time`,
  ]
  const unmeasured = unmeasuredText(figures.tokens.unmeasuredRuns)

  return `${parts.join(', ')}${unmeasured ? ` (${unmeasured})` : ''} move to ${preview.into.name}.`
}

/**
 * The filter box: a case-insensitive match on the name and description. A cleared box is null - a
 * clearable q-input's clear button sends null, not '' - and matches everything, as an empty one does.
 */
export function matchesFilter(outcome: Pick<Outcome, 'name' | 'description'>, text: string | null | undefined): boolean {
  const needle = (text ?? '').trim().toLowerCase()
  if (!needle) return true
  return `${outcome.name}\n${outcome.description}`.toLowerCase().includes(needle)
}

// ---- The dashboard's wording. Each formats a figure the route answered; none computes one. ----

/** An amount of the instance's currency: `$1,240`, `€20,000`, `$12.50`. Null is "not set", never 0. */
export function moneyText(amount: number | null, currency: string): string {
  if (amount === null) return 'not set'
  const whole = Math.abs(amount) >= 100 || Number.isInteger(amount)
  try {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency,
      minimumFractionDigits: whole ? 0 : 2,
      maximumFractionDigits: whole ? 0 : 2,
    }).format(amount)
  } catch {
    return `${amount.toLocaleString()} ${currency}`
  }
}

/** Agent hours from the route's seconds: `13.8 Agent Hours`, `45 agent min`. */
export function agentHoursText(seconds: number): string {
  if (seconds < 3600) return `${Math.round(seconds / 60)} agent min`
  const hours = seconds / 3600
  return `${hours < 100 ? hours.toFixed(1) : Math.round(hours).toLocaleString()} Agent Hours`
}

/** The efficiency figure, `78%`, or `—` when the route has none: no time is no efficiency, never 0%. */
export function efficiencyText(efficiency: number | null): string {
  return efficiency === null ? '—' : `${Math.round(efficiency * 100)}%`
}

/** The tile's one summary line: what the route counted, in words. */
export function tileSummaryText(figures: OutcomeFigures): string {
  const parts = [
    `${figures.workflows.completed} workflow${figures.workflows.completed === 1 ? '' : 's'} completed`,
    `${figures.workflows.open} active`,
    `${figures.backlog.notStarted} item${figures.backlog.notStarted === 1 ? '' : 's'} pending`,
  ]
  if (figures.efficiency !== null) parts.push(`agents working ${efficiencyText(figures.efficiency)} of the time`)
  return parts.join(' · ')
}

export interface BarSegment {
  key: 'notStarted' | 'inProgress' | 'achieved'
  label: string
  count: number
  /** Its share of the bar, 0..100. Only for drawing: the count is what is read. */
  share: number
}

export const BucketLabel: Record<BarSegment['key'], string> = {
  notStarted: 'Not started',
  inProgress: 'In progress',
  achieved: 'Achieved',
}

/** The backlog bar's three segments, in order, each with the route's count. Empty when there are no items. */
export function backlogSegments(backlog: OutcomeFigures['backlog']): BarSegment[] {
  const total = backlog.notStarted + backlog.inProgress + backlog.achieved
  if (total === 0) return []
  return (['notStarted', 'inProgress', 'achieved'] as const).map((key) => ({
    key,
    label: BucketLabel[key],
    count: backlog[key],
    share: (backlog[key] / total) * 100,
  }))
}

/** Each week's bar height, 0..100 of the tallest - by cost when priced, else by agent time. */
export function weekHeights(weekly: OutcomeFigures['weekly']): number[] {
  const values = weekly.map((w) => w.cost ?? w.agentSeconds)
  const max = Math.max(0, ...values)
  return values.map((v) => (max > 0 ? (v / max) * 100 : 0))
}

/** A week's hover text: `Week of Sep 29: $240 · 2.7 Agent Hours · 3 runs`. */
export function weekText(week: OutcomeFigures['weekly'][number], currency: string): string {
  const start = new Date(week.weekStart).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' })
  const runs = week.measuredRuns + week.unmeasuredRuns
  const parts: string[] = []
  if (week.cost !== null) parts.push(moneyText(week.cost, currency))
  parts.push(agentHoursText(week.agentSeconds), `${runs} run${runs === 1 ? '' : 's'}`)
  if (week.unmeasuredRuns > 0) parts.push(unmeasuredText(week.unmeasuredRuns))
  return `Week of ${start}: ${parts.join(' · ')}`
}

/** The rate line above the tiles: `Agent time at $90/h (USD)`, or that no rate is set. */
export function rateText(money: { currency: string; agentHourlyRate: number | null }): string {
  return money.agentHourlyRate === null
    ? `No rate set for agent time (${money.currency})`
    : `Agent time at ${moneyText(money.agentHourlyRate, money.currency)}/h (${money.currency})`
}

// ---- Two presentations of one outcome. Each formats what the route answered; none computes a figure. ----

/** The two readings a person picks between, in the order the picker shows them. */
export const PresentationOptions: { label: string; value: OutcomePresentation }[] = [
  { label: 'Project', value: 'project' },
  { label: 'Operational', value: 'operational' },
]

/** How an outcome is read: its stored choice, and project when nobody has picked one (null or absent). */
export function presentationOf(outcome: Pick<Outcome, 'presentation'>): OutcomePresentation {
  return outcome.presentation === 'operational' ? 'operational' : 'project'
}

/**
 * The Estimated Execution Cost figure's label: `Estimated Execution Cost · 13.8 Agent Hours at $60/h`. The rate is
 * the route's weighted rate of the priced agent time, never the tenant rate; with none, no rate is named.
 */
export function estimateLabel(figures: Pick<OutcomeFigures, 'agentSeconds' | 'cost'>): string {
  const rate = figures.cost.agentRate ?? null
  const hours = agentHoursText(figures.agentSeconds)
  return rate === null
    ? `Estimated Execution Cost · ${hours}`
    : `Estimated Execution Cost · ${hours} at ${moneyText(rate, figures.cost.currency)}/h`
}

/** Each bar's height, 0..100 of the tallest - by cost when any is priced, else by agent time. */
export function seriesHeights(series: readonly { cost: number | null; agentSeconds: number }[]): number[] {
  const priced = series.some((w) => w.cost !== null)
  const values = series.map((w) => (priced ? w.cost ?? 0 : w.agentSeconds))
  const max = Math.max(0, ...values)
  return values.map((v) => (max > 0 ? (v / max) * 100 : 0))
}

/** A week's short label, `Sep 29`, read in UTC as the route's weeks are. */
export function weekStartText(weekStart: string): string {
  return new Date(weekStart).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' })
}

/** A cumulative bar's hover text: `To the end of the week of Sep 29: $240 · 2.7 Agent Hours`. */
export function cumulativeText(week: OutcomeCumulativeWeek, currency: string): string {
  const parts: string[] = []
  if (week.cost !== null) parts.push(moneyText(week.cost, currency))
  parts.push(agentHoursText(week.agentSeconds))
  return `To the end of the week of ${weekStartText(week.weekStart)}: ${parts.join(' · ')}`
}

/** Cost per completed workflow as the route answered it, or `not measurable` - never a division by zero. */
export function perCompletedText(costPerCompleted: number | null | undefined, currency: string): string {
  return costPerCompleted === null || costPerCompleted === undefined ? 'not measurable' : moneyText(costPerCompleted, currency)
}

/** Throughput, as the route counted it: `3 closed · 2 completed`. */
export function throughputText(week: Pick<OutcomeThroughputWeek, 'closed' | 'completed'>): string {
  return `${week.closed} closed · ${week.completed} completed`
}

/** Backlog progress in words: `4 of 9 items achieved`, or that it has none. */
export function backlogProgressText(backlog: OutcomeFigures['backlog']): string {
  const total = backlog.notStarted + backlog.inProgress + backlog.achieved
  return total === 0 ? 'No backlog items' : `${backlog.achieved} of ${total} item${total === 1 ? '' : 's'} achieved`
}

// ---- People's acts: Human Interventions, Human Attention Time, Waiting on a person -------------------------------

/** The three human figures' words, used exactly as written. None is priced; no value word sits near them. */
export const HumanInterventionsLabel = 'Human Interventions'
export const HumanAttentionLabel = 'Human Attention Time'
export const WaitingOnAPersonLabel = 'Waiting on a person'

/** What Human Attention Time says when nothing was measured: never 0, which would read as measured. */
export const NoMeasuredTime = 'no measured time'

/** Human Interventions as the route counted them: `12`, or `—` from an older Host that sent none. */
export function interventionsText(human: HumanFigures | undefined): string {
  return human === undefined ? '—' : human.interventions.toLocaleString()
}

/**
 * Human Attention Time: the route's sum of measured durations, `1h 5m`, or "no measured time" when the route
 * measured none - a null, never shown as 0. Waiting on a person is never part of it.
 */
export function attentionText(human: HumanFigures | undefined): string {
  return human === undefined || human.attentionSeconds === null ? NoMeasuredTime : duration(human.attentionSeconds)
}

/** Waiting on a person, the route's blocked time: a duration of its own, never added to attention. */
export function waitingText(seconds: number): string {
  return duration(seconds)
}

/** One line for a tile: `3 Human Interventions · Human Attention Time 12m` (or "no measured time"). */
export function humanLineText(human: HumanFigures | undefined): string {
  if (human === undefined) return ''
  const count = `${human.interventions.toLocaleString()} ${HumanInterventionsLabel}`
  return `${count} · ${HumanAttentionLabel} ${attentionText(human)}`
}
