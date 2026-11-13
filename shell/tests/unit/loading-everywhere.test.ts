// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import BoardView from '../../src/renderer/src/board/BoardView.vue';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import DocumentsBrowser from '../../src/renderer/src/components/DocumentsBrowser.vue';
import MonitorView from '../../src/renderer/src/monitor/MonitorView.vue';
import TriggersDialog from '../../src/renderer/src/components/TriggersDialog.vue';
import ConciergeView from '../../src/renderer/src/components/ConciergeView.vue';
import IntegrationDialog from '../../src/renderer/src/components/IntegrationDialog.vue';
import HealthPanel from '../../src/renderer/src/components/HealthPanel.vue';
import CardPanel from '../../src/renderer/src/board/CardPanel.vue';
import type { BacklogItem, YawbleApi, TeamSummary } from '../../src/shared/api';

// The two patterns everywhere: a LoadingNote where content will appear until the FIRST answer (an
// error replaces it; later polls never bring it back), and a busy button (disabled, spinner, "…ing") while its call
// runs. A fake core whose calls stay pending until the test answers them, unless listed in `auto`: no real core,
// program, network, port, credential store or browser.
type Auto = Record<string, unknown | ((params: any) => unknown)>;
function fakeCore(auto: Auto = {}) {
  const waiting = new Map<string, { resolve: (v: unknown) => void; reject: (e: unknown) => void }[]>();
  const calls: { method: string; params: any }[] = [];
  const api = {
    getInfo: async () => ({ hostChecks: [] }),
    getAvailableBackends: async () => [],
    call: (method: string, params: any = {}) => {
      calls.push({ method, params });
      if (method in auto) {
        const v = auto[method];
        try { return Promise.resolve(typeof v === 'function' ? (v as (p: any) => unknown)(params) : v); } catch (e) { return Promise.reject(e); }
      }
      return new Promise((resolve, reject) => {
        const list = waiting.get(method) ?? [];
        list.push({ resolve, reject });
        waiting.set(method, list);
      });
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
  async function answer(method: string, value: unknown) {
    const list = waiting.get(method);
    if (!list?.length) throw new Error(`nothing is waiting for ${method} (called: ${calls.map((c) => c.method).join(', ')})`);
    waiting.delete(method);
    for (const w of list) w.resolve(value);
    await flushPromises();
  }
  async function fail(method: string, message: string) {
    const list = waiting.get(method);
    if (!list?.length) throw new Error(`nothing is waiting for ${method}`);
    waiting.delete(method);
    for (const w of list) w.reject(new Error(message));
    await flushPromises();
  }
  /** Settles only the oldest call waiting for `method` (two loads of different subjects in flight). */
  async function settleOne(method: string, ok: boolean, value: unknown) {
    const list = waiting.get(method);
    if (!list?.length) throw new Error(`nothing is waiting for ${method}`);
    const w = list.shift()!;
    if (ok) w.resolve(value); else w.reject(new Error(String(value)));
    await flushPromises();
  }
  const count = (method: string) => calls.filter((c) => c.method === method).length;
  const isWaiting = (method: string) => (waiting.get(method)?.length ?? 0) > 0;
  return { api, calls, answer, fail, settleOne, count, isWaiting };
}

const notes = (w: VueWrapper) => w.findAll('[data-test=loading-note]').map((n) => n.text());
const tick = (ms: number) => new Promise((r) => setTimeout(r, ms));
/** A button's busy state: disabled, the shared spinner inside it, and its words. */
function busyOf(w: VueWrapper, sel: string) {
  const b = w.get(sel);
  return { disabled: b.attributes('disabled') !== undefined, spinner: b.find('[data-test=btn-spinner]').exists(), text: b.text() };
}

const team: TeamSummary = { id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 } as TeamSummary;
const lanes = [{ id: 'todo', label: 'To Do' }, { id: 'in-progress', label: 'In Progress' }, { id: 'blocked', label: 'Needs You' }, { id: 'done', label: 'Done' }];
const item = (over: Partial<BacklogItem> = {}): BacklogItem => ({
  id: 7, label: 'B007', team: null, title: 'Dark mode', body: '# Dark mode', state: 'ready', archivedAt: null, dispatchedTeam: null,
  dispatchedWorkflow: null, dispatchedAt: null, inFlight: false, createdAt: '2026-11-12T10:00:00Z', updatedAt: '2026-11-12T10:00:00Z', dispatchCount: 0, ...over,
});

let wrapper: VueWrapper | null = null;
afterEach(() => { wrapper?.unmount(); wrapper = null; });
function keep<T extends VueWrapper>(w: T) { wrapper = w; return w; }

describe('Backlog: first load', () => {
  it('shows the note until the list arrives; a poll after that does not bring it back', async () => {
    const f = fakeCore();
    const w = keep(mount(BacklogView, { props: { teams: [team], api: f.api, pollMs: 10 } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading the backlog…']);
    expect(w.text()).not.toContain('Nothing in the backlog');
    await f.answer('backlog.list', []);
    expect(notes(w)).toEqual([]);
    expect(w.text()).toContain('Nothing in the backlog');
    await tick(30); // the poll's backlog.list is now in flight, unanswered
    expect(f.isWaiting('backlog.list')).toBe(true);
    expect(notes(w)).toEqual([]);
    expect(w.text()).toContain('Nothing in the backlog');
  });

  it('a failed first load shows the error, not the note', async () => {
    const f = fakeCore();
    const w = keep(mount(BacklogView, { props: { teams: [team], api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    await f.fail('backlog.list', 'the core is restarting');
    expect(notes(w)).toEqual([]);
    expect(w.get('[data-test=backlog-error]').text()).toContain('the core is restarting');
  });
});

describe('Backlog: Dispatch', () => {
  async function opened() {
    const f = fakeCore({ 'backlog.list': [item()], 'backlog.item': { ...item(), dispatches: [] } });
    const w = keep(mount(BacklogView, { props: { teams: [team], api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    await w.get('[data-test=backlog-row]').trigger('click');
    await flushPromises();
    return { f, w };
  }

  it('is held with a spinner while backlog.dispatch runs (a second click sends nothing) and restores when it answers', async () => {
    const { f, w } = await opened();
    await w.get('[data-test=backlog-dispatch]').trigger('click');
    expect(busyOf(w, '[data-test=backlog-dispatch]')).toEqual({ disabled: true, spinner: true, text: 'Dispatching…' });
    await w.get('[data-test=backlog-dispatch]').trigger('click');
    expect(f.count('backlog.dispatch')).toBe(1);
    await f.answer('backlog.dispatch', item({ dispatchedTeam: 'payments', dispatchedAt: '2026-11-12T10:01:00Z', inFlight: true }));
    expect(w.get('[data-test=backlog-dispatch]').find('[data-test=btn-spinner]').exists()).toBe(false);
    expect(w.get('[data-test=backlog-dispatch]').text()).not.toBe('Dispatching…');
  });

  it('a failed dispatch restores the button and shows the error', async () => {
    const { f, w } = await opened();
    await w.get('[data-test=backlog-dispatch]').trigger('click');
    await f.fail('backlog.dispatch', 'the team is archived');
    expect(busyOf(w, '[data-test=backlog-dispatch]')).toEqual({ disabled: false, spinner: false, text: 'Dispatch' });
    expect(w.get('[data-test=backlog-error]').text()).toContain('the team is archived');
  });
});

describe('Board: first load', () => {
  it('shows the note until board.list answers; polls keep the lanes; an error replaces it', async () => {
    const f = fakeCore({ 'integrations.connectionAttention': [] });
    const w = keep(mount(BoardView, { props: { api: f.api, pollMs: 10 } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading the board…']);
    await f.answer('board.list', { lanes, cards: [], teams: [] });
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=lane-todo]').exists()).toBe(true);
    await tick(30);
    expect(f.isWaiting('board.list')).toBe(true);
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=lane-todo]').exists()).toBe(true);

    const g = fakeCore({ 'integrations.connectionAttention': [] });
    const failed = mount(BoardView, { props: { api: g.api, pollMs: 60_000 } });
    await flushPromises();
    await g.fail('board.list', 'no core');
    expect(notes(failed)).toEqual([]);
    expect(failed.get('[data-test=board-error]').text()).toContain('no core');
    failed.unmount();
  });
});

describe('Teams list (the app window)', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); });
  const quiet: Auto = { 'concierge.list': [], 'agents.detect': [], 'link.status': { state: 'off', viewers: [] }, 'companion.list': { pairings: [], actions: [], clients: [] }, 'bus.workflows': [] };

  it('shows the note until the first overview, then the grid; a card action is busy and a poll does not flash the note', async () => {
    const f = fakeCore(quiet);
    const w = keep(mount(App, { props: { api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    await w.get('[data-test=nav-teams]').trigger('click');
    expect(w.get('[data-test=loading]').text()).toBe('Loading teams (starting the core)…');
    expect(w.get('[data-test=sidebar]').text()).toContain('Loading teams…');
    await f.answer('app.overview', { slots: { running: 0, limit: 4, queued: 0 }, teams: [team] });
    expect(w.find('[data-test=loading]').exists()).toBe(false);
    expect(w.findAll('[data-test=team-card]')).toHaveLength(1);

    await w.get('[data-test=card-pause]').trigger('click');
    expect(busyOf(w, '[data-test=card-pause]')).toEqual({ disabled: true, spinner: true, text: 'Pausing…' });
    expect(w.get('[data-test=card-archive]').attributes('disabled')).toBeDefined(); // the others are held too
    await w.get('[data-test=card-pause]').trigger('click');
    expect(f.count('teams.pause')).toBe(1);
    await f.answer('teams.pause', { ok: true });
    // The refresh after the action is in flight: the grid stays, the button still waits for it.
    expect(f.isWaiting('app.overview')).toBe(true);
    expect(w.find('[data-test=loading]').exists()).toBe(false);
    await f.answer('app.overview', { slots: { running: 0, limit: 4, queued: 0 }, teams: [{ ...team, paused: true }] });
    expect(w.get('[data-test=card-resume]').text()).toBe('Resume');
    expect(w.get('[data-test=card-resume]').attributes('disabled')).toBeUndefined();
  });

  it('a failed first overview shows the error in place of the note', async () => {
    const f = fakeCore(quiet);
    const w = keep(mount(App, { props: { api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    await w.get('[data-test=nav-teams]').trigger('click');
    await f.fail('app.overview', 'the core did not start');
    expect(w.find('[data-test=loading]').exists()).toBe(false);
    expect(w.get('[data-test=teams-error]').text()).toContain('the core did not start');
  });
});

describe('a team page', () => {
  const quiet: Auto = { 'teams.syncToBase': { on: true, note: null, behind: null }, 'integrations.asks': [] };
  it('shows the note until its agents arrive; a failure shows the error; Tell Manager is busy while it sends', async () => {
    const f = fakeCore(quiet);
    const w = keep(mount(TeamView, { props: { team, api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading the team…']);
    expect(w.get('[data-test=kpi-agents]').text()).toContain('…');
    await f.answer('bus.decisions', []);
    expect(notes(w)).toEqual(['Loading the team…']); // teams.agents still pending
    await f.answer('teams.agents', []);
    expect(notes(w)).toEqual([]);
    expect(w.get('[data-test=kpi-agents]').text()).toContain('0 agents');

    await w.get('[data-test=tell-text]').setValue('Ship it');
    await w.get('[data-test=tell-send]').trigger('click');
    expect(busyOf(w, '[data-test=tell-send]')).toEqual({ disabled: true, spinner: true, text: 'Sending…' });
    await f.fail('bus.tellManager', 'the Manager is gone');
    expect(busyOf(w, '[data-test=tell-send]')).toEqual({ disabled: false, spinner: false, text: 'Tell Manager ✉' });
    expect(w.get('[data-test=team-error]').text()).toContain('the Manager is gone');
  });

  it('a failed first load replaces the note with the error', async () => {
    const f = fakeCore(quiet);
    const w = keep(mount(TeamView, { props: { team, api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    await f.fail('teams.agents', 'no such team');
    expect(notes(w)).toEqual([]);
    expect(w.get('[data-test=team-error]').text()).toContain('no such team');
  });
});

describe('Documents', () => {
  it('shows the note until docs.list answers, then the folder; an error replaces it', async () => {
    const f = fakeCore();
    const w = keep(mount(DocumentsBrowser, { props: { team, api: f.api } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading documents…']);
    await f.answer('docs.list', { path: '', entries: [] });
    expect(notes(w)).toEqual([]);
    expect(w.find('[data-test=docs-empty]').exists()).toBe(true);

    const g = fakeCore();
    const failed = mount(DocumentsBrowser, { props: { team, api: g.api } });
    await flushPromises();
    await g.fail('docs.list', 'the folder is gone');
    expect(notes(failed)).toEqual([]);
    expect(failed.get('[data-test=docs-error]').text()).toContain('the folder is gone');
    failed.unmount();
  });
});

describe('Monitor: first load', () => {
  const rest: Auto = { 'monitor.messages': { end: 0, messages: [] }, 'monitor.steps': { end: 0, pending: false, steps: [] }, 'teams.agents': [], 'bus.workflows': [] };
  it('shows the note on the wall and in the activity until the first tick ends', async () => {
    const f = fakeCore(rest);
    const w = keep(mount(MonitorView, { props: { api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    expect(w.find('[data-test=monitor-loading]').exists()).toBe(true);
    expect(notes(w)).toContain('Loading activity…');
    await f.answer('app.overview', { slots: { running: 0, limit: 4, queued: 0 }, teams: [] });
    for (let i = 0; i < 5; i++) await flushPromises();
    expect(w.find('[data-test=monitor-loading]').exists()).toBe(false);
    expect(notes(w)).toEqual([]);
    expect(w.text()).toContain('No teams yet');
  });

  it('a failed first tick shows the error, not the note', async () => {
    const f = fakeCore(rest);
    const w = keep(mount(MonitorView, { props: { api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    await f.fail('app.overview', 'the core is down');
    for (let i = 0; i < 3; i++) await flushPromises();
    expect(w.find('[data-test=monitor-loading]').exists()).toBe(false);
    expect(w.text()).toContain('the core is down');
    expect(w.text()).not.toContain('No teams yet');
  });
});

describe('a Save button in a dialog (Triggers)', () => {
  const quiet: Auto = { 'triggers.list': [], 'integrations.watches': [], 'integrations.list': { integrations: [] }, 'triggers.preview': { error: null, next: [] } };
  async function editing() {
    const f = fakeCore(quiet);
    const agents = [{ member: { id: 2, teamId: 1, team: 'payments', name: 'Manager', role: 'manager', cli: 'claude' } }];
    const w = keep(mount(TriggersDialog, { props: { team, agents: agents as any, api: f.api } }));
    await flushPromises();
    await w.get('[data-test=trigger-new]').trigger('click');
    await w.get('[data-test=trigger-name]').setValue('nightly');
    await w.get('[data-test=trigger-instruction]').setValue('Run the tests');
    return { f, w };
  }

  it('Save is held with "Saving…" while triggers.save runs, and the form closes when it answers', async () => {
    const { f, w } = await editing();
    await w.get('[data-test=trigger-save]').trigger('click');
    expect(busyOf(w, '[data-test=trigger-save]')).toEqual({ disabled: true, spinner: true, text: 'Saving…' });
    await w.get('[data-test=trigger-save]').trigger('click');
    expect(f.count('triggers.save')).toBe(1);
    await f.answer('triggers.save', { id: 1, name: 'nightly', nextDueAt: null });
    expect(w.find('[data-test=trigger-save]').exists()).toBe(false);
    expect(w.get('[data-test=triggers-note]').text()).toContain('Saved nightly');
  });

  it('a failed save restores the button and shows the error', async () => {
    const { f, w } = await editing();
    await w.get('[data-test=trigger-save]').trigger('click');
    await f.fail('triggers.save', 'cron is not valid');
    expect(busyOf(w, '[data-test=trigger-save]')).toEqual({ disabled: false, spinner: false, text: 'Save' });
    expect(w.get('[data-test=triggers-error]').text()).toContain('cron is not valid');
  });
});

describe('Concierge: Send', () => {
  const agents = [{ id: 'claude', label: 'Claude', ready: true }];
  const props = (api: YawbleApi, conversationId: string | null = null) => ({ conversationId, teams: [team], agents, defaultCli: 'claude', focusTeamId: null, api });

  it('shows the person\'s message and the thinking state between Send and the first token; a failure shows the error', async () => {
    const f = fakeCore();
    const w = keep(mount(ConciergeView, { props: props(f.api) }));
    await w.get('[data-test=composer-text]').setValue('What is payments doing?');
    await w.get('[data-test=composer-send]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=concierge-thread]').text()).toContain('What is payments doing?');
    expect(w.find('[data-test=chat-thinking]').exists()).toBe(true);
    expect(w.find('[data-test=composer-send]').exists()).toBe(false); // Send became Stop: no second send
    await f.fail('concierge.send', 'no agent is signed in');
    expect(w.find('[data-test=chat-thinking]').exists()).toBe(false);
    expect(w.text()).toContain('no agent is signed in');
  });

  it('the core\'s answer to Send opens the chat (the turn keeps running)', async () => {
    const f = fakeCore();
    const w = keep(mount(ConciergeView, { props: props(f.api) }));
    await w.get('[data-test=composer-text]').setValue('Hi');
    await w.get('[data-test=composer-send]').trigger('click');
    await f.answer('concierge.send', { id: 'c1' });
    expect(w.emitted('opened')).toEqual([['c1']]);
  });

  it('an existing chat shows the note until concierge.get answers', async () => {
    const f = fakeCore({ 'concierge.read': {} });
    const w = keep(mount(ConciergeView, { props: props(f.api, 'c9') }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading the chat…']);
    await f.answer('concierge.get', { id: 'c9', title: 'Old', cli: 'claude', teamId: null, unread: 0, turn: null, watches: [],
      messages: [{ id: 1, role: 'user', text: 'Earlier question', at: '', state: null, error: null, tools: [], cli: null }] });
    expect(notes(w)).toEqual([]);
    expect(w.text()).toContain('Earlier question');
  });
});

describe('Integrations dialog: load', () => {
  it('shows the note until the integration arrives; an error replaces it', async () => {
    const f = fakeCore({ 'integrations.discoverStatus': { state: 'idle' } });
    const w = keep(mount(IntegrationDialog, { props: { api: f.api, kind: 'cli', name: 'gh', teams: [team] } }));
    await flushPromises();
    expect(notes(w)).toContain('Loading the CLI…');
    expect(w.find('[data-test=dialog-status]').text()).not.toContain('sign-in not checked yet');
    await f.answer('integrations.list', { integrations: [{ name: 'gh', label: 'GitHub CLI', path: '/usr/bin/gh', status: 'signed in', all: true, teams: [], configured: true }] });
    expect(notes(w)).not.toContain('Loading the CLI…');
    expect(w.get('[data-test=dialog-status]').text()).toContain('signed in');

    const g = fakeCore({ 'integrations.discoverStatus': { state: 'idle' } });
    const failed = mount(IntegrationDialog, { props: { api: g.api, kind: 'cli', name: 'gh', teams: [team] } });
    await flushPromises();
    await g.fail('integrations.list', 'the core is busy');
    expect(notes(failed)).not.toContain('Loading the CLI…');
    expect(failed.get('[data-test=dialog-error]').text()).toContain('the core is busy');
    failed.unmount();
  });
});

describe('Health: actions', () => {
  const checks = [{ id: 'orphans', title: 'Leftover sandboxes', status: 'warn', detail: '2 left', fix: null, fixes: [{ id: 'clean', label: 'Clean up', kind: 'run', confirm: false }] }];

  it('Run again shows "Checking…" while ops.doctor runs and restores after', async () => {
    const f = fakeCore();
    const w = keep(mount(HealthPanel, { props: { api: f.api } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading health checks…']);
    await f.answer('ops.doctor', { checks });
    expect(notes(w)).toEqual([]);
    await w.get('[data-test=health-run]').trigger('click');
    expect(busyOf(w, '[data-test=health-run]')).toEqual({ disabled: true, spinner: true, text: 'Checking…' });
    expect(notes(w)).toEqual([]); // the checks stay while they are read again
    await f.answer('ops.doctor', { checks });
    expect(busyOf(w, '[data-test=health-run]')).toEqual({ disabled: false, spinner: false, text: 'Run again' });
  });

  it('a fix is held with "Fixing…" while ops.doctorFix runs; a failure restores it and shows the error', async () => {
    const f = fakeCore();
    const w = keep(mount(HealthPanel, { props: { api: f.api } }));
    await flushPromises();
    await f.answer('ops.doctor', { checks });
    await w.get('[data-test=health-fix]').trigger('click');
    expect(busyOf(w, '[data-test=health-fix]')).toEqual({ disabled: true, spinner: true, text: 'Fixing…' });
    await w.get('[data-test=health-fix]').trigger('click');
    expect(f.count('ops.doctorFix')).toBe(1);
    await f.fail('ops.doctorFix', 'a sandbox is still running');
    expect(busyOf(w, '[data-test=health-fix]')).toEqual({ disabled: false, spinner: false, text: 'Clean up' });
    expect(w.get('[data-test=health-error]').text()).toContain('a sandbox is still running');
  });
});

// ---- review fixes: an answer or an error about one subject never lands on another
describe('review fixes: per-subject results', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); });

  it('a team page action started on team A does not show or land on team B', async () => {
    const teamB = { ...team, id: 2, name: 'search' } as TeamSummary;
    const f = fakeCore({
      'concierge.list': [], 'agents.detect': [], 'link.status': { state: 'off', viewers: [] }, 'companion.list': { pairings: [], actions: [], clients: [] },
      'bus.workflows': [], 'app.overview': { slots: { running: 0, limit: 4, queued: 0 }, teams: [team, teamB] },
      'teams.agents': [], 'bus.decisions': [], 'integrations.asks': [], 'teams.syncToBase': { on: true, note: null, behind: null },
    });
    const w = keep(mount(App, { props: { api: f.api, pollMs: 60_000 } }));
    await flushPromises();
    const items = w.findAll('[data-test=team-item]');
    await items[0]!.trigger('click');
    await flushPromises();
    await w.get('[data-test=tell-text]').setValue('For payments');
    await w.get('[data-test=tell-send]').trigger('click');
    expect(w.get('[data-test=tell-send]').text()).toBe('Sending…');

    await w.findAll('[data-test=team-item]')[1]!.trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-title]').text()).toContain('search');
    expect(busyOf(w, '[data-test=tell-send]')).toEqual({ disabled: true, spinner: false, text: 'Tell Manager ✉' }); // disabled: empty box
    await w.get('[data-test=tell-text]').setValue('For search');
    expect(w.get('[data-test=tell-send]').attributes('disabled')).toBeUndefined();

    await f.answer('bus.tellManager', { workflow: 9 });
    expect((w.get('[data-test=tell-text]').element as HTMLInputElement).value).toBe('For search');
    expect(w.find('[data-test=tell-sent]').exists()).toBe(false);
    expect(f.calls.find((c) => c.method === 'bus.tellManager')!.params.teamId).toBe(1);
  });

  it('Concierge: a failed load of the chat the person left shows no error on the one they opened', async () => {
    const f = fakeCore({ 'concierge.read': {} });
    const agents = [{ id: 'claude', label: 'Claude', ready: true }];
    const w = keep(mount(ConciergeView, { props: { conversationId: 'a', teams: [team], agents, defaultCli: 'claude', focusTeamId: null, api: f.api } }));
    await flushPromises();
    await w.setProps({ conversationId: 'b' });
    await flushPromises();
    await f.settleOne('concierge.get', false, 'chat a is gone');
    expect(w.text()).not.toContain('chat a is gone');
    expect(notes(w)).toEqual(['Loading the chat…']);
    await f.settleOne('concierge.get', true, { id: 'b', title: 'B', cli: 'claude', teamId: null, unread: 0, turn: null, watches: [],
      messages: [{ id: 1, role: 'user', text: 'Question b', at: '', state: null, error: null, tools: [], cli: null }] });
    expect(w.text()).toContain('Question b');
    // and its own failure still shows
    const g = fakeCore();
    const failed = mount(ConciergeView, { props: { conversationId: 'c', teams: [team], agents, defaultCli: 'claude', focusTeamId: null, api: g.api } });
    await flushPromises();
    await g.fail('concierge.get', 'chat c is gone');
    expect(failed.text()).toContain('chat c is gone');
    failed.unmount();
  });

  it('Card panel: a Move answered after the person opened another card leaves that card and its form alone', async () => {
    const card = (id: number, title: string) => ({ id, team: 'payments', teamId: 1, member: null, item: null, title, body: '', status: 'queued', lane: 'todo',
      placed: false, awaitingManager: false, workflow: null, createdAt: '', updatedAt: '', trailCount: 0, trail: [] });
    const f = fakeCore();
    const w = keep(mount(CardPanel, { props: { id: 1, lanes, api: f.api } }));
    await flushPromises();
    expect(notes(w)).toEqual(['Loading the card…']);
    expect(w.text()).not.toContain('Loading…Loading');
    await f.answer('board.card', card(1, 'Card one'));
    await w.get('[data-test=card-note]').setValue('move it');
    await w.get('[data-test=card-move]').trigger('click');
    expect(w.get('[data-test=card-move]').text()).toBe('Moving…');

    await w.setProps({ id: 2 });
    await flushPromises();
    await f.answer('board.card', card(2, 'Card two'));
    expect(w.get('[data-test=card-move]').text()).toBe('Move'); // card two is not held
    await w.get('[data-test=card-note]').setValue('a note for two');
    await f.answer('board.move', { ...card(1, 'Card one'), lane: 'done' });
    expect(w.text()).toContain('Card two');
    expect(w.text()).not.toContain('Card one');
    expect((w.get('[data-test=card-note]').element as HTMLInputElement).value).toBe('a note for two');
    expect(w.emitted('changed')).toHaveLength(1);
  });

  it('Health: a Fix finished while Run again runs still reloads the checks after it', async () => {
    const checks = [{ id: 'orphans', title: 'Leftover sandboxes', status: 'warn', detail: '2 left', fix: null, fixes: [{ id: 'clean', label: 'Clean up', kind: 'run', confirm: false }] }];
    const f = fakeCore();
    const w = keep(mount(HealthPanel, { props: { api: f.api } }));
    await flushPromises();
    await f.answer('ops.doctor', { checks });
    await w.get('[data-test=health-run]').trigger('click'); // ops.doctor #2 in flight
    await w.get('[data-test=health-fix]').trigger('click');
    await f.answer('ops.doctorFix', { done: true, needsConfirm: false, message: 'Cleaned.' }); // asks for a reload while #2 runs
    expect(f.count('ops.doctor')).toBe(2);
    await f.answer('ops.doctor', { checks });
    expect(f.count('ops.doctor')).toBe(3); // the queued reload
    await f.answer('ops.doctor', { checks: [] });
    expect(w.findAll('[data-test=health-check]')).toHaveLength(0);
    expect(w.get('[data-test=health-run]').text()).toBe('Run again');
  });

  it('Health: Cancel stays usable while Yes runs; the answer then shows as a message', async () => {
    const checks = [{ id: 'keys', title: 'Old keys', status: 'warn', detail: '1 left', fix: null, fixes: [{ id: 'drop', label: 'Remove them', kind: 'run', confirm: true }] }];
    const f = fakeCore();
    const w = keep(mount(HealthPanel, { props: { api: f.api } }));
    await flushPromises();
    await f.answer('ops.doctor', { checks });
    await w.get('[data-test=health-fix]').trigger('click');
    await f.answer('ops.doctorFix', { done: false, needsConfirm: true, message: 'Remove 1 key?', targets: ['old'] });
    await w.get('[data-test=health-confirm-yes]').trigger('click');
    expect(w.get('[data-test=health-confirm-yes]').text()).toBe('Working…');
    expect(w.get('[data-test=health-confirm-no]').attributes('disabled')).toBeUndefined();
    await w.get('[data-test=health-confirm-no]').trigger('click');
    expect(w.find('[data-test=health-confirm]').exists()).toBe(false);
    await f.answer('ops.doctorFix', { done: true, needsConfirm: false, message: 'Removed 1 key.' });
    expect(w.get('[data-test=health-message]').text()).toBe('Removed 1 key.');
    expect(w.find('[data-test=health-confirm]').exists()).toBe(false);
  });
});
