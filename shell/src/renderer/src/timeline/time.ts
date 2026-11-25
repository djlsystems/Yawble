import { onBeforeUnmount, readonly, ref, shallowRef, type Ref } from 'vue';
import type { ItemTime, TimeCategory, TimeEndedHow, TimelineBand, TimelineMarkerKind } from '../../../shared/api';

// A card's or workflow's overall time, said in words. The core computes every time (BoardCard.time, a workflow
// item's time, timeline.get); the screens only format them. The one thing the screens add is the tick of an OPEN item:
// its overallMs is as of the core's answer, so it is shown plus the seconds since that answer arrived, from ONE shared
// 1 s timer (useNow) however many cards and rows show a time.

/** The categories in the core's merge order (Agent working first), with what the screens call them. */
export const TimeCategories: { id: TimeCategory; label: string; hint: string }[] = [
  { id: 'agent-working', label: 'Agent working', hint: 'A member was running on it.' },
  { id: 'waiting-on-you', label: 'Waiting on you', hint: 'A question or a hand-back waited on a person.' },
  { id: 'stalled', label: 'Stalled', hint: 'A run was on, but showed no sign of work (no tool call, progress or model request) for 10 minutes or more.' },
  { id: 'waiting-for-slot', label: 'Waiting for a slot', hint: 'Queued for a run slot or the heavy-work lease.' },
  { id: 'waiting-on-teammate', label: 'Waiting on a teammate', hint: 'Waiting for another card or member.' },
  { id: 'blocked', label: 'Blocked', hint: 'Blocked, with nobody able to go on.' },
  { id: 'scheduled', label: 'Scheduled', hint: 'Waiting for a wake-up or a trigger.' },
  { id: 'not-started', label: 'Not started', hint: 'Planned, not yet given to a member.' },
  { id: 'idle', label: 'Idle', hint: 'Open, and nothing going on.' },
  { id: 'was-done', label: 'Was done', hint: 'Done, before it was reopened.' },
];
const labels = new Map(TimeCategories.map((c) => [c.id, c.label]));
/** "Agent working" for agent-working; a category this build does not know is shown as it came. */
export const categoryLabel = (c: string): string => labels.get(c as TimeCategory) ?? c;
/** The colour token of a category (styles.css --tl-*); an unknown one reads Idle's. */
export const categoryVar = (c: string): string => `var(--tl-${labels.has(c as TimeCategory) ? c : 'idle'})`;

export const MarkerLabels: Record<TimelineMarkerKind, string> = {
  planned: 'Planned', started: 'Started', reopen: 'Reopened', handback: 'Handed back', 'decision-asked': 'Decision asked',
  'decision-answered': 'Decision answered', 'watchdog-end': 'Ended by the idle watchdog', expired: 'Expired',
};
export const markerLabel = (k: string): string => MarkerLabels[k as TimelineMarkerKind] ?? k;

const EndedHowLabels: Record<TimeEndedHow, string> = { done: 'done', completed: 'completed', closed: 'closed', stopped: 'stopped', expired: 'expired' };
export const endedHowLabel = (h: string | null | undefined): string => (h ? EndedHowLabels[h as TimeEndedHow] ?? h : 'ended');

const SEC = 1000, MIN = 60 * SEC;
const pad = (n: number) => String(n).padStart(2, '0');

/** "1h 12m 05s", "12m 05s", "45s"; a day or more "2d 3h 12m" (seconds no longer matter). Never negative. */
export function formatDuration(ms: number): string {
  const t = Math.max(0, Math.floor((Number.isFinite(ms) ? ms : 0) / SEC));
  const d = Math.floor(t / 86400), h = Math.floor((t % 86400) / 3600), m = Math.floor((t % 3600) / 60), s = t % 60;
  if (d) return `${d}d ${h}h ${pad(m)}m`;
  if (h) return `${h}h ${pad(m)}m ${pad(s)}s`;
  if (m) return `${m}m ${pad(s)}s`;
  return `${s}s`;
}

/** The two largest units, unpadded, for a sentence: "53m", "1h 12m", "45s", "2d 3h". */
export function formatShort(ms: number): string {
  const t = Math.max(0, Math.floor((Number.isFinite(ms) ? ms : 0) / SEC));
  const d = Math.floor(t / 86400), h = Math.floor((t % 86400) / 3600), m = Math.floor((t % 3600) / 60), s = t % 60;
  if (d) return h ? `${d}d ${h}h` : `${d}d`;
  if (h) return m ? `${h}h ${m}m` : `${h}h`;
  if (m) return `${m}m`;
  return `${s}s`;
}

