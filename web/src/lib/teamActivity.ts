import type { ActivitySpan, ActivityState, TeamActivity } from '../api/types'

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
 * member since removed (`current: false`) gets no lane. The label is the board's name for the
 * member when it has one, else the stored name.
 */
export function activityLanes(
  activity: TeamActivity,
  containers: readonly { id: string; name: string }[],
  now: number,
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

  const names = current.map((member) =>
    containers.find((c) => c.id.toLowerCase() === member.member.toLowerCase())?.name ?? member.member)
  const initials = laneInitials(names)

  return current.map((member, index) => ({
    member: member.member,
    name: names[index]!,
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
 * THE TILE'S `aria-label`: how many lanes, each member's state now counted by state, and since when
 * the lanes run - "Statistics: 3 members — 2 running, 1 blocked since 14:20". A member with no span
 * now is counted as such, never as idle.
 */
export function activitySummary(
  window: TeamActivity['window'],
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

  return `Statistics: ${plural(lanes.length, 'member', 'members')} — ${parts.join(', ')}${since}`
}

/**
 * THE CAPTION UNDER THE LANES: since when, and over how many open workflows; for the fallback, when
 * the latest workflow closed; for a team that never ran, that alone.
 */
export function activityCaption(
  window: TeamActivity['window'],
  from: number | null,
  openCount: number,
  latestClosedAt: number | null,
): string {
  if (window === 'none') return 'No workflows yet'

  if (window === 'latest') {
    return latestClosedAt === null ? 'latest workflow' : `latest workflow, closed ${clockTime(latestClosedAt)}`
  }

  const since = from === null ? '' : `since ${clockTime(from)} · `

  return `${since}${plural(openCount, 'open workflow', 'open workflows')}`
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

/**
 * THE STATE COLOURS, per theme, with the tile surface they are drawn on (`chrome`, the same value as
 * `--os-chrome` in `css/app.scss`). Running, waiting, blocked and failed each hold at least 3:1
 * against it in both themes, the contrast a graphical mark needs; idle is a pale track on purpose,
 * an empty lane rather than a mark. Blocked and failed also carry a notch, so no state is told
 * apart by hue alone.
 */
export const StatePalette = {
  light: {
    running: '#2e7d4f',
    waiting: '#5f7385',
    blocked: '#b26a00',
    failed: '#c62828',
    idle: '#e4dfd8',
    chrome: '#f4f1ec',
  },
  dark: {
    running: '#5cb884',
    waiting: '#8fa1b3',
    blocked: '#e0a83a',
    failed: '#ef6b6b',
    idle: '#3a322c',
    chrome: '#221c18',
  },
} as const

/** Relative luminance of a `#rrggbb` colour, as WCAG defines it. */
function luminance(hex: string): number {
  const channel = (offset: number) => {
    const value = parseInt(hex.slice(offset, offset + 2), 16) / 255

    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4
  }

  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5)
}

/** The WCAG contrast ratio of two `#rrggbb` colours, from 1 to 21. */
export function contrastRatio(a: string, b: string): number {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number]

  return (light + 0.05) / (dark + 0.05)
}
