import type {
  CreateTriggerRequest,
  EventDefinition,
  MemberMeasuredCost,
  TeamTrigger,
  TriggerWakeManager,
  UpdateTriggerRequest,
  WatchRootOption,
} from '../api/types'

export type TriggerKind =
  | 'Cron' | 'Every' | 'Once' | 'Event' | 'FolderChange'
  | 'cron' | 'every' | 'once' | 'event' | 'folderChange'
export type TriggerMode = 'cron' | 'every' | 'once' | 'event' | 'folderChange'
export type EveryUnit = 'seconds' | 'minutes' | 'hours' | 'days' | 'weeks'

export interface TriggerShape {
  kind: TriggerKind
  expression?: string | null
  timezone?: string | null
  intervalSeconds?: number | null
  fireAt?: string | null

  /** Which event type fires this trigger. Only meaningful when `kind` is `event`. */
  eventType?: string | null

  /** `field op value`, evaluated before the wake is enqueued. Only meaningful when `kind` is
   *  `event`; null or absent means every message of that type. */
  filter?: string | null

  /** `documents`, or `root:<file-browser root name>`. Only meaningful when `kind` is `folderChange`. */
  watchRoot?: string | null

  /** Relative to `watchRoot`; blank is the root itself. Only meaningful for `folderChange`. */
  watchPath?: string | null

  /** Optional glob the listing is narrowed to. Only meaningful for `folderChange`. */
  watchGlob?: string | null
}

/**
 * The minimum a caller needs to hand `triggerSentence` for one event type - deliberately narrower
 * than the full `EventDefinition`, so a caller need not thread fields it never uses.
 */
export type EventDefinitionLike = Pick<EventDefinition, 'summary'>

/**
 * The shortest interval an every-N schedule may have, and the SERVER'S floor restated.
 *
 * A second copy of a number the Host owns, which this file otherwise avoids - and it earns its
 * place here because the alternative is worse: without it the dialog offers seconds it knows will
 * be refused, and a person meets the rule as a 400 after pressing Save rather than as a disabled
 * button while choosing. If the two drift, the server wins and the dialog is merely optimistic,
 * which is the safe direction for a duplicate to fail in.
 *
 * The bound is the AGENT rather than the scheduler. The runner sleeps until the due instant so it
 * could honour one second, but a run launches a process and takes seconds at best - below ten, an
 * idle-only schedule skips almost every occurrence and one without it queues faster than it drains.
 */
export const MinimumIntervalSeconds = 10

/**
 * A folder-change trigger's poll interval: the default, and the SERVER'S floor restated - the same
 * argument as `MinimumIntervalSeconds` above. The floor is the share rather than the agent: a
 * network share is slow to list, and listing one every few seconds is load on somebody else's
 * machine for a fire that waits out the quiet period anyway.
 */
export const DefaultPollSeconds = 60
export const MinimumPollSeconds = 15

/** How long a changed listing must hold still before it fires - so a burst fires once. */
export const DefaultQuietSeconds = 30

/** The shortest gap between two fires of one folder trigger - the loop guard against a member that
 *  writes into the folder it was woken for. */
export const DefaultMinIntervalSeconds = 60

/** The `watchRoot` value for the team's own documents folder; a file-browser root is `root:<name>`. */
export const DocumentsWatchRoot = 'documents'

/** The event a folder trigger publishes; the server sets it as every folder row's `eventType`. Named
 *  here only so the dialog can offer that event's fields as instruction tokens. */
export const FileChangedEventType = 'file.changed'

export interface TriggerDraft {
  name: string
  container: string
  instruction: string
  mode: TriggerMode
  everyCount: number
  everyUnit: EveryUnit
  cronExpression: string
  cronTimezone: string
  fireAt: string
  /**
   * When a cron or every-N schedule begins, as a `datetime-local` value; blank for "now". It never
   * fires before this. Sent as `fireAt`, which the Host reads as the start for those two kinds.
   */
  startsAt?: string
  idleOnly: boolean
  enabled: boolean

  /** Which event type fires this trigger. Only read when `mode` is `event`. */
  eventType: string

  /** `field op value`. Only read when `mode` is `event`; blank means every message of that type. */
  filter: string

  /** The folder-change fields. Only read when `mode` is `folderChange`. */
  watchRoot: string
  watchPath: string
  watchGlob: string
  pollSeconds: number
  quietSeconds: number
  minIntervalSeconds: number

  /** What a run this trigger started does to the Manager when it ends. */
  wakeManager: TriggerWakeManager

  /** The most billable tokens this trigger may spend in a day; null is no cap. */
  dailyTokenCap: number | null

  /** The outcome this trigger's fires serve, by id; '' (or absent) for none. */
  outcomeId?: string
}

/** A new trigger wakes the Manager only when its run hands back or fails. */
export const DefaultWakeManager: TriggerWakeManager = 'onHandbackOrFailure'

/**
 * A row with no `wakeManager` comes from a Host that predates the setting, and every trigger there
 * wakes the Manager on each completion - so absent reads as `always`, never as the new default.
 */
export function wakeManagerOf(row: Pick<TeamTrigger, 'wakeManager'>): TriggerWakeManager {
  return row.wakeManager ?? 'always'
}

/** The wake choice's options, in the order a person should consider them. */
export const wakeManagerOptions: readonly { label: string; value: TriggerWakeManager }[] = [
  { label: 'Only if it hands back or fails', value: 'onHandbackOrFailure' },
  { label: 'Always', value: 'always' },
  { label: 'Never', value: 'never' },
]

/** The hint under the wake choice, for the option chosen. */
export function wakeManagerHint(choice: TriggerWakeManager): string {
  switch (choice) {
    case 'always':
      return 'Every run this trigger starts wakes the Manager when it ends - a second paid run, even when nothing happened.'
    case 'never':
      return 'Nobody is woken, not even for a failure. The run is still recorded, and a failure still shows on the card and in the feed.'
    default:
      return 'The Manager is woken only when the run hands something back or fails. A run that finds nothing just finishes.'
  }
}

/**
 * A schedule that fires more often than this on an agent member asks the person to confirm first.
 * Plugin members have no minimum: a plugin run costs no model tokens.
 */
export const ShortScheduleSeconds = 5 * 60

export interface OutcomeBadge {
  label: string
  color: 'positive' | 'warning' | 'negative' | 'grey-6' | 'info'
}

/** The minimum `TriggersDialog` needs from the event catalog to label a built-in wake source -
 *  deliberately narrower than `EventDefinitionLike` above, which drops `type` because
 *  `triggerSentence` is only ever called for ONE type at a time. This one is looked up BY type. */
export type EventCatalogEntry = Pick<EventDefinition, 'type' | 'summary'>

/**
 * The category a display name is chosen for - the type's first dot-segment
 * (`agentContainer.started` -> `agentContainer`, `kanban.card.moved` -> `kanban`). Pure string
 * work, no catalog lookup needed: every type this platform declares is dotted, and
 * `EventCatalogCoverageTests` on the server pins that shape.
 */
export function categoryOfEventType(type: string): string {
  const dot = type.indexOf('.')
  return dot === -1 ? type : type.slice(0, dot)
}

/**
 * The human-facing name for a category.
 *
 * `agentContainer` reads "Agent container", never the bare word "container" - the owner's own
 * naming rule, because "container" alone is confused with a Kubernetes container. An unrecognised
 * category (a type added to the server catalog before this map is updated) falls back to itself,
 * capitalised, rather than being hidden - a picker that drops a whole category silently is worse
 * than one with a plain label.
 */
const CategoryLabels: Record<string, string> = {
  agentContainer: 'Agent container',
  workflow: 'Workflow',
  kanban: 'Kanban',
}

export function categoryLabel(category: string): string {
  const known = CategoryLabels[category]
  if (known) return known
  return category.length === 0 ? category : category[0]!.toUpperCase() + category.slice(1)
}

/** One event type, resolved for the picker: its category and that category's display label,
 *  alongside the raw fields the row needs to render. */
