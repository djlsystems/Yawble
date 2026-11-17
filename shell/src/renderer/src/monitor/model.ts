import type { WorkflowSummary } from '../../../shared/api';
import type { MemberState } from '../faces/faces';
import { changesFile, fileName, stepCaption, stepDoing, type StepKind, type StepRow } from './steps';
import { latestWorkflows, openWorkflows, workflowEnds } from './workflows';
import { cardOf } from './rhythm';
import { CodeMap, codeEventKey, shortSha, type CodeEvent } from './codemap';

/**
 * THE MONITOR'S MODEL: every team and member as the Monitor draws them, kept current from two sources. Snapshots
 * (the teams and their member cards, polled) always win: they say who exists and each member's state. The feed
 * (bus messages, monitor.messages) only adds what happens between snapshots: an instruction flies to its member as
 * a comet, a finished run bursts, a progress line floats up, a question raises "needs you". The step feed
 * (monitor.steps, steps.ts) adds what each member does inside its run: an icon rising off it for each step, the
 * files it touches on its team's code map (codemap.ts), and a line under its name saying what it is doing now. The
 * code events (monitor.codeEvents) are the map's truth: a commit's files confirm or correct the steps, and the
 * commit travels as a dot to the team's hub; a merge into the team branch bursts along the hub, a landing on main
 * bursts larger, a push pulses off the edge of the wall.
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
  /**
   * Something waiting for the person flies from its member (or team) to the You node at the wall's edge, and the node
   * pulses as it lands. id: its need-you item's id (B088): one item flies (and sounds) once.
   */
  | { kind: 'you'; from: string; color: Tone; label: string | null; at: number; id?: string }
  /** A workflow node leaves: completed (a green flash, drifting away) or closed (a cross, fading). key: its node. */
  | { kind: 'wfdone'; key: string; team: string; status: 'completed' | 'closed'; at: number }
  | { kind: 'burst'; node: string; color: Tone; big: boolean; at: number }
  | { kind: 'ripple'; node: string; color: Tone; at: number }
  | { kind: 'float'; node: string; color: Tone; text: string; at: number }
  | { kind: 'step'; node: string; step: StepKind; caption: string | null; at: number }
  /** A commit: its files (by map id) gather to the member, a dot travels the member's spoke to the hub and rests there. */
  | { kind: 'commit'; team: string; node: string | null; files: string[]; sha: string; message: string; member: string | null; at: number }
  /** A merge into the team branch (big: false) or a landing on main (big: true): a burst along the hub. */
  | { kind: 'merge'; team: string; big: boolean; at: number }
  /** A push to origin: a pulse off the wall's edge. */
  | { kind: 'push'; team: string; at: number };

/** The wall's colours for what happens (the scene reads the hex values from CSS). */
export type Tone = 'tell' | 'start' | 'progress' | 'done' | 'handback' | 'fail' | 'blocked' | 'decision' | 'person' | 'edit' | 'commit';

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

const CodeVerbs: Record<CodeEvent['kind'], { verb: string; tone: Tone }> = {
  commit: { verb: 'committed', tone: 'commit' },
  'merge-team': { verb: 'merged into the team', tone: 'handback' },
  landed: { verb: 'landed on main', tone: 'done' },
  push: { verb: 'pushed', tone: 'tell' },
};

export const ActivityKeep = 80;

/**
 * One thing waiting for the person (B088): a member's open question, a network ask, a tool ask, a blocked member or a
 * failed one. The header's "N need you" and the You node both count needYouItems(), so they always agree; each thing
 * is one item (a member asking a question and also asking for a host is two).
 */
