// @vitest-environment happy-dom
// The Workflows page opened at any workflow (openWorkflowsPage({ teamId, workflow })): one among the loaded rows is
// selected with its pane open; one outside the rows (older than the newest the core lists) is read with workflows.get
// and opened the same way; one the core does not know is named in a note, with no pane. And the embedded window
// (the cloud page's frame) reports the page in yawble.nav once a Monitor workflow node opens it. Fakes only.
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkflowRow, YawbleApi } from '../../src/shared/api';
import WorkflowsView from '../../src/renderer/src/workflows/WorkflowsView.vue';
import { workflowsRequest } from '../../src/renderer/src/workflows/page';

function row(id: number, p: Partial<WorkflowRow> = {}): WorkflowRow {
  return {
    id, title: `Workflow ${id}`, owner: 'Manager', ownerId: 1, state: 'open', undeclared: false, busy: [], ending: null, startedAt: '2026-11-19T08:00:00Z',
    lastAt: '2026-11-19T09:00:00Z', messages: 3, endedAt: null, expiredAt: null, expiresAt: null, expiryWarning: null, question: null, time: null,
    team: 'payments', teamId: 1, waitingFor: null, working: false, item: null, members: [], status: 'idle', ...p,
  } as WorkflowRow;
}
/** What the list holds (the newest), and an old completed one the core still knows but does not list. */
const listed = [row(30), row(31, { team: 'billing', teamId: 2 }), row(32, { status: 'completed', state: 'completed', endedAt: '2026-11-19T09:30:00Z' })];
const old = row(4, { title: 'An old one', status: 'completed', state: 'completed', endedAt: '2026-10-01T09:00:00Z' });

function fake() {
  const calls: { method: string; params: any }[] = [];
  const api = {
    call: async (method: string, params: any) => {
      calls.push({ method, params });
      if (method === 'workflows.list') return { workflows: listed.filter((w) => params?.teamId === undefined || w.teamId === params.teamId), counts: { all: 3, open: 2, needsYou: 0, done: 1, closed: 0, completedToday: 0 }, truncated: true, backlog: true, now: '' };
      if (method === 'workflows.get') {
        const w = [...listed, old].find((x) => x.id === params.workflow);
        if (!w) throw Object.assign(new Error(`there is no workflow ${params.workflow}`), { code: 'no_such_workflow' });
        return { workflow: w, cards: [], messages: [], spec: null, backlog: true };
      }
      return {};
    },
  } as unknown as YawbleApi;
  return { api, calls, gets: () => calls.filter((c) => c.method === 'workflows.get').map((c) => c.params.workflow) };
}
async function page(f: ReturnType<typeof fake>, request: { teamId?: number | null; workflow?: number | null }) {
  const w = mount(WorkflowsView, { props: { teams: [{ id: 1, name: 'payments' }, { id: 2, name: 'billing' }] as any, api: f.api, pollMs: 600_000, request: { ...request, seq: 1 } }, attachTo: document.body });
  for (let i = 0; i < 6; i++) await flushPromises();
  return w;
}
const selectedRows = (w: Awaited<ReturnType<typeof page>>) => w.findAll('[data-test=workflows-row]').filter((r) => r.classes().includes('sel')).map((r) => Number(r.attributes('data-wf')));

enableAutoUnmount(afterEach);
beforeEach(() => { localStorage.clear(); history.replaceState(null, '', '/'); workflowsRequest.value = null; });

