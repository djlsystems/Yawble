// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Goal, GoalsListAnswer, GoalTotals, YawbleApi } from '../../src/shared/api';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import GoalsView from '../../src/renderer/src/goals/GoalsView.vue';
import GoalPicker from '../../src/renderer/src/goals/GoalPicker.vue';
import { countsLine, liveElapsedMs, nameProblem } from '../../src/renderer/src/goals/goals';
import DeliverablesView from '../../src/renderer/src/deliverables/DeliverablesView.vue';
import { deliverablesRequest } from '../../src/renderer/src/deliverables/page';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { confirmState } from '../../src/renderer/src/confirm';
import { paneCore, paneRow } from './deliverable-pane-fake';
import { stopConfirmStubs, stubConfirm } from './confirm-stub';

// The Goals page (the goals and their deliverables, Unassigned last, fold-downs closed at first, the goal dialog), the goal
// picker on the Deliverables page, its goal filter chip, and the sidebar's order with Goals in it. Fakes only: a scripted
// core answering goals.* and deliverables.* as the contract says.

afterEach(() => { applyRemoteAccess('full'); stopConfirmStubs(); deliverablesRequest.value = null; localStorage.clear(); history.replaceState(null, '', '/'); document.body.innerHTML = ''; });

const time = (elapsedMs: number, endAt: string | null = '2026-11-20T12:00:00Z') => ({ firstAt: '2026-11-20T10:00:00Z', endAt, elapsedMs });
const goal = (id: number, name: string, p: Partial<Goal> = {}): Goal => ({
  id, name, description: '', createdAt: '2026-11-20T09:00:00Z', updatedAt: '2026-11-20T09:00:00Z', deliverables: [], open: 0, total: 0, time: time(0, null), ...p,
});
const unassigned = (p: Partial<GoalTotals> = {}): GoalTotals => ({ deliverables: [], open: 0, total: 0, time: time(0, null), ...p });

