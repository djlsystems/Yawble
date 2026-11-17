// @vitest-environment happy-dom
import { afterEach, describe, expect, it } from 'vitest';
import { memberKey, MonitorModel, RunEndShowMs, RunStaleMs, teamKey, workflowKey, type Effect, type FeedRow, type TeamSnapshot } from '../../src/renderer/src/monitor/model';
import { MonitorScene } from '../../src/renderer/src/monitor/scene';
import { capEffects, cardOf, DensityCaps, FocusDim, focusAlpha, onYouNode, pulsePeriodMs, runFill, teamOfKey, youNodeAt } from '../../src/renderer/src/monitor/rhythm';
import { busyMembers, openWorkflows, workflowEnds, workflowNodeLook, WorkflowNodesEach } from '../../src/renderer/src/monitor/workflows';
import { readStep, type StepKind, type StepRow } from '../../src/renderer/src/monitor/steps';
import type { WorkflowSummary } from '../../src/shared/api';

/**
 * B080 (card 373): the scene's workflow nodes, work rhythm, You node, card-numbered comets, density, focus and reduced
 * motion, with a fake model (MonitorModel fed by hand) and, for the scene itself, a canvas whose 2D context is a stub.
 */

const row = (seq: number, kind: FeedRow['kind'], member: string | null, from: string, text: string | null = null, workflow = 1): FeedRow =>
  ({ seq, kind, team: 'payments', member, from, text, workflow, at: new Date().toISOString() });
const step = (id: number, kind: StepKind, target: string | null = null, extra: Partial<StepRow> = {}): StepRow =>
  ({ id, at: new Date().toISOString(), team: 'payments', member: 'DeveloperPriya', run: 1, kind, tool: kind === 'run' ? 'Bash' : 'Edit', target, ...extra });
const wf = (id: number, state: WorkflowSummary['state'] = 'open', busy: string[] = [], lastAt = new Date(1_000_000 + id * 1000).toISOString()): WorkflowSummary =>
  ({ id, title: `Workflow ${id}`, owner: null, state, undeclared: false, busy, ending: null, startedAt: lastAt, lastAt, messages: 2 });
const member = (name: string, state: TeamSnapshot['members'][number]['state'] = 'idle', role: 'manager' | 'member' = 'member', id = 1) =>
  ({ id, name, role, state, queued: 0, statusLine: null, cli: 'claude' });
const team = (id: number, name: string, members = [member('Manager', 'idle', 'manager'), member('DeveloperPriya'), member('TesterWren')]): TeamSnapshot =>
  ({ id, name, members });

describe('cards on the comets (B080 item 4)', () => {
  it('reads the card number from an instruction', () => {
    expect(cardOf('Card 373 (B080, Monitor scene). Read the card')).toBe(373);
    expect(cardOf('[card 373] Addition to card 373')).toBe(373);
    expect(cardOf('Fix card #12 please')).toBe(12);
    expect(cardOf('Add a README')).toBeNull();
    expect(cardOf('discard 5 lines')).toBeNull(); // "card" inside a word is not a card
    expect(cardOf(null)).toBeNull();
  });

  it('a Manager instruction carries its card number; the hand-back returns along the same line with a check', () => {
    const m = new MonitorModel();
    m.apply(row(1, 'instruction', 'DeveloperPriya', 'Manager', 'Card 373 (B080): the scene'), 0);
    m.apply(row(2, 'handback', 'DeveloperPriya', 'DeveloperPriya', 'done'), 10);
    const [out, back] = m.effects as Extract<Effect, { kind: 'comet' }>[];
    expect(out).toMatchObject({ kind: 'comet', from: memberKey('payments', 'Manager'), to: memberKey('payments', 'DeveloperPriya'), label: '#373', card: 373 });
    expect(back).toMatchObject({ kind: 'comet', from: memberKey('payments', 'DeveloperPriya'), to: memberKey('payments', 'Manager'), mark: 'check', card: 373, label: '#373 ✓' });
  });

  it('a failed or blocked run comes home with a cross', () => {
    const m = new MonitorModel();
    m.apply(row(1, 'instruction', 'TesterWren', 'Manager', 'card 9: verify'), 0);
    m.apply(row(2, 'blocked', 'TesterWren', 'TesterWren', 'no host'), 10);
    m.apply(row(3, 'failed', 'DeveloperPriya', 'DeveloperPriya', 'timed out'), 20);
    const comets = m.effects.filter((e): e is Extract<Effect, { kind: 'comet' }> => e.kind === 'comet' && !!e.mark);
    expect(comets).toEqual([
      expect.objectContaining({ from: memberKey('payments', 'TesterWren'), mark: 'cross', label: '#9 ✗', color: 'blocked' }),
      // Never told on this wall: it goes to the Manager, with words instead of a number.
      expect.objectContaining({ from: memberKey('payments', 'DeveloperPriya'), to: memberKey('payments', 'Manager'), mark: 'cross', label: 'failed', card: null }),
    ]);
  });

  it('an instruction with no card keeps its words; rows read at the start play nothing but remember the card', () => {
    const m = new MonitorModel();
    m.apply(row(1, 'instruction', 'DeveloperPriya', 'Manager', 'Card 40: old'), 0, false);
    expect(m.effects).toHaveLength(0);
    m.apply(row(2, 'handback', 'DeveloperPriya', 'DeveloperPriya'), 5);
    expect(m.effects[0]).toMatchObject({ mark: 'check', card: 40 });
    m.apply(row(3, 'instruction', 'DeveloperPriya', 'person', 'Look at this'), 10);
    expect(m.effects[1]).toMatchObject({ from: teamKey('payments'), label: 'Look at this', card: null, color: 'person' });
  });
});

