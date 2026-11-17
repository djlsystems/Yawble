import type { WorkflowSummary } from '../../../shared/api';
import type { MemberState } from '../faces/faces';
import { changesFile, fileName, FileTrail, stepCaption, stepDoing, type StepKind, type StepRow } from './steps';
import { latestWorkflows, openWorkflows, workflowEnds } from './workflows';
import { cardOf } from './rhythm';

/**
 * THE MONITOR'S MODEL: every team and member as the Monitor draws them, kept current from two sources. Snapshots
 * (the teams and their member cards, polled) always win: they say who exists and each member's state. The feed
 * (bus messages, monitor.messages) only adds what happens between snapshots: an instruction flies to its member as
 * a comet, a finished run bursts, a progress line floats up, a question raises "needs you". The step feed
 * (monitor.steps, steps.ts) adds what each member does inside its run: an icon rising off it for each step, the
 * files it changes beside it for half a minute, and a line under its name saying what it is doing now.
 */

export type FeedKind = 'instruction' | 'completed' | 'failed' | 'handback' | 'progress' | 'blocked' | 'decision' | 'done';

export interface FeedRow {
  seq: number; kind: FeedKind; team: string; member: string | null; from: string; text: string | null; workflow: number; at: string;
}

export interface MemberSnapshot {
  name: string; role: 'manager' | 'member'; state: MemberState; queued: number; statusLine: string | null; cli: string; id: number;
}

export interface TeamSnapshot { id: number; name: string; members: MemberSnapshot[] }

/** An effect for the scene to draw; `at` is when it started (performance.now()). */
export type Effect =
  /** card: the card an instruction is about (its label says it); mark: a hand-back's check or cross, carried home. */
  | { kind: 'comet'; from: string; to: string; color: Tone; label: string | null; at: number; card?: number | null; mark?: 'check' | 'cross' }
  /** Something waiting for the person flies from its member (or team) to the You node at the wall's edge. */
  | { kind: 'you'; from: string; color: Tone; label: string | null; at: number }
  /** A workflow node leaves: completed (a green flash, drifting away) or closed (a cross, fading). key: its node. */
  | { kind: 'wfdone'; key: string; team: string; status: 'completed' | 'closed'; at: number }
  | { kind: 'burst'; node: string; color: Tone; big: boolean; at: number }
  | { kind: 'ripple'; node: string; color: Tone; at: number }
  | { kind: 'float'; node: string; color: Tone; text: string; at: number }
  | { kind: 'step'; node: string; step: StepKind; caption: string | null; at: number };

/** The wall's colours for what happens (the scene reads the hex values from CSS). */
export type Tone = 'tell' | 'start' | 'progress' | 'done' | 'handback' | 'fail' | 'blocked' | 'decision' | 'person' | 'edit';

/** A line of the activity list: a bus message (key "m<seq>"), or a member's file changes gathered ("f<step id>"). */
export interface ActivityRow { key: string; seq: number; tone: Tone; team: string; member: string | null; verb: string; who: string; text: string | null; at: string }

/** What a member did last, for the line under its name: the words, the step's kind, and when (display time). */
export interface Doing { text: string; kind: StepKind; at: number }

/** How long the "now" line under a member's name stays after its last step, display time. */
export const DoingMs = 30_000;
/** File changes by one member within this much of each other (log time) gather on one activity line. */
const GatherFilesMs = 90_000;

export const memberKey = (team: string, member: string) => `m:${team.toLowerCase()}/${member.toLowerCase()}`;
export const teamKey = (team: string) => `t:${team.toLowerCase()}`;
/** A workflow node's key (B080): one per open workflow. */
export const workflowKey = (team: string, id: number) => `wf:${team.toLowerCase()}/${id}`;

/** A command a member is running (a 'run' step), until its next step; exit when the feed knew it. */
export interface RunMark { at: number; target: string | null; ended: number | null; exit: number | null }
/** A run with no next step after this long is taken as over. */
export const RunStaleMs = 10 * 60_000;
/** How long an ended run's ring stays full before it goes. */
export const RunEndShowMs = 900;
/** Steps per member counted over this window for its pulse. */
const RateWindowMs = 60_000;

const verbs: Record<FeedKind, { verb: string; tone: Tone }> = {
  instruction: { verb: 'told', tone: 'tell' },
  completed: { verb: 'finished a run', tone: 'done' },
  failed: { verb: 'run failed', tone: 'fail' },
  handback: { verb: 'handed back', tone: 'handback' },
  progress: { verb: 'progress', tone: 'progress' },
  blocked: { verb: 'is blocked', tone: 'blocked' },
  decision: { verb: 'needs you', tone: 'decision' },
  done: { verb: 'finished the work', tone: 'done' },
};