/** Each part's share of the whole in whole percents that add up to exactly 100 (largest remainder); all 0 for nothing. */
export function shares(ms: number[]): number[] {
  const total = ms.reduce((a, b) => a + Math.max(0, b), 0);
  if (total <= 0) return ms.map(() => 0);
  const raw = ms.map((v) => (Math.max(0, v) / total) * 100);
  const out = raw.map(Math.floor);
  let left = 100 - out.reduce((a, b) => a + b, 0);
  const order = raw.map((v, i) => [v - Math.floor(v), i] as const).sort((a, b) => b[0] - a[0] || a[1] - b[1]);
  for (const [, i] of order) { if (left <= 0) break; out[i]++; left--; }
  return out;
}

/** A clock time in the person's locale, 24 h: "16:01"; with the day when it is not on the same day as `ref`. */
export function clock(iso: string | null | undefined, ref?: string | number | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const hm = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  const r = ref == null ? null : new Date(ref);
  if (r && !Number.isNaN(r.getTime()) && r.toDateString() !== d.toDateString())
    return `${d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })} ${hm}`;
  return hm;
}
/** With seconds, for a band's start and end: "16:01:09". */
export function clockSec(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
}
const ms = (iso: string | null | undefined): number | null => {
  if (!iso) return null;
  const t = Date.parse(iso);
  return Number.isNaN(t) ? null : t;
};
/** The length between two of the core's times (a band, "53m before it started"); 0 when one is missing. */
export const between = (a: string | null | undefined, b: string | null | undefined): number => {
  const x = ms(a), y = ms(b);
  return x == null || y == null ? 0 : Math.max(0, y - x);
};

// ---- the ONE shared timer ----
const nowMs = ref(Date.now());
let users = 0;
let timer: ReturnType<typeof setInterval> | null = null;
function startTicker() {
  if (timer) return;
  nowMs.value = Date.now();
  timer = setInterval(() => { nowMs.value = Date.now(); }, SEC);
}
function stopTicker() { if (timer) { clearInterval(timer); timer = null; } }
/** Subscribes to the shared 1 s clock; returns the unsubscribe. The timer runs only while someone is subscribed. */
export function subscribeNow(): () => void {
  users++;
  startTicker();
  let done = false;
  return () => { if (done) return; done = true; users = Math.max(0, users - 1); if (!users) stopTicker(); };
}
/** The shared clock in a component: subscribed while it is mounted (call in setup). */
export function useNow(): Readonly<Ref<number>> {
  const off = subscribeNow();
  onBeforeUnmount(off);
  return nowRef();
}
/** The shared clock's value, read-only (it moves only while someone is subscribed). */
const nowRo = readonly(nowMs);
export const nowRef = (): Readonly<Ref<number>> => nowRo;
/** For tests: how many subscribe, and whether the one timer is running. */
export const tickerState = () => ({ users, running: timer !== null });

// When each time object arrived (the core's answer): an open item counts on from there.
const arrived = new WeakMap<object, number>();
export function arrivedAt(t: ItemTime): number {
  let a = arrived.get(t);
  if (a === undefined) { a = Date.now(); arrived.set(t, a); }
  return a;
}
/** The overall time to show now: overallMs as the core said it, plus (open only) the time since that answer arrived. */
export function liveOverallMs(t: ItemTime, now: number): number {
  const base = Math.max(0, t.overallMs || 0);
  return t.open ? base + Math.max(0, now - arrivedAt(t)) : base;
}
/** Whether a value from the core looks like a time object (an older core sends none). */
export const hasTime = (t: unknown): t is ItemTime =>
  !!t && typeof t === 'object' && typeof (t as ItemTime).overallMs === 'number' && typeof (t as ItemTime).createdAt === 'string';
/** The words beside the time: "Overall 1h 12m 05s, open" for a tooltip and a screen reader. */
export function overallSays(t: ItemTime, now: number): string {
  return `Overall ${formatDuration(liveOverallMs(t, now))}${t.open ? ', still open' : `, ${endedHowLabel(t.endedHow)}`}${t.rebuilt ? ' (rebuilt from history)' : ''}`;
}