export type NeedYouKind = 'question' | 'network' | 'tool' | 'blocked' | 'failed';
export interface NeedYouItem { id: string; kind: NeedYouKind; team: string; member: string | null; label: string | null }
/** The order the kinds are listed in (the You node's breakdown, the item list). */
export const NeedYouKinds: readonly NeedYouKind[] = ['question', 'network', 'tool', 'blocked', 'failed'];
const NeedYouWords: Record<NeedYouKind, [string, string]> = {
  question: ['question', 'questions'], network: ['network ask', 'network asks'], tool: ['tool ask', 'tool asks'],
  blocked: ['blocked', 'blocked'], failed: ['failed', 'failed'],
};
const NeedYouTone: Record<NeedYouKind, Tone> = { question: 'decision', network: 'decision', tool: 'decision', blocked: 'blocked', failed: 'fail' };
/** One item's kind in words ("question", "network ask", "blocked"). */
export const needYouWord = (kind: NeedYouKind): string => NeedYouWords[kind][0];
/** What is in the list by kind: "2 questions, 1 network ask, 1 failed"; "Nothing needs you" when empty. */
export function needYouSummary(items: readonly Pick<NeedYouItem, 'kind'>[]): string {
  const parts: string[] = [];
  for (const k of NeedYouKinds) {
    const n = items.filter((i) => i.kind === k).length;
    if (n) parts.push(`${n} ${NeedYouWords[k][n === 1 ? 0 : 1]}`);
  }
  return parts.length ? parts.join(', ') : 'Nothing needs you';
}
/** A member's need-you item id: one per member and kind. */
const memberNeedId = (prefix: string, team: string, member: string) => `${prefix}:${team.toLowerCase()}/${member.toLowerCase()}`;
/** A fired item that never showed up in the list (a question the snapshot never saw) is forgotten after this long. */
export const NeedYouForgetMs = 30_000;

export class MonitorModel {
  /** keep: how many activity lines it holds (the Monitor's ActivityKeep; the team board keeps more of one team). */
  constructor(private readonly keep = ActivityKeep) {}

  teams: TeamSnapshot[] = [];
  activity: ActivityRow[] = [];
  effects: Effect[] = [];
  /** Each team's code map: the files its members touch and its commits confirm. */
  readonly map = new CodeMap();
  /** Log time now, for the map's idle time: the wall clock live, the replay's clock in a replay. */
  logNow: () => number = () => Date.now();
  /** The workflow each member was last told in, by member key (its touches belong to it). */
  private memberWorkflow = new Map<string, number>();
  private prunedAt = -Infinity;
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
  private asks = new Map<string, { team: string; member: string | null; kind: 'network' | 'tool'; label: string | null }>();
  /** The words of each member's last question (by member key), for its flyer to the You node. */
  private questions = new Map<string, string | null>();
  /** The need-you items that have flown (and sounded), by id; seen: the list has shown it since. */
  private fired = new Map<string, { at: number; seen: boolean }>();

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
   * flies from its member to the You node once (syncNeedYou); the You node counts them until they are gone from the list.
   * kind: network or tool (without it, read from the key: "n:" / "net" is a network ask, anything else a tool ask).
   */
  setAsks(asks: readonly { key: string; team: string; member: string | null; label?: string | null; kind?: 'network' | 'tool' }[], now = performance.now()): void {
    const next = new Map<string, { team: string; member: string | null; kind: 'network' | 'tool'; label: string | null }>();
    for (const a of asks)
      next.set(a.key, { team: a.team, member: a.member, kind: a.kind ?? (/^n(et)?[:/-]/i.test(a.key) ? 'network' : 'tool'), label: a.label ?? null });
    this.asks = next;
    this.syncNeedYou(now);
  }

  /**
   * Everything waiting for the person, each thing once (B088): members whose question is open, the network and tool
   * asks, blocked and failed members. Its length is the header's "N need you" and the You node's count.
   */
  needYouItems(): NeedYouItem[] {
    const items: NeedYouItem[] = [];
    const states: Partial<Record<MemberState, { kind: NeedYouKind; prefix: string }>> = {
      decision: { kind: 'question', prefix: 'q' }, blocked: { kind: 'blocked', prefix: 'b' }, failed: { kind: 'failed', prefix: 'f' },
    };
    for (const t of this.teams)
      for (const m of t.members) {
        const s = states[m.state];
        if (!s) continue;
        const label = s.kind === 'question' ? this.questions.get(memberKey(t.name, m.name)) ?? m.statusLine : m.statusLine;
        items.push({ id: memberNeedId(s.prefix, t.name, m.name), kind: s.kind, team: t.name, member: m.name, label: label ?? null });
      }
    for (const [key, a] of this.asks) items.push({ id: `a:${key}`, kind: a.kind, team: a.team, member: a.member, label: a.label });
    const rank = (k: NeedYouKind) => NeedYouKinds.indexOf(k);
    return items.sort((a, b) => rank(a.kind) - rank(b.kind));
  }

