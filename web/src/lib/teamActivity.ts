import type { ActivitySpan, ActivityState, ActivityWindow, TeamActivity } from '../api/types'

/**
 * WHAT THE STATISTICS TILE DRAWS FROM ONE `/activity` ANSWER, as plain functions so its initials,
 * its lanes, its words and its colours are pinned without mounting a chart.
 *
 * NO STATE IS DECIDED HERE. The server says which state each stretch was in; these only order the
 * lanes, grow a still-open stretch to now and put the result into words. A stretch nothing recorded
 * stays absent - it is never drawn, and never counted as idle.
 */

/** A span with its instants as milliseconds, an open one already grown to now. */
export interface GrownSpan {
  state: ActivityState
  from: number
  to: number
  /** Still open at the answer's `serverNow`: its `to` is now and moves with the board's clock. */
  open: boolean
  workflow?: number
  reason?: string
}

/** One drawn lane: the member's stored name, the label a person reads, and its initials. */
export interface Lane {
  member: string
  name: string
  initials: string
  spans: GrownSpan[]
}

/** The order states are counted in, in the summary and anywhere else they are listed. */
export const ActivityStates: readonly ActivityState[] = ['running', 'waiting', 'blocked', 'failed', 'idle']

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
 * THE SERVER'S NOW, BETWEEN FETCHES: the board's own ticking clock corrected by `clockOffset`, and
 * never earlier than the answer's `serverNow` - an open span is never drawn shorter than the server
 * already said it was.
 */
export function liveNow(serverNow: string, clock: number, clockOffset: number): number {
  const server = Date.parse(serverNow)
  const corrected = clock + clockOffset

  return Number.isNaN(server) ? corrected : Math.max(server, corrected)
}

/** The spans as instants, an open one (`to: null`) drawn to `now`. */
export function growSpans(spans: readonly ActivitySpan[], now: number): GrownSpan[] {
  return spans.map((span) => {
    const grown: GrownSpan = {
      state: span.state,
      from: Date.parse(span.from),
      to: span.to === null ? now : Date.parse(span.to),
      open: span.to === null,
    }

    if (span.workflow !== undefined) grown.workflow = span.workflow
    if (span.reason !== undefined) grown.reason = span.reason

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
  now: number,
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
    spans: growSpans(member.spans, now),
  }))
}

/** The span a lane is in at an instant, or null when nothing was recorded then. */
export function spanAt(lane: Lane, at: number): GrownSpan | null {
  return lane.spans.find((span) => span.from <= at && (at < span.to || (span.open && at <= span.to))) ?? null
}

