// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
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

function api(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, p?: any) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 }] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'bus.workflows': return workflows as any;
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
  for (let i = 0; i < 6; i++) await flushPromises();
  return w;
}

describe('a workflow clicked on the Monitor', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); document.body.innerHTML = ''; });

  it("opens its team's workflows with that workflow selected", async () => {
    const w = await clickMonitorWorkflow(413);
    const sel = w.findAll('[data-test^=workflow-]').filter((e) => e.classes().includes('sel'));
    expect(sel.map((e) => e.attributes('data-test'))).toEqual(['workflow-413']);
    expect(w.get('[data-test=workflow-413]').attributes('aria-current')).toBe('true');
    expect(w.find('[data-test=backlog-row]').exists()).toBe(false);
    w.unmount();
  });

  it('selects whichever workflow was clicked, whether or not it came from the backlog', async () => {
    const w = await clickMonitorWorkflow(77);
    expect(w.get('[data-test=workflow-77]').classes()).toContain('sel');
    expect(w.get('[data-test=workflow-413]').classes()).not.toContain('sel');
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