export const ActivityKeep = 80;

export class MonitorModel {
  /** keep: how many activity lines it holds (the Monitor's ActivityKeep; the team board keeps more of one team). */
  constructor(private readonly keep = ActivityKeep) {}

  teams: TeamSnapshot[] = [];
  activity: ActivityRow[] = [];
  effects: Effect[] = [];
  /** The files each member changed lately, beside it on the wall. */
  readonly files = new FileTrail();
  /** What each member is doing now, by member key. */
  readonly doing = new Map<string, Doing>();
  /** Events per minute, counted over the last minute of rows. */
  private recent: number[] = [];
  private lastState = new Map<string, MemberState>();
  /** Each team's newest workflows (live only; a replay's model has none), by team key; the rev moves when they change. */
  readonly workflows = new Map<string, WorkflowSummary[]>();
  workflowsRev = 0;
  /** Each team's open workflows (the wall's workflow nodes), newest first, by team key; they move workflowsRev too. */
  readonly openWfs = new Map<string, WorkflowSummary[]>();
  /** The workflow nodes whose last word was "blocked" (workflowKey), until work on them goes on. */
  readonly blockedWfs = new Set<string>();
  /** The card each member was last told about, and who told it, so its hand-back returns along the same line. */
  private told = new Map<string, { from: string; card: number | null }>();
  /** The command each member runs now (or just ended), by member key. */
  readonly runs = new Map<string, RunMark>();
  /** Each member's step times (display time) over the last minute, for its pulse. */
  private stepTimes = new Map<string, number[]>();
  /** What waits for the person besides questions (network and tool asks), by the view's key for each. */
  private asks = new Map<string, { team: string; member: string | null }>();

  /** Takes a snapshot: who exists and their states. A member that starts running ripples. */
  reconcile(teams: TeamSnapshot[], now: number): void {
    this.teams = teams;
    const present = new Set(teams.map((t) => teamKey(t.name)));
    for (const k of [...this.workflows.keys()]) if (!present.has(k)) { this.workflows.delete(k); this.workflowsRev++; }
    for (const k of [...this.openWfs.keys()]) if (!present.has(k)) { this.openWfs.delete(k); this.workflowsRev++; }
    for (const t of teams)
      for (const m of t.members) {
        const key = memberKey(t.name, m.name);
        const before = this.lastState.get(key);
        if (before !== undefined && before !== 'running' && m.state === 'running') this.effects.push({ kind: 'ripple', node: key, color: 'start', at: now });
        this.lastState.set(key, m.state);
      }
  }

  /** Takes a team's workflows (bus.workflows): it keeps the newest few, and moves the rev only if they changed. */
  setWorkflows(team: string, list: readonly WorkflowSummary[], now = performance.now()): void {
    const key = teamKey(team);
    // The open ones are the workflow nodes; one open before and ended now leaves the wall (a flash or a fade).
    const before = this.openWfs.get(key);
    const open = openWorkflows(list);
    if (before) {
      for (const end of workflowEnds(before, list)) {
        const wk = workflowKey(team, end.id);
        this.blockedWfs.delete(wk);
        this.effects.push({ kind: 'wfdone', key: wk, team: team.toLowerCase(), status: end.status, at: now });
      }
    }
    let changed = false;
    if (JSON.stringify(open) !== JSON.stringify(before ?? null)) { this.openWfs.set(key, open); changed = true; }
    const next = latestWorkflows(list);
    if (JSON.stringify(next) !== JSON.stringify(this.workflows.get(key) ?? null)) { this.workflows.set(key, next); changed = true; }
    if (changed) this.workflowsRev++;
    this.capEffects();
  }

  /** Keeps the effects waiting for the scene to the newest 260 (a model nobody drains, as the team board's, stays small). */
  private capEffects(): void {
    if (this.effects.length > 260) this.effects.splice(0, this.effects.length - 260);
  }

  /** Steps per minute a member took lately (display time), for how fast its disc pulses. */
  stepRate(node: string, now: number): number {
    const times = this.stepTimes.get(node);
    if (!times) return 0;
    while (times.length && now - times[0]! > RateWindowMs) times.shift();
    if (!times.length) { this.stepTimes.delete(node); return 0; }
    return times.length * (60_000 / RateWindowMs);
  }