describe('the You node (B080 item 3)', () => {
  it('a question flies to it; it counts open questions and asks until they are answered', () => {
    const m = new MonitorModel();
    m.reconcile([team(1, 'payments', [member('Manager', 'idle', 'manager'), member('DeveloperPriya', 'decision')])], 0);
    expect(m.youCount()).toBe(1);
    m.apply(row(1, 'decision', 'DeveloperPriya', 'DeveloperPriya', 'MIT or Apache?'), 5);
    expect(m.effects.filter((e) => e.kind === 'you')).toEqual([{ kind: 'you', from: memberKey('payments', 'DeveloperPriya'), color: 'decision', label: 'MIT or Apache?', at: 5 }]);
    // Network and tool asks: each new one flies once; the count follows the list.
    m.setAsks([{ key: 'net:1', team: 'payments', member: 'TesterWren', label: 'api.example.com' }, { key: 'tool:2', team: 'payments', member: null }], 10);
    expect(m.youCount()).toBe(3);
    m.setAsks([{ key: 'net:1', team: 'payments', member: 'TesterWren' }, { key: 'tool:2', team: 'payments', member: null }], 20);
    const you = m.effects.filter((e) => e.kind === 'you');
    expect(you.map((e) => (e as Extract<Effect, { kind: 'you' }>).from)).toEqual([memberKey('payments', 'DeveloperPriya'), memberKey('payments', 'TesterWren'), teamKey('payments')]);
    // Answered: the question's member goes back to work, the asks are gone.
    m.reconcile([team(1, 'payments', [member('Manager', 'idle', 'manager'), member('DeveloperPriya', 'running')])], 30);
    m.setAsks([], 30);
    expect(m.youCount()).toBe(0);
  });

  it('sits at the wall\'s right edge and is hit within a few pixels', () => {
    const n = youNodeAt(1280, 800);
    expect(n.x).toBeGreaterThan(1200);
    expect(n.x + n.r).toBeLessThanOrEqual(1280);
    expect(n.y).toBe(400);
    expect(youNodeAt(400, 150).y).toBe(120); // below the counts on a short wall
    expect(onYouNode(n.x, n.y, 1280, 800)).toBe(true);
    expect(onYouNode(n.x - n.r - 5, n.y, 1280, 800)).toBe(true);
    expect(onYouNode(n.x - n.r - 8, n.y, 1280, 800)).toBe(false);
    expect(onYouNode(640, 400, 1280, 800)).toBe(false);
  });
});

