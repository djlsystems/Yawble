// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import {
  CodeMap, codeEventKey, collapse, commitPhase, CommitGatherMs, CommitTravelMs, DefaultLimits, fileLight, layoutTeamMap, MaxMapNodes,
  PopMs, popScale, readCodeEvent, readCodeEvents, idleFade, MapIdleMs, MapFadeMs, type CodeEvent,
} from '../../src/renderer/src/monitor/codemap';
import { MonitorModel, type FeedRow } from '../../src/renderer/src/monitor/model';
import { inTimeOrder, ReplayClock } from '../../src/renderer/src/monitor/timeline';
import type { StepKind, StepRow } from '../../src/renderer/src/monitor/steps';
import MonitorView from '../../src/renderer/src/monitor/MonitorView.vue';
import Timeline from '../../src/renderer/src/monitor/Timeline.vue';
import type { YawbleApi } from '../../src/shared/api';

// Fixtures in the shape card 370's monitor.codeEvents returns (oldest first).
const t0 = Date.parse('2026-11-17T10:00:00Z');
const iso = (ms: number) => new Date(ms).toISOString();
let ids = 0;
const step = (kind: StepKind, target: string, atMs: number, member = 'DeveloperInes', team = 'Code'): StepRow =>
  ({ id: ++ids, at: iso(atMs), team, member, run: 1, kind, tool: kind, target });
const commit = (files: [string, string][], atMs: number, extra: Partial<Record<string, unknown>> = {}): Record<string, unknown> => ({
  kind: 'commit', team: 'Code', member: 'DeveloperInes', branch: 'members/code/developerines', sha: 'abcdef1234567890', message: 'Card 1: the map\n\nbody',
  files: files.map(([status, path]) => ({ path, status })), at: iso(atMs), workflowId: 7, ...extra,
});
const ev = (raw: Record<string, unknown>) => readCodeEvent(raw)!;
const row = (seq: number, kind: FeedRow['kind'], member: string | null, workflow: number, atMs: number): FeedRow =>
  ({ seq, kind, team: 'Code', member, from: 'Manager', text: null, workflow, at: iso(atMs) });

describe('the code events off the wire', () => {
  it('reads the card\'s shape, never trusts it, and sorts oldest first', () => {
    const e = readCodeEvent(commit([['A', 'src/a.ts'], ['R100', 'src/old.ts\tsrc/new.ts'], ['M', './src\\b.ts']], t0));
    expect(e).toMatchObject({ kind: 'commit', member: 'DeveloperInes', workflowId: 7 });
    expect(e!.files).toEqual([
      { path: 'src/a.ts', status: 'A', from: null },
      { path: 'src/new.ts', status: 'R', from: 'src/old.ts' },
      { path: 'src/b.ts', status: 'M', from: null },
    ]);
    expect(readCodeEvent({ kind: 'rebase', team: 'Code', at: iso(t0) })).toBeNull();
    expect(readCodeEvent({ kind: 'push', team: 'Code', at: 'not a time' })).toBeNull();
    expect(readCodeEvent({ kind: 'push', team: 'Code', at: iso(t0), files: 'x' })).toMatchObject({ files: [], member: null, workflowId: null });
    const list = readCodeEvents({ events: [commit([], t0 + 5), commit([], t0), null, 3] });
    expect(list.map((x) => Date.parse(x.at))).toEqual([t0, t0 + 5]);
    expect(readCodeEvents([commit([], t0)])).toHaveLength(1);
    expect(readCodeEvents(undefined)).toEqual([]);
    expect(codeEventKey(list[0]!)).not.toBe(codeEventKey({ ...list[0]!, kind: 'merge-team' }));
  });
});