/** A core holding goals (mutable): goals.list, .save, .delete, .assign, and deliverables.list by goal. */
function goalsCore(start: Goal[] = [], o: { rows?: ReturnType<typeof paneRow>[]; free?: GoalTotals } = {}) {
  let goals = [...start];
  let next = 100;
  const calls: { method: string; params: any }[] = [];
  const rows = o.rows ?? [];
  const api = {
    call: async (method: string, params: any = {}) => {
      calls.push({ method, params });
      switch (method) {
        case 'goals.list': return { goals, unassigned: o.free ?? unassigned(), now: '2026-11-20T12:00:00Z' } satisfies GoalsListAnswer;
        case 'goals.save': {
          const g = params.id ? { ...goals.find((x) => x.id === params.id)!, name: params.name, description: params.description } : goal(++next, params.name, { description: params.description });
          goals = params.id ? goals.map((x) => (x.id === g.id ? g : x)) : [...goals, g];
          return g;
        }
        case 'goals.delete': goals = goals.filter((x) => x.id !== params.id); return { deleted: params.id, unassigned: 0 };
        case 'goals.assign': return { deliverable: params.deliverable, goal: params.goal === null ? null : { id: params.goal, name: goals.find((x) => x.id === params.goal)?.name ?? '?' } };
        case 'deliverables.list': {
          const list = rows.filter((r) => params.goal === undefined || (params.goal === 'none' ? !r.goal : r.goal?.id === params.goal));
          return { deliverables: list, counts: null, truncated: false, backlog: true, now: '2026-11-20T12:00:00Z' };
        }
        case 'deliverables.get': return { deliverable: rows.find((r) => r.id === params.deliverable), cards: [], messages: [], spec: null, backlog: true };
        default: return {};
      }
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  const changes = () => calls.filter((c) => c.method !== 'goals.list' && !c.method.startsWith('deliverables.'));
  return { api, calls, changes };
}

async function page(core: ReturnType<typeof goalsCore>) {
  const w = mount(GoalsView, { props: { api: core.api, pollMs: 600_000 }, attachTo: document.body });
  await flushPromises();
  return w;
}

describe('the sidebar', () => {
  it('lists the places in the order New chat, Concierge, Teams, Goals, Deliverables, Backlog, Kanban board, Workflows, Documents, Monitor, Logs', () => {
    const props = { view: 'concierge' as const, conversations: [], teams: [], conversationId: null, teamId: null, slots: null, remote: false };
    const w = mount(Sidebar, { props });
    const labels = [w.get('[data-test=new-chat]').text(), ...w.findAll('nav.places button').map((b) => b.text().replace(/\d+$/, '').trim())];
    expect(labels).toEqual(['New chat', 'Concierge', 'Teams', 'Goals', 'Deliverables', 'Backlog', 'Kanban board', 'Workflows', 'Documents', 'Monitor', 'Logs']);
    const rail = mount(Sidebar, { props: { ...props, rail: true } });
    const railIds = rail.findAll('.rail-btn[data-test^=rail-]').map((b) => b.attributes('data-test'));
    expect(railIds).toEqual(['rail-new-chat', 'rail-concierge', 'rail-teams', 'rail-goals', 'rail-deliverables', 'rail-backlog', 'rail-board', 'rail-workflows', 'rail-documents', 'rail-monitor', 'rail-logs']);
  });
  it('Goals goes to the goals view', async () => {
    const w = mount(Sidebar, { props: { view: 'concierge', conversations: [], teams: [], conversationId: null, teamId: null, slots: null, remote: false } });
    await w.get('[data-test=nav-goals]').trigger('click');
    expect(w.emitted('go')).toEqual([['goals']]);
  });
});

describe('the pure parts', () => {
  it('a name is required, at most 80 characters and not taken (ignoring case)', () => {
    expect(nameProblem('  ')).toMatch(/name/);
    expect(nameProblem('x'.repeat(81))).toMatch(/80/);
    expect(nameProblem('Launch', [goal(1, 'launch')])).toMatch(/already/);
    expect(nameProblem('Launch', [goal(1, 'launch')], 1)).toBeNull();
    expect(nameProblem('Launch')).toBeNull();
    // every letter, as the core folds (GoalRules.SameName)
    expect(nameProblem('été', [goal(1, 'Été')])).toMatch(/already/);
  });
  it('an open goal counts on from the answer; an ended one does not', () => {
    const now = Date.parse('2026-11-20T12:00:30Z');
    expect(liveElapsedMs(time(60_000, null), '2026-11-20T12:00:00Z', now)).toBe(90_000);
    expect(liveElapsedMs(time(60_000), '2026-11-20T12:00:00Z', now)).toBe(60_000);
    expect(liveElapsedMs({ firstAt: null, endAt: null, elapsedMs: 0 }, '2026-11-20T12:00:00Z', now)).toBe(0);
  });
  it('the counts line', () => {
    expect(countsLine(0, 0)).toBe('No deliverables');
    expect(countsLine(1, 0)).toBe('1 deliverable, none open');
    expect(countsLine(3, 1)).toBe('3 deliverables, 1 open');
  });
});

describe('the Goals page', () => {
  const linked = paneRow({ id: 11, title: 'Partial refunds', status: 'completed', goal: { id: 1, name: 'Payments v2' } });
  const loose = paneRow({ id: 12, title: 'Refund limits', status: 'idle', goal: null });

  it('lists every goal with its counts and elapsed time, and the Unassigned group last', async () => {
    const core = goalsCore([goal(1, 'Payments v2', { description: 'Ship refunds', total: 3, open: 1, deliverables: [11], time: time(3_725_000) }), goal(2, 'Docs', { time: time(0, null) })],
      { free: unassigned({ deliverables: [12], total: 1, open: 1, time: time(65_000) }) });
    const w = await page(core);
    const rows = w.findAll('[data-test=goal-row]');
    expect(rows.map((r) => r.get('[data-test=goal-name]').text())).toEqual(['Payments v2', 'Docs']);
    expect(rows[0].get('[data-test=goal-counts]').text()).toBe('3 deliverables, 1 open');
    expect(rows[0].get('[data-test=goal-elapsed]').text()).toBe('1h 02m 05s');
    expect(rows[0].get('[data-test=goal-description]').text()).toBe('Ship refunds');
    expect(rows[1].get('[data-test=goal-counts]').text()).toBe('No deliverables');
    const items = w.findAll('[data-test=goals-list] > li');
    expect(items.at(-1)!.attributes('data-test')).toBe('goal-unassigned');
    expect(items.at(-1)!.get('[data-test=goal-counts]').text()).toBe('1 deliverable, 1 open');
  });

  it('the fold-down is closed at first; opening it lists the goal\'s deliverables, and one opens the Deliverables page', async () => {
    const core = goalsCore([goal(1, 'Payments v2', { total: 1, deliverables: [11] })], { rows: [linked, loose] });
    const w = await page(core);
    expect(w.find('[data-test=goal-deliverables]').exists()).toBe(false);
    expect(w.findAll('[data-test=goal-fold]').every((b) => b.attributes('aria-expanded') === 'false')).toBe(true);
    expect(core.calls.some((c) => c.method === 'deliverables.list')).toBe(false);
    await w.get('[data-test=goal-row] [data-test=goal-fold]').trigger('click');
    await flushPromises();
    expect(core.calls.find((c) => c.method === 'deliverables.list')!.params).toMatchObject({ goal: 1, state: 'all' });
    const ds = w.findAll('[data-test=goal-row] [data-test=goal-deliverable]');
    expect(ds.map((d) => d.attributes('data-id'))).toEqual(['11']);
    expect(ds[0].text()).toContain('Partial refunds');
    expect(ds[0].text()).toContain('payments');
    expect(ds[0].get('[data-test=goal-deliverable-status]').text()).toBe('Completed');
    await ds[0].trigger('click');
    expect(deliverablesRequest.value).toMatchObject({ deliverable: 11 });
    await w.get('[data-test=goal-show-all]').trigger('click');
    expect(deliverablesRequest.value).toMatchObject({ goal: { id: 1, name: 'Payments v2' } });
    // Unassigned folds down to the deliverables with no goal
    await w.get('[data-test=goal-unassigned] [data-test=goal-fold]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=goal-unassigned] [data-test=goal-deliverable]').map((d) => d.attributes('data-id'))).toEqual(['12']);
  });

  it('a fold-down whose list was cut says so and links to the full list', async () => {
    const many = paneRow({ id: 21, title: 'One of many', goal: { id: 1, name: 'Payments v2' } });
    const core = goalsCore([goal(1, 'Payments v2', { total: 900, deliverables: [21] })], { rows: [many] });
    const call = core.api.call.bind(core.api);
    (core.api as any).call = async (m: string, p: any) => (m === 'deliverables.list' ? { ...(await call(m, p) as object), truncated: true } : call(m, p));
    const w = await page(core);
    await w.get('[data-test=goal-row] [data-test=goal-fold]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=goal-deliverables-cut]').text()).toBe('Showing the newest 1; open the Deliverables page for all.');
    expect(w.find('[data-test=goal-show-all]').exists()).toBe(true);
  });

  it('New goal saves a name and description; Edit renames it', async () => {
    const core = goalsCore([goal(1, 'Payments v2')]);
    const w = await page(core);
    await w.get('[data-test=goals-new]').trigger('click');
    // a taken name is refused before anything is sent
    await w.get('[data-test=goal-dialog-name]').setValue('payments V2');
    await w.get('[data-test=goal-dialog]').trigger('submit');
    await flushPromises();
    expect(w.get('[data-test=goal-dialog-problem]').text()).toMatch(/already/);
    expect(core.changes()).toEqual([]);
    await w.get('[data-test=goal-dialog-name]').setValue('  Launch  ');
    await w.get('[data-test=goal-dialog-description]').setValue('The public launch');
    await w.get('[data-test=goal-dialog]').trigger('submit');
    await flushPromises();
    expect(core.changes()).toEqual([{ method: 'goals.save', params: { name: 'Launch', description: 'The public launch' } }]);
    expect(w.find('[data-test=goal-dialog]').exists()).toBe(false);
    expect(w.findAll('[data-test=goal-name]').map((n) => n.text())).toEqual(['Payments v2', 'Launch']);

    await w.findAll('[data-test=goal-edit]')[0].trigger('click');
    expect((w.get('[data-test=goal-dialog-name]').element as HTMLInputElement).value).toBe('Payments v2');
    await w.get('[data-test=goal-dialog-name]').setValue('Payments');
    await w.get('[data-test=goal-dialog]').trigger('submit');
    await flushPromises();
    expect(core.changes().at(-1)).toEqual({ method: 'goals.save', params: { id: 1, name: 'Payments', description: '' } });
    expect(w.findAll('[data-test=goal-name]').map((n) => n.text())).toEqual(['Payments', 'Launch']);
  });

  it('Delete asks in the window\'s own confirm dialog (never the browser\'s), and Cancel deletes nothing', async () => {
    const browser = vi.fn(() => true);
    const was = (window as { confirm?: unknown }).confirm;
    (window as { confirm?: unknown }).confirm = browser;
    const core = goalsCore([goal(1, 'Payments v2', { total: 2, deliverables: [11, 13] })]);
    const w = await page(core);
    await w.get('[data-test=goal-edit]').trigger('click');
    await w.get('[data-test=goal-dialog-delete]').trigger('click');
    await flushPromises();
    expect(confirmState.value?.message).toBe('Delete the goal "Payments v2"? Its 2 deliverables become Unassigned.');
    confirmState.value!.resolve(false);
    await flushPromises();
    expect(core.changes()).toEqual([]);
    stubConfirm(() => true);
    await w.get('[data-test=goal-dialog-delete]').trigger('click');
    await flushPromises();
    expect(core.changes()).toEqual([{ method: 'goals.delete', params: { id: 1 } }]);
    expect(w.find('[data-test=goal-dialog]').exists()).toBe(false);
    expect(w.findAll('[data-test=goal-row]')).toHaveLength(0);
    expect(browser).not.toHaveBeenCalled();
    (window as { confirm?: unknown }).confirm = was;
  });

  it('a Visitor\'s New goal, Edit and dialog buttons are disabled', async () => {
    applyRemoteAccess('read');
    const core = goalsCore([goal(1, 'Payments v2')]);
    const w = await page(core);
    expect((w.get('[data-test=goals-new]').element as HTMLButtonElement).disabled).toBe(true);
    expect((w.get('[data-test=goal-edit]').element as HTMLButtonElement).disabled).toBe(true);
    // the fold-down still reads
    expect((w.get('[data-test=goal-fold]').element as HTMLButtonElement).disabled).toBe(false);
  });
});