describe('the workflow nodes (B080 item 1)', () => {
  it('each open workflow is a node; completed and closed ones are not; the busy list says who works it', () => {
    const list = [wf(1, 'completed'), wf(2, 'open', ['DeveloperPriya is running']), wf(3, 'closed'), wf(4, 'open')];
    expect(openWorkflows(list).map((w) => w.id)).toEqual([4, 2]);
    expect(openWorkflows(Array.from({ length: 20 }, (_, i) => wf(i + 1)))).toHaveLength(WorkflowNodesEach);
    expect(busyMembers(wf(2, 'open', ['DeveloperPriya is running', ' TesterWren queued ', '']))).toEqual(['DeveloperPriya', 'TesterWren']);
    expect(workflowNodeLook(wf(2, 'open', ['x']), false)).toMatchObject({ status: 'working', icon: 'play' });
    expect(workflowNodeLook(wf(4), false)).toMatchObject({ status: 'waiting', icon: 'pause' });
    expect(workflowNodeLook(wf(4), true)).toMatchObject({ status: 'blocked', tone: 'blocked', icon: 'bang' });
  });

  it('a workflow that completes or closes leaves with its own effect; the model tracks the blocked look', () => {
    expect(workflowEnds([wf(1), wf(2), wf(3, 'completed')], [wf(1, 'completed'), wf(2, 'closed'), wf(3, 'completed')]))
      .toEqual([{ id: 1, status: 'completed' }, { id: 2, status: 'closed' }]);
    const m = new MonitorModel();
    m.setWorkflows('Payments', [wf(1), wf(2, 'open', ['DeveloperPriya is running'])], 0);
    expect(m.openWfs.get(teamKey('payments'))!.map((w) => w.id)).toEqual([2, 1]);
    expect(m.effects).toHaveLength(0); // the first list ends nothing
    const rev = m.workflowsRev;
    m.apply(row(1, 'blocked', 'DeveloperPriya', 'DeveloperPriya', 'stuck', 2), 5);
    expect(m.blockedWfs.has(workflowKey('payments', 2))).toBe(true);
    m.apply(row(2, 'progress', 'DeveloperPriya', 'DeveloperPriya', 'going again', 2), 6);
    expect(m.blockedWfs.has(workflowKey('payments', 2))).toBe(false);
    m.effects.length = 0;
    m.setWorkflows('payments', [wf(1, 'closed'), wf(2, 'completed')], 50);
    expect(m.effects).toEqual([
      { kind: 'wfdone', key: workflowKey('payments', 1), team: 'payments', status: 'closed', at: 50 },
      { kind: 'wfdone', key: workflowKey('payments', 2), team: 'payments', status: 'completed', at: 50 },
    ]);
    expect(m.openWfs.get(teamKey('payments'))).toEqual([]);
    expect(m.workflowsRev).toBeGreaterThan(rev);
  });
});

describe('the work rhythm (B080 item 2)', () => {
  it('pulses faster with more steps, not at all when quiet', () => {
    expect(pulsePeriodMs(0)).toBeNull();
    const slow = pulsePeriodMs(2)!, busy = pulsePeriodMs(30)!, frantic = pulsePeriodMs(1000)!;
    expect(slow).toBeGreaterThan(busy);
    expect(busy).toBeGreaterThan(frantic);
    expect(frantic).toBe(450); // capped
    const m = new MonitorModel();
    for (let i = 0; i < 6; i++) m.applyStep(step(i + 1, 'read', 'a.ts'), i * 1000);
    expect(m.stepRate(memberKey('payments', 'DeveloperPriya'), 6000)).toBe(6);
    expect(m.stepRate(memberKey('payments', 'DeveloperPriya'), 70_000)).toBe(0); // a minute later: quiet
    m.applyStep(step(9, 'read', 'b.ts'), 0, false);
    expect(m.stepRate(memberKey('payments', 'DeveloperPriya'), 0)).toBe(0); // the past does not pulse
  });

  it('a command\'s ring fills while it runs and ends at the next step; an exit code bursts green or red', () => {
    expect(runFill(0)).toBe(0);
    expect(runFill(5000)).toBeGreaterThan(0.1);
    expect(runFill(60_000)).toBeGreaterThan(runFill(5000));
    expect(runFill(10 * 60_000)).toBeLessThan(1);
    const key = memberKey('payments', 'DeveloperPriya');
    const m = new MonitorModel();
    m.applyStep(step(1, 'run', 'npm test'), 100);
    expect(m.runs.get(key)).toEqual({ at: 100, target: 'npm test', ended: null, exit: null });
    m.applyStep(step(2, 'read', 'a.ts'), 4100);
    expect(m.runs.get(key)!.ended).toBe(4100);
    expect(m.effects.filter((e) => e.kind === 'burst')).toHaveLength(0); // no exit code known: no burst
    m.tidy(4100 + RunEndShowMs + 1);
    expect(m.runs.has(key)).toBe(false);
    m.applyStep(step(3, 'run', 'npm test', { exit: 0 }), 5000);
    m.applyStep(step(4, 'run', 'npm run lint', { exit: 2 }), 6000);
    expect(m.effects.filter((e) => e.kind === 'burst')).toEqual([
      { kind: 'burst', node: key, color: 'done', big: false, at: 5000 },
      { kind: 'burst', node: key, color: 'fail', big: true, at: 6000 },
    ]);
    expect(m.runs.get(key)).toMatchObject({ exit: 2, ended: 6000 });
    // A run that never hears again is over after RunStaleMs; the end of the member's run ends it too.
    m.applyStep(step(5, 'run', 'sleep'), 10_000);
    m.tidy(10_000 + RunStaleMs + 1);
    expect(m.runs.has(key)).toBe(false);
    m.applyStep(step(6, 'run', 'build'), 20_000);
    m.apply(row(9, 'completed', 'DeveloperPriya', 'DeveloperPriya'), 21_000);
    expect(m.runs.get(key)!.ended).toBe(21_000);
  });

  it('reads an exit code off the wire only when it is a whole number', () => {
    expect(readStep({ id: 1, at: 'x', team: 't', member: 'm', kind: 'run', tool: 'Bash', target: 'npm test', exit: 1 })).toMatchObject({ exit: 1 });
    expect(readStep({ id: 1, at: 'x', team: 't', member: 'm', kind: 'run', tool: 'Bash', target: 'npm test', exit: 'x' })).not.toHaveProperty('exit');
    expect(readStep({ id: 1, at: 'x', team: 't', member: 'm', kind: 'run', tool: 'Bash', target: 'npm test' })).not.toHaveProperty('exit');
  });

  it('thinking is the last step being a thought, for the "now" line\'s half minute', () => {
    const m = new MonitorModel();
    const key = memberKey('payments', 'DeveloperPriya');
    m.applyStep(step(1, 'think'), 0);
    expect(m.thinking(key, 1000)).toBe(true);
    expect(m.thinking(key, 31_000)).toBe(false);
    m.applyStep(step(2, 'edit', 'a.ts'), 2000);
    expect(m.thinking(key, 2500)).toBe(false);
  });
});