export interface EventTypeOption {
  type: string
  category: string
  categoryLabel: string
  summary: string
}

/**
 * Every event type the catalog declares, sorted for the picker: by category's DISPLAY LABEL first
 * (so "Agent container" and "Kanban" land where a reader expects alphabetically, not where the wire
 * word "container" would sort them), then by CODE within a category. Pure, so the ordering is
 * testable without a DOM - see this file's own header comment on why a pure function is the only
 * testable place here.
 */
export function sortEventTypesForPicker(events: EventCatalogEntry[]): EventTypeOption[] {
  return events
    .map((entry) => {
      const category = categoryOfEventType(entry.type)
      return {
        type: entry.type,
        category,
        categoryLabel: categoryLabel(category),
        summary: entry.summary,
      }
    })
    .sort((a, b) => a.categoryLabel.localeCompare(b.categoryLabel) || a.type.localeCompare(b.type))
}

/**
 * One row in the event-type `QSelect`'s flat option list - either a non-selectable CATEGORY HEADER
 * or a selectable event type. Quasar's `QSelect` has no native option-group support, and the usual
 * workaround - a flat list with disabled header rows - is what this produces: `option-disable`
 * marks a header `disable: true`, which QSelect's own keyboard navigation already SKIPS when
 * arrowing through the list, so grouping costs nothing for keyboard users. The alternative (a
 * second, nested picker per category) would lose single-list arrow-key navigation entirely.
 *
 * `value` for a header is a placeholder that can never collide with a real event type (event types
 * are always dotted with no leading `__`), so even if a caller forgot `option-disable` a header
 * could not be mistaken for a live selection.
 */
export interface EventTypeSelectOption {
  value: string
  label: string
  caption?: string
  isHeader: boolean
  disable: boolean
}

export function eventTypeSelectOptions(events: EventCatalogEntry[]): EventTypeSelectOption[] {
  const sorted = sortEventTypesForPicker(events)
  const options: EventTypeSelectOption[] = []
  let lastCategory: string | null = null

  for (const entry of sorted) {
    if (entry.category !== lastCategory) {
      options.push({
        value: `__category:${entry.category}`,
        label: entry.categoryLabel,
        isHeader: true,
        disable: true,
      })
      lastCategory = entry.category
    }

    options.push({
      value: entry.type,
      label: entry.type,
      caption: entry.summary,
      isHeader: false,
      disable: false,
    })
  }

  return options
}

/**
 * One thing this member wakes on - backed by a trigger row, or built in. `TriggersDialog` renders
 * one list of these rather than the `triggers` table alone, which is what makes the screen answer
 * "what wakes this member" instead of "what triggers has someone configured" - see
 * `builtInWakeSources` and `instructionWakeSource` below for where each kind comes from.
 */
export interface WakeSource {
  type: string
  /** The title: the event name, as the New trigger picker shows it; words for the instruction row. */
  label: string
  /** The catalog's description of the event, shown under the name; null when it has none. */
  summary: string | null
  isInstruction: boolean
}

/**
 * The message type a container's own instructions arrive as. The `tell` MCP tool reaches a member
 * through this type, and every container is subscribed to it STRUCTURALLY -
 * `EffectiveSubscriptions.EffectiveTypesAsync` adds it for every container, never as an opt-in a
 * trigger row could represent - see `ContainerHost.cs` and `EffectiveSubscriptions.cs`.
 *
 * Restated here rather than imported, because the Host's `MessageTypes.InstructionFor` is C# and
 * this file otherwise avoids a second store of a fact the server owns (see `MinimumIntervalSeconds`
 * above for the same argument applied to a number). The exception is made here because this is a
 * literal STRING FORMAT with nothing computed on either side -
 * `agentContainer.instruction.{Team}/{Name}`, from `ContainerId.ToString()` - and `triggers.spec.ts`
 * pins it against that exact shape so a server-side rename is caught rather than silently producing
 * a phantom built-in row.
 */
export function instructionTypeFor(team: string, container: string): string {
  return `agentContainer.instruction.${team}/${container}`
}

function summaryForEventType(type: string, events: EventCatalogEntry[]): string | null {
  const summary = events.find((entry) => entry.type === type)?.summary?.trim()
  return summary ? summary.replace(/\.+$/, '') : null
}

/**
 * The wake sources in `ContainerSnapshot.subscribes` that have NO trigger row behind them - for a
 * seeded Manager, the six `TeamRegistry.ManagerSubscriptions()` types
 * (`agentContainer.completed`/`failed`/`rejected`, the three `kanban.card.*` types), unioned with
 * anything a tenant admin has subscribed the container to by hand outside the trigger system.
 *
 * "Covered by a trigger" means exactly: some row of kind `event` AND `enabled` names this type as
 * its `eventType`. The `enabled` half is NOT redundant with what is in `subscribes`. It is tempting
 * to reason "a schedule trigger adds nothing to `subscribes`, so only an event trigger's type is
 * ever there, and a disabled one is already absent" and skip checking `enabled`. That reasoning
 * holds for a type an event trigger ITSELF puts into `subscribes`, but not for one of the six
 * `ManagerSubscriptions()` types, which are in `subscribes` STRUCTURALLY regardless of any trigger.
 * Disable an event trigger on `agentContainer.completed` and `subscribes` still carries
 * `agentContainer.completed` - not because of the trigger, but because a Manager always does - while
 * the trigger row itself reads "disabled". Without this check the type would vanish from BOTH the
 * trigger row (disabled) and the built-in list (wrongly excluded as "covered"), which is exactly the
 * mystery this dialog exists to remove, one layer in. Pinned by
 * `builtInWakeSources > a type with a disabled event trigger still appears as a built-in row`.
 *
 * The container's own instruction type is EXCLUDED here even though it is also in `subscribes` -
 * `instructionWakeSource` renders it as its own row, always present and never mistaken for a type a
 * trigger could ever name.
 */
export function builtInWakeSources(
  subscribes: string[],
  rows: Pick<TeamTrigger, 'kind' | 'eventType' | 'enabled'>[],
  team: string,
  container: string,
  events: EventCatalogEntry[],
): WakeSource[] {
  const instructionType = instructionTypeFor(team, container)
  const triggeredTypes = new Set(
    rows
      .filter((row) => row.enabled && normaliseKind(row.kind) === 'event' && row.eventType)
      .map((row) => row.eventType as string),
  )

  return subscribes
    .filter((type) => type !== instructionType && !triggeredTypes.has(type))
    .map((type) => ({
      type,
      label: type,
      summary: summaryForEventType(type, events),
      isInstruction: false,
    }))
}

/**
 * The row for direct instructions (the `tell` MCP tool) - shown ALWAYS, because "no mystery" is the
 * whole point of this dialog and a person asking how a member wakes should see that being told
 * something directly is one of the ways, exactly like any other wake source. It is never backed by a
 * trigger row and never editable, same as a built-in.
 *
 * The label says `tell`, the MCP tool's name, and not a CLI command: there is no `harness tell`
 * command, and a person or a skill reading this row must not go looking for one.
 */
export function instructionWakeSource(team: string, container: string): WakeSource {
  return {
    type: instructionTypeFor(team, container),
    label: 'Direct instructions (tell)',
    summary: null,
    isInstruction: true,
  }
}

const SecondsPerMinute = 60
const SecondsPerHour = 60 * SecondsPerMinute
const SecondsPerDay = 24 * SecondsPerHour
const SecondsPerWeek = 7 * SecondsPerDay
const SecondsPerYear = 366 * SecondsPerDay

function normaliseKind(kind: TriggerKind): TriggerMode {
  const folded = kind.toLowerCase()
  if (folded === 'cron') return 'cron'
  if (folded === 'once') return 'once'
  if (folded === 'event') return 'event'
  if (folded === 'folderchange') return 'folderChange'
  return 'every'
}

function plural(count: number, unit: string): string {
  return `${count} ${unit}${count === 1 ? '' : 's'}`
}