  /**
   * Fires the need-you items that are new (B088): each one flies to the You node, which pulses as it lands, and sounds
   * (sound.ts), once per id - a later poll, a change event or a feed row for the same item fires nothing. An item that
   * leaves the list is forgotten, so the same thing waiting again later is new. play false: the items are taken as
   * already there (the Monitor's start), counted but not flown. Returns the list.
   */
  syncNeedYou(now = performance.now(), play = true): NeedYouItem[] {
    const items = this.needYouItems();
    const present = new Set(items.map((i) => i.id));
    for (const [id, f] of this.fired) if (!present.has(id) && (f.seen || Math.abs(now - f.at) > NeedYouForgetMs)) this.fired.delete(id);
    for (const it of items) {
      const f = this.fired.get(it.id);
      if (f) { f.seen = true; continue; }
      this.fired.set(it.id, { at: now, seen: true });
      if (play)
        this.effects.push({
          kind: 'you', from: it.member ? memberKey(it.team, it.member) : teamKey(it.team), color: NeedYouTone[it.kind],
          label: clip(it.label ?? (it.kind === 'blocked' || it.kind === 'failed' ? it.kind : null), 30), at: now, id: it.id,
        });
    }
    this.capEffects();
    return items;
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
    // Which workflow a member works in, so the files its steps touch light that workflow's part of the code map.
    if (row.kind === 'instruction' && row.member) this.memberWorkflow.set(target, row.workflow);
    if (row.kind === 'done') this.map.endWorkflow(row.team, row.workflow);
    if (row.kind === 'decision' && row.member) this.questions.set(target, row.text);
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
      {
        // The question flies at once, before the next snapshot shows it; it is its member's question item, so the
        // snapshot's list does not fly it again (syncNeedYou).
        const id = row.member ? memberNeedId('q', row.team, row.member) : null;
        if (id && this.fired.has(id)) break;
        if (id) this.fired.set(id, { at: now, seen: false });
        this.effects.push({ kind: 'you', from: target, color: 'decision', label: clip(row.text, 30), at: now, ...(id ? { id } : {}) });
        break;
      }
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
    // The map is built from every step, played or not; only a played one pops and lights.
    this.map.touchStep(step, this.memberWorkflow.get(node) ?? null, live ? now : null);
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
    this.effects.push({ kind: 'step', node, step: step.kind, caption: step.kind === 'edit' || step.kind === 'create' ? null : stepCaption(step), at: now });
    this.capEffects();
  }

  /**
   * Applies a code event: always, its files correct the team's map and it joins the activity list; live, a commit's
   * dot travels from its member to the hub, a merge bursts along the hub (a landing larger), a push pulses off the edge.
   */
  applyCode(ev: CodeEvent, now: number, live = true): void {
    const lit = this.map.applyCode(ev, live ? now : null);
    const look = CodeVerbs[ev.kind];
    const first = ev.message.split('\n')[0]!.trim();
    this.activity.unshift({
      key: `c${codeEventKey(ev)}`, seq: 0, tone: look.tone, team: ev.team, member: ev.member,
      verb: look.verb, who: spaced(ev.member ?? ev.team),
      text: ev.kind === 'push' ? ev.branch || null : [ev.sha ? shortSha(ev.sha) : '', clip(first, 60) ?? '', ev.files.length ? `(${ev.files.length} file${ev.files.length === 1 ? '' : 's'})` : ''].filter(Boolean).join(' ') || null,
      at: ev.at,
    });
    if (this.activity.length > this.keep) for (const gone of this.activity.splice(this.keep)) this.gathered.delete(gone.key);
    if (!live) return;
    this.recent.push(now);
    const tk = ev.team.toLowerCase();
    if (ev.kind === 'commit')
      this.effects.push({
        kind: 'commit', team: ev.team, node: ev.member ? memberKey(ev.team, ev.member) : null, files: lit.map((f) => `${tk}|${f.path}`),
        sha: ev.sha, message: first, member: ev.member, at: now,
      });
    else if (ev.kind === 'push') this.effects.push({ kind: 'push', team: ev.team, at: now });
    else this.effects.push({ kind: 'merge', team: ev.team, big: ev.kind === 'landed', at: now });
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

  /** Forgets the map's idle files (twice a second) and the "now" lines that have run out (the scene calls this each frame). */
  tidy(now: number): void {
    if (now - this.prunedAt >= 500 || now < this.prunedAt) { this.prunedAt = now; this.map.prune(this.logNow()); }
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

  /** The header's counts; needYou is needYouItems()'s length, the You node's count. */
  counts(): { running: number; waiting: number; needYou: number; members: number } {
    let running = 0, waiting = 0, members = 0;
    for (const t of this.teams)
      for (const m of t.members) {
        members++;
        if (m.state === 'running') running++;
        else if (m.state === 'waiting') waiting++;
      }
    return { running, waiting, needYou: this.needYouItems().length, members };
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
