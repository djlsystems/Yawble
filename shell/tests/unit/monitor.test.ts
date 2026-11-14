// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { memberKey, MonitorModel, teamKey, type FeedRow } from '../../src/renderer/src/monitor/model';
import MonitorView from '../../src/renderer/src/monitor/MonitorView.vue';
import Timeline from '../../src/renderer/src/monitor/Timeline.vue';
import { aimsAtEverything, FitOnceMs } from '../../src/renderer/src/monitor/scene';
import type { YawbleApi, WorkflowSummary } from '../../src/shared/api';
import { entryTime, latestWorkflows, panelHit, panelLayout, panelRadius, panelScale, workflowLabel, workflowLook, WorkflowLooks, WorkflowsShown } from '../../src/renderer/src/monitor/workflows';
import { fileAlpha, FileTrail, FileTtlMs, MaxFileNodes, readStep, stepCaption, stepDoing, type StepKind, type StepRow } from '../../src/renderer/src/monitor/steps';
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

  it('keeps a member\'s files in their places, counts the overflow, and forgets them 30 s after their last touch', () => {
    const trail = new FileTrail();
    for (let i = 0; i < MaxFileNodes + 2; i++) trail.touch('m', `f${i}.ts`, 'edit', i);
    let { placed, counted } = trail.of('m');
    expect(placed.map((f) => f.slot).sort()).toEqual([0, 1, 2, 3, 4, 5]);
    expect(counted).toHaveLength(2);
    // Touched again: same place, a touch more, alive longer.
    const again = trail.touch('m', 'f0.ts', 'edit', 20_000);
    expect(again).toMatchObject({ slot: 0, touches: 2 });
    trail.tidy(FileTtlMs + 10);
    ({ placed, counted } = trail.of('m'));
    expect(placed.map((f) => f.path)).toEqual(['f0.ts']);
    expect(counted).toHaveLength(0);
    expect(fileAlpha(again, 20_000 + FileTtlMs - 1)).toBeGreaterThan(0);
    expect(fileAlpha(again, 20_000 + FileTtlMs)).toBe(0);
    trail.tidy(20_000 + FileTtlMs);
    expect(trail.size).toBe(0);
  });

  it('plays a step as an icon, a file and a "now" line, and gathers file changes on one activity line', () => {
    const m = new MonitorModel();
    m.applyStep(step(1, 'edit', 'src/a.ts'), 100);
    m.applyStep(step(2, 'run', 'npm test'), 200);
    m.applyStep(step(3, 'create', 'src/b.ts'), 300);
    expect(m.effects.map((e) => e.kind)).toEqual(['step', 'step', 'step']);
    expect(m.effects[1]).toMatchObject({ step: 'run', caption: 'npm test' });
    expect(m.files.of(memberKey('payments', 'DeveloperPriya')).placed.map((f) => f.name)).toEqual(['a.ts', 'b.ts']);
    expect(m.doing.get(memberKey('payments', 'DeveloperPriya'))).toMatchObject({ text: 'writing b.ts', kind: 'create' });
    expect(m.activity).toHaveLength(1);
    expect(m.activity[0]).toMatchObject({ verb: 'wrote files', text: 'a.ts, b.ts', tone: 'edit' });
    // Read at the start: listed, not played.
    const quiet = new MonitorModel();
    quiet.applyStep(step(4, 'edit', 'x.ts'), 0, false);
    expect(quiet.effects).toHaveLength(0);
    expect(quiet.files.size).toBe(0);
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
    expect(m.effects.map((e) => e.kind)).toEqual(['comet', 'burst', 'float', 'burst']);
    expect(m.effects[0]).toMatchObject({ from: memberKey('payments', 'Manager'), to: memberKey('payments', 'DeveloperPriya'), label: 'Add a README' });
    expect(m.effects[3]).toMatchObject({ node: teamKey('payments'), big: true });
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

  it('lays a team\'s panel out as a header and one row per workflow, newest first', () => {
    const list = [3, 1, 2].map((i) => wf(i, at(1_000_000 + i * 1000)));
    expect(latestWorkflows(list).map((w) => w.id)).toEqual([3, 2, 1]);
    const one = panelLayout(5);
    expect(one.rows).toBe(5);
    expect(one.h).toBe(one.pad * 2 + one.headerH + 5 * one.rowH);
    expect(panelLayout(2).h).toBeLessThan(one.h);
    expect(panelLayout(0).h).toBe(one.pad * 2 + one.headerH);
    // Twice the scale, twice every size.
    const big = panelLayout(5, 2);
    expect(big.w).toBe(one.w * 2);
    expect(big.h).toBe(one.h * 2);
    expect(panelRadius(one)).toBeCloseTo(Math.hypot(one.w, one.h) / 2);
  });

  it('finds the header or the row under a point of the panel', () => {
    const l = panelLayout(3);
    expect(panelHit(l, -1, 5)).toBeNull();
    expect(panelHit(l, 5, l.h + 1)).toBeNull();
    expect(panelHit(l, l.w + 1, 5)).toBeNull();
    expect(panelHit(l, 10, l.pad + 2)).toBe('header');
    const rowY = (i: number) => l.pad + l.headerH + i * l.rowH + l.rowH / 2;
    expect(panelHit(l, 10, rowY(0))).toBe(0);
    expect(panelHit(l, 10, rowY(2))).toBe(2);
    // The padding under the last row is the panel, not a row.
    expect(panelHit(l, 10, l.h - 1)).toBe('panel');
  });

  it('grows the panel as the wall zooms out, so its words stay readable', () => {
    expect(panelScale(1)).toBe(1);
    expect(panelScale(2)).toBe(1); // zoomed in: the words just grow with the camera
    expect(panelScale(0.4)).toBeGreaterThan(1);
    expect(panelScale(0.4) * 10 * 0.4).toBeCloseTo(8); // 10 px words never shown below 8 px
    expect(panelScale(0.05)).toBe(2.4); // capped
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
