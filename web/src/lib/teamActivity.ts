import type { ActivitySpan, ActivityState, ActivityWindow, TeamActivity, TeamTokenRun } from '../api/types'
import { localDate, localStretch, localTime } from './localTime'

/**
 * WHAT THE STATISTICS TILE DRAWS FROM ONE `/activity` ANSWER, as plain functions so its initials,
 * its lanes, its words and its colours are pinned without mounting a chart.
 *
 * NO STATE IS DECIDED HERE. The server says which state each stretch was in; these only order the
 * lanes, draw a still-open stretch to the window's end and put the result into words. NOTHING READS
 * THE CLOCK: the window is the server's, and it moves only when something happens in a workflow. A stretch nothing recorded
 * stays absent - it is never drawn, and never counted as idle.
 */

/** A span with its instants as milliseconds, an open one drawn to the window's end. */
export interface GrownSpan {
  state: ActivityState
  from: number
  to: number
  /** Still in this state at the window's end: its `to` is that end. */
  open: boolean
  workflow?: number
  reason?: string
  reasonKind?: string
}

/** One drawn lane: the member's stored name, the label a person reads, and its initials. */
export interface Lane {
  member: string
  name: string
  initials: string
  spans: GrownSpan[]
}

/** The order states are counted in, in the summary and anywhere else they are listed. */
export const ActivityStates: readonly ActivityState[] = ['running', 'waiting', 'held', 'blocked', 'failed', 'idle']

/** A state as a person reads it: `held` is "waiting for a slot", the rest their own word. */
export function stateWords(state: ActivityState): string {
  return state === 'held' ? 'waiting for a slot' : state
}

/**
 * WHAT A HOLD WAITED FOR, after "waiting for a slot": the hold's own sentence less its "waiting for "
 * ("memory: 11.2 of 12.9 GB in use"), "no worker connected" for a worker, and nothing for the run
 * limit, which the words already say.
 */
export function heldFor(span: { reason?: string; reasonKind?: string }): string | null {
  if (span.reasonKind === 'slot') return null
  if (span.reasonKind === 'worker') return 'no worker connected'
  if (!span.reason) return null

  return span.reason.replace(/^waiting for /, '')
}

/**
 * A NAME'S INITIALS: its words split on space, `-`, `_` and `.`; two or more words give the first
 * and the LAST word's initial ("Ana Maria Lopez" → "AL"), one word gives its first letter. Always
 * uppercase, so "rhea" and "Rhea" read the same.
 */
export function memberInitials(name: string): string {
  const words = name.split(/[\s\-_.]+/).filter((word) => word.length > 0)

  if (words.length === 0) return '?'

  const first = words[0]!
  const last = words[words.length - 1]!

  return (words.length === 1 ? first[0]! : first[0]! + last[0]!).toUpperCase()
}

/**
 * EVERY LANE'S INITIALS, in the order given (board order). Initials two lanes share are numbered in
 * that order ("D1", "D2"), every one of them and not only the second, so neither reads as the
 * "real" D. Initials nobody shares are left as they are.
 */
export function laneInitials(names: readonly string[]): string[] {
  const bare = names.map(memberInitials)
  const counts = new Map<string, number>()

  for (const initials of bare) counts.set(initials, (counts.get(initials) ?? 0) + 1)

  const seen = new Map<string, number>()

  return bare.map((initials) => {
    if ((counts.get(initials) ?? 0) < 2) return initials

    const n = (seen.get(initials) ?? 0) + 1
    seen.set(initials, n)

    return `${initials}${n}`
  })
}

/**
 * WHERE AN ANSWER'S LANES END: its window's `to`, never past its `serverNow`. A span the server left
 * open is drawn to here and no further, however long ago the answer was read.
 */
export function windowEnd(activity: TeamActivity): number {
  const now = Date.parse(activity.serverNow)
  const to = activity.to === null ? now : Date.parse(activity.to)

  return Number.isNaN(now) ? to : Math.min(to, now)
}

