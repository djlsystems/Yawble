// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import type { YawbleApi } from '../../src/shared/api';

const wf = (id: number, title: string, state: string, lastAt: string) =>
  ({ id, title, owner: 'Manager', state, undeclared: false, busy: [], ending: state === 'completed' ? 'done' : null, startedAt: '', lastAt, messages: 3 });
const workflows = [wf(402, 'Backlog item B002', 'completed', '2026-11-11T10:00:00Z'), wf(413, 'Backlog item B009', 'completed', '2026-11-11T12:00:00Z'), wf(77, 'Told directly', 'open', '2026-11-11T09:00:00Z')];
const backlog = [1, 2, 3].map((id) => ({
  id, label: `B00${id}`, team: 'payments', title: `Item ${id}`, body: '', state: 'ready', position: id, createdAt: '', updatedAt: '',
  archivedAt: null, dispatchedTeam: null, dispatchedWorkflow: null, dispatchedAt: null, inFlight: false, dispatchCount: 0,
}));

/** The Workflows page's rows (workflows.list / workflows.get) for the same workflows. */
const pageRow = (w: (typeof workflows)[number]) => ({
  ...w, ownerId: 1, endedAt: w.state === 'completed' ? w.lastAt : null, expiredAt: null, expiresAt: null, expiryWarning: null, question: null, time: null,
  team: 'payments', teamId: 1, waitingFor: null, working: false, item: null, members: [], status: w.state === 'completed' ? 'completed' : 'idle',
});
const calls: { method: string; p: any }[] = [];

function api(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, p?: any) => {
      calls.push({ method, p });
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 }] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'bus.workflows': return workflows as any;
        case 'workflows.list': return { workflows: workflows.map(pageRow), counts: { all: 3, open: 1, needsYou: 0, done: 2, closed: 0, completedToday: 0 }, truncated: false, backlog: true, now: '' } as any;
        case 'workflows.get': {
          const w = workflows.find((x) => x.id === p.workflow);
          if (!w) throw Object.assign(new Error('no such workflow'), { code: 'no_such_workflow' });
          return { workflow: pageRow(w), cards: [], messages: [], spec: null, backlog: true } as any;
        }
        case 'backlog.list': return backlog as any;
        case 'backlog.item': return { ...backlog.find((i) => i.id === p.id), dispatches: [] } as any;
        case 'monitor.messages': return { end: 0, messages: [] } as any;
        case 'monitor.steps': return { end: 0, pending: false, steps: [] } as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

async function clickMonitorWorkflow(id: number) {
  const w = mount(App, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
  await flushPromises();
  await w.get('[data-test=nav-monitor]').trigger('click');
  for (let i = 0; i < 4; i++) await flushPromises();
  await w.findAll('[data-test=monitor-workflow]').find((b) => b.attributes('data-id') === String(id))!.trigger('click');
  await vi.waitFor(() => expect(w.find('[data-test=workflows-view]').exists()).toBe(true)); // loaded when first opened
  for (let i = 0; i < 6; i++) await flushPromises();
  return w;
}

const selectedRows = (w: Awaited<ReturnType<typeof clickMonitorWorkflow>>) =>
  w.findAll('[data-test=workflows-row]').filter((e) => e.classes().includes('sel')).map((e) => Number(e.attributes('data-wf')));
const teamFilter = (w: Awaited<ReturnType<typeof clickMonitorWorkflow>>) => (w.get('[data-test=workflows-team-filter]').element as HTMLSelectElement).value;

describe('a workflow clicked on the Monitor', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); document.body.innerHTML = ''; calls.length = 0; history.replaceState(null, '', '/'); });

  it('opens the Workflows page at that workflow: its team, All, selected, its pane open, no dialog', async () => {
    const w = await clickMonitorWorkflow(413);
    expect(w.find('[data-test=monitor-workflow]').exists()).toBe(false); // the page, not the Monitor
    expect(w.find('[data-test=workflows-view]').exists()).toBe(true);
    expect(document.querySelector('[aria-modal="true"]')).toBeNull();
    expect(selectedRows(w)).toEqual([413]);
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('413');
    expect(teamFilter(w)).toBe('1');
    expect(w.get('[data-test=workflows-chip-all]').attributes('aria-selected')).toBe('true');
    expect(w.find('[data-test=backlog-row]').exists()).toBe(false);
    w.unmount();
  });

  it('Esc closes the pane and stays on the page, as on the page itself', async () => {
    const w = await clickMonitorWorkflow(413);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    for (let i = 0; i < 4; i++) await flushPromises();
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false);
    expect(w.find('[data-test=workflows-view]').exists()).toBe(true);
    w.unmount();
  });

  it("the team's workflows header opens the page at the team's workflows, nothing selected", async () => {
    const w = mount(App, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=nav-monitor]').trigger('click');
    for (let i = 0; i < 4; i++) await flushPromises();
    await w.get('[data-test=monitor-workflows-team]').trigger('click');
    await vi.waitFor(() => expect(w.find('[data-test=workflows-view]').exists()).toBe(true));
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(w.find('[data-test=workflows-view]').exists()).toBe(true);
    expect(document.querySelector('[aria-modal="true"]')).toBeNull();
    expect(teamFilter(w)).toBe('1');
    expect(selectedRows(w)).toEqual([]);
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false);
    w.unmount();
  });

  it('selects whichever workflow was clicked, whether or not it came from the backlog', async () => {
    const w = await clickMonitorWorkflow(77);
    expect(selectedRows(w)).toEqual([77]);
    w.unmount();
  });
});

describe('the backlog shows the item you are on as selected', () => {
  it('marks the clicked row selected', async () => {
    const w = mount(BacklogView, { props: { teams: [], api: api(), pollMs: 60_000 } });
    await flushPromises();
    await w.findAll('[data-test=backlog-row]')[1].trigger('click');
    const rows = w.findAll('[data-test=backlog-row]');
    expect(rows.map((r) => r.classes().includes('sel'))).toEqual([false, true, false]);
    expect(rows[1].attributes('aria-selected')).toBe('true');
  });
});