describe('density and focus (B080 item 5)', () => {
  const steps = (node: string, n: number, at0 = 0): Effect[] => Array.from({ length: n }, (_, i) => ({ kind: 'step', node, step: 'read', caption: null, at: at0 + i }));
  const comet = (i: number, from = memberKey('a', 'manager'), to = memberKey('a', 'dev')): Effect => ({ kind: 'comet', from, to, color: 'tell', label: null, at: i });

  it('caps the effects drawn per frame by density, keeping what flies and ends before the step icons', () => {
    const many: Effect[] = [...steps(memberKey('a', 'dev'), 40), ...Array.from({ length: 30 }, (_, i) => comet(100 + i)), ...steps(memberKey('a', 'qa'), 40)];
    const calm = capEffects(many, 'calm'), normal = capEffects(many, 'normal'), lively = capEffects(many, 'lively');
    expect(calm.length).toBe(DensityCaps.calm.effects);
    expect(calm.every((e) => e.kind === 'comet')).toBe(true); // comets win
    expect(normal.length).toBe(30 + 2 * DensityCaps.normal.stepsEach); // all comets, then a few icons on each member
    expect(lively.filter((e) => e.kind === 'step' && e.node === memberKey('a', 'dev'))).toHaveLength(DensityCaps.lively.stepsEach);
    expect(DensityCaps.calm.effects).toBeLessThan(DensityCaps.normal.effects);
    expect(DensityCaps.normal.effects).toBeLessThan(DensityCaps.lively.effects);
    // The kept ones stay in their order, the newest of a kind kept first.
    const icons = normal.filter((e) => e.kind === 'step' && e.node === memberKey('a', 'dev'));
    expect(icons.map((e) => e.at)).toEqual([37, 38, 39]);
    const floats: Effect[] = Array.from({ length: 30 }, (_, i) => ({ kind: 'float', node: memberKey('a', `m${i}`), color: 'progress', text: 'x', at: i }));
    expect(capEffects(floats, 'calm')).toHaveLength(DensityCaps.calm.floats);
  });

  it('a focused team shows only its own effects (and comets that reach it); the rest is dimmed', () => {
    const mixed: Effect[] = [comet(1), comet(2, memberKey('b', 'manager'), memberKey('b', 'dev')), comet(3, teamKey('b'), memberKey('a', 'dev')),
      { kind: 'wfdone', key: workflowKey('b', 4), team: 'b', status: 'completed', at: 4 }, { kind: 'you', from: memberKey('b', 'dev'), color: 'decision', label: null, at: 5 }];
    expect(capEffects(mixed, 'normal', 'a').map((e) => e.at)).toEqual([1, 3]);
    expect(capEffects(mixed, 'normal', 'b').map((e) => e.at)).toEqual([2, 3, 4, 5]);
    expect(capEffects(mixed, 'normal', null)).toHaveLength(5);
    expect(teamOfKey(memberKey('Payments', 'Dev'))).toBe('payments');
    expect(teamOfKey(workflowKey('payments', 3))).toBe('payments');
    expect(teamOfKey('w:payments')).toBe('payments');
    expect(teamOfKey('you')).toBeNull();
    expect(focusAlpha(memberKey('a', 'dev'), null)).toBe(1);
    expect(focusAlpha(memberKey('a', 'dev'), 'a')).toBe(1);
    expect(focusAlpha(teamKey('b'), 'a')).toBe(FocusDim);
  });
});