describe('the map from the steps', () => {
  it('builds one tree per team: a Write is a new file, an Edit bright, a Read faint; a line per member', () => {
    const map = new CodeMap();
    map.touchStep(step('create', 'src/a.ts', t0), 7, 100);
    map.touchStep(step('edit', 'src/b.ts', t0 + 1), 7, 200);
    map.touchStep(step('read', 'docs/c.md', t0 + 2), 7, 300);
    map.touchStep(step('read', 'src/b.ts', t0 + 3, 'TesterKofi'), null, 400);
    map.touchStep(step('run', 'npm test', t0 + 4), 7, 500); // not a file
    map.touchStep(step('edit', 'x', t0, 'Other', 'Other team'), null, null);
    expect(map.teamNames().sort()).toEqual(['Code', 'Other team']);
    expect(map.files('Code').map((f) => f.path).sort()).toEqual(['docs/c.md', 'src/a.ts', 'src/b.ts']);
    const a = map.get('Code', 'src/a.ts')!, b = map.get('Code', 'src/b.ts')!, c = map.get('Code', 'docs/c.md')!;
    expect(a).toMatchObject({ created: true, confirmed: false, folder: 'src', bornShown: 100 });
    expect(b.created).toBe(false);
    // Two members on one file: both are kept, each with its strongest touch.
    expect([...b.members.values()].map((m) => [m.member, m.kind])).toEqual([['DeveloperInes', 'edit'], ['TesterKofi', 'read']]);
    // Light: a changed file rests brighter than a read one; a fresh touch lights it more.
    expect(fileLight(b, 10_000)).toBeGreaterThan(fileLight(c, 10_000));
    expect(fileLight(b, 250)).toBeGreaterThan(fileLight(b, 10_000));
  });

  it('prunes a file after the idle time (log time) and when every workflow that touched it has ended', () => {
    const m = new MonitorModel();
    m.map.idleMs = 10 * 60_000;
    m.apply(row(1, 'instruction', 'DeveloperInes', 7, t0), 0, false);
    m.apply(row(2, 'instruction', 'TesterKofi', 8, t0), 0, false);
    m.applyStep(step('edit', 'src/only7.ts', t0 + 1000), 0, false);
    m.applyStep(step('edit', 'src/both.ts', t0 + 1000), 0, false);
    m.applyStep(step('read', 'src/both.ts', t0 + 2000, 'TesterKofi'), 0, false);
    m.applyStep(step('edit', 'src/late.ts', t0 + 9 * 60_000, 'Unknown'), 0, false); // no workflow known: idle time only
    // Workflow 7 ends: its only file leaves; the one workflow 8 touched too stays.
    m.apply(row(3, 'done', null, 7, t0 + 3000), 0, false);
    expect(m.map.files('Code').map((f) => f.path).sort()).toEqual(['src/both.ts', 'src/late.ts']);
    m.apply(row(4, 'done', null, 8, t0 + 4000), 0, false);
    expect(m.map.files('Code').map((f) => f.path)).toEqual(['src/late.ts']);
    // Idle: the model prunes against its log clock (the replay's in a replay).
    m.logNow = () => t0 + 9 * 60_000 + 10 * 60_000 - 1;
    m.tidy(1000);
    expect(m.map.size).toBe(1);
    m.logNow = () => t0 + 9 * 60_000 + 10 * 60_000 + 1;
    m.tidy(2000);
    expect(m.map.size).toBe(0);
  });
});

