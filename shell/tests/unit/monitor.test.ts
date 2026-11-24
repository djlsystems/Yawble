// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { memberKey, MonitorModel, teamKey, type FeedRow } from '../../src/renderer/src/monitor/model';
import MonitorView from '../../src/renderer/src/monitor/MonitorView.vue';
import Timeline from '../../src/renderer/src/monitor/Timeline.vue';
import { aimsAtEverything, ClusterCore, ClusterGap, FitOnceMs, mapRoomFor, MonitorScene, nearestPair, neighbourSpread, teamHome } from '../../src/renderer/src/monitor/scene';
import { clampLayout, isDefaultLayout, LayoutDefaults, LayoutFields, LayoutMax, LayoutMin, readLayout, writeLayout } from '../../src/renderer/src/monitor/layout';
import { collapse, DefaultLimits, layoutTeamMap, MapMembersReach, MapPastMembers, mapRoomOf } from '../../src/renderer/src/monitor/codemap';
import type { TeamSnapshot } from '../../src/renderer/src/monitor/model';
import type { YawbleApi, WorkflowSummary } from '../../src/shared/api';
import { entryTime, latestWorkflows, nodeLabelFont, NodeLabelMax, placeWorkflowLabels, workflowLabel, workflowLook, WorkflowLooks, workflowNodeLabel, WorkflowsShown } from '../../src/renderer/src/monitor/workflows';
import { readStep, stepCaption, stepDoing, type StepKind, type StepRow } from '../../src/renderer/src/monitor/steps';
import { MaxGapMs, mergeByTime, ReplayClock, ReplayRunningMs, ReplayStates, toneOfRow, trimByTime } from '../../src/renderer/src/monitor/timeline';

const row = (seq: number, kind: FeedRow['kind'], member: string | null, from: string, text: string | null = null, at = new Date().toISOString()): FeedRow =>
  ({ seq, kind, team: 'payments', member, from, text, workflow: 1, at });
const step = (id: number, kind: StepKind, target: string | null, offsetMs = 0, member = 'DeveloperPriya'): StepRow =>
  ({ id, at: new Date(Date.now() + offsetMs).toISOString(), team: 'payments', member, run: 1, kind, tool: kind === 'run' ? 'Bash' : 'Edit', target });
const at = (ms: number) => new Date(ms).toISOString();

describe('the camera', () => {
  it('Auto fit aims at everything except for 12 s after a move; Pinned only during a Fit', () => {
    expect(aimsAtEverything(false, 100_000, -Infinity, -Infinity)).toBe(true);
    expect(aimsAtEverything(false, 100_000, 95_000, -Infinity)).toBe(false); // moved 5 s ago
    expect(aimsAtEverything(false, 100_000, 87_000, -Infinity)).toBe(true); // 13 s ago
    expect(aimsAtEverything(true, 100_000, -Infinity, -Infinity)).toBe(false); // pinned: never on its own
    expect(aimsAtEverything(true, 100_000, -Infinity, 100_000 + FitOnceMs)).toBe(true); // a Fit under way
    expect(aimsAtEverything(true, 100_000 + FitOnceMs, -Infinity, 100_000 + FitOnceMs)).toBe(false); // and it ends
  });
});

describe('the steps', () => {
  it('reads a step off the wire, captions it and says what the member is doing', () => {
    expect(readStep({ id: 3, at: 'x', team: 't', member: 'm', kind: 'nonsense', tool: 'Z', target: '' })).toMatchObject({ kind: 'tool', target: null });
    expect(readStep({ id: 'no' })).toBeNull();
    expect(stepCaption({ kind: 'run', tool: 'Bash', target: 'npm run build && npm test' }, 12)).toBe('npm run bui…');
    expect(stepCaption({ kind: 'web', tool: 'WebFetch', target: 'https://example.com/a/b' })).toBe('example.com');
    expect(stepCaption({ kind: 'bus', tool: 'tell', target: 'TesterWren' })).toBe('tell → TesterWren');
    expect(stepDoing({ kind: 'edit', tool: 'Edit', target: 'web/src/parser.ts' })).toBe('editing parser.ts');
  });

  it('plays a step as an icon, a file on the map and a "now" line, and gathers file changes on one activity line', () => {
    const m = new MonitorModel();
    m.applyStep(step(1, 'edit', 'src/a.ts'), 100);
    m.applyStep(step(2, 'run', 'npm test'), 200);
    m.applyStep(step(3, 'create', 'src/b.ts'), 300);
    expect(m.effects.map((e) => e.kind)).toEqual(['step', 'step', 'step']);
    expect(m.effects[1]).toMatchObject({ step: 'run', caption: 'npm test' });
    expect(m.map.files('payments').map((f) => f.name).sort()).toEqual(['a.ts', 'b.ts']);
    expect(m.map.get('payments', 'src/b.ts')).toMatchObject({ created: true, bornShown: 300 });
    expect(m.doing.get(memberKey('payments', 'DeveloperPriya'))).toMatchObject({ text: 'writing b.ts', kind: 'create' });
    expect(m.activity).toHaveLength(1);
    expect(m.activity[0]).toMatchObject({ verb: 'wrote files', text: 'a.ts, b.ts', tone: 'edit' });
    // Read at the start: listed and on the map, not played (no pop, no light).
    const quiet = new MonitorModel();
    quiet.applyStep(step(4, 'edit', 'x.ts'), 0, false);
    expect(quiet.effects).toHaveLength(0);
    expect(quiet.map.get('payments', 'x.ts')).toMatchObject({ bornShown: -Infinity, editShown: -Infinity });
    expect(quiet.activity[0]!.text).toBe('x.ts');
  });
});