// ---------- the scene itself, on a stub canvas ----------

/** A 2D context that takes every call and setting (measureText measures 6 px a character). */
function stubContext(): CanvasRenderingContext2D {
  const calls: string[] = [];
  const target: Record<string | symbol, unknown> = { calls };
  return new Proxy(target, {
    get(t, p) {
      if (p in t) return t[p];
      if (p === 'measureText') return (s: string) => ({ width: s.length * 6 });
      return (..._: unknown[]) => { calls.push(String(p)); return { addColorStop() {} }; };
    },
    set(t, p, v) { t[p] = v; return true; },
  }) as unknown as CanvasRenderingContext2D;
}

const scenes: MonitorScene[] = [];
function makeScene(w = 1280, h = 800): { scene: MonitorScene; canvas: HTMLCanvasElement; ctx: CanvasRenderingContext2D } {
  const canvas = document.createElement('canvas');
  const ctx = stubContext();
  Object.defineProperty(canvas, 'getContext', { value: () => ctx });
  Object.defineProperty(canvas, 'clientWidth', { value: w });
  Object.defineProperty(canvas, 'clientHeight', { value: h });
  canvas.setPointerCapture = () => {};
  document.body.appendChild(canvas);
  const scene = new MonitorScene(canvas);
  scenes.push(scene);
  return { scene, canvas, ctx };
}
/** One frame of the scene, at `now` (its frame loop is private; requestAnimationFrame would run it the same way). */
const frame = (scene: MonitorScene, now: number) => (scene as unknown as { frame: (t: number) => void }).frame(now);

afterEach(() => { for (const s of scenes.splice(0)) s.destroy(); document.body.innerHTML = ''; });

