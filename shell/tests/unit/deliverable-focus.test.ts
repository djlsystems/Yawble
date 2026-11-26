// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import type { YawbleApi } from '../../src/shared/api';

const wf = (id: number, title: string, state: string, lastAt: string) =>
  ({ id, title, owner: 'Manager', state, undeclared: false, busy: [], ending: state === 'completed' ? 'done' : null, startedAt: '', lastAt, messages: 3 });
const deliverables = [wf(402, 'Backlog item B002', 'completed', '2026-11-11T10:00:00Z'), wf(413, 'Backlog item B009', 'completed', '2026-11-11T12:00:00Z'), wf(77, 'Told directly', 'open', '2026-11-11T09:00:00Z')];
const backlog = [1, 2, 3].map((id) => ({
  id, label: `B00${id}`, team: 'payments', title: `Item ${id}`, body: '', state: 'ready', position: id, createdAt: '', updatedAt: '',
  archivedAt: null, dispatchedTeam: null, dispatchedDeliverable: null, dispatchedAt: null, inFlight: false, dispatchCount: 0,
}));

/** The Deliverables page's rows (deliverables.list / deliverables.get) for the same deliverables. */
const pageRow = (w: (typeof deliverables)[number]) => ({
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
        case 'bus.deliverables': return deliverables as any;
        case 'deliverables.list': return { deliverables: deliverables.map(pageRow), counts: { all: 3, open: 1, needsYou: 0, done: 2, closed: 0, completedToday: 0 }, truncated: false, backlog: true, now: '' } as any;
        case 'deliverables.get': {
          const w = deliverables.find((x) => x.id === p.deliverable);
          if (!w) throw Object.assign(new Error('no such deliverable'), { code: 'no_such_deliverable' });
          return { deliverable: pageRow(w), cards: [], messages: [], spec: null, backlog: true } as any;
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

async function clickMonitorDeliverable(id: number) {
  const w = mount(App, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
  await flushPromises();
  await w.get('[data-test=nav-monitor]').trigger('click');
  for (let i = 0; i < 4; i++) await flushPromises();
  await w.findAll('[data-test=monitor-deliverable]').find((b) => b.attributes('data-id') === String(id))!.trigger('click');
  await vi.waitFor(() => expect(w.find('[data-test=deliverables-view]').exists()).toBe(true)); // loaded when first opened
  for (let i = 0; i < 6; i++) await flushPromises();
  return w;
}

const selectedRows = (w: Awaited<ReturnType<typeof clickMonitorDeliverable>>) =>
  w.findAll('[data-test=deliverables-row]').filter((e) => e.classes().includes('sel')).map((e) => Number(e.attributes('data-wf')));
const teamFilter = (w: Awaited<ReturnType<typeof clickMonitorDeliverable>>) => (w.get('[data-test=deliverables-team-filter]').element as HTMLSelectElement).value;

describe('a deliverable clicked on the Monitor', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); document.body.innerHTML = ''; calls.length = 0; history.replaceState(null, '', '/'); });

  it('opens the Deliverables page at that deliverable: its team, All, selected, its pane open, no dialog', async () => {
    const w = await clickMonitorDeliverable(413);
    expect(w.find('[data-test=monitor-deliverable]').exists()).toBe(false); // the page, not the Monitor
    expect(w.find('[data-test=deliverables-view]').exists()).toBe(true);
    expect(document.querySelector('[aria-modal="true"]')).toBeNull();
    expect(selectedRows(w)).toEqual([413]);
    expect(w.get('[data-test=deliverable-pane]').attributes('data-id')).toBe('413');
    expect(teamFilter(w)).toBe('1');
    expect(w.get('[data-test=deliverables-chip-all]').attributes('aria-selected')).toBe('true');
    expect(w.find('[data-test=backlog-row]').exists()).toBe(false);
    w.unmount();
  });

  it('Esc closes the pane and stays on the page, as on the page itself', async () => {
    const w = await clickMonitorDeliverable(413);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    for (let i = 0; i < 4; i++) await flushPromises();
    expect(w.find('[data-test=deliverable-pane]').exists()).toBe(false);
    expect(w.find('[data-test=deliverables-view]').exists()).toBe(true);
    w.unmount();
  });

  it("the team's deliverables header opens the page at the team's deliverables, nothing selected", async () => {
    const w = mount(App, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=nav-monitor]').trigger('click');
    for (let i = 0; i < 4; i++) await flushPromises();
    await w.get('[data-test=monitor-deliverables-team]').trigger('click');
    await vi.waitFor(() => expect(w.find('[data-test=deliverables-view]').exists()).toBe(true));
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(w.find('[data-test=deliverables-view]').exists()).toBe(true);
    expect(document.querySelector('[aria-modal="true"]')).toBeNull();
    expect(teamFilter(w)).toBe('1');
    expect(selectedRows(w)).toEqual([]);
    expect(w.find('[data-test=deliverable-pane]').exists()).toBe(false);
    w.unmount();
  });

  it('selects whichever deliverable was clicked, whether or not it came from the backlog', async () => {
    const w = await clickMonitorDeliverable(77);
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