/** The spans as instants, an open one (`to: null`) drawn to `end`; one reaching `end` is open there. */
export function growSpans(spans: readonly ActivitySpan[], end: number): GrownSpan[] {
  return spans.map((span) => {
    const to = span.to === null ? end : Date.parse(span.to)
    const grown: GrownSpan = { state: span.state, from: Date.parse(span.from), to, open: to >= end }

    if (span.workflow !== undefined) grown.workflow = span.workflow
    if (span.reason !== undefined) grown.reason = span.reason
    if (span.reasonKind !== undefined) grown.reasonKind = span.reasonKind

    return grown
  })
}

/**
 * ONE LANE PER CURRENT MEMBER: the Manager first, then the members as the board lists them
 * (`containers`, matched by id), then any the board does not list yet in the server's order. A
 * member since removed (`current: false`) gets no lane, unless `removed` asks for them: then they
 * follow the current ones, in the server's order, labelled "<name> (removed)". The label is the
 * board's name for the member when it has one, else the stored name.
 */
export function activityLanes(
  activity: TeamActivity,
  containers: readonly { id: string; name: string }[],
  end: number,
  options: { removed?: boolean } = {},
): Lane[] {
  const boardIndex = (member: string) => {
    const index = containers.findIndex((c) => c.id.toLowerCase() === member.toLowerCase())

    return index < 0 ? containers.length : index
  }

  const current = activity.members
    .map((member, serverIndex) => ({ member, serverIndex }))
    .filter(({ member }) => member.current)
    .sort((a, b) =>
      Number(b.member.isManager) - Number(a.member.isManager)
      || boardIndex(a.member.member) - boardIndex(b.member.member)
      || a.serverIndex - b.serverIndex)
    .map(({ member }) => member)

  const removed = options.removed ? activity.members.filter((member) => !member.current) : []
  const listed = [...current, ...removed]

  const names = listed.map((member) => member.current
    ? containers.find((c) => c.id.toLowerCase() === member.member.toLowerCase())?.name ?? member.member
    : member.member)
  const initials = laneInitials(names)

  return listed.map((member, index) => ({
    member: member.member,
    name: member.current ? names[index]! : `${names[index]!} (removed)`,
    initials: initials[index]!,
    spans: growSpans(member.spans, end),
  }))
}

/** The span a lane is in at an instant, or null when nothing was recorded then. */
export function spanAt(lane: Lane, at: number): GrownSpan | null {
  return lane.spans.find((span) => span.from <= at && (at < span.to || (span.open && at <= span.to))) ?? null
}

/** How long a stretch lasted, in words short enough for one tooltip line. */
export function stretchLength(ms: number): string {
  const minutes = Math.floor(ms / 60_000)

  if (minutes < 1) return 'under a minute'
  if (minutes < 60) return `${minutes} min`

  const hours = Math.floor(minutes / 60)

  if (hours < 24) return minutes % 60 === 0 ? `${hours} h` : `${hours} h ${minutes % 60} min`

  const days = Math.floor(hours / 24)

  return hours % 24 === 0 ? `${days} d` : `${days} d ${hours % 24} h`
}