describe('the Workflows page opened at a workflow', () => {
  it('one among the loaded rows: selected, its pane open, its team the filter, All; no extra read for the row', async () => {
    const f = fake();
    const w = await page(f, { teamId: 1, workflow: 32 });
    expect(selectedRows(w)).toEqual([32]);
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('32');
    expect((w.get('[data-test=workflows-team-filter]').element as HTMLSelectElement).value).toBe('1');
    expect(w.get('[data-test=workflows-chip-all]').attributes('aria-selected')).toBe('true');
    expect(f.gets().every((id) => id === 32)).toBe(true); // the pane's own reads only
    expect(w.find('[data-test=workflows-not-found]').exists()).toBe(false);
  });

  it('an old one outside the rows: read with workflows.get, shown as a row, selected, its pane open', async () => {
    const f = fake();
    const w = await page(f, { teamId: 1, workflow: 4 });
    expect(f.gets()).toContain(4);
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('4');
    expect(selectedRows(w)).toEqual([4]);
    expect(w.get('[data-wf="4"] [data-test=workflows-title]').text()).toBe('An old one');
    expect(w.find('[data-test=workflows-not-found]').exists()).toBe(false);
  });

  it('one the core does not know: "Workflow #N was not found", no pane, nothing selected; choosing a row clears the note', async () => {
    const f = fake();
    const w = await page(f, { teamId: null, workflow: 999 });
    expect(w.get('[data-test=workflows-not-found]').text()).toContain('Workflow #999 was not found.');
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false);
    expect(selectedRows(w)).toEqual([]);
    expect(location.hash).not.toContain('sel=');
    await w.get('[data-wf="30"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=workflows-not-found]').exists()).toBe(false);
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('30');
  });

  it('a read that fails otherwise says so, with no pane', async () => {
    const f = fake();
    const call = f.api.call;
    (f.api as any).call = async (m: string, p: any) => { if (m === 'workflows.get') throw new Error('core unavailable'); return call(m, p); };
    const w = await page(f, { workflow: 4 });
    expect(w.get('[data-test=workflows-not-found]').text()).toContain('Workflow #4 could not be loaded');
    expect(w.find('[data-test=workflow-pane]').exists()).toBe(false);
  });

  it('a later request at another workflow, while the page is shown, opens that one', async () => {
    const f = fake();
    const w = await page(f, { workflow: 999 });
    expect(w.find('[data-test=workflows-not-found]').exists()).toBe(true);
    await w.setProps({ request: { teamId: 1, workflow: 4, seq: 2 } });
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(w.find('[data-test=workflows-not-found]').exists()).toBe(false);
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('4');
  });
});

// ---- embedded in the cloud page: the bridge's yawble.nav follows the page ----

describe('embedded: a Monitor workflow node opens the page and yawble.nav reports it', { timeout: 30_000 }, () => {
  const realParent = Object.getOwnPropertyDescriptor(window, 'parent');
  afterEach(() => {
    document.body.innerHTML = '';
    delete (window as any).yawble;
    vi.unstubAllGlobals();
    if (realParent) Object.defineProperty(window, 'parent', realParent); else delete (window as any).parent;
    history.replaceState(null, '', '/');
  });

  it('the Workflows place is active after the click, Monitor not; no dialog', async () => {
    vi.resetModules();
    history.replaceState(null, '', '/remote.html?embed=1');
    vi.stubGlobal('matchMedia', (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {} }));
    const posted: any[] = [];
    const parent = { postMessage: (msg: unknown) => posted.push(JSON.parse(JSON.stringify(msg))) };
    Object.defineProperty(window, 'parent', { configurable: true, get: () => parent });
    (window as any).yawble = { remote: true };
    const summary = (id: number) => ({ id, title: `Workflow ${id}`, owner: 'Manager', state: 'open', undeclared: false, busy: [], ending: null, startedAt: '', lastAt: '2026-11-19T09:00:00Z', messages: 2 });
    const api = {
      remote: true,
      getInfo: async () => { throw new Error('no'); },
      getAvailableBackends: async () => [],
      call: async (method: string, p?: any) => {
        switch (method) {
          case 'app.overview': return { slots: { running: 0, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'payments', archived: false, running: 0, queued: 0, members: 1 }] };
          case 'bus.workflows': return [summary(30)];
          case 'workflows.list': return { workflows: [row(30)], counts: { all: 1, open: 1, needsYou: 0, done: 0, closed: 0, completedToday: 0 }, truncated: false, backlog: true, now: '' };
          case 'workflows.get': return { workflow: row(p.workflow), cards: [], messages: [], spec: null, backlog: true };
          case 'monitor.messages': return { end: 0, messages: [] };
          case 'monitor.steps': return { end: 0, pending: false, steps: [] };
          default: return [];
        }
      },
      onEvent: () => () => {},
      openTerminal: async () => {},
    } as unknown as YawbleApi;
    const App = (await import('../../src/renderer/src/App.vue')).default;
    const w = mount(App, { props: { api, pollMs: 60_000 }, attachTo: document.body });
    await flushPromises();
    const active = () => posted.filter((m) => m.type === 'yawble.nav').at(-1)?.nav.places.filter((p: any) => p.active).map((p: any) => p.id);
    window.dispatchEvent(new MessageEvent('message', { data: { type: 'yawble.navigate', to: { kind: 'place', id: 'monitor' } }, origin: location.origin, source: parent as any }));
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(active()).toEqual(['monitor']);
    await w.get('[data-test=monitor-workflow][data-id="30"]').trigger('click');
    await vi.waitFor(() => expect(w.find('[data-test=workflows-view]').exists()).toBe(true));
    for (let i = 0; i < 6; i++) await flushPromises();
    expect(active()).toEqual(['workflows']);
    expect(document.querySelector('[aria-modal="true"]')).toBeNull();
    expect(w.get('[data-test=workflow-pane]').attributes('data-id')).toBe('30');
    w.unmount();
  });
});