describe('the map from the commits', () => {
  it('confirms, adds and deletes nodes; renames move them', () => {
    const map = new CodeMap();
    map.applyCode(ev(commit([['A', 'src/new.ts'], ['M', 'src/kept.ts'], ['R', 'src/was.ts\tsrc/is.ts']], t0)), 50);
    expect(map.files('Code').map((f) => f.path).sort()).toEqual(['src/is.ts', 'src/kept.ts', 'src/new.ts']);
    expect(map.get('Code', 'src/new.ts')).toMatchObject({ created: true, confirmed: true, sha: 'abcdef1234567890', bornShown: 50 });
    expect(map.get('Code', 'src/kept.ts')).toMatchObject({ created: false, confirmed: true });
    expect([...map.get('Code', 'src/kept.ts')!.members.keys()]).toEqual(['developerines']);
    map.applyCode(ev(commit([['D', 'src/kept.ts'], ['R', 'src/is.ts\tlib/is.ts']], t0 + 10)), null);
    expect(map.files('Code').map((f) => f.path).sort()).toEqual(['lib/is.ts', 'src/new.ts']);
    // A push changes no file.
    expect(map.applyCode(ev({ kind: 'push', team: 'Code', branch: 'main', sha: 'f', message: '', files: [{ path: 'z', status: 'A' }], at: iso(t0 + 20) }), 1)).toEqual([]);
    expect(map.size).toBe(2);
  });

  it('overrides what the steps said', () => {
    const map = new CodeMap();
    // A Write over a file the commit says was modified: an edit, not a new file.
    map.touchStep(step('create', 'src/a.ts', t0), null, 10);
    expect(map.get('Code', 'src/a.ts')!.created).toBe(true);
    map.applyCode(ev(commit([['M', 'src/a.ts']], t0 + 1000)), 20);
    expect(map.get('Code', 'src/a.ts')).toMatchObject({ created: false, confirmed: true });
    // Once confirmed, a later Write does not make it new again.
    map.touchStep(step('create', 'src/a.ts', t0 + 2000), null, 30);
    expect(map.get('Code', 'src/a.ts')!.created).toBe(false);
    // An Edit the commit calls an add: new.
    map.touchStep(step('edit', 'src/b.ts', t0), null, 10);
    map.applyCode(ev(commit([['A', 'src/b.ts']], t0 + 1000)), 20);
    expect(map.get('Code', 'src/b.ts')!.created).toBe(true);
    // A file the commit deleted is gone, and a step older than that commit (read late) does not bring it back...
    map.touchStep(step('edit', 'src/c.ts', t0), null, 10);
    map.applyCode(ev(commit([['D', 'src/c.ts']], t0 + 1000)), 20);
    expect(map.get('Code', 'src/c.ts')).toBeUndefined();
    map.touchStep(step('edit', 'src/c.ts', t0 + 500), null, 30);
    expect(map.get('Code', 'src/c.ts')).toBeUndefined();
    // ...but a newer step does (it was made again), provisional until a commit says so.
    map.touchStep(step('create', 'src/c.ts', t0 + 5000), null, 40);
    expect(map.get('Code', 'src/c.ts')).toMatchObject({ created: true, confirmed: false });
  });

  it('a commit in the model lights its files and sends a dot from its member to the hub; merges, landings and pushes burst', () => {
    const m = new MonitorModel();
    m.applyStep(step('edit', 'src/a.ts', t0), 5, true);
    m.effects.length = 0;
    m.applyCode(ev(commit([['M', 'src/a.ts'], ['A', 'src/b.ts']], t0 + 10)), 100, true);
    m.applyCode(ev({ kind: 'merge-team', team: 'Code', branch: 'members/code/developerines', sha: 'aa', message: 'Merge', files: [{ path: 'src/b.ts', status: 'A' }], at: iso(t0 + 20) }), 110, true);
    m.applyCode(ev({ kind: 'landed', team: 'Code', branch: 'main', sha: 'bb', message: 'Merge', files: [], at: iso(t0 + 30) }), 120, true);
    m.applyCode(ev({ kind: 'push', team: 'Code', branch: 'main', sha: 'bb', message: '', files: [], at: iso(t0 + 40) }), 130, true);
    expect(m.effects.map((e) => e.kind)).toEqual(['commit', 'merge', 'merge', 'push']);
    expect(m.effects[0]).toMatchObject({ node: 'm:code/developerines', files: ['code|src/a.ts', 'code|src/b.ts'], sha: 'abcdef1234567890', message: 'Card 1: the map' });
    expect(m.effects.slice(1, 3).map((e) => (e as { big: boolean }).big)).toEqual([false, true]);
    expect(m.activity.map((r) => r.verb)).toEqual(['pushed', 'landed on main', 'merged into the team', 'committed', 'edited files']);
    expect(m.activity[3]!.text).toBe('abcdef1 Card 1: the map (2 files)');
    // Applied quietly (the start, a replay's past): the map and the list, no effects.
    const q = new MonitorModel();
    q.applyCode(ev(commit([['A', 'src/z.ts']], t0)), 0, false);
    expect(q.effects).toHaveLength(0);
    expect(q.map.get('Code', 'src/z.ts')!.bornShown).toBe(-Infinity);
  });
});