describe('the replay', () => {
  it('plays rows and steps in their time, faster than the wall, skipping the quiet', () => {
    const t0 = Date.parse('2026-11-09T10:00:00Z');
    const rows = [row(1, 'instruction', 'DeveloperPriya', 'Manager', 'go', at(t0)), row(2, 'completed', 'DeveloperPriya', 'DeveloperPriya', null, at(t0 + 3_600_000))];
    const steps: StepRow[] = [{ ...step(1, 'edit', 'a.ts'), at: at(t0 + 30_000) }];
    const clock = new ReplayClock(rows, steps, t0 - 1000, 60, MaxGapMs);
    expect(clock.step(100).rows.map((r) => r.seq)).toEqual([1]); // 100 ms at 60x is 6 s of log time
    expect(clock.step(400).steps).toHaveLength(1); // 24 s more
    // An hour of quiet takes no longer than the gap: the next step lands at most MaxGapMs of log time later.
    const due = clock.step(MaxGapMs / 60 + 1);
    expect(due.rows.map((r) => r.seq)).toEqual([2]);
    expect(clock.drained).toBe(true);
    // It never runs past the moment it is told to stop at (now).
    const capped = new ReplayClock([], [], t0, 60);
    capped.step(10_000, t0 + 5);
    expect(capped.at).toBe(t0 + 5);
  });

  it('works out the members\' states from what it plays', () => {
    const s = new ReplayStates();
    const key = memberKey('payments', 'DeveloperPriya');
    const t0 = Date.parse('2026-11-09T10:00:00Z');
    expect(s.of(key, t0)).toBe('idle');
    s.row(row(1, 'instruction', 'DeveloperPriya', 'Manager', 'go', at(t0)));
    expect(s.of(key, t0)).toBe('waiting');
    s.step({ ...step(1, 'read', 'a.ts'), at: at(t0 + 1000) });
    expect(s.of(key, t0 + 2000)).toBe('running');
    expect(s.of(key, t0 + 1000 + ReplayRunningMs + 1)).toBe('idle');
    s.row(row(2, 'decision', 'DeveloperPriya', 'payments/DeveloperPriya', 'MIT?', at(t0 + 5000)));
    expect(s.of(key, t0 + 6000)).toBe('decision');
  });

  it('keeps the timeline sorted and trimmed', () => {
    const list = [row(1, 'progress', 'A', 'A', null, at(1000)), row(2, 'progress', 'A', 'A', null, at(3000))];
    mergeByTime(list, [row(3, 'progress', 'A', 'A', null, at(2000))]);
    expect(list.map((r) => r.seq)).toEqual([1, 3, 2]);
    trimByTime(list, 1500, 10);
    expect(list.map((r) => r.seq)).toEqual([3, 2]);
    trimByTime(list, 0, 1);
    expect(list.map((r) => r.seq)).toEqual([2]);
    expect(toneOfRow({ kind: 'instruction', from: 'person' })).toBe('person');
  });
});

describe('the Monitor model', () => {
  it('turns feed rows into effects and activity lines', () => {
    const m = new MonitorModel();
    m.apply(row(1, 'instruction', 'DeveloperPriya', 'Manager', 'Add a README'), 0);
    m.apply(row(2, 'decision', 'DeveloperPriya', 'payments/DeveloperPriya', 'MIT or Apache?'), 10);
    m.apply(row(3, 'done', 'Manager', 'Manager', 'README'), 20);
    // (A question also flies to the You node.)
    expect(m.effects.map((e) => e.kind)).toEqual(['comet', 'burst', 'float', 'you', 'burst']);
    expect(m.effects[0]).toMatchObject({ from: memberKey('payments', 'Manager'), to: memberKey('payments', 'DeveloperPriya'), label: 'Add a README' });
    expect(m.effects[4]).toMatchObject({ node: teamKey('payments'), big: true });
    expect(m.activity[0]!.verb).toBe('finished the work');
    expect(m.activity[2]).toMatchObject({ verb: 'Manager told', who: 'Developer Priya' });
    expect(m.perMinute(30)).toBe(3);
  });

  it('lists rows read at the start without playing them, and ripples a member that starts running', () => {
    const m = new MonitorModel();
    m.apply(row(1, 'failed', 'TesterWren', 'TesterWren', 'timed out'), 0, false);
    expect(m.effects).toHaveLength(0);
    expect(m.activity[0]!.verb).toBe('run failed');
    const team = (state: 'idle' | 'running') => [{ id: 1, name: 'payments', members: [{ id: 2, name: 'TesterWren', role: 'member' as const, cli: 'claude', state, queued: 0, statusLine: null }] }];
    m.reconcile(team('idle'), 0);
    m.reconcile(team('running'), 5);
    expect(m.effects).toEqual([{ kind: 'ripple', node: memberKey('payments', 'TesterWren'), color: 'start', at: 5 }]);
    expect(m.counts()).toMatchObject({ running: 1, members: 1 });
  });
});