  /**
   * What waits for the person besides questions: network and tool asks (the view's list, keyed as it likes). A new one
   * flies from its member to the You node; the You node counts them until they are gone from the list.
   */
  setAsks(asks: readonly { key: string; team: string; member: string | null; label?: string | null }[], now = performance.now()): void {
    const next = new Map<string, { team: string; member: string | null }>();
    for (const a of asks) {
      next.set(a.key, { team: a.team, member: a.member });
      if (!this.asks.has(a.key))
        this.effects.push({ kind: 'you', from: a.member ? memberKey(a.team, a.member) : teamKey(a.team), color: 'decision', label: clip(a.label ?? null, 30), at: now });
    }
    this.asks = next;
    this.capEffects();
  }

  /** How many things wait for the person (the You node's count): members whose question is open, and the asks. */
  youCount(): number {
    let n = this.asks.size;
    for (const t of this.teams) for (const m of t.members) if (m.state === 'decision') n++;
    return n;
  }

  /** Applies a feed row: an effect for the scene and a line for the activity list. */
  apply(row: FeedRow, now: number, live = true): void {
    const v = verbs[row.kind];
    const target = row.member ? memberKey(row.team, row.member) : teamKey(row.team);
    const whoName = row.kind === 'instruction' ? row.member ?? row.team : row.member ?? row.from;
    this.activity.unshift({
      key: `m${row.seq}`, seq: row.seq, tone: row.kind === 'instruction' && row.from === 'person' ? 'person' : v.tone, team: row.team, member: row.member,
      verb: row.kind === 'instruction' ? `${row.from === 'person' ? 'You' : row.from === 'host' ? 'Schedule' : spaced(row.from)} told` : v.verb,
      who: spaced(whoName), text: row.text, at: row.at,
    });
    if (this.activity.length > this.keep) for (const gone of this.activity.splice(this.keep)) this.gathered.delete(gone.key);
    const source = row.from === 'person' || row.from === 'host' ? teamKey(row.team) : memberKey(row.team, row.from);
    // A workflow's blocked look lasts until work on it goes on (read at the start too, so the look is right at once).
    const wk = workflowKey(row.team, row.workflow);
    if (row.kind === 'blocked') this.blockedWfs.add(wk);
    else if (row.kind !== 'decision') this.blockedWfs.delete(wk);
    // Who told a member what, so its hand-back returns along the same line with the card's number.
    if (row.kind === 'instruction' && row.member) this.told.set(target, { from: source, card: cardOf(row.text) });
    if (!live) return;
    this.recent.push(now);
    // The member's run is over: so is any command it was running.
    const run = this.runs.get(target);
    if (run && run.ended === null && (row.kind === 'completed' || row.kind === 'failed' || row.kind === 'handback' || row.kind === 'blocked')) run.ended = now;
    const told = this.told.get(target);
    const back = (mark: 'check' | 'cross', color: Tone, words: string) => {
      if (!row.member) return;
      const card = told?.card ?? null;
      this.effects.push({ kind: 'comet', from: target, to: told?.from ?? memberKey(row.team, 'Manager'), color, label: card !== null ? `#${card} ${mark === 'check' ? '✓' : '✗'}` : words, at: now, card, mark });
    };
    switch (row.kind) {
      case 'instruction': {
        const card = cardOf(row.text);
        this.effects.push({ kind: 'comet', from: source, to: target, color: row.from === 'person' ? 'person' : 'tell', label: card !== null ? `#${card}` : clip(row.text, 36), at: now, card });
        break;
      }
      case 'handback':
        back('check', 'handback', 'handed back');
        break;
      case 'progress':
        if (row.text) this.effects.push({ kind: 'float', node: target, color: 'progress', text: clip(row.text, 40)!, at: now });
        break;
      case 'completed':
        this.effects.push({ kind: 'burst', node: target, color: 'done', big: false, at: now });
        break;
      case 'failed':
        this.effects.push({ kind: 'burst', node: target, color: 'fail', big: true, at: now });
        back('cross', 'fail', 'failed');
        break;
      case 'blocked':
        this.effects.push({ kind: 'burst', node: target, color: 'blocked', big: true, at: now });
        this.effects.push({ kind: 'float', node: target, color: 'blocked', text: 'blocked', at: now });
        back('cross', 'blocked', 'blocked');
        break;
      case 'decision':
        this.effects.push({ kind: 'burst', node: target, color: 'decision', big: false, at: now });
        this.effects.push({ kind: 'float', node: target, color: 'decision', text: 'needs you', at: now });
        this.effects.push({ kind: 'you', from: target, color: 'decision', label: clip(row.text, 30), at: now });
        break;
      case 'done':
        this.effects.push({ kind: 'burst', node: teamKey(row.team), color: 'done', big: true, at: now });
        break;
    }
    this.capEffects();
  }