describe('the collapsed view', () => {
  const big = () => {
    const map = new CodeMap();
    // 12 folders, the busiest first: folder i has 14 - i files, folder 0 edited, the rest read.
    for (let i = 0; i < 12; i++) for (let j = 0; j < 14 - i; j++) map.touchStep(step(i === 0 ? 'edit' : 'read', `pkg/f${i}/file${j}.ts`, t0 + i * 100 + j), null, null);
    return map;
  };

  it('a small map shows everything', () => {
    const map = new CodeMap();
    for (let j = 0; j < 12; j++) map.touchStep(step('edit', `src/f${j}.ts`, t0 + j), null, null);
    const v = map.view('Code');
    expect(v.folders).toHaveLength(1);
    expect(v.folders[0]!.files).toHaveLength(12);
    expect(v.folders[0]!.more).toHaveLength(0);
    expect(v.moreFolders).toBe(0);
  });

  it('a large map collapses to its busiest folders, with "+N" per folder for the rest', () => {
    const v = big().view('Code');
    expect(v.total).toBe(102);
    expect(v.folders).toHaveLength(DefaultLimits.maxFolders);
    expect(v.folders[0]!.path).toBe('pkg/f0');
    expect(v.folders[0]!.heat).toBe(1);
    expect(v.folders[1]!.heat).toBeLessThan(1);
    for (const f of v.folders) {
      expect(f.files.length).toBeLessThanOrEqual(DefaultLimits.perFolder + 1);
      expect(f.files.length + f.more.length).toBe(14 - Number(f.path.slice(5)));
    }
    expect(v.folders[0]!.more).toHaveLength(14 - DefaultLimits.perFolder);
    expect(v.folders.reduce((n, f) => n + f.files.length, 0)).toBeLessThanOrEqual(DefaultLimits.maxFiles);
    expect(v.moreFolders).toBe(4);
    expect(v.moreFiles).toBe(6 + 5 + 4 + 3); // folders f8..f11
    // Laid out: a node per drawn file, a "+N" slot per folder that has more, a "+N folders" place.
    const laid = layoutTeamMap(v, { x: 0, y: 0 }, 0, 200);
    expect(laid.files).toHaveLength(v.folders.reduce((n, f) => n + f.files.length, 0));
    expect(laid.rest).not.toBeNull();
    for (const f of laid.folders) expect(Math.hypot(f.x, f.y)).toBeCloseTo(200, 5);
    // A folder with room for all but one shows that one rather than "+1".
    const map = new CodeMap();
    for (let j = 0; j < DefaultLimits.perFolder + 1; j++) map.touchStep(step('edit', `a/x${j}.ts`, t0 + j), null, null);
    for (let j = 0; j < DefaultLimits.maxFiles; j++) map.touchStep(step('read', `b/y${j}.ts`, t0 + j), null, null);
    const a = map.view('Code').folders.find((f) => f.path === 'a')!;
    expect(a.more).toHaveLength(0);
  });

  it('the cap is kept on the busiest when the budget runs out', () => {
    const files = big().files('Code');
    const v = collapse('Code', files, { maxFiles: 10, maxFolders: 8, perFolder: 8 });
    expect(v.folders.reduce((n, f) => n + f.files.length, 0)).toBeLessThanOrEqual(11);
    expect(v.folders[0]!.files).toHaveLength(8);
  });
});