function describeDuration(seconds: number): string {
  if (seconds % SecondsPerWeek === 0) return plural(seconds / SecondsPerWeek, 'week')
  if (seconds % SecondsPerDay === 0) return plural(seconds / SecondsPerDay, 'day')
  if (seconds % SecondsPerHour === 0) return plural(seconds / SecondsPerHour, 'hour')
  if (seconds % SecondsPerMinute === 0) return plural(seconds / SecondsPerMinute, 'minute')
  return plural(seconds, 'second')
}

function formatClock(hours: string, minutes: string, seconds: string): string {
  return seconds === '0' || seconds === '00'
    ? `${hours.padStart(2, '0')}:${minutes.padStart(2, '0')}`
    : `${hours.padStart(2, '0')}:${minutes.padStart(2, '0')}:${seconds.padStart(2, '0')}`
}

function formatInstantUtc(instant: Date): string {
  const year = instant.getUTCFullYear().toString().padStart(4, '0')
  const month = (instant.getUTCMonth() + 1).toString().padStart(2, '0')
  const day = instant.getUTCDate().toString().padStart(2, '0')
  const hours = instant.getUTCHours().toString().padStart(2, '0')
  const minutes = instant.getUTCMinutes().toString().padStart(2, '0')

  return `${year}-${month}-${day} ${hours}:${minutes} UTC`
}

function splitCron(expression: string): string[] {
  return expression
    .trim()
    .split(/\s+/)
}

function describeCron(expression: string, timezone: string | null | undefined): string {
  const parts = splitCron(expression)
  const cron = parts.length === 6 ? parts : ['0', ...parts]
  const [
    seconds = '',
    minutes = '',
    hours = '',
    dayOfMonth = '',
    month = '',
    dayOfWeek = '',
  ] = cron

  const zone = timezone ? ` ${timezone}` : ''

  if (
    dayOfMonth === '*' &&
    month === '*' &&
    /^(1-5|MON-FRI|[1-5])$/i.test(dayOfWeek) &&
    /^\d+$/.test(hours) &&
    /^\d+$/.test(minutes) &&
    /^\d+$/.test(seconds)
  ) {
    return `weekdays at ${formatClock(hours, minutes, seconds)}${zone}`
  }

  if (dayOfMonth === '*' && month === '*' && dayOfWeek === '*') {
    if (/^\*\/\d+$/.test(seconds) && minutes === '*' && hours === '*') {
      return `every ${describeDuration(Number.parseInt(seconds.slice(2), 10))}`
    }

    if (seconds === '0' && /^\*\/\d+$/.test(minutes) && hours === '*') {
      return `every ${describeDuration(Number.parseInt(minutes.slice(2), 10) * SecondsPerMinute)}`
    }

    if (seconds === '0' && minutes === '0' && /^\*\/\d+$/.test(hours)) {
      return `every ${describeDuration(Number.parseInt(hours.slice(2), 10) * SecondsPerHour)}`
    }
  }

  return `cron ${expression}${zone}`
}

/**
 * A filter read back for PHRASING only - deliberately not the server's grammar object, which lives
 * in C# and never crosses the wire as anything but the raw string. Soft-fails (returns null) on
 * anything it cannot read, exactly like `TriggerFilter.Matches` does at evaluation time: a sentence
 * that cannot describe a malformed filter still renders the event half rather than throwing.
 */
function parseFilterForPhrase(filter: string): { field: string; op: 'eq' | 'contains'; value: string } | null {
  const parts = filter.trim().split(/\s+/)
  if (parts.length < 3) return null

  const [field = '', rawOp = '', ...rest] = parts
  const op = rawOp.toLowerCase()
  if (op !== 'eq' && op !== 'contains') return null

  const value = rest.join(' ')
  if (field.length === 0 || value.length === 0) return null

  return { field, op, value }
}

function filterClause(filter: string | null | undefined): string {
  if (!filter?.trim()) return ''

  const parsed = parseFilterForPhrase(filter)
  if (!parsed) return ''

  return parsed.op === 'eq'
    ? `, where ${parsed.field} is ${parsed.value}`
    : `, where ${parsed.field} contains ${parsed.value}`
}

/**
 * The words for an EVENT trigger come from the catalog's own `EventDefinition.summary`, handed in
 * by the caller - never a map of event type to phrase kept here. A hand-written copy of what an
 * event means is a second store of a fact `GET /api/events` already owns, and the two would drift
 * the moment a summary changed. `definition` is therefore a PARAMETER, never fetched inside this
 * function - see `MinimumIntervalSeconds` above for the same argument applied to a number instead
 * of a string.
 *
 * Absent a definition (a type the catalog does not know, or one not yet loaded), this falls back to
 * the raw event type rather than guessing a phrase - honest and plain, the same choice
 * `AgentCatalog.For` and `EventCatalog.For` make on the server: no fallback text stands in for a
 * definition nobody supplied.
 */
export function triggerSentence(trigger: TriggerShape, definition?: EventDefinitionLike | null): string {
  const kind = normaliseKind(trigger.kind)

  if (kind === 'event') {
    const eventType = trigger.eventType?.trim()
    const summary = definition?.summary?.trim()
    const phrase = summary
      ? summary.replace(/\.+$/, '')
      : (eventType || 'an event')

    return `when ${phrase}${filterClause(trigger.filter)}`
  }

  if (kind === 'folderChange') {
    const glob = trigger.watchGlob?.trim()
    return `when files change in ${watchLocation(trigger.watchRoot, trigger.watchPath)}${glob ? ` matching ${glob}` : ''}`
  }

  if (kind === 'every') {
    if (!Number.isInteger(trigger.intervalSeconds) || (trigger.intervalSeconds ?? 0) <= 0) {
      return 'every ?'
    }

    return `every ${describeDuration(trigger.intervalSeconds!)}`
  }

  if (kind === 'once') {
    if (!trigger.fireAt) return 'once'
    const instant = new Date(trigger.fireAt)
    if (Number.isNaN(instant.getTime())) return 'once'
    return `once, at ${formatInstantUtc(instant)}`
  }

  if (!trigger.expression?.trim()) return 'custom cron'
  return describeCron(trigger.expression, trigger.timezone)
}

/**
 * Where a folder trigger looks, as a person reads it: `documents/inbox`, `Scans/incoming`. The root
 * is named by what the dialog calls it - the file-browser root's own name, not the `root:` wire
 * prefix, which is a storage detail.
 */
export function watchLocation(watchRoot: string | null | undefined, watchPath: string | null | undefined): string {
  const root = watchRoot?.trim() || DocumentsWatchRoot
  const rootName = root.startsWith('root:') ? root.slice('root:'.length) : root
  const path = (watchPath ?? '').trim().replace(/^\/+|\/+$/g, '')
  return path ? `${rootName}/${path}` : rootName
}

/**
 * The dialog's Root picker: what `GET .../triggers/watch-roots` offered - the team's documents, then
 * every file-browser root the operator marked `allowWatch`; a root without the flag is never offered,
 * because offering it would make the server's refusal the first thing a person learns.
 *
 * Documents is ALWAYS there, even when that call failed or has not answered: it needs no operator
 * setup, and a dialog with an empty Root picker cannot make the one trigger that always works.
 *
 * A row that already names a root that is not offered (the flag is off, or the root is missing)
 * keeps that root as an option, so editing the row shows what it says rather than silently
 * re-pointing it at documents. The server's refusal on save is what explains it.
 */
export function watchRootOptions(offered: WatchRootOption[], current?: string | null): WatchRootOption[] {
  const options: WatchRootOption[] = offered.some((option) => option.value === DocumentsWatchRoot)
    ? [...offered]
    : [{ label: 'Team documents', value: DocumentsWatchRoot }, ...offered]

  const kept = current?.trim()
  if (kept && !options.some((option) => option.value === kept)) {
    options.push({ label: `${watchLocation(kept, '')} (not watchable)`, value: kept })
  }

  return options
}