describe('the workflows on the wall', () => {
  const wf = (id: number, lastAt: string, state: WorkflowSummary['state'] = 'open', busy: string[] = [], title = `Workflow ${id}`): WorkflowSummary =>
    ({ id, title, owner: null, state, undeclared: state === 'open' && !busy.length, busy, ending: null, startedAt: lastAt, lastAt, messages: 3 });

  it('keeps the last five by their last entry, newest first', () => {
    const list = [1, 2, 3, 4, 5, 6, 7].map((i) => wf(i, at(Date.parse('2026-11-11T10:00:00Z') + ((i * 5) % 7) * 60_000)));
    expect(latestWorkflows(list).map((w) => w.id)).toEqual([4, 1, 5, 2, 6]);
    expect(latestWorkflows(list, 2)).toHaveLength(2);
    expect(WorkflowsShown).toBe(5);
  });

  it('gives each state its colour, icon and words', () => {
    const t = at(Date.now());
    expect(workflowLook(wf(1, t, 'completed'))).toMatchObject({ status: 'completed', tone: 'done', icon: 'check' });
    expect(workflowLook(wf(1, t, 'open', ['DeveloperPriya is running']))).toMatchObject({ status: 'working', tone: 'start', icon: 'flow' });
    expect(workflowLook(wf(1, t, 'open'))).toMatchObject({ status: 'waiting', tone: 'handback', icon: 'pause' });
    expect(workflowLook(wf(1, t, 'closed'))).toMatchObject({ status: 'closed', tone: 'inkDim', icon: 'cross' });
    expect(WorkflowLooks.map((l) => l.status)).toEqual(['completed', 'working', 'waiting', 'closed', 'expired']);
    for (const l of WorkflowLooks) expect(l.label.length).toBeGreaterThan(0);
  });

  it('labels a node with its number and a short title, and the last entry compactly', () => {
    expect(workflowLabel(wf(12, at(0), 'open', [], 'Add a README'))).toBe('#12 Add a README');
    const long = workflowLabel(wf(7, at(0), 'open', [], 'Rewrite the payment reconciliation job end to end'));
    expect(long.startsWith('#7 Rewrite the payment')).toBe(true);
    expect(long.endsWith('…')).toBe(true);
    expect(long.length).toBe('#7 '.length + 28);
    const now = new Date(2026, 10, 11, 15, 0).getTime();
    expect(entryTime(new Date(2026, 10, 11, 12, 41).toISOString(), now)).toBe('12:41 PM');
    expect(entryTime(new Date(2026, 10, 11, 0, 5).toISOString(), now)).toBe('12:05 AM');
    expect(entryTime(new Date(2026, 10, 10, 12, 41).toISOString(), now)).toBe('Nov 10 12:41 PM');
    expect(entryTime(new Date(2026, 0, 3, 9, 7).toISOString(), now)).toBe('Jan 3 9:07 AM');
    expect(entryTime('not a time', now)).toBe('');
  });

  it('labels a workflow node "#id title", cut, with "Backlog item " dropped', () => {
    expect(workflowNodeLabel(wf(12, at(0), 'open', [], 'Add a README'))).toBe('#12 Add a README');
    const backlog = workflowNodeLabel(wf(3848, at(0), 'open', [], "Backlog item B081 Remote UI: xterm's link handler asks first"));
    expect(backlog).toBe("#3848 B081 Remote UI: xterm's…"); // the docs' example
    expect(backlog).not.toContain('Backlog item');
    expect(backlog.endsWith('…')).toBe(true);
    expect(backlog.length).toBe(NodeLabelMax);
    expect(NodeLabelMax).toBeGreaterThanOrEqual(28);
    expect(NodeLabelMax).toBeLessThanOrEqual(32);
    expect(workflowNodeLabel(wf(5, at(0), 'open', [], '  '))).toBe('#5');
    // A phone zoomed out: only "#id"; zoomed in, or a desktop window at any zoom (whatever its canvas), the title too.
    const t = wf(3848, at(0), 'open', [], 'Backlog item B081 Remote UI');
    expect(workflowNodeLabel(t, { phone: true, zoom: 0.4 })).toBe('#3848');
    expect(workflowNodeLabel(t, { phone: true, zoom: 1 })).toBe('#3848 B081 Remote UI');
    expect(workflowNodeLabel(t, { phone: false, zoom: 0.25 })).toBe('#3848 B081 Remote UI');
    expect(workflowNodeLabel(t, { zoom: 0.39 })).toBe('#3848 B081 Remote UI');
  });

  it('places workflow labels so that none covers another or a member', () => {
    const measure = (s: string) => s.length * 5;
    const h = 11;
    const ask = (key: string, x: number, y: number, rank: number, full = `${key} a long title here`) => ({ key, x, y, r: 10, full, short: key, rank });
    // Two nodes side by side, close: the first (by rank) keeps its place under the node; the second goes above.
    const two = placeWorkflowLabels([ask('#2', 30, 0, 2), ask('#1', 0, 0, 1)], measure, h);
    expect(two.get('#1')).toMatchObject({ text: '#1 a long title here', above: false });
    expect(two.get('#2')).toMatchObject({ text: '#2 a long title here', above: true });
    // A third, between them: no room above or below for the full label, so the short one, or nothing.
    const three = placeWorkflowLabels([ask('#1', 0, 0, 1), ask('#2', 30, 0, 2), ask('#3', 15, 0, 3)], measure, h);
    expect(three.get('#3')?.text ?? null).not.toBe('#3 a long title here');
    // Far apart: every label under its node, in full.
    const apart = placeWorkflowLabels([ask('#1', 0, 0, 1), ask('#2', 500, 0, 2)], measure, h);
    for (const l of apart.values()) expect(l).toMatchObject({ above: false });
    // A member's name below the node: the label goes above it.
    const name = placeWorkflowLabels([ask('#1', 0, 0, 1)], measure, h, [{ x: -50, y: 12, w: 100, h: 13 }]);
    expect(name.get('#1')).toMatchObject({ above: true });
    // Whatever the layout, no two labels placed overlap.
    let seed = 3;
    const rnd = () => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648;
    const many = Array.from({ length: 30 }, (_, i) => ask(`#${i}`, rnd() * 300, rnd() * 200, i));
    const placed = [...placeWorkflowLabels(many, measure, h).values()].filter((l) => l !== null);
    expect(placed.length).toBeGreaterThan(10);
    for (const [i, a] of placed.entries()) for (const b of placed.slice(i + 1))
      expect(a.x < b.x + b.w - 1 && b.x < a.x + a.w - 1 && a.y < b.y + b.h - 1 && b.y < a.y + a.h - 1).toBe(false);
  });

  it('draws a node\'s label never below 8 px on screen', () => {
    for (const k of [2, 1, 0.6, 0.3, 0.2]) expect(nodeLabelFont(k) * k).toBeGreaterThanOrEqual(8 - 1e-9);
    expect(nodeLabelFont(1)).toBe(9);
    expect(nodeLabelFont(0)).toBe(9);
  });
  it('the model keeps each team\'s newest five and says when they change', () => {
    const m = new MonitorModel();
    const list = [1, 2, 3, 4, 5, 6].map((i) => wf(i, at(1_000_000 + i * 1000)));
    m.setWorkflows('Payments', list);
    expect(m.workflows.get(teamKey('payments'))!.map((w) => w.id)).toEqual([6, 5, 4, 3, 2]);
    const rev = m.workflowsRev;
    m.setWorkflows('payments', [...list].reverse());
    expect(m.workflowsRev).toBe(rev); // nothing changed
    m.setWorkflows('payments', [wf(9, at(2_000_000), 'completed')]);
    expect(m.workflowsRev).toBe(rev + 1);
    // A team that is gone takes its workflows with it.
    m.reconcile([{ id: 2, name: 'search', members: [] }], 0);
    expect(m.workflows.has(teamKey('payments'))).toBe(false);
  });
});