describe('the replay plays commits and merges at their times', () => {
  it('orders rows, steps and code events by time; a commit before the merge that brings it in', () => {
    const rows = [row(1, 'instruction', 'DeveloperInes', 7, t0)];
    const steps = [step('edit', 'src/a.ts', t0 + 1000), step('edit', 'src/b.ts', t0 + 3000)];
    const events = readCodeEvents([
      commit([['M', 'src/a.ts']], t0 + 2000),
      { kind: 'merge-team', team: 'Code', branch: 'members/code/developerines', sha: 'm1', message: 'Merge', files: [{ path: 'src/a.ts', status: 'M' }], at: iso(t0 + 2000) },
      { kind: 'landed', team: 'Code', branch: 'main', sha: 'm2', message: 'Merge', files: [], at: iso(t0 + 4000) },
    ]);
    const order = inTimeOrder(rows, steps, events).map((p) => ('row' in p ? 'row' : 'step' in p ? `step:${p.step.target}` : `event:${p.event.kind}`));
    expect(order).toEqual(['row', 'step:src/a.ts', 'event:commit', 'event:merge-team', 'step:src/b.ts', 'event:landed']);
    // The clock hands them out as their times come (60x: 100 ms of wall is 6 s of log time).
    const clock = new ReplayClock(rows, steps, t0 - 1, 60, undefined, events);
    const first = clock.step(25); // 1.5 s
    expect(first.events).toHaveLength(0);
    const second = clock.step(25); // to 3 s
    expect(second.events.map((e) => e.kind)).toEqual(['commit', 'merge-team']);
    expect(clock.drained).toBe(false);
    expect(clock.step(50).events.map((e) => e.kind)).toEqual(['landed']);
    expect(clock.drained).toBe(true);
    // A replay started after the commit does not play it again.
    expect(new ReplayClock([], [], t0 + 2500, 60, undefined, events).step(100).events.map((e) => e.kind)).toEqual(['landed']);
  });
});

describe('reduced motion', () => {
  it('no pops and no travel: the map just updates', () => {
    expect(popScale(1000, 1000 + PopMs / 3, true)).toBe(1);
    expect(popScale(1000, 1000 + PopMs / 3, false)).not.toBe(1);
    expect(popScale(-Infinity, 5, false)).toBe(1); // applied quietly: no pop either
    expect(popScale(1000, 1000 + PopMs, false)).toBe(1);
    expect(commitPhase(10, true)).toEqual({ gather: null, travel: 1, resting: true });
    expect(commitPhase(10, false).gather).toBeGreaterThan(0);
    const mid = commitPhase(CommitGatherMs + CommitTravelMs / 2, false);
    expect(mid.travel).toBeGreaterThan(0);
    expect(mid.travel).toBeLessThan(1);
    expect(commitPhase(CommitGatherMs + CommitTravelMs, false)).toMatchObject({ travel: 1, resting: true });
  });
});

describe('the budget', () => {
  it('500 files across 4 teams: built, and a frame\'s update, within a fixed budget', () => {
    const teams = ['Alpha', 'Beta', 'Gamma', 'Delta'];
    const steps: StepRow[] = [];
    const events: CodeEvent[] = [];
    for (let i = 0; i < 500; i++) {
      const team = teams[i % 4]!;
      const path = `src/mod${i % 23}/sub${i % 3}/file${i}.ts`;
      steps.push(step(i % 3 === 0 ? 'read' : i % 3 === 1 ? 'edit' : 'create', path, t0 + i * 10, `Dev${i % 5}`, team));
      if (i % 10 === 9) events.push(ev({ ...commit([['M', path], ['A', `${path}.snap`]], t0 + i * 10 + 5), team }));
    }
    const m = new MonitorModel();
    const built0 = performance.now();
    for (const p of inTimeOrder([], steps, events)) {
      if ('step' in p) m.applyStep(p.step, 0, false);
      else if ('event' in p) m.applyCode(p.event, 0, false);
    }
    const built = performance.now() - built0;
    expect(m.map.size).toBeGreaterThanOrEqual(500);
    // A frame: every team's collapsed view and its layout (the scene re-views at most every MapViewMs; here every frame).
    const frames: number[] = [];
    let drawn = 0;
    for (let f = 0; f < 30; f++) {
      const s = performance.now();
      drawn = 0;
      const each = Math.floor(MaxMapNodes / teams.length);
      teams.forEach((t, i) => {
        const v = m.map.view(t, { maxFiles: Math.min(DefaultLimits.maxFiles, each) });
        drawn += layoutTeamMap(v, { x: i * 400, y: 0 }, i, 220).files.length;
      });
      frames.push(performance.now() - s);
    }
    frames.sort((a, b) => a - b);
    expect(drawn).toBeLessThanOrEqual(MaxMapNodes);
    expect(built).toBeLessThan(250);
    expect(frames[Math.floor(frames.length / 2)]!).toBeLessThan(8); // the median frame, well inside 16.7 ms
  });
});