// ---- the timeline's axis: created -> ended/now, long quiet stretches compressed behind a break mark ----
export interface Axis {
  start: number; end: number;
  /** A time's place on the axis, 0..1. */
  at: (t: number) => number;
  /** The compressed stretches: where the break mark sits (0..1), how long the stretch really is, and its real start and end. */
  breaks: { at: number; ms: number; from: number; to: number }[];
}
/** A stretch with nothing changing (no band edge, no marker) at least this long AND this share of the whole is compressed. */
export const BreakMinMs = 2 * 60 * MIN;
export const BreakMinShare = 0.25;
/** A compressed stretch is drawn this share of the uncompressed rest. */
export const BreakDrawShare = 0.06;

export function buildAxis(start: number, end: number, edges: number[]): Axis {
  if (!(end > start)) end = start + 1;
  const pts = [...new Set([start, end, ...edges.filter((e) => e > start && e < end)])].sort((a, b) => a - b);
  const total = end - start;
  const long = (a: number, b: number) => b - a >= BreakMinMs && b - a > total * BreakMinShare;
  let rest = 0;
  for (let i = 1; i < pts.length; i++) if (!long(pts[i - 1], pts[i])) rest += pts[i] - pts[i - 1];
  const compress = rest > 0;
  const seg: { a: number; b: number; w: number; long: boolean }[] = [];
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i], l = compress && long(a, b);
    seg.push({ a, b, long: l, w: l ? Math.max(rest * BreakDrawShare, 1) : b - a });
  }
  const sum = seg.reduce((x, s) => x + s.w, 0) || 1;
  const starts: number[] = [];
  let acc = 0;
  for (const s of seg) { starts.push(acc); acc += s.w; }
  const at = (t: number): number => {
    if (t <= start) return 0;
    if (t >= end) return 1;
    let i = seg.findIndex((s) => t <= s.b);
    if (i < 0) i = seg.length - 1;
    const s = seg[i];
    return (starts[i] + ((t - s.a) / (s.b - s.a || 1)) * s.w) / sum;
  };
  const breaks = seg.flatMap((s, i) => (s.long ? [{ at: (starts[i] + s.w / 2) / sum, ms: s.b - s.a, from: s.a, to: s.b }] : []));
  return { start, end, at, breaks };
}

/** How much of a band (start..end, ms) lies inside the axis's compressed stretches, so is drawn shorter than it is. */
export function cutMs(axis: Pick<Axis, 'breaks'>, start: number, end: number): number {
  return axis.breaks.reduce((sum, b) => sum + Math.max(0, Math.min(end, b.to) - Math.max(start, b.from)), 0);
}

/** The axis's end: the item's end, else the latest of now and the bands' and markers' last times. */
export function axisEnd(t: ItemTime, bands: TimelineBand[], markers: { at: string }[], now: number): number {
  const ended = ms(t.endedAt);
  if (!t.open && ended != null) return ended;
  let last = now;
  for (const b of bands) last = Math.max(last, ms(b.end) ?? 0, ms(b.start) ?? 0);
  for (const m of markers) last = Math.max(last, ms(m.at) ?? 0);
  return last;
}
export const isoMs = ms;

/** Room kept between a break label and the start / end labels or another break label, in px. */
export const TickGapPx = 6;
/**
 * Where each break label goes on the tick row (its left edge in px), or null to hide it. A label is centred under
 * its break mark, then slid inside the room between the start label's right edge and the end label's left edge
 * (and after the previous break label). One that does not fit there is hidden rather than drawn over another
 * (otherwise "≈ 2d 17h" over "now" read "≈ 2d 1now").
 */
export function placeBreakLabels(width: number, startRight: number, endLeft: number, labels: { x: number; w: number }[], gap = TickGapPx): (number | null)[] {
  let min = startRight + gap;
  const max = Math.min(endLeft, width) - gap;
  return labels.map(({ x, w }) => {
    const left = Math.min(Math.max(x - w / 2, min), max - w);
    if (left < min || left + w > max) return null;
    min = left + w + gap;
    return left;
  });
}

// ---- the one Timeline dialog: opened from a card or a workflow row, one at a time ----
export interface TimelineTarget { kind: 'card' | 'workflow'; id: number; title?: string; team?: string | null }
export const timelineTarget = shallowRef<TimelineTarget | null>(null);
export function openTimeline(t: TimelineTarget): void { timelineTarget.value = t; }
export function closeTimeline(): void { timelineTarget.value = null; }
