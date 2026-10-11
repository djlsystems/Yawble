import type { Outcome, OutcomeFigures, OutcomeStatus, OutcomeWorkflowLine } from '../api/outcomes'

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
 * The Tokens cell's figure. AN UNMEASURED RUN IS NEVER SHOWN AS 0: with no measured run there is no
 * number, so the cell says "not measured" and the count follows it.
 */
export function tokensFigure(tokens: OutcomeFigures['tokens']): string {
  if (tokens.measuredRuns === 0) return tokens.unmeasuredRuns > 0 ? 'not measured' : '—'
  return compactTokens(tokens.billable)
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

/** `2 open · 5/9`: open, then completed of total, all the route's counts. */
export function workflowsText(workflows: OutcomeFigures['workflows']): string {
  return `${workflows.open} open · ${workflows.completed}/${workflows.total}`
}

/** `3h 5m · 9h`: the median and the longest workflow. NEVER A SUM of workflows. */
export function elapsedText(elapsed: OutcomeFigures['elapsed']): string {
  if (elapsed.medianSeconds === null || elapsed.longestSeconds === null) return '—'
  return `${duration(elapsed.medianSeconds)} · ${duration(elapsed.longestSeconds)}`
}

function teamName(team: OutcomeFigures['teams'][number]): string {
  return team.deleted ? `${team.name} (deleted)` : team.name
}

/** Every team, a gone one named deleted: the Teams cell's tooltip. */
export function teamsText(teams: OutcomeFigures['teams']): string {
  return teams.map(teamName).join(', ')
}

/** How many team names the Teams cell shows before "+N more". */
export const TeamsShown = 3

/**
 * `Alpha, Beta, Old crew (deleted) +27 more`: the Teams cell. The No outcome row can name dozens of
 * teams, which would push every figure column off-screen; the rest, deleted ones counted, are in
 * the tooltip (`teamsText`).
 */
export function teamsCellText(teams: OutcomeFigures['teams']): string {
  const shown = teams.slice(0, TeamsShown).map(teamName).join(', ')
  const more = teams.length - TeamsShown
  return more > 0 ? `${shown} +${more} more` : shown
}

export function firstLine(text: string | null | undefined): string {
  return (text ?? '').split(/\r?\n/, 1)[0]?.trim() ?? ''
}

/** How many workflows a proposed outcome's links name, as Needs You reads it: the route's total. */
export function linkedWorkflowsText(total: number): string {
  return `${total} workflow${total === 1 ? '' : 's'} linked`
}

export function when(at: string | null | undefined): string {
  return at ? new Date(at).toLocaleString() : '—'
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
  return new Date(from).getTime() < new Date(ledgerStartedAt).getTime()
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

/** An outcome's budget (the API's `value`) as a number, or null when nobody has set one. */
export function valueAmount(value: string | null | undefined): number | null {
  if (value === null || value === undefined || value.trim() === '') return null
  const amount = Number(value)
  return Number.isFinite(amount) ? amount : null
}

/** What share of the budget the cost is, `6%` - or nothing when either is not known or the budget is 0. */
export function shareOfValueText(cost: number | null, value: number | null): string {
  if (cost === null || value === null || value <= 0) return ''
  const share = (cost / value) * 100
  return share > 0 && share < 1 ? '<1%' : `${Math.round(share)}%`
}

/** Agent hours from the route's seconds: `13.8 agent h`, `45 agent min`. */
export function agentHoursText(seconds: number): string {
  if (seconds < 3600) return `${Math.round(seconds / 60)} agent min`
  const hours = seconds / 3600
  return `${hours < 100 ? hours.toFixed(1) : Math.round(hours).toLocaleString()} agent h`
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

/** A week's hover text: `Week of Sep 29: $240 · 2.7 agent h · 3 runs`. */
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