/** An instant as the board shows one: local 24-hour HH:MM. */
export function clockTime(ms: number): string {
  const date = new Date(ms)

  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
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
 * THE TILE'S `aria-label`: since when the lanes run, once at the front, then how many lanes and each
 * member's state now counted by state - "Statistics since 14:20: 3 members - 2 running, 1 blocked".
 * A member with no span now is counted as such, never as idle.
 */
export function activitySummary(
  window: ActivityWindow,
  lanes: readonly Lane[],
  from: number | null,
  now: number,
): string {
  if (window === 'none') return 'Statistics: No workflows yet'

  const counts = new Map<ActivityState, number>()
  let nothing = 0

  for (const lane of lanes) {
    const span = spanAt(lane, now)

    if (span) counts.set(span.state, (counts.get(span.state) ?? 0) + 1)
    else nothing++
  }

  const parts = ActivityStates
    .filter((state) => counts.has(state))
    .map((state) => `${counts.get(state)} ${state}`)

  if (nothing > 0) parts.push(`${nothing} with no runs now`)

  const since = from === null ? '' : ` since ${clockTime(from)}`

  return `Statistics${since}: ${plural(lanes.length, 'member', 'members')} - ${parts.join(', ')}`
}

/**
 * THE CAPTION UNDER THE LANES: since when, and over how many open workflows; for the fallback, when
 * the latest workflow closed; for a team that never ran, that alone. A null `openCount` is a
 * workflow list not read yet, and names no count rather than a wrong one.
 */
export function activityCaption(
  window: ActivityWindow,
  from: number | null,
  openCount: number | null,
  latestClosedAt: number | null,
): string {
  if (window === 'none') return 'No workflows yet'

  if (window === 'latest') {
    return latestClosedAt === null ? 'latest workflow' : `latest workflow, closed ${clockTime(latestClosedAt)}`
  }

  const parts = []

  if (from !== null) parts.push(`since ${clockTime(from)}`)
  if (openCount !== null) parts.push(plural(openCount, 'open workflow', 'open workflows'))

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
export function tooltipHtml(lanes: readonly Lane[], at: number): string {
  const rows = lanes.map((lane) => {
    const span = spanAt(lane, at)
    const chip = `<span class="stats-chip stats-chip--${span ? span.state : 'none'}"></span>`
    const who = `<span class="stats-tip-initials">${escapeHtml(lane.initials)}</span> `
      + `<span class="stats-tip-name">${escapeHtml(lane.name)}</span>`

    let what = 'no runs in this window'

    if (span) {
      what = `${span.state} ${stretchLength(span.to - span.from)}`
      if (span.reason) what += ` (${span.reason})`
    }

    return `<div class="stats-tip-row">${chip}${who} — ${escapeHtml(what)}</div>`
  })

  return `<div class="stats-tip"><div class="stats-tip-time">${clockTime(at)}</div>${rows.join('')}</div>`
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

const noTime = (): StateTime => ({ running: 0, waiting: 0, blocked: 0, failed: 0, idle: 0 })

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

const labelFormats = new Map<string, Intl.DateTimeFormat>()

/** A zone's weekday, day, month name and year at an instant, in English. */
function calendarWords(ms: number, timeZone: string) {
  let format = labelFormats.get(timeZone)

  if (!format) {
    format = new Intl.DateTimeFormat('en-GB', { timeZone, weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
    labelFormats.set(timeZone, format)
  }

  const parts = format.formatToParts(ms)
  const part = (type: string) => parts.find((p) => p.type === type)?.value ?? ''

  return { weekday: part('weekday'), day: part('day'), month: part('month'), year: part('year') }
}

/** A bucket as the tooltip names it: "Tue 14:20–14:21", "Tue 14:00–15:00", "Tue 6 Oct", "Oct 2026". */
export function bucketLabel(from: number, to: number, bucket: Bucket, timeZone: string): string {
  const words = calendarWords(from, timeZone)

  if (bucket === 'day') return `${words.weekday} ${words.day} ${words.month}`
  if (bucket === 'month') return `${words.month} ${words.year}`

  const time = (ms: number) => {
    const wall = wallClock(ms, timeZone)

    return `${String(wall.hour).padStart(2, '0')}:${String(wall.minute).padStart(2, '0')}`
  }

  return `${words.weekday} ${time(from)}–${time(to)}`
}

/** Member-time in words short enough for one tooltip line: "40 s", "3 min 20 s", "2 h 5 min". */
export function memberTime(ms: number): string {
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
): string {
  const split = (ms: StateTime) => ActivityStates
    .filter((state) => ms[state] > 0)
    .map((state) => `${state} ${memberTime(ms[state])}`)

  const totals = ActivityStates
    .filter((state) => column.totals[state] > 0)
    .map((state) => `<div class="stats-tip-row"><span class="stats-chip stats-chip--${state}"></span>`
      + `${escapeHtml(`${state} ${memberTime(column.totals[state])}`)}</div>`)

  const members = column.members.map((share) => {
    const person = people.get(share.member) ?? { name: share.member, initials: memberInitials(share.member) }

    return `<div class="stats-tip-row"><span class="stats-tip-initials">${escapeHtml(person.initials)}</span> `
      + `<span class="stats-tip-name">${escapeHtml(person.name)}</span> — ${escapeHtml(split(share.ms).join(', '))}</div>`
  })

  return `<div class="stats-tip"><div class="stats-tip-time">${escapeHtml(bucketLabel(column.from, column.to, bucket, timeZone))}</div>`
    + `${totals.join('')}<div class="stats-tip-members">${members.join('')}</div></div>`
}

/** The Statistics dialog's periods, in the order its picker lists them. */
export type ActivityPeriod = 'open' | 'hour' | 'day' | 'month' | 'year'

/** Each period's words and the bucket its columns are. */
export const ActivityPeriods: readonly { value: ActivityPeriod; label: string; bucket: Bucket }[] = [
  { value: 'open', label: 'Open workflows', bucket: 'minute' },
  { value: 'hour', label: 'Last hour', bucket: 'minute' },
  { value: 'day', label: 'Last day', bucket: 'hour' },
  { value: 'month', label: 'Last month', bucket: 'day' },
  { value: 'year', label: 'Last year', bucket: 'month' },
]

/**
 * THE INSTANTS A PERIOD ASKS `/activity` FOR, ending at `now`; null for Open workflows, which is the
 * team's own window and asks for none. A month and a year step back by the UTC calendar, as the
 * server measures its one-year limit, so "Last year" is never refused for being an hour too long.
 */
export function periodRange(period: ActivityPeriod, now: number): { from: string; to: string } | null {
  if (period === 'open') return null

  const from = new Date(now)

  if (period === 'hour') from.setTime(now - 3_600_000)
  else if (period === 'day') from.setTime(now - 86_400_000)
  else if (period === 'month') from.setUTCMonth(from.getUTCMonth() - 1)
  else from.setUTCFullYear(from.getUTCFullYear() - 1)

  return { from: from.toISOString(), to: new Date(now).toISOString() }
}

/** The y axis's unit for a bucket, and how many milliseconds one of it is. */
export function bucketUnit(bucket: Bucket): { name: string; ms: number } {
  return bucket === 'minute' || bucket === 'hour'
    ? { name: 'member-minutes', ms: 60_000 }
    : { name: 'member-hours', ms: 3_600_000 }
}

/** The colours a state is drawn in, and the tile's own colour its notches are cut from. */
export type StatePalette = Record<ActivityState, string> & { chrome: string }

/**
 * ONE RECTANGLE OF ONE STATE, as chart shapes: the state's colour, and for blocked and failed a notch
 * cut from the background - one corner for blocked, both left corners for failed - so neither is
 * told by hue alone. The tile's lanes and the dialog's columns draw states the same way.
 */
export function stateShapes(x: number, y: number, width: number, height: number, state: ActivityState, palette: StatePalette) {
  const children: { type: string; shape: Record<string, unknown>; style: { fill: string } }[] = [
    { type: 'rect', shape: { x, y, width, height }, style: { fill: palette[state] } },
  ]

  const notch = Math.min(6, height, width)

  if (state === 'blocked' || state === 'failed') {
    children.push({ type: 'polygon', shape: { points: [[x, y], [x + notch, y], [x, y + notch]] }, style: { fill: palette.chrome } })
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