/** A listing's duration as a person reads it: `340 ms`, `2.4 s`. */
export function formatPollDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) return '?'
  if (ms < 1000) return `${Math.round(ms)} ms`
  return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`
}

/**
 * The folder half of a trigger row's caption: when it last polled, how long that listing took, and
 * when it last fired. The duration is shown because a network share is slow to list, and a person
 * choosing a poll interval needs to see it.
 */
export function folderPollSummary(
  row: Pick<TeamTrigger, 'lastPollAt' | 'lastPollMs' | 'lastPollEntries' | 'lastChangeAt'>,
  now = new Date(),
): string {
  const entries = row.lastPollEntries === null || row.lastPollEntries === undefined
    ? ''
    : `, ${plural(row.lastPollEntries, 'file')}`
  const poll = row.lastPollAt
    ? `last poll ${renderPast(row.lastPollAt, now)}, took ${formatPollDuration(row.lastPollMs)}${entries}`
    : 'not polled yet'

  // `lastChangeAt`, not `lastFiredAt`: a folder trigger FIRES when it publishes `file.changed`.
  // `lastFiredAt` is when that event last woke the member, which idle-only and busy-skip can hold
  // back - the outcome chip beside the row already says that half.
  const fired = row.lastChangeAt ? `last fired ${renderPast(row.lastChangeAt, now)}` : 'never fired'

  return `${poll} • ${fired}`
}

function renderPast(iso: string, now: Date): string {
  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return 'at an unreadable time'

  const deltaSeconds = Math.floor((now.getTime() - at.getTime()) / 1000)
  if (deltaSeconds < 5) return 'just now'
  return `${relativePhrase(deltaSeconds)} ago`
}

function relativePhrase(deltaSeconds: number): string {
  const abs = Math.abs(deltaSeconds)

  if (abs < SecondsPerMinute) return plural(abs, 'second')
  if (abs < SecondsPerHour) return plural(Math.floor(abs / SecondsPerMinute), 'minute')
  if (abs < SecondsPerDay) return plural(Math.floor(abs / SecondsPerHour), 'hour')
  return plural(Math.floor(abs / SecondsPerDay), 'day')
}

export function renderNextDue(nextDueAt: string | null | undefined, now = new Date()): string {
  if (!nextDueAt) return 'not scheduled'

  const due = new Date(nextDueAt)
  if (Number.isNaN(due.getTime())) return 'invalid date'

  const deltaSeconds = Math.floor((due.getTime() - now.getTime()) / 1000)
  if (Math.abs(deltaSeconds) < 30) return 'due now'

  const phrase = relativePhrase(deltaSeconds)
  return deltaSeconds > 0 ? `in ${phrase}` : `${phrase} ago`
}

export function outcomeBadge(outcome: string | null | undefined): OutcomeBadge {
  if (!outcome) return { label: 'never fired', color: 'grey-6' }

  switch (outcome) {
    case 'fired':
      return { label: 'fired', color: 'positive' }
    case 'skipped':
      return { label: 'skipped (busy)', color: 'warning' }
    case 'missed':
      return { label: 'missed', color: 'negative' }
    case 'member-missing':
      return { label: 'member missing', color: 'negative' }
    // The fire was skipped because the trigger's daily token cap was reached.
    case 'capped':
      return { label: 'skipped (daily cap)', color: 'warning' }
    default:
      return { label: outcome.replace(/[-_]/g, ' '), color: 'info' }
  }
}

/**
 * What the daily cap is holding, in one line: a schedule asleep until the next day says when it
 * resumes, in its own timezone (UTC when it has none); otherwise how many fires the cap skipped
 * today. Null when the cap is holding nothing.
 */
export function cappedLine(row: Pick<TeamTrigger, 'cappedUntil' | 'skippedToday' | 'timezone'>): string | null {
  const until = row.cappedUntil ? new Date(row.cappedUntil) : null
  if (until && !Number.isNaN(until.getTime())) {
    const zone = row.timezone && isValidTimezone(row.timezone) ? row.timezone : 'UTC'
    return `Capped until ${formatZonedMinute(until, zone)}`
  }

  return row.skippedToday ? `skipped today: ${row.skippedToday}` : null
}

/** `2026-09-29 00:00 Asia/Tokyo`: an instant to the minute, as the clock in `zone` reads it. */
function formatZonedMinute(instant: Date, zone: string): string {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: zone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(instant)
  const read = (name: string) => parts.find((p) => p.type === name)?.value ?? ''

  return `${read('year')}-${read('month')}-${read('day')} ${read('hour')}:${read('minute')} ${zone}`
}

export function isValidTimezone(zone: string): boolean {
  try {
    Intl.DateTimeFormat('en-US', { timeZone: zone })
    return true
  } catch {
    return false
  }
}

function isValidCronField(field: string): boolean {
  return field
    .split(',')
    .every((part) => /^(\*|\d+|\d+-\d+)(\/\d+)?$/.test(part))
}

/**
 * Why a filter cannot be saved yet, or NULL when it can - the CLIENT'S half of
 * `TriggerFilter.RefusalFor`, checking only the grammar `field op value` can be read from. It
 * cannot check a field against the event's declared list, because that list lives in the catalog
 * this function is not handed - the server has the last word there, same as `MinimumIntervalSeconds`
 * above: the dialog is merely optimistic, and the safe direction for the duplicate to fail in is the
 * server winning.
 */
function filterDraftProblem(filter: string): string | null {
  const trimmed = filter.trim()
  if (trimmed.length === 0) return null

  return parseFilterForPhrase(trimmed) === null
    ? 'A filter reads `field op value`, where op is `eq` or `contains`.'
    : null
}

/**
 * Why this draft cannot be saved yet, or NULL when it can.
 *
 * ONE ANSWER TO ONE QUESTION, and that is the whole reason it exists. Asking two things - "are the
 * required fields filled" in the component and "does the shape validate" in `validateSchedule` -
 * and combining them by hand invites a mismatch: `validateSchedule` returns `string | null`, and a
 * component comparing that against `''` would disable Save forever while the warning banner
 * rendered nothing, because `null` is falsy too.
 *
 * A rule split across a pure function and a component is a rule no test should have to recover
 * from rendered fallout, and the component half would be the fragile half. This function is the
 * whole rule, and it is testable.
 *
 * THE EVENT ARM LIVES HERE TOO, for the identical reason - splitting it into the pure function for
 * clock shapes and the component for event shapes would reopen exactly the gap this function exists
 * to close, just for a different `mode`.
 *
 * Returns the SAME contract `validateSchedule` does - a message or null - so the two compose
 * without a conversion for anyone to get wrong.
 */
export function triggerDraftProblem(draft: TriggerDraft, now = new Date()): string | null {
  const problems = triggerFieldProblems(draft, now)

  for (const field of TriggerFieldOrder) {
    const problem = problems[field]
    if (problem !== undefined) return problem
  }

  return null
}

/** The draft fields a problem can belong to - one per input in the editor. */
export type TriggerField =
  | 'eventType'
  | 'name'
  | 'container'
  | 'instruction'
  | 'filter'
  | 'everyCount'
  | 'fireAt'
  | 'startsAt'
  | 'cronExpression'
  | 'cronTimezone'
  | 'watchRoot'
  | 'watchPath'
  | 'pollSeconds'
  | 'quietSeconds'
  | 'minIntervalSeconds'
  | 'dailyTokenCap'

/** The order `triggerDraftProblem` reports in, which is the order a person should fix them in. */
const TriggerFieldOrder: readonly TriggerField[] = [
  'eventType',
  'name',
  'container',
  'instruction',
  'filter',
  'everyCount',
  'fireAt',
  'startsAt',
  'cronExpression',
  'cronTimezone',
  'watchRoot',
  'watchPath',
  'pollSeconds',
  'quietSeconds',
  'minIntervalSeconds',
  'dailyTokenCap',
]

/**
 * Every reason this draft cannot be saved, BY FIELD, so the editor can put each one under the input
 * it is about. `triggerDraftProblem` is the first of these in `TriggerFieldOrder`: one rule, read
 * two ways, never a second copy of it in the component.
 *
 * Only the fields the draft's `mode` uses are judged - a cron expression left over from before the
 * kind was switched to `every` is not a problem with an `every` trigger.
 */
export function triggerFieldProblems(
  draft: TriggerDraft,
  now = new Date(),
): Partial<Record<TriggerField, string>> {
  const problems: Partial<Record<TriggerField, string>> = {}

  // Reported FIRST, deliberately. For every other mode "what fires this" is implicit in a shape
  // that is always present (the mode itself), but an event trigger with no event type chosen has
  // not really named a WHEN yet - the same category of question as "which member" - and answering
  // "give it a name" first would send someone to fill in prose for a trigger that cannot fire
  // regardless.
  if (draft.mode === 'event' && draft.eventType.trim().length === 0) {
    problems.eventType = 'Choose which event fires this trigger.'
  }

  if (draft.name.trim().length === 0) problems.name = 'A schedule needs a name.'
  if (draft.container.trim().length === 0) problems.container = 'Choose which member this wakes.'
  if (draft.instruction.trim().length === 0) problems.instruction = 'Say what the member should be told.'

  if (draft.mode === 'event') {
    const filter = filterDraftProblem(draft.filter)
    if (filter !== null) problems.filter = filter
  } else if (draft.mode === 'every') {
    // REFUSED RATHER THAN CLAMPED, and the difference is a real one. `secondsForEvery` opens with
    // `Math.max(1, Math.floor(count))`, so an EMPTY box - a number input yields NaN - and a typed
    // zero both become 1, and the schedule that gets created fires every second forever. Validating
    // the clamped value could therefore never fail, which is why this checks the raw count first.
    if (!Number.isFinite(draft.everyCount) || draft.everyCount < 1) {
      problems.everyCount = 'How often? Enter a whole number of 1 or more.'
    } else {
      // The SAME number the request will carry, unit applied. Validating the raw count instead
      // means validating something nobody sends - the two would agree only while the unit was
      // seconds.
      const every = validateSchedule(
        { kind: 'every', intervalSeconds: secondsForEvery(draft.everyCount, draft.everyUnit) },
        now,
      )
      if (every !== null) problems.everyCount = every
    }
  } else if (draft.mode === 'once') {
    const once = validateSchedule({ kind: 'once', fireAt: draft.fireAt }, now)
    if (once !== null) problems.fireAt = once
  } else if (draft.mode === 'folderChange') {
    Object.assign(problems, folderDraftProblems(draft))
  } else {
    const expression = cronExpressionProblem(draft.cronExpression)
    if (expression !== null) problems.cronExpression = expression

    const zone = cronTimezoneProblem(draft.cronTimezone)
    if (zone !== null) problems.cronTimezone = zone
  }

  // A start is optional; one that is there must be a date and time.
  if ((draft.mode === 'cron' || draft.mode === 'every')
    && (draft.startsAt ?? '').trim().length > 0
    && fromDateTimeLocalValue(draft.startsAt ?? '') === null) {
    problems.startsAt = 'The start is not a valid date and time; clear it to start now.'
  }

  if (draft.dailyTokenCap !== null && (!Number.isInteger(draft.dailyTokenCap) || draft.dailyTokenCap < 1)) {
    problems.dailyTokenCap = 'A daily cap is a whole number of tokens, 1 or more. Leave it blank for no cap.'
  }

  return problems
}

/**
 * The CLIENT'S half of the folder rules - shape only. Whether the path resolves inside the root, is a
 * symlink, or names a root that allows watching is the server's to say, because only it can look;
 * that refusal comes back as a sentence and is shown in the dialog. This refuses what is wrong
 * before anything is looked at: an absolute path, a climb out with `..`, a number below its floor.
 */
function folderDraftProblems(draft: TriggerDraft): Partial<Record<TriggerField, string>> {
  const problems: Partial<Record<TriggerField, string>> = {}

  if (draft.watchRoot.trim().length === 0) problems.watchRoot = 'Choose which folder root to watch.'

  const path = draft.watchPath.trim()
  if (path.startsWith('/') || path.startsWith('\\') || /^[A-Za-z]:/.test(path)) {
    problems.watchPath = 'The path is relative to the root - leave out the leading slash or drive.'
  } else if (path.split(/[\\/]/).includes('..')) {
    problems.watchPath = 'The path cannot climb out of the root with `..`.'
  }

  if (!Number.isInteger(draft.pollSeconds) || draft.pollSeconds < MinimumPollSeconds) {
    problems.pollSeconds = `Poll at most every ${MinimumPollSeconds} seconds - enter a whole number of ${MinimumPollSeconds} or more.`
  }

  if (!Number.isInteger(draft.quietSeconds) || draft.quietSeconds < 0) {
    problems.quietSeconds = 'Enter a whole number of seconds, 0 or more.'
  }

  if (!Number.isInteger(draft.minIntervalSeconds) || draft.minIntervalSeconds < 0) {
    problems.minIntervalSeconds = 'Enter a whole number of seconds, 0 or more.'
  }

  return problems
}

function cronExpressionProblem(expression: string | null | undefined): string | null {
  if (!expression?.trim()) return 'Cron expression is required.'
  const fields = splitCron(expression)
  if (fields.length !== 5 && fields.length !== 6) return 'Cron expression must have 5 or 6 fields.'
  if (!fields.every(isValidCronField)) return 'Cron expression contains an invalid field.'
  return null
}

function cronTimezoneProblem(timezone: string | null | undefined): string | null {
  if (!timezone?.trim()) return 'Timezone is required for cron schedules.'
  if (!isValidTimezone(timezone)) return 'Timezone must be a valid IANA zone.'
  return null
}

export function validateSchedule(
  schedule: TriggerShape,
  now = new Date(),
): string | null {
  const kind = normaliseKind(schedule.kind)

  if (kind === 'every') {
    if (!Number.isInteger(schedule.intervalSeconds)) return 'Interval must be a whole number of seconds.'

    if ((schedule.intervalSeconds ?? 0) < MinimumIntervalSeconds) {
      return `The shortest interval is ${MinimumIntervalSeconds} seconds.`
    }

    return null
  }

  if (kind === 'once') {
    if (!schedule.fireAt?.trim()) return 'Choose when this one-off schedule should fire.'
    const fireAt = new Date(schedule.fireAt)
    if (Number.isNaN(fireAt.getTime())) return 'The one-off time is not a valid timestamp.'
    if (fireAt.getTime() <= now.getTime()) return 'A one-off schedule must be in the future.'
    return null
  }

  // `kind === 'event'` falls through to here too, but nothing in this file ever calls it with one -
  // `triggerDraftProblem` branches on `event` before reaching this function, and event rows are
  // validated server-side against the catalog this function is never handed. Kept unguarded rather
  // than special-cased, since a caller that DID reach this with an event shape would want the same
  // honest "not a valid cron expression" answer a blank/absent expression already produces.
  return cronExpressionProblem(schedule.expression) ?? cronTimezoneProblem(schedule.timezone)
}

export function secondsForEvery(count: number, unit: EveryUnit): number {
  const whole = Math.max(1, Math.floor(count))
  switch (unit) {
    case 'minutes':
      return whole * SecondsPerMinute
    case 'hours':
      return whole * SecondsPerHour
    case 'days':
      return whole * SecondsPerDay
    case 'weeks':
      return whole * SecondsPerWeek
    default:
      return whole
  }
}

export function everyFromSeconds(
  intervalSeconds: number | null | undefined,
): { everyCount: number; everyUnit: EveryUnit } {
  if (!Number.isInteger(intervalSeconds) || (intervalSeconds ?? 0) < 1) {
    return { everyCount: 5, everyUnit: 'minutes' }
  }

  const seconds = intervalSeconds ?? 0
  if (seconds % SecondsPerWeek === 0) return { everyCount: seconds / SecondsPerWeek, everyUnit: 'weeks' }
  if (seconds % SecondsPerDay === 0) return { everyCount: seconds / SecondsPerDay, everyUnit: 'days' }
  if (seconds % SecondsPerHour === 0) return { everyCount: seconds / SecondsPerHour, everyUnit: 'hours' }
  if (seconds % SecondsPerMinute === 0) return { everyCount: seconds / SecondsPerMinute, everyUnit: 'minutes' }
  return { everyCount: seconds, everyUnit: 'seconds' }
}

export function toDateTimeLocalValue(iso: string | null | undefined): string {
  if (!iso) return ''
  const instant = new Date(iso)
  if (Number.isNaN(instant.getTime())) return ''
  const local = new Date(instant.getTime() - (instant.getTimezoneOffset() * 60_000))
  return local.toISOString().slice(0, 16)
}

export function fromDateTimeLocalValue(localValue: string): string | null {
  const trimmed = localValue.trim()
  if (trimmed.length === 0) return null
  const instant = new Date(trimmed)
  if (Number.isNaN(instant.getTime())) return null
  return instant.toISOString()
}

export function draftForCreate(defaultContainer = 'Manager'): TriggerDraft {
  return {
    name: '',
    container: defaultContainer,
    instruction: '',
    mode: 'every',
    everyCount: 5,
    everyUnit: 'minutes',
    cronExpression: '0 */5 * * * *',
    cronTimezone: 'UTC',
    fireAt: '',
    startsAt: '',
    idleOnly: true,
    enabled: true,
    eventType: '',
    filter: '',
    watchRoot: DocumentsWatchRoot,
    watchPath: '',
    watchGlob: '',
    pollSeconds: DefaultPollSeconds,
    quietSeconds: DefaultQuietSeconds,
    minIntervalSeconds: DefaultMinIntervalSeconds,
    wakeManager: DefaultWakeManager,
    dailyTokenCap: null,
    outcomeId: '',
  }
}

export function draftFromTrigger(row: TeamTrigger): TriggerDraft {
  const mode = normaliseKind(row.kind)
  const every = everyFromSeconds(row.intervalSeconds)

  return {
    name: row.name,
    container: row.container,
    instruction: row.instruction,
    mode,
    everyCount: every.everyCount,
    everyUnit: every.everyUnit,
    cronExpression: row.expression ?? '0 */5 * * * *',
    cronTimezone: row.timezone ?? 'UTC',
    fireAt: toDateTimeLocalValue(row.fireAt),
    startsAt: mode === 'cron' || mode === 'every' ? toDateTimeLocalValue(row.fireAt) : '',
    idleOnly: row.idleOnly,
    enabled: row.enabled,
    eventType: row.eventType ?? '',
    filter: row.filter ?? '',
    watchRoot: row.watchRoot ?? DocumentsWatchRoot,
    watchPath: row.watchPath ?? '',
    watchGlob: row.watchGlob ?? '',
    pollSeconds: row.pollSeconds ?? DefaultPollSeconds,
    quietSeconds: row.quietSeconds ?? DefaultQuietSeconds,
    minIntervalSeconds: row.minIntervalSeconds ?? DefaultMinIntervalSeconds,
    wakeManager: wakeManagerOf(row),
    dailyTokenCap: row.dailyTokenCap ?? null,
    outcomeId: row.outcomeId ?? '',
  }
}

function wireShapeFromDraft(draft: TriggerDraft) {
  const name = draft.name.trim()
  const container = draft.container.trim()
  const instruction = draft.instruction.trim()
  const kind: TriggerMode = draft.mode

  const expression = kind === 'cron' ? draft.cronExpression.trim() || null : null
  const timezone = kind === 'cron' ? draft.cronTimezone.trim() || null : null
  const intervalSeconds = kind === 'every' ? secondsForEvery(draft.everyCount, draft.everyUnit) : null
  // A one-off's instant, or a cron / every-N schedule's start - both travel as `fireAt`.
  const fireAt = kind === 'once'
    ? fromDateTimeLocalValue(draft.fireAt)
    : kind === 'cron' || kind === 'every'
      ? fromDateTimeLocalValue(draft.startsAt ?? '')
      : null
  const eventType = kind === 'event' ? draft.eventType.trim() || null : null
  const filter = kind === 'event' ? draft.filter.trim() || null : null

  const folder = kind === 'folderChange'
  const watchRoot = folder ? draft.watchRoot.trim() || null : null
  const watchPath = folder ? draft.watchPath.trim() : null
  const watchGlob = folder ? draft.watchGlob.trim() || null : null
  const pollSeconds = folder ? draft.pollSeconds : null
  const quietSeconds = folder ? draft.quietSeconds : null
  const minIntervalSeconds = folder ? draft.minIntervalSeconds : null

  return {
    name,
    container,
    instruction,
    kind,
    expression,
    timezone,
    intervalSeconds,
    fireAt,
    idleOnly: draft.idleOnly,
    enabled: draft.enabled,
    eventType,
    filter,
    watchRoot,
    watchPath,
    watchGlob,
    pollSeconds,
    quietSeconds,
    minIntervalSeconds,
    wakeManager: draft.wakeManager,
    dailyTokenCap: draft.dailyTokenCap,
    outcomeId: draft.outcomeId?.trim() ?? '',
  }
}

export function createTriggerRequestFromDraft(draft: TriggerDraft): CreateTriggerRequest {
  const wire = wireShapeFromDraft(draft)
  return {
    name: wire.name,
    container: wire.container,
    instruction: wire.instruction,
    kind: wire.kind,
    expression: wire.expression,
    timezone: wire.timezone,
    intervalSeconds: wire.intervalSeconds,
    fireAt: wire.fireAt,
    idleOnly: wire.idleOnly,
    enabled: wire.enabled,
    eventType: wire.eventType,
    filter: wire.filter,
    // Only a folder trigger carries the folder fields, so every other kind's body is unchanged and
    // a Host that predates them is never sent a field it does not know.
    ...(wire.kind === 'folderChange'
      ? {
          watchRoot: wire.watchRoot,
          watchPath: wire.watchPath,
          watchGlob: wire.watchGlob,
          pollSeconds: wire.pollSeconds,
          quietSeconds: wire.quietSeconds,
          minIntervalSeconds: wire.minIntervalSeconds,
        }
      : {}),
    wakeManager: wire.wakeManager,
    dailyTokenCap: wire.dailyTokenCap,
    // Only when one is chosen, so a trigger with none sends what it always sent.
    ...(wire.outcomeId ? { outcomeId: wire.outcomeId } : {}),
  }
}

export function updateTriggerPatchFromDraft(
  existing: TeamTrigger,
  draft: TriggerDraft,
): UpdateTriggerRequest {
  const next = wireShapeFromDraft(draft)
  const patch: UpdateTriggerRequest = {}
  const existingFireAtMs = existing.fireAt === null ? null : Date.parse(existing.fireAt)
  const nextFireAtMs = next.fireAt === null ? null : Date.parse(next.fireAt)
  const fireAtChanged = (
    next.fireAt !== null && Number.isNaN(nextFireAtMs)
  ) || (
    existing.fireAt !== null && Number.isNaN(existingFireAtMs)
  ) || (
    nextFireAtMs !== existingFireAtMs
  )

  if (next.name !== existing.name) patch.name = next.name
  if (next.container !== existing.container) patch.container = next.container
  if (next.instruction !== existing.instruction) patch.instruction = next.instruction
  if (next.kind !== existing.kind) patch.kind = next.kind
  if (next.expression !== existing.expression) patch.expression = next.expression
  if (next.timezone !== existing.timezone) patch.timezone = next.timezone
  if (next.intervalSeconds !== existing.intervalSeconds) patch.intervalSeconds = next.intervalSeconds
  if (fireAtChanged) patch.fireAt = next.fireAt
  if (next.idleOnly !== existing.idleOnly) patch.idleOnly = next.idleOnly
  if (next.enabled !== existing.enabled) patch.enabled = next.enabled
  // A folder trigger's `eventType` is the server's (always `file.changed`), and this dialog offers no
  // filter for it - so neither is compared, or opening a folder row and saving unchanged would PATCH
  // `eventType: null` over the server's value.
  if (next.kind !== 'folderChange') {
    if (next.eventType !== existing.eventType) patch.eventType = next.eventType
    if (next.filter !== existing.filter) patch.filter = next.filter
  }
  if (next.watchRoot !== (existing.watchRoot ?? null)) patch.watchRoot = next.watchRoot
  if (next.watchPath !== (existing.watchPath ?? null)) patch.watchPath = next.watchPath
  if (next.watchGlob !== (existing.watchGlob ?? null)) patch.watchGlob = next.watchGlob
  if (next.pollSeconds !== (existing.pollSeconds ?? null)) patch.pollSeconds = next.pollSeconds
  if (next.quietSeconds !== (existing.quietSeconds ?? null)) patch.quietSeconds = next.quietSeconds
  if (next.minIntervalSeconds !== (existing.minIntervalSeconds ?? null)) {
    patch.minIntervalSeconds = next.minIntervalSeconds
  }
  // Compared against what the row MEANS, so an older row (no `wakeManager`, read as `always`) saved
  // unchanged sends nothing - and choosing `always` for it is not a change either.
  if (next.wakeManager !== wakeManagerOf(existing)) patch.wakeManager = next.wakeManager
  if (next.dailyTokenCap !== (existing.dailyTokenCap ?? null)) patch.dailyTokenCap = next.dailyTokenCap
  // '' clears it, which is the route's own word for none.
  if (next.outcomeId !== (existing.outcomeId ?? '')) patch.outcomeId = next.outcomeId

  return patch
}

interface CronMatcher {
  seconds: Set<number>
  minutes: Set<number>
  hours: Set<number>
  days: Set<number>
  months: Set<number>
  weekdays: Set<number>
}

function fillRange(start: number, end: number, step: number, target: Set<number>) {
  for (let value = start; value <= end; value += step) target.add(value)
}

function parseCronField(field: string, min: number, max: number, wrapSunday = false): Set<number> | null {
  const values = new Set<number>()
  const list = field.split(',')

  for (const atom of list) {
    const part = atom.trim()
    if (part.length === 0) return null
    const [rawBase, rawStep] = part.split('/')
    const base = rawBase ?? ''
    const step = rawStep === undefined ? 1 : Number.parseInt(rawStep, 10)
    if (!Number.isInteger(step) || step < 1) return null

    if (base === '*') {
      fillRange(min, max, step, values)
      continue
    }

    if (base.includes('-')) {
      const [rawStart = '', rawEnd = ''] = base.split('-')
      const start = Number.parseInt(rawStart, 10)
      const end = Number.parseInt(rawEnd, 10)
      if (!Number.isInteger(start) || !Number.isInteger(end)) return null
      if (start < min || end > max || start > end) return null
      fillRange(start, end, step, values)
      continue
    }

    const exact = Number.parseInt(base, 10)
    if (!Number.isInteger(exact) || exact < min || exact > max) return null
    values.add(exact)
  }

  if (wrapSunday && values.has(7)) {
    values.delete(7)
    values.add(0)
  }

  return values
}

function parseCronMatcher(expression: string): CronMatcher | null {
  const fields = splitCron(expression)
  if (fields.length !== 5 && fields.length !== 6) return null
  const cron = fields.length === 5 ? ['0', ...fields] : fields
  const [
    secondsRaw = '',
    minutesRaw = '',
    hoursRaw = '',
    daysRaw = '',
    monthsRaw = '',
    weekdaysRaw = '',
  ] = cron

  const seconds = parseCronField(secondsRaw, 0, 59)
  const minutes = parseCronField(minutesRaw, 0, 59)
  const hours = parseCronField(hoursRaw, 0, 23)
  const days = parseCronField(daysRaw, 1, 31)
  const months = parseCronField(monthsRaw, 1, 12)
  const weekdays = parseCronField(weekdaysRaw, 0, 7, true)

  if (!seconds || !minutes || !hours || !days || !months || !weekdays) return null

  return { seconds, minutes, hours, days, months, weekdays }
}

const WeekdayMap: Record<string, number> = {
  Sun: 0,
  Mon: 1,
  Tue: 2,
  Wed: 3,
  Thu: 4,
  Fri: 5,
  Sat: 6,
}

function zonedParts(instant: Date, formatter: Intl.DateTimeFormat) {
  const parts = formatter.formatToParts(instant)
  const read = (name: string) => parts.find((p) => p.type === name)?.value ?? ''

  return {
    year: Number.parseInt(read('year'), 10),
    month: Number.parseInt(read('month'), 10),
    day: Number.parseInt(read('day'), 10),
    hour: Number.parseInt(read('hour'), 10),
    minute: Number.parseInt(read('minute'), 10),
    second: Number.parseInt(read('second'), 10),
    weekday: WeekdayMap[read('weekday')] ?? -1,
  }
}

function zonedFormatter(timezone: string): Intl.DateTimeFormat {
  return new Intl.DateTimeFormat('en-US', {
    timeZone: timezone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hourCycle: 'h23',
    weekday: 'short',
  })
}

function cronMatch(
  matcher: CronMatcher,
  formatter: Intl.DateTimeFormat,
  instant: Date,
): boolean {
  const parts = zonedParts(instant, formatter)
  return matcher.months.has(parts.month)
    && matcher.days.has(parts.day)
    && matcher.weekdays.has(parts.weekday)
    && matcher.hours.has(parts.hour)
    && matcher.minutes.has(parts.minute)
    && matcher.seconds.has(parts.second)
}

function formatZonedPreview(instant: Date, timezone: string): string {
  const formatter = new Intl.DateTimeFormat('en-US', {
    timeZone: timezone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hourCycle: 'h23',
  })

  return `${formatter.format(instant)} ${timezone}`
}

export function previewCronOccurrences(
  expression: string,
  timezone: string,
  count = 5,
  now = new Date(),
  startsAt: Date | null = null,
): string[] {
  // Before a start, the first time is the first one AT OR AFTER it (the Host's rule): the search
  // below begins one second after `after`, so `after` is one second before the start.
  const after = startsAt !== null && startsAt.getTime() > now.getTime()
    ? new Date(startsAt.getTime() - 1000)
    : now
  return nextCronInstants(expression, timezone, count, after)
    .map((instant) => formatZonedPreview(instant, timezone))
}

/** The next `count` instants a cron expression fires after `after`; empty when it cannot run. */
function nextCronInstants(expression: string, timezone: string, count: number, after: Date): Date[] {
  if (!isValidTimezone(timezone)) return []
  const matcher = parseCronMatcher(expression)
  if (!matcher) return []
  const formatter = zonedFormatter(timezone)

  const wanted = Math.max(1, count)
  const found: Date[] = []
  const fromMs = after.getTime() + 1000
  const limitMs = fromMs + (SecondsPerYear * 1000)

  for (let minute = fromMs - (fromMs % 60_000); minute <= limitMs; minute += 60_000) {
    for (const second of matcher.seconds) {
      const candidateMs = minute + (second * 1000)
      if (candidateMs < fromMs) continue
      const candidate = new Date(candidateMs)
      if (!cronMatch(matcher, formatter, candidate)) continue
      found.push(candidate)
      if (found.length >= wanted) return found
    }
  }

  return found
}

/** A preset the cron builder offers; days of the week are 0 (Sunday) to 6. */
export type CronBuilderSpec =
  | { preset: 'minutes', every: number }
  | { preset: 'hourly', minute: number }
  | { preset: 'daily', hour: number, minute: number }
  | { preset: 'weekly', days: number[], hour: number, minute: number }
  | { preset: 'monthly', dayOfMonth: number, hour: number, minute: number }

/**
 * The cron builder's output: the Host's SECONDS-format expression (six fields, `sec min hour dom
 * month dow`) for a preset, or null when the preset is not yet complete (a week with no day
 * chosen). Hand-editing the expression afterwards stays possible; this only writes it.
 */
export function cronFromBuilder(spec: CronBuilderSpec): string | null {
  const whole = (value: number, min: number, max: number) =>
    Math.min(max, Math.max(min, Math.floor(Number.isFinite(value) ? value : min)))

  switch (spec.preset) {
    case 'minutes':
      return `0 */${whole(spec.every, 1, 59)} * * * *`
    case 'hourly':
      return `0 ${whole(spec.minute, 0, 59)} * * * *`
    case 'daily':
      return `0 ${whole(spec.minute, 0, 59)} ${whole(spec.hour, 0, 23)} * * *`
    case 'weekly': {
      const days = [...new Set(spec.days.map((day) => whole(day, 0, 6)))].sort((a, b) => a - b)
      if (days.length === 0) return null
      return `0 ${whole(spec.minute, 0, 59)} ${whole(spec.hour, 0, 23)} * * ${days.join(',')}`
    }
    case 'monthly':
      return `0 ${whole(spec.minute, 0, 59)} ${whole(spec.hour, 0, 23)} ${whole(spec.dayOfMonth, 1, 31)} * *`
  }
}

/**
 * The trigger dialog's "Next N occurrences" for a draft: made only of what the schedule is made of -
 * the expression, the timezone and the start - so it shows while the name or the instruction is
 * still empty - it does not wait for the whole form to be valid. Empty for another kind, or for an
 * expression or timezone that cannot run; an unreadable start is treated as "now".
 */
export function cronPreviewForDraft(draft: TriggerDraft, count = 5, now = new Date()): string[] {
  if (draft.mode !== 'cron') return []
  if (cronExpressionProblem(draft.cronExpression) !== null) return []
  if (cronTimezoneProblem(draft.cronTimezone) !== null) return []

  const start = fromDateTimeLocalValue(draft.startsAt ?? '')
  return previewCronOccurrences(draft.cronExpression, draft.cronTimezone, count, now, start === null ? null : new Date(start))
}

/**
 * The shortest gap, in seconds, between two fires of a draft's schedule, or null when the draft has
 * no repeating schedule (a one-off, an event or a folder trigger) or one that cannot run yet.
 *
 * For cron it is the shortest gap among the next few fires, so `0 * 9 * * *` - every minute of the
 * nine o'clock hour - counts as every minute, which is what it costs while it runs.
 */
export function shortestScheduleGapSeconds(draft: TriggerDraft, now = new Date()): number | null {
  if (draft.mode === 'every') {
    if (!Number.isFinite(draft.everyCount) || draft.everyCount < 1) return null
    return secondsForEvery(draft.everyCount, draft.everyUnit)
  }
  if (draft.mode !== 'cron') return null
  if (cronExpressionProblem(draft.cronExpression) !== null) return null
  if (cronTimezoneProblem(draft.cronTimezone) !== null) return null

  const instants = nextCronInstants(draft.cronExpression, draft.cronTimezone, 12, now)
  let shortest: number | null = null
  for (let index = 1; index < instants.length; index++) {
    const gap = ((instants[index] as Date).getTime() - (instants[index - 1] as Date).getTime()) / 1000
    if (shortest === null || gap < shortest) shortest = gap
  }
  return shortest
}

/**
 * Whether saving this draft asks the person to confirm first: a schedule more frequent than every
 * five minutes on an agent member. A plugin member never does - its runs spend no model tokens.
 */
export function needsShortScheduleConfirmation(
  draft: TriggerDraft,
  memberKind: 'agent' | 'plugin',
  now = new Date(),
): boolean {
  if (memberKind !== 'agent') return false
  const gap = shortestScheduleGapSeconds(draft, now)
  return gap !== null && gap < ShortScheduleSeconds
}

function runs(count: number): string {
  return plural(count, 'run')
}

/** A plugin member's cost line: it runs no model, so its token cost is known, and it is zero. */
export const NoModelCostLine = 'Runs no model: no token cost'

/**
 * The member's measured cost, as one line: the median billable tokens of its recent runs and how
 * many were not measured, or that none was. Never a projection - only what runs actually reported.
 * A plugin member runs no model, and says so rather than a median of zero.
 */
export function measuredCostLine(cost: MemberMeasuredCost): string {
  if (cost.kind === 'plugin') return NoModelCostLine

  if (cost.medianBillableTokens === null || cost.measuredRuns === 0) {
    return cost.unmeasuredRuns > 0
      ? `No runs measured yet - its last ${runs(cost.unmeasuredRuns)} reported no usage.`
      : 'No runs measured yet.'
  }

  const median = `${cost.medianBillableTokens.toLocaleString()} billable tokens`
  const unmeasured = cost.unmeasuredRuns > 0 ? `, ${cost.unmeasuredRuns} not measured` : ''
  return `Median ${median} per run, over its last ${runs(cost.lastRuns)} (${cost.measuredRuns} measured${unmeasured}).`
}

/**
 * What a trigger spent today against its cap: `spent today / cap`. Unmeasured runs are said as such,
 * never folded in as zero; a row the Host sends no spend for says it is not known.
 */
export function spentTodayLine(row: Pick<TeamTrigger, 'spentToday' | 'dailyTokenCap' | 'capReachedToday'>): string | null {
  const spent = row.spentToday
  const cap = row.dailyTokenCap ?? null

  if (!spent) return cap === null ? null : `spent today not known / cap ${cap.toLocaleString()} tokens`

  // Runs that reported nothing are not a zero: with none measured the figure is not "0 tokens".
  const measured = spent.measuredRuns === 0 && spent.unmeasuredRuns > 0
    ? 'nothing measured'
    : `${spent.billableTokens.toLocaleString()} tokens`
  const unmeasured = spent.unmeasuredRuns > 0 ? ` + ${runs(spent.unmeasuredRuns)} not measured` : ''
  const total = `${measured}${unmeasured}`
  const reached = row.capReachedToday ? ' - cap reached, fires again tomorrow' : ''

  return cap === null
    ? `spent today ${total} (no cap)`
    : `spent today ${total} / cap ${cap.toLocaleString()} tokens${reached}`
}

/**
 * The confirmation a short schedule on an agent member asks for: how often it fires, what one run of
 * this member has actually cost (or that it is not known), and the cheaper shape - a plugin that
 * watches and an event trigger that wakes this member only when something happened.
 */
export function shortScheduleConfirmation(draft: TriggerDraft, cost: MemberMeasuredCost | null, now = new Date()): string {
  const gap = shortestScheduleGapSeconds(draft, now)
  const often = gap === null ? 'often' : `every ${describeDuration(gap)}`

  const perRun = cost === null
    ? 'Its measured cost per run could not be read.'
    : cost.medianBillableTokens === null || cost.measuredRuns === 0
      ? 'None of its runs has been measured yet, so its cost per run is not known.'
      : `Each run has cost a median of ${cost.medianBillableTokens.toLocaleString()} billable tokens, measured over its last ${runs(cost.lastRuns)}.`

  return `This fires ${often}, and every fire is a paid model run. ${perRun} `
    + 'A plugin can watch for free and publish an event when it finds something; an event trigger then wakes this member only when something happened.'
}

/**
 * A plugin member's skill, named from its Agent reference: `plugin:<id>` is `plugin-<id>`, the name
 * the platform forces on a plugin's skill and the `hiring` tool lists. Null for anything else.
 */
export function pluginSkillFor(agent: string | null | undefined): string | null {
  const match = /^plugin:(.+)$/.exec((agent ?? '').trim())
  return match ? `plugin-${match[1]}` : null
}

/**
 * The instruction box's heading and hint. An agent is TOLD something; a plugin is handed the same
 * text as its work, and reads it as a command in its own syntax, which its skill documents - so
 * for a plugin the box is a Command and the hint names where the commands are described.
 */
export function instructionFieldText(
  memberKind: 'agent' | 'plugin',
  memberAgent: string | null | undefined,
): { label: string; hint: string } {
  if (memberKind !== 'plugin') {
    return { label: 'Instruction', hint: 'What the member is told when this fires.' }
  }

  const skill = pluginSkillFor(memberAgent)
  return {
    label: 'Command',
    hint: skill
      ? `Sent to the plugin as its instruction, in the plugin's own command syntax: see its skill ${skill}.`
      : "Sent to the plugin as its instruction, in the plugin's own command syntax: see its skill.",
  }
}
