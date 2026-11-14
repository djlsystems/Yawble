import type { WorkflowSummary } from '../../../shared/api';
import type { MemberState } from '../faces/faces';
import { changesFile, fileName, FileTrail, stepCaption, stepDoing, type StepKind, type StepRow } from './steps';
import { latestWorkflows } from './workflows';

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
  | { kind: 'comet'; from: string; to: string; color: Tone; label: string | null; at: number }
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

  /** Takes a snapshot: who exists and their states. A member that starts running ripples. */
  reconcile(teams: TeamSnapshot[], now: number): void {
    this.teams = teams;
    const present = new Set(teams.map((t) => teamKey(t.name)));
    for (const k of [...this.workflows.keys()]) if (!present.has(k)) { this.workflows.delete(k); this.workflowsRev++; }
    for (const t of teams)
      for (const m of t.members) {
        const key = memberKey(t.name, m.name);
        const before = this.lastState.get(key);
        if (before !== undefined && before !== 'running' && m.state === 'running') this.effects.push({ kind: 'ripple', node: key, color: 'start', at: now });
        this.lastState.set(key, m.state);
      }
  }

  /** Takes a team's workflows (bus.workflows): it keeps the newest few, and moves the rev only if they changed. */
  setWorkflows(team: string, list: readonly WorkflowSummary[]): void {
    const key = teamKey(team);
    const next = latestWorkflows(list);
    if (JSON.stringify(next) === JSON.stringify(this.workflows.get(key) ?? null)) return;
    this.workflows.set(key, next);
    this.workflowsRev++;
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
    if (!live) return;
    this.recent.push(now);
    const source = row.from === 'person' || row.from === 'host' ? teamKey(row.team) : memberKey(row.team, row.from);
    switch (row.kind) {
      case 'instruction':
        this.effects.push({ kind: 'comet', from: source, to: target, color: row.from === 'person' ? 'person' : 'tell', label: clip(row.text, 36), at: now });
        break;
      case 'handback':
        this.effects.push({ kind: 'comet', from: target, to: memberKey(row.team, 'Manager'), color: 'handback', label: 'handed back', at: now });
        break;
      case 'progress':
        if (row.text) this.effects.push({ kind: 'float', node: target, color: 'progress', text: clip(row.text, 40)!, at: now });
        break;
      case 'completed':
        this.effects.push({ kind: 'burst', node: target, color: 'done', big: false, at: now });
        break;
      case 'failed':
        this.effects.push({ kind: 'burst', node: target, color: 'fail', big: true, at: now });
        break;
      case 'blocked':
        this.effects.push({ kind: 'burst', node: target, color: 'blocked', big: true, at: now });
        this.effects.push({ kind: 'float', node: target, color: 'blocked', text: 'blocked', at: now });
        break;
      case 'decision':
        this.effects.push({ kind: 'burst', node: target, color: 'decision', big: false, at: now });
        this.effects.push({ kind: 'float', node: target, color: 'decision', text: 'needs you', at: now });
        break;
      case 'done':
        this.effects.push({ kind: 'burst', node: teamKey(row.team), color: 'done', big: true, at: now });
        break;
    }
    if (this.effects.length > 260) this.effects.splice(0, this.effects.length - 260);
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
    // A reply is how nearly every run ends: the line keeps the last real thing the member did.
    if (step.kind !== 'say') this.doing.set(node, { text: stepDoing(step), kind: step.kind, at: now });
    if (changesFile(step)) this.files.touch(node, step.target!, step.kind === 'create' ? 'create' : 'edit', now);
    this.effects.push({ kind: 'step', node, step: step.kind, caption: step.kind === 'edit' || step.kind === 'create' ? null : stepCaption(step), at: now });
    if (this.effects.length > 260) this.effects.splice(0, this.effects.length - 260);
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