  /**
   * Applies a step: live, an icon rises off the member (with a short note: the command, the pattern, who a tell went
   * to), a changed file takes its node beside it, and its "now" line says what it does. Always, a changed file joins
   * the activity list, gathered with the member's other changes of the last minute or so.
   */
  applyStep(step: StepRow, now: number, live = true): void {
    const node = memberKey(step.team, step.member);
    if (changesFile(step)) this.gatherFile(step);
    if (!live) return;
    this.recent.push(now);
    const times = this.stepTimes.get(node);
    if (times) times.push(now); else this.stepTimes.set(node, [now]);
    // A command's ring: it fills from its 'run' step until the member's next step (or at once when the exit code came
    // with it), then bursts green or red by its exit code when that is known.
    const running = this.runs.get(node);
    if (running && running.ended === null) running.ended = now;
    if (step.kind === 'run') {
      const exit = typeof step.exit === 'number' ? step.exit : null;
      this.runs.set(node, { at: now, target: step.target, ended: exit === null ? null : now, exit });
      if (exit !== null) this.effects.push({ kind: 'burst', node, color: exit === 0 ? 'done' : 'fail', big: exit !== 0, at: now });
    }
    // A reply is how nearly every run ends: the line keeps the last real thing the member did.
    if (step.kind !== 'say') this.doing.set(node, { text: stepDoing(step), kind: step.kind, at: now });
    if (changesFile(step)) this.files.touch(node, step.target!, step.kind === 'create' ? 'create' : 'edit', now);
    this.effects.push({ kind: 'step', node, step: step.kind, caption: step.kind === 'edit' || step.kind === 'create' ? null : stepCaption(step), at: now });
    this.capEffects();
  }

  /** The files gathered on each file line of the activity list, by its key. */
  private gathered = new Map<string, Set<string>>();

  private gatherFile(step: StepRow): void {
    const name = fileName(step.target!);
    const at = Date.parse(step.at);
    // The member's latest file line, if it is near the top and recent: the change joins it.
    const top = this.activity.slice(0, 4).find((r) => r.key.startsWith('f') && r.team === step.team && r.member === step.member);
    const names = top ? this.gathered.get(top.key) : undefined;
    if (top && names && Math.abs(at - Date.parse(top.at)) < GatherFilesMs) {
      names.add(name);
      const list = [...names];
      top.text = list.slice(0, 4).join(', ') + (list.length > 4 ? ` +${list.length - 4}` : '');
      if (step.kind === 'create') top.verb = 'wrote files';
      if (step.at > top.at) top.at = step.at;
      return;
    }
    const key = `f${step.id}`;
    this.gathered.set(key, new Set([name]));
    this.activity.unshift({
      key, seq: -step.id, tone: 'edit', team: step.team, member: step.member,
      verb: step.kind === 'create' ? 'wrote files' : 'edited files', who: spaced(step.member), text: name, at: step.at,
    });
    if (this.activity.length > this.keep) {
      for (const gone of this.activity.splice(this.keep)) this.gathered.delete(gone.key);
    }
  }

  /** Forgets the files and "now" lines that have run out (the scene calls this each frame through the view). */
  tidy(now: number): void {
    this.files.tidy(now);
    for (const [k, d] of this.doing) if (now - d.at > DoingMs) this.doing.delete(k);
    for (const [k, r] of this.runs) if (r.ended !== null ? now - r.ended > RunEndShowMs : now - r.at > RunStaleMs) this.runs.delete(k);
  }

  /** Whether a member is thinking now (its last step was a thought, within the "now" line's half minute). */
  thinking(node: string, now: number): boolean {
    const d = this.doing.get(node);
    return !!d && d.kind === 'think' && now - d.at < DoingMs;
  }

  /** Effects older than their life are dropped by the scene; this keeps the per-minute count honest. */
  perMinute(now: number): number {
    this.recent = this.recent.filter((t) => now - t < 60_000);
    return this.recent.length;
  }

  counts(): { running: number; waiting: number; needYou: number; members: number } {
    let running = 0, waiting = 0, needYou = 0, members = 0;
    for (const t of this.teams)
      for (const m of t.members) {
        members++;
        if (m.state === 'running') running++;
        else if (m.state === 'waiting') waiting++;
        else if (m.state === 'decision' || m.state === 'blocked' || m.state === 'failed') needYou++;
      }
    return { running, waiting, needYou, members };
  }
}

/** "DeveloperPriya" as words. */
export function spaced(name: string): string {
  return name.replace(/([a-z])([A-Z])/g, '$1 $2');
}

function clip(s: string | null, n: number): string | null {
  if (!s) return null;
  const one = s.replace(/\s+/g, ' ').trim();
  return one.length <= n ? one : one.slice(0, n - 1) + '…';
}
