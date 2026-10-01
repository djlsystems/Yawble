import type { Outcome, OutcomeFigures, OutcomeStatus } from '../api/outcomes'

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

/** The filter box: a case-insensitive match on the name and description. */
export function matchesFilter(outcome: Pick<Outcome, 'name' | 'description'>, text: string): boolean {
  const needle = text.trim().toLowerCase()
  if (!needle) return true
  return `${outcome.name}\n${outcome.description}`.toLowerCase().includes(needle)
}