describe('the scene draws the map', () => {
  /** A 2D context that counts what is drawn and draws nothing. */
  function fakeCanvas() {
    const calls: Record<string, number> = {};
    const ctx = new Proxy({} as Record<string, unknown>, {
      get(target, key: string) {
        if (key in target) return target[key];
        if (key === 'createRadialGradient' || key === 'createLinearGradient') return () => ({ addColorStop() {} });
        if (key === 'measureText') return () => ({ width: 10 });
        return (..._: unknown[]) => { calls[key] = (calls[key] ?? 0) + 1; };
      },
      set(target, key: string, value) { target[key] = value; return true; },
    });
    const canvas = document.createElement('canvas');
    Object.defineProperty(canvas, 'clientWidth', { value: 1200 });
    Object.defineProperty(canvas, 'clientHeight', { value: 800 });
    (canvas as unknown as { getContext: () => unknown }).getContext = () => ctx;
    return { canvas, calls };
  }

  it('draws files, folders, "+N", member lines, a commit dot, merges and a push without a fault, and offers the hovers', async () => {
    const { MonitorScene } = await import('../../src/renderer/src/monitor/scene');
    const { canvas, calls } = fakeCanvas();
    const scene = new MonitorScene(canvas);
    const m = new MonitorModel();
    const teams = ['Code', 'Other'].map((name, i) => ({ id: i, name, members: [
      { id: 1, name: 'DeveloperInes', role: 'member' as const, state: 'running' as const, queued: 0, statusLine: null, cli: 'claude' },
      { id: 2, name: 'TesterKofi', role: 'member' as const, state: 'idle' as const, queued: 0, statusLine: null, cli: 'claude' },
    ] }));
    m.reconcile(teams, 0);
    m.logNow = () => t0 + 60_000;
    scene.model = m;
    scene.setTeams(teams);
    for (let i = 0; i < 120; i++) m.applyStep(step(i % 2 ? 'edit' : 'read', `src/d${i % 11}/f${i}.ts`, t0 + i, i % 3 ? 'DeveloperInes' : 'TesterKofi', i % 4 ? 'Code' : 'Other'), 1000, true);
    m.applyCode(ev(commit([['M', 'src/d1/f1.ts'], ['A', 'src/new.ts']], t0 + 500)), 1000, true);
    m.applyCode(ev({ kind: 'merge-team', team: 'Code', branch: 'b', sha: 's', message: 'm', files: [], at: iso(t0 + 600) }), 1000, true);
    m.applyCode(ev({ kind: 'landed', team: 'Code', branch: 'main', sha: 's2', message: 'm', files: [], at: iso(t0 + 700) }), 1000, true);
    m.applyCode(ev({ kind: 'push', team: 'Code', branch: 'main', sha: 's2', message: '', files: [], at: iso(t0 + 800) }), 1000, true);
    scene.effects.push(...m.effects.splice(0));
    const frame = (scene as unknown as { frame: (t: number) => void }).frame;
    for (const t of [1016, 1200, 1500, 2000, 3000, 6000]) frame(t);
    scene.destroy();
    const nodes = (scene as unknown as { mapNodes: { kind: string }[] }).mapNodes;
    expect(nodes.filter((n) => n.kind === 'file').length).toBeGreaterThan(0);
    expect(nodes.filter((n) => n.kind === 'file').length).toBeLessThanOrEqual(MaxMapNodes);
    expect(nodes.some((n) => n.kind === 'folder')).toBe(true);
    expect(nodes.some((n) => n.kind === 'more')).toBe(true); // 'Code' has 90 files in 11 folders: collapsed
    expect((scene as unknown as { commitSpots: unknown[] }).commitSpots).toHaveLength(1); // resting at the hub by now
    expect(calls.arc).toBeGreaterThan(100);
  });
});

