import type { DiagnosticSeverity, DiagnosticsFilter, DiagnosticsView } from '../api/types'

/**
 * Admin › Diagnostics, minus the rendering.
 *
 * The query string, the three-state empty answer and the words the screen puts on a dotted kind
 * live here rather than in the dialog, for the reason `lib/ribbon.ts` keeps its two disable
 * predicates out of `RibbonBar.vue`: they are decisions, they are the part worth pinning, and a
 * decision inside a `.vue` template can only be tested by mounting the thing it is inside.
 */

/**
 * The three severities, in the order the filter offers them: loudest first.
 *
 * A COPY OF A CLOSED SET, UNLIKE `kinds`, AND THE ASYMMETRY IS DELIBERATE. The kind vocabulary is
 * twenty-odd dotted names that grow with the platform, so the screen is served it (see
 * `DiagnosticsView.kinds`). This is three words that are the severity scale itself — `DiagnosticSeverity`
 * exists to have exactly three and says so — and the TypeScript union in `api/types.ts` already has
 * to spell them out to type anything at all. A fetch to learn what they are would be a request to
 * discover something the type system already fixed.
 */
export const DiagnosticSeverities: readonly DiagnosticSeverity[] = ['Error', 'Warning', 'Info']

/**
 * The query string for a diagnostics read, or '' when nothing is narrowed.
 *
 * SAME SHAPE AS `kanbanQuery`, AND FOR THE SAME REASONS. The ORDER is fixed rather than object-key
 * order so two equal filters produce one URL — which is what lets a test, a cache and a network log
 * compare them by string. Empty values are DROPPED rather than sent empty, so "cleared" has one
 * spelling on the wire instead of two that differ by control.
 *
 * `kinds` and `severities` REPEAT rather than joining with a comma: the server reads them as string
 * arrays, and a comma would make a kind that ever contained one unrepresentable. They are a union —
 * two kinds mean "either" — which is what a multi-select means to the person using it.
 */
export function diagnosticsQuery(filter: DiagnosticsFilter, before?: number, take = 50): string {
  const parts: string[] = []

  for (const kind of filter.kinds ?? []) {
    if (kind) parts.push(`kind=${encodeURIComponent(kind)}`)
  }

  for (const severity of filter.severities ?? []) {
    if (severity) parts.push(`severity=${encodeURIComponent(severity)}`)
  }

  // `from` and `to` are whatever the date inputs hold — `YYYY-MM-DD`. The server parses them the
  // same way `/api/kanban/board` parses its window, so the two screens' date controls mean the same
  // thing rather than one of them meaning something subtly else.
  if (filter.from) parts.push(`from=${encodeURIComponent(filter.from)}`)
  if (filter.to) parts.push(`to=${encodeURIComponent(filter.to)}`)

  // NOT TRIMMED, AND NOT NORMALISED. The server takes `%` and `_` literally, so a search is exactly
  // what somebody typed; a client that helpfully stripped something would be a second opinion about
  // the same string.
  if (filter.search) parts.push(`search=${encodeURIComponent(filter.search)}`)

  // The cursor, never an offset: rows with `seq < before`, and the top when there is none.
  // `take` is always sent - a page size is not a filter, and not a default the server should guess.
  if (before !== undefined) parts.push(`before=${before}`)
  parts.push(`take=${take}`)

  return `?${parts.join('&')}`
}

/** How many narrowings are on, for the `Clear (n)` button — the same affordance the Kanban filter
 *  bar offers, so a person who has met one has met both. Paging is not a filter and is not counted. */
export function activeDiagnosticsFilterCount(filter: DiagnosticsFilter): number {
  return (
    (filter.kinds?.length ? 1 : 0)
    + (filter.severities?.length ? 1 : 0)
    + (filter.from ? 1 : 0)
    + (filter.to ? 1 : 0)
    + (filter.search ? 1 : 0)
  )
}

/**
 * WHY THIS GRID IS EMPTY, AND THERE ARE FOUR ANSWERS RATHER THAN ONE.
 *
 * The screen says when it is EMPTY because nothing was captured, versus empty because nothing went
 * wrong. Those are different, and this product has the rule elsewhere — `(unknown)` is a real state.
 *
 * - `rows` — there is something to show.
 * - `unreadable` — the store could not be read, and the screen must SAY so. Rendering this as
 *   "empty" tells the reader that a broken instance is a healthy one, which is the single worst
 *   sentence this screen can say.
 * - `never` — the store is readable and has never held a row. On a host that writes its startup
 *   facts at every boot that is itself a fault, not a clean bill of health.
 * - `filtered` — the store holds rows and this filter matched none of them. Nothing went wrong
 *   *here*, and the way out is to widen the filter rather than to worry.
 * - `quiet` — the store holds rows, nothing is narrowed, and the page is still empty. Everything
 *   captured has aged out under the retention bound.
 */