describe('the Monitor view', () => {
  it('shows the counts and the activity from the core', async () => {
    const api = {
      call: async (method: string) => {
        if (method === 'app.overview') return { slots: { running: 0, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 }] };
        if (method === 'teams.agents') return [{ member: { id: 2, teamId: 1, team: 'payments', name: 'Manager', role: 'manager', cli: 'claude' }, state: 'idle', attention: 'decision', queuedInstructions: 0, statusLine: null }];
        if (method === 'monitor.messages') return { end: 5, messages: [row(5, 'decision', 'Manager', 'Manager', 'Ship it?')] };
        if (method === 'monitor.steps') return { end: 1, pending: false, steps: [step(1, 'edit', 'src/a.ts', -5_000)] };
        throw new Error(method);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    for (let i = 0; i < 5; i++) await flushPromises();
    expect(w.get('[data-test=monitor-counts]').text()).toContain('1 need you');
    expect(w.get('[data-test=monitor-row]').text()).toContain('needs you');
    // The step read at the start joins the activity list as a file line, and the timeline is there.
    expect(w.findAll('[data-test=monitor-row]').map((r) => r.text()).join('|')).toContain('Ship it?');
    expect(w.findAll('[data-test=monitor-row]').map((r) => r.text()).join('|')).toContain('a.ts');
    expect(w.find('[data-test=monitor-timeline]').exists()).toBe(true);
    // Each row's face wears its member's ring as on the wall: the Manager needs you; the step's DeveloperPriya is not on the team, so idle.
    expect(w.findAll('[data-test=monitor-row] [data-test=member-avatar]').map((a) => a.attributes('data-state'))).toEqual(['decision', 'idle']);
    w.unmount();
  });
  it('an activity row\'s face follows its member\'s state: running turns, idle when the member is unknown', async () => {
    let state = 'running';
    const api = {
      call: async (method: string) => {
        if (method === 'app.overview') return { slots: { running: 1, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 1, running: 1, queued: 0 }] };
        if (method === 'teams.agents') return [{ member: { id: 3, teamId: 1, team: 'payments', name: 'DeveloperPriya', role: 'member', cli: 'claude' }, state, attention: null, queuedInstructions: 0, statusLine: null }];
        if (method === 'monitor.messages') return { end: 6, messages: [row(5, 'instruction', 'DeveloperPriya', 'Manager', 'go'), row(6, 'progress', 'Gone', 'Gone', 'hi')] };
        if (method === 'monitor.steps') return { end: 0, pending: false, steps: [] };
        throw new Error(method);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    for (let i = 0; i < 5; i++) await flushPromises();
    const faces = () => Object.fromEntries(w.findAll('[data-test=monitor-row]').map((r) => [r.text().includes('Gone') ? 'gone' : 'priya', r.find('[data-test=member-avatar]').attributes('data-state')]));
    expect(faces()).toEqual({ priya: 'running', gone: 'idle' });
    w.unmount();
  });
  it('shows each team\'s five newest workflows live, opens the team\'s workflows from one, and hides them in a replay', async () => {
    const base = Date.now() - 3_600_000;
    const wfs: WorkflowSummary[] = [1, 2, 3, 4, 5, 6, 7].map((i) => ({
      id: i, title: `Job ${i}`, owner: null, state: i === 7 ? 'completed' : 'open', undeclared: i !== 6, busy: i === 6 ? ['DeveloperPriya is running'] : [],
      ending: null, startedAt: at(base), lastAt: at(base + i * 60_000), messages: i,
    }));
    const asked: Record<string, unknown>[] = [];
    const api = {
      call: async (method: string, params: Record<string, unknown> = {}) => {
        if (method === 'app.overview') return { slots: { running: 0, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 }] };
        if (method === 'teams.agents') return [{ member: { id: 2, teamId: 1, team: 'payments', name: 'Manager', role: 'manager', cli: 'claude' }, state: 'idle', attention: null, queuedInstructions: 0, statusLine: null }];
        if (method === 'monitor.messages') return { end: 5, messages: [row(5, 'progress', 'Manager', 'Manager', 'on it', at(Date.now() - 120_000))] };
        if (method === 'monitor.steps') return { end: 0, pending: false, steps: [] };
        if (method === 'bus.workflows') { asked.push(params); return [...wfs].reverse(); }
        throw new Error(method);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(asked[0]).toMatchObject({ teamId: 1, limit: 5 });
    const items = w.findAll('[data-test=monitor-workflow]');
    expect(items.map((x) => x.attributes('data-id'))).toEqual(['7', '6', '5', '4', '3']);
    expect(items[0]!.text()).toContain('#7 Job 7');
    expect(items[0]!.text()).toContain('Completed');
    expect(items[1]!.text()).toContain('Being worked');
    expect(items[2]!.text()).toContain('Waiting');
    // The legend starts collapsed (only its toggle shows); opened, it names the four states.
    expect(w.find('[data-test=monitor-legend]').exists()).toBe(false);
    await w.get('[data-test=monitor-legend-toggle]').trigger('click');
    expect(w.get('[data-test=monitor-legend-toggle]').attributes('aria-expanded')).toBe('true');
    const legend = w.get('[data-test=monitor-legend-workflows]').text();
    for (const words of ['completed', 'being worked', 'waiting', 'closed']) expect(legend.toLowerCase()).toContain(words);
    // Opening one asks for that workflow (the app shows the Backlog item it came from, or the team's workflows).
    await items[0]!.trigger('click');
    expect(w.emitted('openWorkflow')?.[0]).toEqual([1, Number(items[0]!.attributes('data-id'))]);
    // One panel per team: its header opens the team's workflows too.
    const headers = w.findAll('[data-test=monitor-workflows-team]');
    expect(headers).toHaveLength(1);
    expect(headers[0]!.text()).toContain('payments');
    await headers[0]!.trigger('click');
    expect(w.emitted('openTeam')?.[0]).toEqual([1, 'workflows']);
    // A replay shows no workflows (the wall would be faking their history).
    w.findComponent(Timeline).vm.$emit('seek', Date.now() - 60_000);
    await flushPromises();
    expect(w.find('[data-test=monitor-replaying]').exists()).toBe(true);
    expect(w.findAll('[data-test=monitor-workflow]')).toHaveLength(0);
    expect(w.find('[data-test=monitor-legend-workflows]').exists()).toBe(false);
    expect(w.find('[data-test=monitor-legend]').exists()).toBe(true); // still open in a replay, without the workflows part
    await w.get('[data-test=monitor-live]').trigger('click');
    expect(w.findAll('[data-test=monitor-workflow]')).toHaveLength(5);
    // The choice is remembered: a new Monitor opens with the legend shown; closing it is remembered too.
    w.unmount();
    const again = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(again.find('[data-test=monitor-legend]').exists()).toBe(true);
    await again.get('[data-test=monitor-legend-toggle]').trigger('click');
    expect(again.find('[data-test=monitor-legend]').exists()).toBe(false);
    expect(localStorage.getItem('yawble.monitor.legend')).toBe('collapsed');
    again.unmount();
    localStorage.removeItem('yawble.monitor.legend');
  });
  it('the camera control: Auto fit or Pinned, lit and remembered; Fit fits once and pins', async () => {
    localStorage.removeItem('yawble.monitor.camera');
    const api = {
      call: async (method: string) => {
        if (method === 'app.overview') return { slots: { running: 0, limit: 4, queued: 0 }, teams: [] };
        if (method === 'teams.agents') return [];
        if (method === 'monitor.messages') return { end: 0, messages: [] };
        if (method === 'monitor.steps') return { end: 0, pending: false, steps: [] };
        throw new Error(method);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    const lit = () => ['monitor-autofit', 'monitor-pin'].filter((t) => w.get(`[data-test=${t}]`).classes().includes('on'));
    expect(lit()).toEqual(['monitor-autofit']); // Auto fit, as before
    await w.get('[data-test=monitor-fit]').trigger('click');
    expect(lit()).toEqual(['monitor-pin']);
    expect(localStorage.getItem('yawble.monitor.camera')).toBe('pinned');
    w.unmount();

    const again = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(again.get('[data-test=monitor-pin]').attributes('aria-pressed')).toBe('true'); // kept for next time
    await again.get('[data-test=monitor-autofit]').trigger('click');
    expect(again.get('[data-test=monitor-autofit]').attributes('aria-pressed')).toBe('true');
    expect(localStorage.getItem('yawble.monitor.camera')).toBe('auto');
    again.unmount();
    localStorage.removeItem('yawble.monitor.camera');
  });
  it('the cog beside Pinned opens the network forces; a slider changes and remembers its multiplier; Reset clears it', async () => {
    localStorage.removeItem('yawble.monitor.layout');
    const api = {
      call: async (method: string) => {
        if (method === 'app.overview') return { slots: { running: 0, limit: 4, queued: 0 }, teams: [] };
        if (method === 'teams.agents') return [];
        if (method === 'monitor.messages') return { end: 0, messages: [] };
        if (method === 'monitor.steps') return { end: 0, pending: false, steps: [] };
        throw new Error(method);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    // The cog sits in the camera controls, right after the Auto fit | Fit | Pinned group, then the sound
    // (the density moved into the cog's settings panel); the panel is closed.
    const controls = w.get('.controls');
    const order = Array.from(controls.element.children).map((el) => el.getAttribute('data-test')).filter(Boolean);
    expect(order.slice(-2)).toEqual(['monitor-layout-toggle', 'monitor-sound']);
    expect(controls.find('[data-test=monitor-density]').exists()).toBe(false);
    expect(w.find('[data-test=monitor-layout]').exists()).toBe(false);
    await w.get('[data-test=monitor-layout-toggle]').trigger('click');
    const panel = w.get('[data-test=monitor-layout]');
    for (const key of ['distance', 'strength', 'repulsion', 'gravity', 'friction', 'spacing']) expect(panel.find(`[data-test=monitor-layout-${key}]`).exists()).toBe(true);
    expect(panel.text()).toContain('Team spacing');
    expect(panel.text()).toContain('How far apart the teams sit.');
    expect(w.get('[data-test=monitor-layout-reset]').attributes('disabled')).toBeDefined();

    const repulsion = w.get('[data-test=monitor-layout-repulsion]');
    (repulsion.element as HTMLInputElement).value = '1.8';
    await repulsion.trigger('input');
    expect(JSON.parse(localStorage.getItem('yawble.monitor.layout')!)).toMatchObject({ repulsion: 1.8, distance: 0.3 });
    expect(w.get('[data-test=monitor-layout-toggle]').classes()).toContain('changed');
    w.unmount();

    const again = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    await again.get('[data-test=monitor-layout-toggle]').trigger('click');
    expect((again.get('[data-test=monitor-layout-repulsion]').element as HTMLInputElement).value).toBe('1.8'); // kept for next time
    // Team spacing is kept beside the other five, clamped the same way.
    const spacing = again.get('[data-test=monitor-layout-spacing]');
    (spacing.element as HTMLInputElement).value = '9';
    await spacing.trigger('input');
    expect(JSON.parse(localStorage.getItem('yawble.monitor.layout')!)).toMatchObject({ repulsion: 1.8, spacing: 3 });
    await again.get('[data-test=monitor-layout-reset]').trigger('click');
    expect(localStorage.getItem('yawble.monitor.layout')).toBeNull();
    expect((again.get('[data-test=monitor-layout-spacing]').element as HTMLInputElement).value).toBe(String(LayoutDefaults.spacing));
    await again.get('[data-test=monitor-layout]').trigger('keydown', { key: 'Escape' });
    expect(again.find('[data-test=monitor-layout]').exists()).toBe(false);
    again.unmount();
  });
  it('collapses the activity pane to a thin rail and opens it again, and remembers that', async () => {
    localStorage.removeItem('yawble.monitor.activity');
    const api = {
      call: async (method: string) => {
        if (method === 'app.overview') return { slots: { running: 0, limit: 4, queued: 0 }, teams: [] };
        if (method === 'teams.agents') return [];
        if (method === 'monitor.messages') return { end: 0, messages: [] };
        if (method === 'monitor.steps') return { end: 0, pending: false, steps: [] };
        throw new Error(method);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    for (let i = 0; i < 3; i++) await flushPromises();
    expect(w.get('[data-test=monitor-activity]').classes()).not.toContain('collapsed');
    await w.get('[data-test=monitor-activity-toggle]').trigger('click');
    expect(w.get('[data-test=monitor-activity]').classes()).toContain('collapsed');
    expect(w.find('[data-test=monitor-row]').exists()).toBe(false);
    expect(w.get('[data-test=monitor-activity-toggle]').attributes('aria-expanded')).toBe('false');
    w.unmount();

    const again = mount(MonitorView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(again.get('[data-test=monitor-activity]').classes()).toContain('collapsed'); // kept for next time
    await again.get('[data-test=monitor-activity-toggle]').trigger('click');
    expect(again.get('[data-test=monitor-activity]').classes()).not.toContain('collapsed');
    again.unmount();
  });
});

describe('Team spacing', () => {
  it('is a sixth layout setting, clamped, stored and reset like the other five', () => {
    expect(LayoutFields.map((f) => f.key)).toEqual(['distance', 'strength', 'repulsion', 'gravity', 'friction', 'spacing']);
    expect(LayoutFields.at(-1)).toEqual({ key: 'spacing', label: 'Team spacing', help: 'How far apart the teams sit.' });
    expect(LayoutDefaults.spacing).toBe(1);
    expect([clampLayout(0.1), clampLayout(9), clampLayout('1.234'), clampLayout('x', LayoutDefaults.spacing)]).toEqual([LayoutMin, LayoutMax, 1.25, 1]);
    const store = new Map<string, string>();
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v), removeItem: (k: string) => void store.delete(k) };
    writeLayout({ ...LayoutDefaults, spacing: 0.75 }, storage);
    expect(JSON.parse(store.get('yawble.monitor.layout')!)).toMatchObject({ spacing: 0.75, distance: LayoutDefaults.distance });
    expect(readLayout(storage)).toEqual({ ...LayoutDefaults, spacing: 0.75 });
    store.set('yawble.monitor.layout', JSON.stringify({ spacing: 7 }));
    expect(readLayout(storage).spacing).toBe(LayoutMax);
    store.set('yawble.monitor.layout', JSON.stringify({ spacing: 'far', gravity: 1 }));
    expect(readLayout(storage)).toEqual({ ...LayoutDefaults, gravity: 1 }); // a bad one is the default; an old store without it too
    expect(isDefaultLayout({ ...LayoutDefaults, spacing: 1.05 })).toBe(false);
    writeLayout({ ...LayoutDefaults }, storage); // Reset: nothing stored
    expect(store.has('yawble.monitor.layout')).toBe(false);
  });

  it('scales the team homes; the oval puts the two nearest clusters apart by the room their maps need', () => {
    for (const count of [2, 3, 4]) for (let i = 0; i < count; i++) {
      const one = teamHome(i, count, 170, 1), two = teamHome(i, count, 170, 2);
      expect(two.x).toBeCloseTo(one.x * 2);
      expect(two.y).toBeCloseTo(one.y * 2);
    }
    expect(teamHome(0, 1, 170, 3)).toEqual({ x: 0, y: 0 }); // a team alone sits in the middle
    const hubs = (count: number, room: number) => Array.from({ length: count }, (_, i) => teamHome(i, count, room, 1));
    const nearest = (ps: { x: number; y: number }[]) => Math.min(...ps.flatMap((p, a) => ps.slice(a + 1).map((q) => Math.hypot(p.x - q.x, p.y - q.y))));
    for (const count of [2, 3, 4]) for (const room of [0, 60, 170])
      expect(nearest(hubs(count, room))).toBeCloseTo(Math.max(2 * (ClusterCore + room) + ClusterGap, 140 * nearestPair(count))); // the oval's x radius is never under 140
    expect(nearestPair(2)).toBeCloseTo(2);
    expect(nearestPair(3)).toBeLessThan(nearestPair(2)); // three on the flat oval: the two on the right sit nearest
    // No map, no room. A map's arc points away from the others: only what wraps back toward them counts. A
    // narrow map (two folders) and a half-round one (six) stay behind the members; a wide one (eleven) wraps round past them.
    const m = new MonitorModel();
    m.logNow = () => Date.now();
    const files = (n: number, folders: number) => { for (let i = 0; i < n; i++) m.applyStep({ id: 1000 + i, at: new Date().toISOString(), team: `t${folders}`, member: 'Dev', run: 1, kind: 'edit', tool: 'Edit', target: `src/d${i % folders}/f${i}.ts` }, 0, true); };
    files(40, 2); files(24, 6); files(120, 11);
    const room = (t: string, spread = 0) => mapRoomOf(m.map.view(t, DefaultLimits), ClusterCore, spread);
    expect(mapRoomOf(collapse('none', [], DefaultLimits))).toBe(0);
    expect(room('t2')).toBe(0);
    expect(room('t6')).toBe(0); // was about 170: depth × sin(89°), the arc's sideways spread
    expect(room('t11')).toBeGreaterThan(0);
    expect(room('t11')).toBeLessThan(170);
    // Neighbours off to the side (three teams) see more of a map than one straight across.
    expect(room('t6', neighbourSpread(3))).toBeGreaterThanOrEqual(room('t6'));
    expect(neighbourSpread(2)).toBe(0);
    expect(neighbourSpread(3)).toBeGreaterThan(0);
    expect(neighbourSpread(3)).toBeLessThan(Math.PI / 2);
    const views = ['t2', 't6', 't11'].map((t) => m.map.view(t, DefaultLimits));
    expect(mapRoomFor(views)).toBe(Math.ceil(room('t11') / 10) * 10);
    expect(mapRoomFor([])).toBe(0);
  });

  it('a map is measured toward the neighbours, not across: its nodes past the core reach no further than the room', () => {
    const m = new MonitorModel();
    m.logNow = () => Date.now();
    for (const folders of [1, 3, 6, 9, 11]) for (let i = 0; i < folders * 6; i++)
      m.applyStep({ id: 5000 + folders * 100 + i, at: new Date().toISOString(), team: `f${folders}`, member: 'Dev', run: 1, kind: 'edit', tool: 'Edit', target: `src/d${i % folders}/f${i}.ts` }, 0, true);
    for (const folders of [1, 3, 6, 9, 11]) {
      const v = m.map.view(`f${folders}`, DefaultLimits);
      const laid = layoutTeamMap(v, { x: 0, y: 0 }, 0, MapMembersReach + MapPastMembers);
      // The neighbour is straight back (x < 0): the furthest a node with its label reaches that way is core + room.
      const nodes = [...laid.folders, ...laid.files, ...laid.folders.map((f) => ({ x: f.moreX })), ...(laid.rest ? [laid.rest] : [])];
      const back = Math.max(0, ...nodes.map((p) => -p.x + 40));
      expect(back).toBeLessThanOrEqual(ClusterCore + mapRoomOf(v, ClusterCore, 0) + 1e-6);
      expect(mapRoomOf(v, ClusterCore, 0)).toBeCloseTo(Math.max(0, back - ClusterCore), 6);
    }
  });

  /** A scene on a stub canvas with count teams of four, each with a wide code map and five open workflow nodes, settled. */
  const scenes: MonitorScene[] = [];
  afterEach(() => { for (const s of scenes.splice(0)) s.destroy(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
  /** stagger: frames run between each team's map and each team's workflows arriving (as a live wall gets them). */
  function settledWall(count: number, spacing = LayoutDefaults.spacing, frames = 700, opts: { seed?: number; stagger?: number } = {}) {
    // The test runs the frames itself: the scene's own requestAnimationFrame loop must not run too.
    vi.stubGlobal('requestAnimationFrame', () => 0); vi.stubGlobal('cancelAnimationFrame', () => {});
    let seed = opts.seed ?? 7;
    const stagger = opts.stagger ?? 0;
    vi.spyOn(Math, 'random').mockImplementation(() => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
    const ctx = new Proxy({} as Record<string, unknown>, {
      get: (t, k: string) => (k in t ? t[k] : k === 'measureText' ? (s: string) => ({ width: String(s).length * 6 }) : () => ({ addColorStop() {} })),
      set: (t, k: string, v) => { t[k] = v; return true; },
    });
    const canvas = document.createElement('canvas');
    Object.defineProperty(canvas, 'getContext', { value: () => ctx });
    Object.defineProperty(canvas, 'clientWidth', { value: 1400 });
    Object.defineProperty(canvas, 'clientHeight', { value: 860 });
    const scene = new MonitorScene(canvas);
    scenes.push(scene);
    const names = ['Alpha', 'Beta', 'Gamma'].slice(0, count);
    const teams: TeamSnapshot[] = names.map((name, i) => ({ id: i + 1, name, members: ['Manager', 'DeveloperTomas', 'TesterKofi', 'DesignerAmy'].map((n, j) =>
      ({ id: j + 1, name: n, role: j ? 'member' as const : 'manager' as const, state: 'idle' as const, queued: 0, statusLine: null, cli: 'claude' })) }));
    const m = new MonitorModel();
    m.reconcile(teams, 0);
    const t0 = Date.parse('2026-11-17T10:00:00Z');
    m.logNow = () => t0 + 60_000;
    const wfs: WorkflowSummary[] = [1, 2, 3, 4, 5].map((id) => ({ id, title: `Workflow ${id}`, owner: null, state: 'open', undeclared: false, busy: [], ending: null, startedAt: at(t0), lastAt: at(t0 + id), messages: 2 }));
    const frame = (scene as unknown as { frame: (t: number) => void }).frame;
    let f = 0;
    const run = (n: number) => { for (let i = 0; i < n; i++) frame(1000 + ++f * 16); };
    const maps = () => {
      let id = 0;
      for (const name of names) {
        if (stagger) run(stagger);
        for (let i = 0; i < 120; i++) m.applyStep({ id: ++id, at: at(t0 + i), team: name, member: i % 2 ? 'DeveloperTomas' : 'TesterKofi', run: 1, kind: i % 2 ? 'edit' : 'read', tool: 'Edit', target: `src/d${i % 11}/f${i}.ts` }, 1000, true);
      }
    };
    const flows = () => { for (const name of names) { if (stagger) run(stagger); m.setWorkflows(name, wfs, 0); } };
    if (!stagger) { maps(); flows(); }
    scene.model = m;
    scene.setTeams(teams);
    if (stagger) { maps(); flows(); }
    if (spacing !== LayoutDefaults.spacing) scene.setLayout({ ...LayoutDefaults, spacing });
    run(frames);
    return scene;
  }
  const gaps = (scene: MonitorScene) => {
    const cl = scene.clusters();
    return cl.flatMap((a, i) => cl.slice(i + 1).map((b) => Math.max(b.box.x0 - a.box.x1, a.box.x0 - b.box.x1, b.box.y0 - a.box.y1, a.box.y0 - b.box.y1)));
  };
  const homeDistances = (scene: MonitorScene) => {
    const hs = scene.clusters().map((c) => c.home);
    return hs.flatMap((p, i) => hs.slice(i + 1).map((q) => Math.round(Math.hypot(p.x - q.x, p.y - q.y))));
  };

  /** The least distance between two teams' drawn things (each part's box), for every pair of teams: as the team-spacing e2e spec measures it. */
  const partGaps = (scene: MonitorScene) => {
    const cl = scene.clusters();
    const apart = (a: { x0: number; y0: number; x1: number; y1: number }, b: typeof a) => {
      const dx = Math.max(0, b.x0 - a.x1, a.x0 - b.x1), dy = Math.max(0, b.y0 - a.y1, a.y0 - b.y1);
      return dx || dy ? Math.hypot(dx, dy) : -1; // -1: they overlap
    };
    return cl.flatMap((a, i) => cl.slice(i + 1).map((b) => Math.min(...a.parts.flatMap((p) => b.parts.map((q) => apart(p, q))))));
  };

  it('at the default, two teams\' clusters (members, workflow nodes, code map) sit close but do not overlap', () => {
    const scene = settledWall(2);
    const cl = scene.clusters();
    expect(cl).toHaveLength(2);
    for (const c of cl) expect(c.box.x1 - c.box.x0).toBeGreaterThan(300); // the map is in the boxes
    // The oval is measured from the clusters drawn (settleSpacing), so the homes are what puts them ClusterGap
    // apart: 440 by the estimate, 580 and 816 before.
    expect(homeDistances(scene)[0]).toBeLessThan(440);
    for (const g of gaps(scene)) expect(g).toBeLessThan(300);
    for (const g of scene.drawnGaps()) { expect(g).toBeGreaterThan(ClusterGap - 12); expect(g).toBeLessThan(ClusterGap + 12); }
    for (const c of cl) expect(Math.hypot(c.hub.x - c.home.x, c.hub.y - c.home.y)).toBeLessThan(1); // the hubs are held at their homes
  });

  it('at the default, three teams\' clusters sit close and do not overlap either', () => {
    const scene = settledWall(3);
    expect(Math.max(...homeDistances(scene))).toBeLessThan(771); // were 771, 771, 520, 860, 860, 580 before
    const g = scene.drawnGaps();
    for (const d of g) expect(d).toBeGreaterThan(0);
    expect(Math.min(...g)).toBeGreaterThan(ClusterGap - 12); // the nearest two: ClusterGap apart (154 when first measured)
    expect(Math.min(...g)).toBeLessThan(ClusterGap + 12);
  });

  it('settles to the same gap every run, whatever the birth spots and the order things arrive in', () => {
    // Before the fix, 3 teams' nearest gap measured at 4-248 and 2 teams' at -23 to 157 from run to run.
    for (const count of [2, 3]) {
      const nearest: number[] = [];
      for (const seed of [1, 42, 555]) for (const stagger of [0, 37]) {
        const scene = settledWall(count, LayoutDefaults.spacing, 1200, { seed, stagger });
        nearest.push(Math.min(...scene.drawnGaps()));
        for (const c of scene.clusters()) expect(Math.hypot(c.hub.x - c.home.x, c.hub.y - c.home.y)).toBeLessThan(1);
        scene.destroy();
      }
      expect(Math.min(...nearest), `${count} teams: ${nearest.map(Math.round)}`).toBeGreaterThan(ClusterGap - 15);
      expect(Math.max(...nearest) - Math.min(...nearest), `${count} teams: ${nearest.map(Math.round)}`).toBeLessThan(25);
    }
  }, 60_000);

  it('a workflow label keeps off its own hub\'s name pill and its own team\'s map', () => {
    for (const count of [2, 3]) {
      const scene = settledWall(count);
      const s = scene as unknown as { nodes: Map<string, { kind: string; team: string }>; mapNodes: { x: number; y: number; kind: string; team: { team: string } }[]; pillBox: (n: unknown) => { x: number; y: number; w: number; h: number } };
      const pills = [...s.nodes.values()].filter((n) => n.kind === 'team').map((n) => s.pillBox(n));
      const marks = s.mapNodes.map((m) => (m.kind === 'folder' ? { x: m.x - 6, y: m.y - 5, w: 12, h: 10 } : { x: m.x - 3.6, y: m.y - 3.6, w: 7.2, h: 7.2 }));
      const hit = (a: { x: number; y: number; w: number; h: number }, b: typeof a) => a.x < b.x + b.w - 1 && b.x < a.x + a.w - 1 && a.y < b.y + b.h - 1 && b.y < a.y + a.h - 1;
      const boxes = scene.workflowNodes().flatMap((n) => (n.box ? [n.box] : []));
      expect(boxes.length).toBeGreaterThan(0);
      for (const b of boxes) {
        for (const p of pills) expect(hit(b, p)).toBe(false);
        for (const m of marks) expect(hit(b, m)).toBe(false);
      }
    }
  });

  it('the workflow labels on a settled wall do not overlap, and most show their title', () => {
    for (const count of [2, 3]) {
      const scene = settledWall(count);
      const nodes = scene.workflowNodes();
      expect(nodes).toHaveLength(count * 5);
      const boxes = nodes.flatMap((n) => (n.box ? [n.box] : []));
      for (const [i, a] of boxes.entries()) for (const b of boxes.slice(i + 1))
        expect(a.x < b.x + b.w - 1 && b.x < a.x + a.w - 1 && a.y < b.y + b.h - 1 && b.y < a.y + a.h - 1).toBe(false);
      expect(nodes.filter((n) => n.label.startsWith(`#${n.id} Workflow`)).length).toBeGreaterThanOrEqual(nodes.length / 2);
    }
  });

  it('changing it moves the team homes live, and the hubs follow', () => {
    const scene = settledWall(2, LayoutDefaults.spacing, 400);
    const before = scene.clusters();
    scene.setLayout({ ...LayoutDefaults, spacing: 2 });
    const after = scene.clusters();
    for (let i = 0; i < 2; i++) expect(after[i]!.home.x).toBeCloseTo(before[i]!.home.x * 2);
    const frame = (scene as unknown as { frame: (t: number) => void }).frame;
    for (let i = 1; i <= 500; i++) frame(20_000 + i * 16);
    const moved = scene.clusters();
    expect(Math.abs(moved[0]!.hub.x - moved[1]!.hub.x)).toBeGreaterThan(Math.abs(before[0]!.hub.x - before[1]!.hub.x) * 1.6);
  });
});
