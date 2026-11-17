// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { memberKey, MonitorModel, teamKey, type FeedRow } from '../../src/renderer/src/monitor/model';
import MonitorView from '../../src/renderer/src/monitor/MonitorView.vue';
import Timeline from '../../src/renderer/src/monitor/Timeline.vue';
import { aimsAtEverything, ClusterCore, ClusterGap, FitOnceMs, mapRoomFor, MonitorScene, nearestPair, teamHome } from '../../src/renderer/src/monitor/scene';
import { clampLayout, isDefaultLayout, LayoutDefaults, LayoutFields, LayoutMax, LayoutMin, readLayout, writeLayout } from '../../src/renderer/src/monitor/layout';
import { collapse, DefaultLimits, mapRoomOf } from '../../src/renderer/src/monitor/codemap';
import type { TeamSnapshot } from '../../src/renderer/src/monitor/model';
import type { YawbleApi, WorkflowSummary } from '../../src/shared/api';
import { entryTime, latestWorkflows, nodeLabelFont, NodeLabelMax, workflowLabel, workflowLook, WorkflowLooks, workflowNodeLabel, WorkflowsShown } from '../../src/renderer/src/monitor/workflows';
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
    // (B080: a question also flies to the You node.)
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
    expect(workflowLook(wf(1, t, 'open', ['DeveloperPriya is running']))).toMatchObject({ status: 'working', tone: 'start', icon: 'play' });
    expect(workflowLook(wf(1, t, 'open'))).toMatchObject({ status: 'waiting', tone: 'handback', icon: 'pause' });
    expect(workflowLook(wf(1, t, 'closed'))).toMatchObject({ status: 'closed', tone: 'inkDim', icon: 'cross' });
    expect(WorkflowLooks.map((l) => l.status)).toEqual(['completed', 'working', 'waiting', 'closed']);
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

  it('labels a workflow node "#id title", cut, with "Backlog item " dropped (B090)', () => {
    expect(workflowNodeLabel(wf(12, at(0), 'open', [], 'Add a README'))).toBe('#12 Add a README');
    const backlog = workflowNodeLabel(wf(3848, at(0), 'open', [], "Backlog item B081 Remote UI: xterm's link handler asks first"));
    expect(backlog.startsWith("#3848 B081 Remote UI: xterm")).toBe(true);
    expect(backlog).not.toContain('Backlog item');
    expect(backlog.endsWith('…')).toBe(true);
    expect(backlog.length).toBe(NodeLabelMax);
    expect(NodeLabelMax).toBeGreaterThanOrEqual(28);
    expect(NodeLabelMax).toBeLessThanOrEqual(32);
    expect(workflowNodeLabel(wf(5, at(0), 'open', [], '  '))).toBe('#5');
    // A phone's wall zoomed out: only "#id"; zoomed in, or a wide wall at any zoom, the title too.
    const t = wf(3848, at(0), 'open', [], 'Backlog item B081 Remote UI');
    expect(workflowNodeLabel(t, { wallPx: 375, zoom: 0.4 })).toBe('#3848');
    expect(workflowNodeLabel(t, { wallPx: 375, zoom: 1 })).toBe('#3848 B081 Remote UI');
    expect(workflowNodeLabel(t, { wallPx: 1400, zoom: 0.25 })).toBe('#3848 B081 Remote UI');
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
    // The cog sits in the camera controls, right after the Auto fit | Fit | Pinned group, then density and
    // sound (card 374); the panel is closed.
    const controls = w.get('.controls');
    const order = Array.from(controls.element.children).map((el) => el.getAttribute('data-test')).filter(Boolean);
    expect(order.slice(-3)).toEqual(['monitor-layout-toggle', 'monitor-density', 'monitor-sound']);
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

describe('Team spacing (B089)', () => {
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
    // No map, no room; a wide map needs its full depth; a narrow one (two folders) only part of it.
    const m = new MonitorModel();
    m.logNow = () => Date.now();
    const files = (n: number, folders: number) => { for (let i = 0; i < n; i++) m.applyStep({ id: 1000 + i, at: new Date().toISOString(), team: `t${folders}`, member: 'Dev', run: 1, kind: 'edit', tool: 'Edit', target: `src/d${i % folders}/f${i}.ts` }, 0, true); };
    files(40, 2); files(120, 11);
    const narrow = mapRoomOf(m.map.view('t2', DefaultLimits)), wide = mapRoomOf(m.map.view('t11', DefaultLimits));
    expect(mapRoomOf(collapse('none', [], DefaultLimits))).toBe(0);
    expect(narrow).toBeGreaterThan(0);
    expect(wide).toBeGreaterThan(narrow * 2);
    expect(mapRoomFor([m.map.view('t2', DefaultLimits), m.map.view('t11', DefaultLimits)])).toBe(Math.ceil(wide / 10) * 10);
    expect(mapRoomFor([])).toBe(0);
  });

  /** A scene on a stub canvas with count teams of four, each with a wide code map and five open workflow nodes, settled. */
  const scenes: MonitorScene[] = [];
  afterEach(() => { for (const s of scenes.splice(0)) s.destroy(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
  function settledWall(count: number, spacing = LayoutDefaults.spacing, frames = 700) {
    // The test runs the frames itself: the scene's own requestAnimationFrame loop must not run too.
    vi.stubGlobal('requestAnimationFrame', () => 0); vi.stubGlobal('cancelAnimationFrame', () => {});
    let seed = 7;
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
    let id = 0;
    for (const name of names) {
      for (let i = 0; i < 120; i++) m.applyStep({ id: ++id, at: at(t0 + i), team: name, member: i % 2 ? 'DeveloperTomas' : 'TesterKofi', run: 1, kind: i % 2 ? 'edit' : 'read', tool: 'Edit', target: `src/d${i % 11}/f${i}.ts` }, 1000, true);
      m.setWorkflows(name, wfs, 0);
    }
    scene.model = m;
    scene.setTeams(teams);
    if (spacing !== LayoutDefaults.spacing) scene.setLayout({ ...LayoutDefaults, spacing });
    const frame = (scene as unknown as { frame: (t: number) => void }).frame;
    for (let i = 1; i <= frames; i++) frame(1000 + i * 16);
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

  it('at the default, two teams\' clusters (members, workflow nodes, code map) sit close but do not overlap', () => {
    const scene = settledWall(2);
    const cl = scene.clusters();
    expect(cl).toHaveLength(2);
    for (const c of cl) expect(c.box.x1 - c.box.x0).toBeGreaterThan(300); // the map is in the boxes
    expect(homeDistances(scene)).toEqual([580]); // was 816 with a flat MapRoom of 190
    for (const g of gaps(scene)) { expect(g).toBeGreaterThan(0); expect(g).toBeLessThan(300); }
    for (const c of cl) expect(Math.hypot(c.hub.x - c.home.x, c.hub.y - c.home.y)).toBeLessThan(80); // the hubs sit near their homes
  });

  it('at the default, three teams\' clusters do not overlap either', () => {
    const scene = settledWall(3);
    expect(homeDistances(scene)).toEqual([860, 860, 580]);
    for (const g of gaps(scene)) expect(g).toBeGreaterThan(0);
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