export type DiagnosticsEmptyState = 'rows' | 'unreadable' | 'never' | 'filtered' | 'quiet'

export function diagnosticsEmptyState(
  view: Pick<DiagnosticsView, 'hasAny'> & { page: { events: unknown[] } },
  filter: DiagnosticsFilter,
): DiagnosticsEmptyState {
  if (view.page.events.length > 0) return 'rows'

  // UNREADABLE IS CHECKED FIRST AND BEATS EVERYTHING. `hasAny` is `null` only when the store could
  // not answer, and a store that cannot say whether it holds anything cannot be reported as holding
  // nothing.
  if (view.hasAny === null) return 'unreadable'
  if (view.hasAny === false) return 'never'

  return activeDiagnosticsFilterCount(filter) > 0 ? 'filtered' : 'quiet'
}

/** The sentence the screen shows for each. Kept beside the decision so a new state cannot be added
 *  without somebody having to write what it says. */
export const DiagnosticsEmptyMessage: Record<Exclude<DiagnosticsEmptyState, 'rows'>, string> = {
  unreadable:
    'The diagnostics store could not be read, so this screen cannot say whether anything was '
    + 'captured. That is (unknown), not empty.',
  never:
    'Nothing has ever been captured. This instance records its startup facts at every boot, so an '
    + 'empty store is itself worth looking into.',
  filtered: 'Nothing matches these filters. Nothing went wrong in this window.',
  quiet: 'Nothing to show. Everything captured has aged out under the retention bound.',
}

/** Loudest reads loudest. Used for the severity chip, so a page of rows is scannable for the one
 *  that matters rather than uniformly grey. */
export const DiagnosticSeverityColor: Record<DiagnosticSeverity, string> = {
  Error: 'negative',
  Warning: 'warning',
  Info: 'grey-6',
}

/**
 * Human words for a dotted kind, the way `TenantLogDialog` has them for its dotted verbs: *"A log a
 * person has to decode is one they stop reading."*
 *
 * NOT EXHAUSTIVE ON PURPOSE, and `diagnosticKindLabel` falls back to the raw name. The vocabulary is
 * served by the host rather than copied here, so a kind added on the server appears in the filter
 * the day it ships — reading a little more technically than the rest until somebody writes it a
 * sentence. A map that had to be complete would be the second place the vocabulary lives, which is
 * exactly what serving it avoided.
 */
export const DiagnosticKindWording: Record<string, string> = {
  'http.unhandled-exception': 'Unhandled exception',
  'http.server-error': 'Server error',
  'http.refused': 'Refused',
  'db.busy': 'Database busy',
  'db.busy-timeout-expired': 'Database busy timeout expired',
  'db.migration-failed': 'Migration failed',
  'db.journal-mode': 'Journal mode',
  'process.executable-not-found': 'Executable not found',
  'process.launch-failed': 'Launch failed',
  'process.killed': 'Process killed',
  'process.output-held-open': 'Output held open',
  'transport.disconnected': 'Disconnected',
  'transport.reconnecting': 'Reconnecting',
  'worker.dropped': 'Worker dropped',
  'worker.refused': 'Worker refused',
  'worker.run-unanswered': 'Worker did not end a run',
  'worker.stale-run-stopped': 'Stale run stopped',
  'startup.instance': 'Instance',
  'startup.backup-written': 'Backup written',
  'startup.team-paused': 'Team paused',
  'startup.no-prompt-chosen': 'No prompt chosen',
  'startup.no-agent-allowlist': 'No agent allowlist',
  'startup.preset-without-usage-format': 'Preset without usage format',
  'startup.prerequisite-missing': 'Prerequisite missing',
}

export function diagnosticKindLabel(kind: string): string {
  return DiagnosticKindWording[kind] ?? kind
}

/**
 * THE GRID BELOW `md`: when it happened, how loud, what kind, and what it said.
 *
 * Nine columns do not seat in a phone-width dialog; the table would scroll sideways inside a card
 * that already fits, and the message - the column a person opened this to read - would be the one
 * off the edge. Route, status, exception type, source and detail are still one search away: the
 * search box matches them whether or not their columns are showing.
 */
export const DiagnosticsNarrowColumns: readonly string[] = ['occurredAt', 'severity', 'kind', 'message']

/** `undefined` is QTable's "show every column". */
export function diagnosticsVisibleColumns(narrow: boolean): string[] | undefined {
  return narrow ? [...DiagnosticsNarrowColumns] : undefined
}