describe('the Monitor with the code read', () => {
  const overview = { slots: { running: 0, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'Code', repoPath: null, members: 1, running: 0, queued: 0 }] };
  const agents = [{ member: { id: 2, teamId: 1, team: 'Code', name: 'DeveloperInes', role: 'member', cli: 'claude' }, state: 'idle', attention: null, queuedInstructions: 0, statusLine: null }];

  it('is a no-op when the core has no monitor.codeEvents: asked once per way, then never again', async () => {
    const asked: string[] = [];
    const api = {
      call: async (method: string) => {
        asked.push(method);
        if (method === 'app.overview') return overview;
        if (method === 'teams.agents') return agents;
        if (method === 'monitor.messages') return { end: 0, messages: [] };
        if (method === 'monitor.steps') return { end: 1, pending: false, steps: [step('edit', 'src/a.ts', Date.now() - 5000, 'DeveloperInes', 'Code')] };
        if (method === 'bus.workflows') return [];
        throw new Error(`unknown method ${method}`);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 20 } });
    // A few polls of the steps go by (the Monitor goes on), and the code read is never asked again.
    await vi.waitFor(async () => { await flushPromises(); expect(asked.filter((m) => m === 'monitor.steps').length).toBeGreaterThan(3); }, { timeout: 4000 });
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(asked.filter((m) => m === 'monitor.codeEvents')).toHaveLength(2); // all teams, then per team, then off
    expect(asked.filter((m) => m === 'monitor.steps').length).toBeGreaterThan(1); // the Monitor goes on
    expect(w.find('[data-test=monitor-counts]').exists()).toBe(true);
    expect(w.findAll('[data-test=monitor-row]').map((r) => r.text()).join('|')).toContain('a.ts');
    w.unmount();
  });

  it('takes the events (once each, though the read overlaps), lists them and hands them to the timeline', async () => {
    const asked: Record<string, unknown>[] = [];
    const now = Date.now();
    const list = [
      commit([['A', 'src/a.ts']], now - 60_000),
      { kind: 'merge-team', team: 'Code', branch: 'members/code/developerines', sha: 'feed123456', message: 'Merge', files: [{ path: 'src/a.ts', status: 'A' }], at: iso(now - 30_000) },
    ];
    const api = {
      call: async (method: string, params: Record<string, unknown> = {}) => {
        if (method === 'app.overview') return overview;
        if (method === 'teams.agents') return agents;
        if (method === 'monitor.messages') return { end: 0, messages: [] };
        if (method === 'monitor.steps') return { end: 0, pending: false, steps: [] };
        if (method === 'monitor.codeEvents') { asked.push(params); return list; }
        if (method === 'bus.workflows') return [];
        throw new Error(method);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 20 } });
    await vi.waitFor(async () => { await flushPromises(); expect(asked.length).toBeGreaterThan(1); }, { timeout: 4000 });
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(asked.length).toBeGreaterThan(1);
    expect(Date.parse(String(asked[0]!.since))).toBeLessThan(now - 60_000); // the start of the timeline
    expect(Date.parse(String(asked.at(-1)!.since))).toBe(now - 30_000); // then from the newest taken
    const rows = w.findAll('[data-test=monitor-row]').map((r) => r.text());
    expect(rows.filter((r) => r.includes('committed'))).toHaveLength(1);
    expect(rows.filter((r) => r.includes('merged into the team'))).toHaveLength(1);
    expect(rows.join('|')).toContain('abcdef1 Card 1: the map');
    expect((w.findComponent(Timeline).props('events') as CodeEvent[]).map((e) => e.kind)).toEqual(['commit', 'merge-team']);
    w.unmount();
  });

  it('keeps a file two minutes after its last touch, fading it over the last 20 s, with no setting for it', async () => {
    expect(new CodeMap().idleMs).toBe(MapIdleMs);
    expect(MapIdleMs).toBe(120_000);
    const f = { lastAt: 0 };
    expect(idleFade(f, 60_000)).toBe(1);
    expect(idleFade(f, MapIdleMs - MapFadeMs)).toBe(1);
    expect(idleFade(f, MapIdleMs - MapFadeMs / 2)).toBeCloseTo(0.5);
    expect(idleFade(f, MapIdleMs)).toBe(0);
    const api = { call: async () => { throw new Error('offline'); }, onEvent: () => () => {} } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=monitor-map-idle]').exists()).toBe(false);
    w.unmount();
  });
});
