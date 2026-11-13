import type { MemberState } from '../faces/faces';
import { memberKey, type FeedRow, type Tone } from './model';
import type { StepRow } from './steps';

/**
 * THE TIMELINE AND THE REPLAY. The timeline across the bottom of the Monitor is the last stretch of time (a choice of
 * Windows) with a tick for each bus message, coloured by its tone, and the members' steps in a lane under them.
 * Clicking it (or dragging and letting go) replays from that moment: the wall forgets the present and plays the
 * messages and the steps from there at Speed times their pace, never waiting more than MaxGapMs of their time for
 * the next one (the quiet is skipped), until it catches up with now and goes live.
 */

export const Windows = [{ label: '15m', minutes: 15 }, { label: '1h', minutes: 60 }, { label: '4h', minutes: 240 }, { label: '12h', minutes: 720 }] as const;
export const DefaultWindowMinutes = 60;
export const MaxWindowMinutes = 720;
export const Speeds = [10, 30, 60, 120, 300] as const;
export const DefaultSpeed = 60;
export const MaxGapMs = 20_000;

/** A time on the wire as ms. */
export const timeOf = (x: { at: string }): number => Date.parse(x.at);

/** A row's tone, as the model gives it (the timeline's colour). */
export function toneOfRow(row: Pick<FeedRow, 'kind' | 'from'>): Tone {
  switch (row.kind) {
    case 'instruction': return row.from === 'person' ? 'person' : 'tell';
    case 'completed': case 'done': return 'done';
    case 'failed': return 'fail';
    case 'handback': return 'handback';
    case 'progress': return 'progress';
    case 'blocked': return 'blocked';
    case 'decision': return 'decision';
  }
}

/** Inserts `items` (each with `at`) into `into`, which is kept sorted by time; returns `into`. Cheap for appends. */
export function mergeByTime<T extends { at: string }>(into: T[], items: readonly T[]): T[] {
  for (const item of items) {
    const t = timeOf(item);
    let i = into.length;
    while (i > 0 && timeOf(into[i - 1]!) > t) i--;
    into.splice(i, 0, item);
  }
  return into;
}

/** Drops what is older than `from` (ms) from a list sorted by time, and keeps at most `max` of the newest. */
export function trimByTime<T extends { at: string }>(list: T[], from: number, max: number): T[] {
  let cut = 0;
  while (cut < list.length && timeOf(list[cut]!) < from) cut++;
  cut = Math.max(cut, list.length - max);
  if (cut > 0) list.splice(0, cut);
  return list;
}

export interface ReplayDue { rows: FeedRow[]; steps: StepRow[] }

/**
 * THE REPLAY'S CLOCK: log time that runs `speed` times faster than the wall's, from `start`. Each step returns the
 * rows and steps now due, in order. Before the next one it never waits more than `maxGap` of log time.
 */
export class ReplayClock {
  private ri = 0;
  private si = 0;
  private virtual: number;

  constructor(private rows: readonly FeedRow[], private steps: readonly StepRow[], start: number, public speed: number, private maxGap = MaxGapMs) {
    this.virtual = start;
    this.ri = firstAtOrAfter(rows, start);
    this.si = firstAtOrAfter(steps, start);
  }

  /** The log time reached, ms. */
  get at(): number { return this.virtual; }

  /** Nothing more to play from what it was given. */
  get drained(): boolean { return this.ri >= this.rows.length && this.si >= this.steps.length; }

  /** Takes rows and steps that arrived after it started (live polling goes on during a replay). */
  extend(rows: readonly FeedRow[], steps: readonly StepRow[]): void {
    this.rows = rows;
    this.steps = steps;
  }

  step(wallMs: number, until = Number.POSITIVE_INFINITY): ReplayDue {
    const due: ReplayDue = { rows: [], steps: [] };
    const next = Math.min(
      this.ri < this.rows.length ? timeOf(this.rows[this.ri]!) : Number.POSITIVE_INFINITY,
      this.si < this.steps.length ? timeOf(this.steps[this.si]!) : Number.POSITIVE_INFINITY,
    );
    // Skip the quiet: never wait longer than maxGap of log time for the next row or step.
    if (Number.isFinite(next) && next - this.virtual > this.maxGap) this.virtual = next - this.maxGap;
    this.virtual = Math.min(until, this.virtual + wallMs * this.speed);
    while (this.ri < this.rows.length && timeOf(this.rows[this.ri]!) <= this.virtual) due.rows.push(this.rows[this.ri++]!);
    while (this.si < this.steps.length && timeOf(this.steps[this.si]!) <= this.virtual) due.steps.push(this.steps[this.si++]!);
    return due;
  }
}

function firstAtOrAfter(list: readonly { at: string }[], t: number): number {
  let lo = 0, hi = list.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (timeOf(list[mid]!) < t) lo = mid + 1; else hi = mid;
  }
  return lo;
}

/** How long, in log time, a member that has stopped taking steps still counts as running in a replay. */
export const ReplayRunningMs = 120_000;

/**
 * THE MEMBERS' STATES IN A REPLAY. The snapshots say how the members are now, not how they were, so a replay works
 * them out from what it plays: told is waiting, a step is running (for ReplayRunningMs after the last one), a
 * finished run or a hand-back is idle, and a question, a block or a failure is that until something else happens.
 */
export class ReplayStates {
  private states = new Map<string, { state: MemberState; at: number }>();

  row(row: FeedRow): void {
    if (!row.member) return;
    const key = memberKey(row.team, row.member);
    const at = timeOf(row);
    const state: MemberState | null =
      row.kind === 'instruction' ? (this.states.get(key)?.state === 'running' ? null : 'waiting')
        : row.kind === 'completed' || row.kind === 'handback' || row.kind === 'done' ? 'idle'
          : row.kind === 'failed' ? 'failed'
            : row.kind === 'decision' ? 'decision'
              : row.kind === 'blocked' ? 'blocked'
                : null;
    if (state) this.states.set(key, { state, at });
  }

  step(step: StepRow): void {
    this.states.set(memberKey(step.team, step.member), { state: 'running', at: timeOf(step) });
  }

  of(key: string, now: number): MemberState {
    const s = this.states.get(key);
    if (!s) return 'idle';
    if (s.state === 'running' && now - s.at > ReplayRunningMs) return 'idle';
    return s.state;
  }

  clear(): void { this.states.clear(); }
}

/** Where the timeline's labelled ticks go: a round step that leaves at least `minPx` between labels. */
export function axisTicks(from: number, to: number, width: number, minPx = 70): number[] {
  const steps = [60_000, 2 * 60_000, 5 * 60_000, 10 * 60_000, 15 * 60_000, 30 * 60_000, 3_600_000, 2 * 3_600_000, 3 * 3_600_000];
  const span = Math.max(1, to - from);
  const step = steps.find((s) => (s / span) * width >= minPx) ?? steps[steps.length - 1]!;
  const offset = new Date().getTimezoneOffset() * 60_000;
  const ticks: number[] = [];
  // Round in local time, so the labels read 14:00, 14:15...
  for (let t = Math.ceil((from - offset) / step) * step + offset; t <= to; t += step) ticks.push(t);
  return ticks;
}