const plural = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`

/**
 * THE TILE'S `aria-label`: the window once at the front, then how many lanes and each member's
 * state at the window's end counted by state - "Statistics 11:38:02 AM – 3:41:17 PM: 3 members - 2
 * running, 1 blocked". A member with no span there is counted as such, never as idle.
 */
export function activitySummary(
  window: ActivityWindow,
  lanes: readonly Lane[],
  from: number | null,
  end: number,
): string {
  if (window === 'none') return 'Statistics: No workflows yet'

  const counts = new Map<ActivityState, number>()
  let nothing = 0

  for (const lane of lanes) {
    const span = spanAt(lane, end)

    if (span) counts.set(span.state, (counts.get(span.state) ?? 0) + 1)
    else nothing++
  }

  const parts = ActivityStates
    .filter((state) => counts.has(state))
    .map((state) => `${counts.get(state)} ${stateWords(state)}`)

  if (nothing > 0) parts.push(`${nothing} with no runs at the end`)

  const stretch = from === null ? '' : ` ${localStretch(from, end)}`

  return `Statistics${stretch}: ${plural(lanes.length, 'member', 'members')} - ${parts.join(', ')}`
}

/**
 * THE CAPTION UNDER THE LANES: the window's stretch, and how many workflows are open when any are;
 * for a team that never ran, that alone. A null `openCount` is a workflow list not read yet, and
 * names no count rather than a wrong one.
 */
export function activityCaption(
  window: ActivityWindow,
  from: number | null,
  to: number | null,
  openCount: number | null,
): string {
  if (window === 'none') return 'No workflows yet'

  const parts = []

  if (from !== null && to !== null) parts.push(localStretch(from, to))
  if (openCount !== null && openCount > 0) parts.push(plural(openCount, 'open workflow', 'open workflows'))

  return parts.join(' · ')
}

/** Text made safe to place in markup: a name or a reason is characters, never tags. */
export function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

/**
 * THE HOVER TOOLTIP AS MARKUP: the time under the cursor, then one line per lane - colour chip,
 * initials, full name, and the state with how long that stretch lasted and its reason, or "no runs
 * in this window" where nothing was recorded at that time.
 *
 * EVERY NAME AND REASON IS ESCAPED, and the markup carries CLASSES, NEVER A `style` ATTRIBUTE: the
 * app's CSP refuses inline styles, so a styled chip would arrive unstyled and log a violation.
 */
export function tooltipHtml(lanes: readonly Lane[], at: number, withDate: boolean): string {
  const rows = lanes.map((lane) => {
    const span = spanAt(lane, at)
    const chip = `<span class="stats-chip stats-chip--${span ? span.state : 'none'}"></span>`
    const who = `<span class="stats-tip-initials">${escapeHtml(lane.initials)}</span> `
      + `<span class="stats-tip-name">${escapeHtml(lane.name)}</span>`

    let what = 'no runs in this window'

    if (span) {
      what = `${stateWords(span.state)} ${stretchLength(span.to - span.from)}`
      const reason = span.state === 'held' ? heldFor(span) : span.reason
      if (reason) what += ` (${reason})`
    }

    return `<div class="stats-tip-row">${chip}${who} — ${escapeHtml(what)}</div>`
  })

  return `<div class="stats-tip"><div class="stats-tip-time">${escapeHtml(localTime(at, { date: withDate }))}</div>${rows.join('')}</div>`
}

/** How wide one column of the Statistics dialog is: a local minute, hour, day or month. */
export type Bucket = 'minute' | 'hour' | 'day' | 'month'

/** Milliseconds per state. */
export type StateTime = Record<ActivityState, number>

/** One member's share of a column. */
export interface MemberTime {
  member: string
  ms: StateTime
}

/**
 * ONE COLUMN: a bucket's edges as instants, the member-time recorded in it per state, and the same
 * per member - only the members with any time in it, in the order the lanes were given.
 */
export interface Column {
  from: number
  to: number
  totals: StateTime
  members: MemberTime[]
}

const noTime = (): StateTime => ({ running: 0, waiting: 0, held: 0, blocked: 0, failed: 0, idle: 0 })

const zoneFormats = new Map<string, Intl.DateTimeFormat>()

/** A zone's wall clock at an instant, as numbers. */
function wallClock(ms: number, timeZone: string) {
  let format = zoneFormats.get(timeZone)

  if (!format) {
    format = new Intl.DateTimeFormat('en-US', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric',
      month: 'numeric',
      day: 'numeric',
      hour: 'numeric',
      minute: 'numeric',
      second: 'numeric',
    })
    zoneFormats.set(timeZone, format)
  }

  const part = (parts: Intl.DateTimeFormatPart[], type: string) => Number(parts.find((p) => p.type === type)?.value)
  const parts = format.formatToParts(ms)

  return {
    year: part(parts, 'year'),
    month: part(parts, 'month'),
    day: part(parts, 'day'),
    hour: part(parts, 'hour'),
    minute: part(parts, 'minute'),
    second: part(parts, 'second'),
  }
}

/** How far a zone's wall clock is ahead of UTC at an instant. */
function zoneOffset(ms: number, timeZone: string): number {
  const wall = wallClock(ms, timeZone)
  const whole = Math.floor(ms / 1000) * 1000

  return Date.UTC(wall.year, wall.month - 1, wall.day, wall.hour, wall.minute, wall.second) - whole
}

/**
 * THE FIRST INSTANT A ZONE'S CLOCK READS a local midnight (month is 1-based and may overflow, as
 * `Date.UTC` allows). Where clocks go back over it, the earlier of the two; where they jump over it,
 * the instant they jump - the day still starts, just not at 00:00.
 */
function startOfLocalDay(year: number, month: number, day: number, timeZone: string): number {
  const wall = Date.UTC(year, month - 1, day)
  const candidates = [zoneOffset(wall, timeZone)]

  candidates.push(zoneOffset(wall - candidates[0]!, timeZone))

  const exact = candidates
    .map((offset) => wall - offset)
    .filter((instant) => instant + zoneOffset(instant, timeZone) === wall)

  if (exact.length > 0) return Math.min(...exact)

  // A GAP: the local midnight never happened. Find the jump between the two readings.
  let low = Math.min(wall - candidates[0]!, wall - candidates[1]!)
  let high = Math.max(wall - candidates[0]!, wall - candidates[1]!)

  while (high - low > 1) {
    const middle = Math.floor((low + high) / 2)

    if (middle + zoneOffset(middle, timeZone) >= wall) high = middle
    else low = middle
  }

  return high
}

/** The start of the local bucket an instant falls in. */
function bucketStart(ms: number, bucket: Bucket, timeZone: string): number {
  if (bucket === 'minute' || bucket === 'hour') {
    const size = bucket === 'minute' ? 60_000 : 3_600_000
    const local = ms + zoneOffset(ms, timeZone)

    return ms - (((local % size) + size) % size)
  }

  const wall = wallClock(ms, timeZone)

  return startOfLocalDay(wall.year, wall.month, bucket === 'day' ? wall.day : 1, timeZone)
}

/**
 * The start of the bucket after the one starting at `start`. Minutes and hours step by real time,
 * so an hour clocks repeat is a column of its own; days and months step by the calendar, so a day
 * clocks go back over is 25 hours long and one they jump over 23.
 */
function nextBucket(start: number, bucket: Bucket, timeZone: string): number {
  if (bucket === 'minute') return bucketStart(start + 60_000, bucket, timeZone)
  if (bucket === 'hour') return bucketStart(start + 3_600_000, bucket, timeZone)

  // Read the wall clock just past the start: at a jumped midnight the start reads the day before.
  const wall = wallClock(start + 3_600_000, timeZone)

  return bucket === 'day'
    ? startOfLocalDay(wall.year, wall.month, wall.day + 1, timeZone)
    : startOfLocalDay(wall.year, wall.month + 1, 1, timeZone)
}

/**
 * MEMBER-TIME PER BUCKET: every span split at the bucket edges of `timeZone` - local minute, hour,
 * midnight or the first of the month - and summed per state, and per member for the tooltip. An
 * open span counts up to the `to` it was grown to. A bucket nothing was recorded in has no column:
 * no data adds nothing, and is never idle.
 */
export function bucketActivity(
  lanes: readonly { member: string; spans: readonly GrownSpan[] }[],
  bucket: Bucket,
  timeZone: string,
): Column[] {
  const columns = new Map<number, Column>()

  lanes.forEach((lane) => {
    for (const span of lane.spans) {
      let start = bucketStart(span.from, bucket, timeZone)

      while (start < span.to) {
        const end = nextBucket(start, bucket, timeZone)
        const time = Math.min(end, span.to) - Math.max(start, span.from)

        if (time > 0) {
          let column = columns.get(start)

          if (!column) {
            column = { from: start, to: end, totals: noTime(), members: [] }
            columns.set(start, column)
          }

          let mine = column.members.find((m) => m.member === lane.member)

          if (!mine) {
            mine = { member: lane.member, ms: noTime() }
            column.members.push(mine)
          }

          column.totals[span.state] += time
          mine.ms[span.state] += time
        }

        start = end
      }
    }
  })

  const order = new Map(lanes.map((lane, index) => [lane.member, index]))

  return [...columns.values()]
    .sort((a, b) => a.from - b.from)
    .map((column) => ({
      ...column,
      members: column.members.sort((a, b) => order.get(a.member)! - order.get(b.member)!),
    }))
}

/**
 * A BUCKET AS THE TOOLTIP NAMES IT, in the browser's locale and the given zone: a minute or an hour
 * by its two ends' times of day ("2:20:00 PM – 2:21:00 PM"), each with its date when the window
 * crosses days; a day by its date; a month by its month and year.
 */
export function bucketLabel(from: number, to: number, bucket: Bucket, timeZone: string, withDate: boolean): string {
  if (bucket === 'day') return localDate(from, timeZone)
  if (bucket === 'month') return new Date(from).toLocaleDateString(undefined, { timeZone, year: 'numeric', month: 'long' })

  return `${localTime(from, { date: withDate, timeZone })} – ${localTime(to, { date: withDate, timeZone })}`
}

/**
 * A TIME AXIS LABEL as the locale reads it: within minute and hour columns a time of day, or the
 * date where the axis crosses a local midnight; within day columns the date; within months the month.
 */
export function axisTimeLabel(ms: number, bucket: Bucket, timeZone: string): string {
  if (bucket === 'month') return new Date(ms).toLocaleDateString(undefined, { timeZone, year: 'numeric', month: 'long' })
  if (bucket === 'day') return localDate(ms, timeZone)

  const wall = wallClock(ms, timeZone)

  return wall.hour === 0 && wall.minute === 0 && wall.second === 0 ? localDate(ms, timeZone) : localTime(ms, { timeZone })
}

/**
 * Member-time in words short enough for one tooltip line: "40 s", "3 min 20 s", "2 h 5 min". Time
 * under a second is "<1 s": a state listed with time in it never reads as none.
 */
export function memberTime(ms: number): string {
  if (ms > 0 && ms < 1000) return '<1 s'

  const seconds = Math.round(ms / 1000)

  if (seconds < 60) return `${seconds} s`

  const minutes = Math.floor(seconds / 60)

  if (minutes < 60) return seconds % 60 === 0 ? `${minutes} min` : `${minutes} min ${seconds % 60} s`

  const hours = Math.floor(minutes / 60)

  return minutes % 60 === 0 ? `${hours} h` : `${hours} h ${minutes % 60} min`
}

/**
 * ONE COLUMN'S HOVER TOOLTIP AS MARKUP: the bucket, each state's total, then each member's split
 * with the lane's initials ("IL Ines Lopez — running 40 s, blocked 20 s"). States with no time are
 * left out. Escaped text in classed markup, never a `style` attribute, as in {@link tooltipHtml}.
 */
export function columnTooltipHtml(
  column: Column,
  bucket: Bucket,
  timeZone: string,
  people: ReadonlyMap<string, { name: string; initials: string }>,
  options: { hidden?: ReadonlySet<ActivityState>; withDate?: boolean } = {},
): string {
  // A STATE HIDDEN IN THE LEGEND is left out of the totals and the splits: the hover says what is drawn.
  const shown = ActivityStates.filter((state) => !options.hidden?.has(state))
  const split = (ms: StateTime) => shown
    .filter((state) => ms[state] > 0)
    .map((state) => `${stateWords(state)} ${memberTime(ms[state])}`)

  const totals = shown
    .filter((state) => column.totals[state] > 0)
    .map((state) => `<div class="stats-tip-row"><span class="stats-chip stats-chip--${state}"></span>`
      + `${escapeHtml(`${stateWords(state)} ${memberTime(column.totals[state])}`)}</div>`)

  const members = column.members.flatMap((share) => {
    const person = people.get(share.member) ?? { name: share.member, initials: memberInitials(share.member) }
    const parts = split(share.ms)

    return parts.length === 0
      ? []
      : [`<div class="stats-tip-row"><span class="stats-tip-initials">${escapeHtml(person.initials)}</span> `
        + `<span class="stats-tip-name">${escapeHtml(person.name)}</span> — ${escapeHtml(parts.join(', '))}</div>`]
  })

  const label = bucketLabel(column.from, column.to, bucket, timeZone, options.withDate ?? false)

  return `<div class="stats-tip"><div class="stats-tip-time">${escapeHtml(label)}</div>`
    + `${totals.join('')}<div class="stats-tip-members">${members.join('')}</div></div>`
}

/**
 * THE COLUMN SIZE THAT FITS A WINDOW, for the Statistics dialog and the Tokens chart alike: the
 * minute up to two hours, the hour up to three days, the day up to three months by the UTC calendar,
 * the month beyond.
 */
export function windowBucket(from: number, to: number): Bucket {
  const span = to - from

  if (span <= 2 * 3_600_000) return 'minute'
  if (span <= 3 * 86_400_000) return 'hour'

  // THREE CALENDAR MONTHS ON, held to the last day of that month: 31 January runs to 30 April, not
  // on into May as `setUTCMonth` would carry it.
  const start = new Date(from)
  const month = start.getUTCMonth() + 3
  const lastDay = new Date(Date.UTC(start.getUTCFullYear(), month + 1, 0)).getUTCDate()
  const threeMonths = Date.UTC(
    start.getUTCFullYear(), month, Math.min(start.getUTCDate(), lastDay),
    start.getUTCHours(), start.getUTCMinutes(), start.getUTCSeconds(), start.getUTCMilliseconds())

  return to <= threeMonths ? 'day' : 'month'
}

/** The words for a column size in a subtitle: "per minute", "per hour", "per day", "per month". */
export function bucketWords(bucket: Bucket): string {
  return `per ${bucket}`
}

/**
 * WHERE THE HOVER BOX GOES, in the chart's own pixels as an ECharts `tooltip.position` answers: BESIDE
 * the pointer, never over it, so the hover line stays in sight - `gap` to its right, or `gap` to its
 * left where the box would run off the screen's right edge. Vertically centred on the pointer and
 * held on the screen. It may reach past the chart's own bounds: the box is appended to the body.
 */
export function tooltipBeside(
  point: readonly number[],
  box: readonly number[],
  chart: { left: number; top: number },
  viewport: { width: number; height: number },
  gap: number,
): [number, number] {
  const [x, y] = [point[0] ?? 0, point[1] ?? 0]
  const [width, height] = [box[0] ?? 0, box[1] ?? 0]

  const right = x + gap
  const left = x - gap - width
  const fitsRight = chart.left + right + width <= viewport.width
  const fitsLeft = chart.left + left >= 0

  // The screen's top in the chart's pixels; written as a subtraction so a chart at 0 reads 0, not -0.
  const screenTop = 0 - chart.top
  const top = Math.max(Math.min(y - height / 2, viewport.height - chart.top - height), screenTop)

  return [fitsRight || !fitsLeft ? right : left, top]
}

/** Which figure of a run the Tokens chart sums. */
export type TokenMetric = 'billable' | 'in' | 'cacheRead' | 'cacheWrite' | 'out'

/** The metric picker's choices, in its order: Billable first, the default. */
export const TokenMetrics: readonly { value: TokenMetric; label: string }[] = [
  { value: 'billable', label: 'Billable' },
  { value: 'in', label: 'In' },
  { value: 'cacheRead', label: 'Cache read' },
  { value: 'cacheWrite', label: 'Cache write' },
  { value: 'out', label: 'Out' },
]

/** Why a run has no figure: it reported no usage, or only a combined total with no split. */
export type FigureGap = 'unmeasured' | 'split'

/**
 * A RUN'S FIGURE FOR A METRIC, or why it has none. An unmeasured run has none for any metric; a
 * combined total has its billable and no In, Cache read, Cache write or Out - "split not reported".
 * A figure a measured run did not report is not measured either: nothing is read as 0.
 */
export function runFigure(run: TeamTokenRun, metric: TokenMetric): { value: number } | { gap: FigureGap } {
  if (!run.measured) return { gap: 'unmeasured' }

  const value = {
    billable: run.billable,
    in: run.tokensIn,
    cacheRead: run.tokensCachedIn,
    cacheWrite: run.tokensCacheCreation,
    out: run.tokensOut,
  }[metric]

  if (value !== undefined) return { value }

  return metric !== 'billable' && run.combined !== undefined ? { gap: 'split' } : { gap: 'unmeasured' }
}

/** One run's figure at the instant it ended; `value` null with the `gap` that explains it. */
export interface InstantFigure {
  member: string
  at: number
  value: number | null
  gap?: FigureGap
}

/** One member's share of a token bucket: its summed figure, and how many of its runs had one or not. */
export interface MemberFigure {
  member: string
  value: number
  measured: number
  unmeasured: number
  unsplit: number
}

/**
 * ONE TOKEN BUCKET: its edges, the figure summed over the runs that ended in it, how many runs had a
 * figure, how many were not measured and how many reported no split - and the same per member.
 */
export interface FigureColumn {
  from: number
  to: number
  total: number
  measured: number
  unmeasured: number
  unsplit: number
  members: MemberFigure[]
}

/**
 * FIGURES AT AN INSTANT, PER BUCKET: each run's figure placed in the local bucket of `timeZone` its
 * end fell in - an instant on an edge belongs to the bucket that starts there - and summed, per
 * member in `order` (members not in it after, as met). A run with no figure is COUNTED, never added
 * as 0, and a bucket holding only such runs still has a column so it can say so. A bucket where no
 * run ended has none.
 */
export function bucketFigures(
  points: readonly InstantFigure[],
  order: readonly string[],
  bucket: Bucket,
  timeZone: string,
): FigureColumn[] {
  const columns = new Map<number, FigureColumn>()

  for (const point of points) {
    const start = bucketStart(point.at, bucket, timeZone)
    let column = columns.get(start)

    if (!column) {
      column = { from: start, to: nextBucket(start, bucket, timeZone), total: 0, measured: 0, unmeasured: 0, unsplit: 0, members: [] }
      columns.set(start, column)
    }

    let mine = column.members.find((m) => m.member === point.member)

    if (!mine) {
      mine = { member: point.member, value: 0, measured: 0, unmeasured: 0, unsplit: 0 }
      column.members.push(mine)
    }

    if (point.value !== null) {
      column.total += point.value
      column.measured++
      mine.value += point.value
      mine.measured++
    } else if (point.gap === 'split') {
      column.unsplit++
      mine.unsplit++
    } else {
      column.unmeasured++
      mine.unmeasured++
    }
  }

  const rank = (member: string) => {
    const index = order.indexOf(member)

    return index < 0 ? order.length : index
  }

  return [...columns.values()]
    .sort((a, b) => a.from - b.from)
    .map((column) => ({ ...column, members: column.members.sort((a, b) => rank(a.member) - rank(b.member)) }))
}

/** A token count as a person reads it, grouped in their locale. */
export function tokenCount(value: number): string {
  return value.toLocaleString()
}

/** The words for runs with no figure: "2 runs not measured", "1 run: split not reported". */
function gapWords(unmeasured: number, unsplit: number): string[] {
  const words: string[] = []

  if (unmeasured > 0) words.push(`${plural(unmeasured, 'run', 'runs')} not measured`)
  if (unsplit > 0) words.push(`${plural(unsplit, 'run', 'runs')}: split not reported`)

  return words
}

/**
 * ONE TOKEN BUCKET'S HOVER TOOLTIP AS MARKUP: the bucket, each member's figure and its runs with no
 * figure, the bucket's total and its runs with no figure. A member or a bucket whose runs all lack a
 * figure reads so, never as 0. Escaped text in classed markup, never a `style` attribute, as in
 * {@link tooltipHtml}.
 */
export function figureTooltipHtml(
  column: FigureColumn,
  bucket: Bucket,
  timeZone: string,
  people: ReadonlyMap<string, { name: string }>,
  options: { hidden?: ReadonlySet<string>; gapsHidden?: boolean; withDate?: boolean } = {},
): string {
  // A MEMBER HIDDEN IN THE LEGEND is left out of the lines and of the total, and with the
  // not-measured markers hidden so are the runs with no figure: the hover says what is drawn.
  const gaps = (unmeasured: number, unsplit: number) => (options.gapsHidden ? [] : gapWords(unmeasured, unsplit))
  const shown = column.members.filter((share) => !options.hidden?.has(share.member))
  const sum = (pick: (share: MemberFigure) => number) => shown.reduce((total, share) => total + pick(share), 0)
  const visible = options.hidden?.size
    ? { total: sum((m) => m.value), measured: sum((m) => m.measured), unmeasured: sum((m) => m.unmeasured), unsplit: sum((m) => m.unsplit) }
    : column

  const members = shown.flatMap((share) => {
    const name = people.get(share.member)?.name ?? share.member
    const parts = [...(share.measured > 0 ? [tokenCount(share.value)] : []), ...gaps(share.unmeasured, share.unsplit)]

    if (parts.length === 0) return []

    return `<div class="stats-tip-row"><span class="tokens-chip tokens-chip--${seriesSlot(share.member, people)}"></span>`
      + `<span class="stats-tip-name">${escapeHtml(name)}</span> — ${escapeHtml(parts.join(', '))}</div>`
  })

  const total = visible.measured > 0 ? `Total ${tokenCount(visible.total)}` : 'Total not measured'
  const gapRows = gaps(visible.unmeasured, visible.unsplit)
    .map((words) => `<div class="stats-tip-row tokens-tip-gap">${escapeHtml(words)}</div>`)
  const label = bucketLabel(column.from, column.to, bucket, timeZone, options.withDate ?? false)

  return `<div class="stats-tip"><div class="stats-tip-time">${escapeHtml(label)}</div>`
    + `<div class="stats-tip-members">${members.join('')}</div>`
    + `<div class="stats-tip-row tokens-tip-total">${escapeHtml(total)}</div>${gapRows.join('')}</div>`
}

/** How many member colours the theme has (`--os-series-1` to `--os-series-8`). */
export const SeriesSlots = 8

/** A member's colour slot, 1-based, by its place among `people` (board order): fixed, never by rank. */
function seriesSlot(member: string, people: ReadonlyMap<string, unknown>): number {
  const index = [...people.keys()].indexOf(member)

  return index < 0 ? 1 : (index % SeriesSlots) + 1
}

/** The y axis's unit for a bucket, and how many milliseconds one of it is. */
export function bucketUnit(bucket: Bucket): { name: string; ms: number } {
  return bucket === 'minute' || bucket === 'hour'
    ? { name: 'member-minutes', ms: 60_000 }
    : { name: 'member-hours', ms: 3_600_000 }
}

/** The colours a state is drawn in, and the tile's own colour its notches are cut from. */
export type StatePalette = Record<ActivityState, string> & { chrome: string }

/** Held's stripes: two pixels of the background every six, the first three in. */
const HeldStripe = { offset: 3, width: 2, every: 6 }

/**
 * ONE RECTANGLE OF ONE STATE, as chart shapes: the state's colour, and for blocked and failed a notch
 * cut from the background - one corner for blocked, both left corners for failed - and for held
 * upright stripes cut the same way, so none of them is told by hue alone. The tile's lanes and the
 * dialog's columns draw states the same way.
 */
export function stateShapes(x: number, y: number, width: number, height: number, state: ActivityState, palette: StatePalette) {
  const children: { type: string; shape: Record<string, unknown>; style: { fill: string } }[] = [
    { type: 'rect', shape: { x, y, width, height }, style: { fill: palette[state] } },
  ]

  const notch = Math.min(6, height, width)

  if (state === 'blocked' || state === 'failed') {
    children.push({ type: 'polygon', shape: { points: [[x, y], [x + notch, y], [x, y + notch]] }, style: { fill: palette.chrome } })
  }

  if (state === 'held') {
    for (let stripe = x + HeldStripe.offset; stripe + HeldStripe.width <= x + width; stripe += HeldStripe.every) {
      children.push({ type: 'rect', shape: { x: stripe, y, width: HeldStripe.width, height }, style: { fill: palette.chrome } })
    }
  }

  if (state === 'failed') {
    children.push({
      type: 'polygon',
      shape: { points: [[x, y + height], [x + notch, y + height], [x, y + height - notch]] },
      style: { fill: palette.chrome },
    })
  }

  return children
}