describe('the goal picker', () => {
  it('offers every goal, Unassigned and New goal…, and a choice assigns', async () => {
    const core = goalsCore([goal(1, 'Payments v2'), goal(2, 'Docs')]);
    const w = mount(GoalPicker, { props: { api: core.api, deliverable: 11, goal: null }, attachTo: document.body });
    await flushPromises();
    const sel = w.get('[data-test=goal-picker-select]');
    expect(sel.findAll('option').map((o) => o.text())).toEqual(['Unassigned', 'Payments v2', 'Docs', 'New goal…']);
    expect((sel.element as HTMLSelectElement).value).toBe('');
    await sel.setValue('2');
    await flushPromises();
    expect(core.changes()).toEqual([{ method: 'goals.assign', params: { deliverable: 11, goal: 2 } }]);
    expect(w.emitted('changed')).toEqual([[{ id: 2, name: 'Docs' }]]);
    await sel.setValue('');
    await flushPromises();
    expect(core.changes().at(-1)).toEqual({ method: 'goals.assign', params: { deliverable: 11, goal: null } });
  });

  it('New goal… opens the dialog, creates the goal, then assigns it', async () => {
    const core = goalsCore([goal(1, 'Payments v2')]);
    const w = mount(GoalPicker, { props: { api: core.api, deliverable: 11, goal: { id: 1, name: 'Payments v2' } }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=goal-picker-select]').setValue('new');
    await flushPromises();
    expect((w.get('[data-test=goal-picker-select]').element as HTMLSelectElement).value).toBe('1'); // still its goal while asking
    await w.get('[data-test=goal-dialog-name]').setValue('Launch');
    await w.get('[data-test=goal-dialog]').trigger('submit');
    await flushPromises();
    expect(core.changes()).toEqual([
      { method: 'goals.save', params: { name: 'Launch', description: '' } },
      { method: 'goals.assign', params: { deliverable: 11, goal: 101 } },
    ]);
    expect((w.get('[data-test=goal-picker-select]').element as HTMLSelectElement).value).toBe('101');
  });

  it('a Visitor\'s picker is disabled', async () => {
    applyRemoteAccess('read');
    const core = goalsCore([goal(1, 'Payments v2')]);
    const w = mount(GoalPicker, { props: { api: core.api, deliverable: 11, goal: null }, attachTo: document.body });
    await flushPromises();
    expect((w.get('[data-test=goal-picker-select]').element as HTMLSelectElement).disabled).toBe(true);
  });
});

describe('the Deliverables page', () => {
  const rows = [
    paneRow({ id: 11, title: 'Partial refunds', goal: { id: 1, name: 'Payments v2' } }),
    paneRow({ id: 12, title: 'Refund limits', goal: null }),
  ];
  function core() {
    return paneCore(() => rows, { extra: {
      'goals.list': () => ({ goals: [goal(1, 'Payments v2')], unassigned: unassigned(), now: '2026-11-20T12:00:00Z' }),
      'goals.assign': (p: any) => ({ deliverable: p.deliverable, goal: p.goal ? { id: p.goal, name: 'Payments v2' } : null }),
    } });
  }

  it('each row has a goal picker showing its goal', async () => {
    const c = core();
    const w = mount(DeliverablesView, { props: { teams: [{ id: 1, name: 'payments' }] as never, api: c.api, pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    const picker = (id: number) => w.get(`[data-test=deliverables-row][data-wf="${id}"] [data-test=goal-picker-select]`);
    expect((picker(11).element as HTMLSelectElement).value).toBe('1');
    expect((picker(12).element as HTMLSelectElement).value).toBe('');
    // one goals.list for the page, not one per row
    expect(c.calls.filter((x) => x.method === 'goals.list')).toHaveLength(1);
    await picker(12).setValue('1');
    await flushPromises();
    expect(c.methods()).toEqual(['goals.assign']);
    expect(c.calls.find((x) => x.method === 'goals.assign')!.params).toEqual({ deliverable: 12, goal: 1 });
  });

  it('opened from a goal: a goal chip, the list read for that goal, and clearing the chip shows all again', async () => {
    const c = core();
    const w = mount(DeliverablesView, { props: { teams: [{ id: 1, name: 'payments' }] as never, api: c.api, pollMs: 600_000, request: { goal: { id: 1, name: 'Payments v2' }, seq: 1 } }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=deliverables-goal-chip]').text()).toContain('Goal: Payments v2');
    const lists = c.calls.filter((x) => x.method === 'deliverables.list');
    expect(lists.at(-1)!.params).toMatchObject({ goal: 1, state: 'all' });
    expect(w.findAll('[data-test=deliverables-row]').map((r) => r.attributes('data-wf'))).toEqual(['11']);
    await w.get('[data-test=deliverables-goal-chip-clear]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=deliverables-goal-chip]').exists()).toBe(false);
    expect(c.calls.filter((x) => x.method === 'deliverables.list').at(-1)!.params.goal).toBeUndefined();
    expect(w.findAll('[data-test=deliverables-row]').map((r) => r.attributes('data-wf')).sort()).toEqual(['11', '12']);
  });

  it('a goal deleted elsewhere: the next goals read drops its chip and the page shows every goal\'s', async () => {
    const c = paneCore(() => rows, { extra: { 'goals.list': () => ({ goals: [], unassigned: unassigned(), now: '2026-11-20T12:00:00Z' }) } });
    const w = mount(DeliverablesView, { props: { teams: [] as never, api: c.api, pollMs: 600_000, request: { goal: { id: 1, name: 'Payments v2' }, seq: 3 } }, attachTo: document.body });
    await flushPromises();
    expect(w.find('[data-test=deliverables-goal-chip]').exists()).toBe(false);
    expect(c.calls.filter((x) => x.method === 'deliverables.list').at(-1)!.params.goal).toBeUndefined();
  });

  it('opened at a deliverable while filtered to a goal: the filter goes so its row is in the table', async () => {
    const c = core();
    const w = mount(DeliverablesView, { props: { teams: [] as never, api: c.api, pollMs: 600_000, request: { goal: { id: 1, name: 'Payments v2' }, seq: 4 } }, attachTo: document.body });
    await flushPromises();
    expect(w.findAll('[data-test=deliverables-row]').map((r) => r.attributes('data-wf'))).toEqual(['11']);
    await w.setProps({ request: { deliverable: 12, seq: 5 } });
    await flushPromises();
    expect(w.find('[data-test=deliverables-goal-chip]').exists()).toBe(false);
    expect(w.findAll('[data-test=deliverables-row]').map((r) => r.attributes('data-wf')).sort()).toEqual(['11', '12']);
  });

  it('each poll reads the goals too, so a goal made elsewhere reaches the pickers', async () => {
    let list = [goal(1, 'Payments v2')];
    const c = paneCore(() => rows, { extra: { 'goals.list': () => ({ goals: list, unassigned: unassigned(), now: '2026-11-20T12:00:00Z' }) } });
    const w = mount(DeliverablesView, { props: { teams: [] as never, api: c.api, pollMs: 20 }, attachTo: document.body });
    await flushPromises();
    list = [...list, goal(2, 'Docs')];
    await new Promise((r) => setTimeout(r, 80));
    await flushPromises();
    const options = w.get('[data-test=deliverables-row][data-wf="12"] [data-test=goal-picker-select]').findAll('option').map((o) => o.text());
    expect(options).toContain('Docs');
    w.unmount();
  });

  it('a goal with no deliverables: "Nothing matches these filters" with Show all, not the empty-app message', async () => {
    const c = paneCore(() => [], { extra: { 'goals.list': () => ({ goals: [goal(2, 'Docs')], unassigned: unassigned(), now: '2026-11-20T12:00:00Z' }) } });
    const w = mount(DeliverablesView, { props: { teams: [] as never, api: c.api, pollMs: 600_000, request: { goal: { id: 2, name: 'Docs' }, seq: 6 } }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=deliverables-empty]').text()).toContain('Nothing matches these filters.');
    expect(w.find('[data-test=deliverables-empty-clear]').exists()).toBe(true);
    expect(w.find('[data-test=deliverables-empty-backlog]').exists()).toBe(false);
  });

  it('opened at Unassigned: the chip says No goal and asks for goal "none"', async () => {
    const c = core();
    const w = mount(DeliverablesView, { props: { teams: [] as never, api: c.api, pollMs: 600_000, request: { goal: { id: 'none', name: 'Unassigned' }, seq: 2 } }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=deliverables-goal-chip]').text()).toContain('No goal');
    expect(c.calls.filter((x) => x.method === 'deliverables.list').at(-1)!.params.goal).toBe('none');
    expect(w.findAll('[data-test=deliverables-row]').map((r) => r.attributes('data-wf'))).toEqual(['12']);
  });
});