describe('the scene (B080)', () => {
  it('draws a node per open workflow, tied to the members working it or to its hub', () => {
    const { scene } = makeScene();
    const m = new MonitorModel();
    const teams = [team(7, 'Payments')];
    m.reconcile(teams, 0);
    m.setWorkflows('Payments', [wf(1, 'open', ['DeveloperPriya is running', 'TesterWren queued']), wf(2), wf(3, 'completed')], 0);
    m.apply(row(1, 'blocked', 'TesterWren', 'TesterWren', 'no host', 2), 0, false);
    scene.model = m;
    scene.setTeams(teams);
    frame(scene, 16);
    const nodes = scene.workflowNodes().sort((a, b) => a.id - b.id);
    expect(nodes).toEqual([
      { key: workflowKey('payments', 1), id: 1, to: [memberKey('payments', 'DeveloperPriya'), memberKey('payments', 'TesterWren')], status: 'working' },
      { key: workflowKey('payments', 2), id: 2, to: [teamKey('payments')], status: 'blocked' },
    ]);
    // Workflow 1 completes: its node leaves (drawn by its effect from where it was).
    m.setWorkflows('Payments', [wf(1, 'completed'), wf(2), wf(3, 'completed')], 32);
    scene.effects.push(...m.effects.splice(0));
    frame(scene, 32);
    expect(scene.workflowNodes().map((n) => n.id)).toEqual([2]);
    expect(scene.drawn).toEqual([expect.objectContaining({ kind: 'wfdone', key: workflowKey('payments', 1), status: 'completed' })]);
  });

  it('setFocus takes a team id, zooms to it and leaves with null; setDensity takes the three levels only', () => {
    const { scene } = makeScene();
    const teams = [team(7, 'Payments'), team(9, 'Search')];
    scene.model = new MonitorModel();
    scene.setTeams(teams);
    expect(scene.focus).toBeNull();
    scene.setFocus(9);
    expect(scene.focus).toBe(9);
    expect(scene.focusTeam()).toBe('search');
    // Effects of the other team are not drawn while focused.
    scene.effects.push({ kind: 'burst', node: memberKey('payments', 'DeveloperPriya'), color: 'done', big: false, at: 100 },
      { kind: 'burst', node: memberKey('search', 'DeveloperPriya'), color: 'done', big: false, at: 100 });
    frame(scene, 110);
    expect(scene.drawn.map((e) => (e as Extract<Effect, { kind: 'burst' }>).node)).toEqual([memberKey('search', 'DeveloperPriya')]);
    scene.setFocus(42); // a team that is not on the wall: no focus in effect
    expect(scene.focusTeam()).toBeNull();
    scene.setFocus(null);
    expect(scene.focus).toBeNull();
    frame(scene, 120);
    expect(scene.drawn).toHaveLength(2);
    expect(scene.densityLevel).toBe('normal');
    scene.setDensity('calm');
    expect(scene.densityLevel).toBe('calm');
    scene.setDensity('wild' as never);
    expect(scene.densityLevel).toBe('calm');
    scene.setDensity('lively');
    expect(scene.densityLevel).toBe('lively');
  });

  it('caps what one frame draws by the density', () => {
    const { scene } = makeScene();
    scene.model = new MonitorModel();
    scene.setTeams([team(1, 'Payments')]);
    const node = memberKey('payments', 'DeveloperPriya');
    for (let i = 0; i < 200; i++) scene.effects.push({ kind: 'burst', node, color: 'done', big: false, at: 1000 + i });
    scene.setDensity('calm');
    frame(scene, 1300);
    expect(scene.drawn).toHaveLength(DensityCaps.calm.effects);
    scene.setDensity('lively');
    frame(scene, 1310);
    expect(scene.drawn).toHaveLength(DensityCaps.lively.effects);
  });

  it('hitYouNode answers in canvas CSS pixels, and a press on the You node picks nothing', () => {
    const { scene, canvas } = makeScene(1000, 600);
    scene.model = new MonitorModel();
    scene.setTeams([team(1, 'Payments')]);
    frame(scene, 16);
    const you = youNodeAt(1000, 600);
    expect(scene.hitYouNode(you.x, you.y)).toBe(true);
    expect(scene.hitYouNode(500, 300)).toBe(false);
    const picks: (string | null)[] = [];
    scene.onPick = (k) => picks.push(k);
    canvas.getBoundingClientRect = () => ({ left: 0, top: 0, right: 1000, bottom: 600, width: 1000, height: 600, x: 0, y: 0, toJSON() {} });
    canvas.dispatchEvent(new PointerEvent('pointerdown', { clientX: you.x, clientY: you.y, pointerId: 1, pointerType: 'mouse' }));
    canvas.dispatchEvent(new PointerEvent('pointerup', { clientX: you.x, clientY: you.y, pointerId: 1, pointerType: 'mouse' }));
    expect(picks).toEqual([]);
  });

  it('draws the You node with its count, and a frame under reduced motion draws still comets without throwing', () => {
    const { scene, ctx } = makeScene();
    const m = new MonitorModel();
    const teams = [team(1, 'Payments', [member('Manager', 'idle', 'manager'), member('DeveloperPriya', 'decision')])];
    m.reconcile(teams, 0);
    scene.model = m;
    scene.setTeams(teams);
    scene.setReducedMotion(true);
    expect(scene.reduced).toBe(true);
    m.apply(row(1, 'instruction', 'DeveloperPriya', 'Manager', 'Card 5: go'), 1000);
    m.apply(row(2, 'decision', 'DeveloperPriya', 'DeveloperPriya', 'which?'), 1000);
    m.applyStep(step(1, 'run', 'npm test'), 1000);
    m.applyStep(step(2, 'think'), 1000);
    scene.effects.push(...m.effects.splice(0));
    const texts: string[] = [];
    (ctx as unknown as Record<string, unknown>).fillText = (s: string) => { texts.push(s); };
    frame(scene, 2500); // past a moving comet's life (1100 ms), within a still one's
    expect(scene.drawn.some((e) => e.kind === 'comet')).toBe(true);
    expect(texts).toContain('You');
    expect(texts).toContain('1'); // one question waits
    expect(texts).toContain('#5');
    scene.setReducedMotion(false);
    frame(scene, 2600);
    expect(scene.drawn.some((e) => e.kind === 'comet')).toBe(false); // moving, its life is over
  });
});
