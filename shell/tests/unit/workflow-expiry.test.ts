// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { BoardCard, TeamSummary, WorkflowSummary, YawbleApi } from '../../src/shared/api';
import { workflowTime } from '../../src/shared/api';
import { statusTip } from '../../src/renderer/src/workflows/page';
import { mountPane, paneCore, paneRow } from './workflow-pane-fake';
import { stopConfirmStubs, stubConfirm } from './confirm-stub';
import TeamWorkflows from '../../src/renderer/src/components/TeamWorkflows.vue';
import WorkflowExpiryAsks from '../../src/renderer/src/components/WorkflowExpiryAsks.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import { entryTime, workflowEnds, workflowLook, WorkflowLooks } from '../../src/renderer/src/monitor/workflows';
import { MonitorModel, workflowKey } from '../../src/renderer/src/monitor/model';
import { expiresSoon, expiryWarningText, until } from '../../src/renderer/src/workflowExpiry';
import { applyRemoteAccess, READ_ONLY_TITLE } from '../../src/renderer/src/remoteAccess';

// An expired workflow reads "expired" (not completed, not closed) with its time from endedAt; an open
// one that waits on the person's answer and expires within a day is in Needs you with Extend (bus.workflowExtend);
// Team settings edits workflowExpiryDays (teams.workflowExpiry). Against the core's contract; fakes only.

const hours = (h: number) => new Date(Date.now() + h * 3600_000).toISOString();
const team = { id: 1, name: 'payments' } as TeamSummary;
const base = { owner: 'Manager', undeclared: false, busy: [] as string[], ending: null, startedAt: hours(-200), messages: 3, endedAt: null, expiredAt: null, expiresAt: null };
const expired: WorkflowSummary = { ...base, id: 9, title: 'Old export', state: 'expired', ending: 'expired: no activity for 3 days',
  lastAt: hours(-2), endedAt: hours(-96), expiredAt: hours(-24) };
const completed: WorkflowSummary = { ...base, id: 8, title: 'Done thing', state: 'completed', lastAt: hours(-50), endedAt: hours(-50) };
const closed: WorkflowSummary = { ...base, id: 7, title: 'Dropped thing', state: 'closed', lastAt: hours(-60), endedAt: hours(-60) };
const expiring: WorkflowSummary = { ...base, id: 12, title: 'Refund limits', state: 'open', undeclared: true, lastAt: hours(-52), expiresAt: hours(20), question: 3 };

function fakeApi(answers: Record<string, (p: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api = {
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a(params);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  return { api, calls };
}

afterEach(() => { applyRemoteAccess('full'); stopConfirmStubs(); document.body.innerHTML = ''; });

// The same workflows as rows of the Workflows page (workflows.list), for its pane.
const expiredRow = paneRow({ id: 9, title: 'Old export', status: 'expired', ending: expired.ending, lastAt: expired.lastAt, endedAt: expired.endedAt, expiredAt: expired.expiredAt });
const expiringRow = paneRow({ id: 12, title: 'Refund limits', status: 'idle', undeclared: true, lastAt: expiring.lastAt, expiresAt: expiring.expiresAt });

describe('the "expired" state', () => {
  it('the monitor looks give expired its own status, label and icon, distinct from completed and closed', () => {
    expect(workflowLook(expired).status).toBe('expired');
    expect(workflowLook(expired).label).toBe('Expired');
    expect(workflowLook(expired).icon).toBe('clock');
    expect(workflowLook(completed).status).toBe('completed');
    expect(workflowLook(closed).status).toBe('closed');
    expect(WorkflowLooks.map((l) => l.status)).toContain('expired');
  });

  it('an ended workflow\'s time is its endedAt (an expired one: its last activity, not the expiry); an open one its lastAt', () => {
    expect(workflowTime(expired)).toBe(expired.endedAt);
    expect(workflowTime(expiring)).toBe(expiring.lastAt);
    expect(workflowTime({ state: 'closed', lastAt: 'x', endedAt: undefined })).toBe('x'); // an older core: no endedAt
  });

  it('the Workflows page\'s pane reads "Expired", with the expiry in its activity; no actions on it', async () => {
    const rows = [expiredRow, paneRow({ id: 8, status: 'completed' }), paneRow({ id: 7, status: 'closed' })];
    const core = paneCore(() => rows, { messages: { 9: [{ seq: 4, at: expired.expiredAt!, kind: 'expired', from: 'host', member: null, text: 'no activity for 3 days' }] } });
    const w = await mountPane(expiredRow, core.api);
    expect(w.get('[data-test=workflow-pane-status]').text()).toBe('Expired');
    expect(w.get('[data-test=workflow-pane-status]').attributes('title')).toBe('Expired');
    const m = w.get('[data-test=workflow-pane-message]');
    expect(m.attributes('data-kind')).toBe('expired');
    expect(m.text()).toContain('The app expired');
    expect(m.text()).toContain('no activity for 3 days');
    expect(w.find('[data-test=workflow-pane-actions]').exists()).toBe(false);
    w.unmount();
    for (const [id, word] of [[8, 'Completed'], [7, 'Closed']] as const) {
      const p = await mountPane(rows.find((r) => r.id === id)!, core.api);
      expect(p.get('[data-test=workflow-pane-status]').text()).toBe(word);
      expect(p.find('[data-test=workflow-pane-actions]').exists()).toBe(false);
      p.unmount();
    }
    expect(core.methods()).toEqual([]);
  });

  it('the Teams-list tile row is marked expired, reads "expired", and shows the endedAt time', async () => {
    const { api } = fakeApi({ 'bus.workflows': () => [expired, completed] });
    const w = mount(TeamWorkflows, { props: { teamId: 1, api, pollMs: 60_000 } });
    await flushPromises();
    const row = w.get('[data-test=team-workflow][data-id="9"]');
    expect(row.attributes('data-status')).toBe('expired');
    expect(row.classes()).toContain('expired');
    expect(row.get('[data-test=team-workflow-expired]').text()).toContain('expired');
    expect(row.attributes('title')).toMatch(/^Expired · #9/);
    expect(row.get('[data-test=team-workflow-time]').text()).toBe(entryTime(expired.endedAt!));
    expect(w.get('[data-test=team-workflow][data-id="8"]').attributes('data-status')).toBe('completed');
    w.unmount();
  });

  it('a workflow node on the Monitor that expires leaves with an "expired" wfdone effect, not "closed"', () => {
    const open = { ...expired, state: 'open' as const, endedAt: null, expiredAt: null };
    expect(workflowEnds([open, { ...closed, state: 'open' }], [expired, closed])).toEqual([{ id: 9, status: 'expired' }, { id: 7, status: 'closed' }]);
    const m = new MonitorModel();
    m.setWorkflows('payments', [open], 0);
    m.effects.length = 0;
    m.setWorkflows('payments', [expired], 10);
    expect(m.effects).toEqual([{ kind: 'wfdone', key: workflowKey('payments', 9), team: 'payments', status: 'expired', at: 10 }]);
  });
});

describe('Extend', () => {
  it('the Needs-you item reads "#12 will expire tomorrow; it waits on your answer to question 3" and Extend calls bus.workflowExtend', async () => {
    let list = [expiring, expired];
    const { api, calls } = fakeApi({
      'bus.workflows': () => list,
      'bus.workflowExtend': (p) => { list = list.map((x) => (x.id === p.workflow ? { ...x, expiresAt: null } : x)); return {}; },
    });
    const cards = [{ id: 4, team: 'payments', teamId: 1, status: 'needs-decision', workflow: 12 } as BoardCard];
    const w = mount(WorkflowExpiryAsks, { props: { api, cards, pollMs: 60_000 } });
    await flushPromises();
    expect(w.emitted('count')!.at(-1)).toEqual([1]);
    expect(w.get('[data-test=workflow-expiry-text]').text()).toBe('#12 will expire tomorrow; it waits on your answer to question 3');
    expect(w.find('[data-test=workflow-expiry-9]').exists()).toBe(false);
    await w.get('[data-test=workflow-expiry-extend]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'bus.workflowExtend')!.params).toEqual({ teamId: 1, workflow: 12 });
    expect(w.find('[data-test=workflow-expiry-12]').exists()).toBe(false);
    expect(w.emitted('count')!.at(-1)).toEqual([0]);
    w.unmount();
  });

  it('no item without a question waiting (no needs-decision card, no question), nor one expiring later than a day', async () => {
    const quiet = { ...expiring, question: null };
    const later = { ...expiring, id: 13, expiresAt: hours(40) };
    const { api } = fakeApi({ 'bus.workflows': () => [quiet, later] });
    const cards = [{ id: 5, team: 'payments', teamId: 1, status: 'needs-decision', workflow: 13 } as BoardCard];
    const w = mount(WorkflowExpiryAsks, { props: { api, cards, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=workflow-expiry-asks]').exists()).toBe(false);
    w.unmount();
  });

  it('Extend is read-only for a Visitor: disabled with the read-only title, nothing called', async () => {
    applyRemoteAccess('read');
    const { api, calls } = fakeApi({ 'bus.workflows': () => [expiring] });
    const cards = [{ id: 4, team: 'payments', teamId: 1, status: 'needs-decision', workflow: 12 } as BoardCard];
    const w = mount(WorkflowExpiryAsks, { props: { api, cards, pollMs: 60_000 } });
    await flushPromises();
    const b = w.get('[data-test=workflow-expiry-extend]').element as HTMLButtonElement;
    expect(b.disabled).toBe(true);
    expect(b.title).toBe(READ_ONLY_TITLE);
    expect(calls.some((c) => c.method === 'bus.workflowExtend')).toBe(false);
    w.unmount();
  });

  it('the Workflows page\'s pane shows when an expiring workflow expires, with Extend (asked first) calling bus.workflowExtend', async () => {
    const asked = stubConfirm(() => true);
    const core = paneCore(() => [expiringRow]);
    const w = await mountPane(expiringRow, core.api);
    expect(w.get('[data-test=workflow-pane-status]').attributes('title')).toMatch(/^Open, nobody is working it\. Expires in 19h 5\dm if nothing happens in it$/);
    expect(statusTip(expiringRow)).toContain('Expires in 19h');
    await w.get('[data-test=workflow-pane-extend]').trigger('click');
    await flushPromises();
    expect(asked).toHaveBeenCalledWith('Extend #12? Its expiry clock starts again.');
    expect(core.calls.find((c) => c.method === 'bus.workflowExtend')!.params).toEqual({ teamId: 1, workflow: 12 });
    w.unmount();
  });

  it('Extend asked and declined calls nothing', async () => {
    stubConfirm(() => false);
    const core = paneCore(() => [expiringRow]);
    const w = await mountPane(expiringRow, core.api);
    await w.get('[data-test=workflow-pane-extend]').trigger('click');
    await flushPromises();
    expect(core.methods()).toEqual([]);
    w.unmount();
  });

  it('an Extend refused with already_ended says it has already ended, in Needs you and in the Workflows page\'s pane', async () => {
    const ended = () => { throw Object.assign(new Error('workflow #12 has already ended'), { code: 'already_ended' }); };
    const text = '#12 has already ended, so there is nothing to extend. Telling its team something in it reopens it.';
    const one = fakeApi({ 'bus.workflows': () => [expiring], 'bus.workflowExtend': ended });
    const cards = [{ id: 4, team: 'payments', teamId: 1, status: 'needs-decision', workflow: 12 } as BoardCard];
    const a = mount(WorkflowExpiryAsks, { props: { api: one.api, cards, pollMs: 60_000 } });
    await flushPromises();
    await a.get('[data-test=workflow-expiry-extend]').trigger('click');
    await flushPromises();
    expect(a.get('[data-test=workflow-expiry-error]').text()).toBe(text);
    a.unmount();
    stubConfirm(() => true);
    const two = paneCore(() => [expiringRow], { extra: { 'bus.workflowExtend': ended } });
    const d = await mountPane(expiringRow, two.api);
    await d.get('[data-test=workflow-pane-extend]').trigger('click');
    await flushPromises();
    expect(d.get('[data-test=workflow-pane-error]').text()).toBe(text);
    d.unmount();
  });

  it('the helpers: expiresSoon within a day only for open ones, until, and the text without a question number', () => {
    expect(expiresSoon(expiring)).toBe(true);
    expect(expiresSoon({ state: 'open', expiresAt: hours(30) })).toBe(false);
    expect(expiresSoon({ state: 'expired', expiresAt: hours(1) })).toBe(false);
    expect(expiresSoon({ state: 'open', expiresAt: null })).toBe(false);
    const now = Date.parse('2026-11-17T12:00:00Z');
    expect(until('2026-11-17T12:29:30Z', now)).toBe('in 29 min');
    expect(until('2026-11-18T07:59:00Z', now)).toBe('in 19 h');
    expect(until('2026-11-17T11:00:00Z', now)).toBe('within a minute');
    expect(expiryWarningText({ id: 4, question: null })).toBe('#4 will expire tomorrow; it waits on your answer to a question');
  });
});

describe('the team setting "Expire idle workflows after N days"', () => {
  const answers = (days: { v: number }) => ({
    'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
    'teams.push': () => ({ on: false, origin: null }), 'teams.approvals': () => [], 'teams.syncToBase': () => ({ on: true, note: null, baseBranch: 'main', behind: null }),
    'teams.repos': () => ({ main: { path: 'C:/r', baseBranch: 'main', managed: true, origin: null, name: null }, extras: [] }),
    'integrations.list': () => ({ integrations: [], pendingAsks: 0 }), 'integrations.mcpList': () => [], 'integrations.apiList': () => [],
    'teams.workflowExpiry': (p: any) => { if (p.days !== undefined) days.v = p.days; return { days: days.v }; },
  });
  const mountIt = (api: YawbleApi) => mount(TeamManageDialog, { props: { team: { ...team, members: 1, running: 0, queued: 0, repoPath: null }, api },
    global: { stubs: { IntegrationDialog: true, Modal: { template: '<div><slot /></div>' } } } });

  it('shows the team\'s days (default 3) and saving 5 sends teams.workflowExpiry { teamId, days: 5 }', async () => {
    const days = { v: 3 };
    const { api, calls } = fakeApi(answers(days));
    const w = mountIt(api);
    await flushPromises();
    const field = w.get('[data-test=team-workflow-expiry-days]');
    expect((field.element as HTMLInputElement).value).toBe('3');
    expect(w.get('[data-test=team-workflow-expiry]').text()).toContain('Expire idle workflows after');
    expect((w.get('[data-test=team-workflow-expiry-save]').element as HTMLButtonElement).disabled).toBe(true); // unchanged
    await field.setValue('5');
    await w.get('[data-test=team-workflow-expiry-save]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'teams.workflowExpiry').at(-1)!.params).toEqual({ teamId: 1, days: 5 });
    expect(days.v).toBe(5);
    expect(w.find('[data-test=team-workflow-expiry-saved]').exists()).toBe(true);
    w.unmount();
  });

  it('refuses 0 days (Save disabled) and an older core without the setting shows no field', async () => {
    const { api } = fakeApi(answers({ v: 3 }));
    const w = mountIt(api);
    await flushPromises();
    await w.get('[data-test=team-workflow-expiry-days]').setValue('0');
    expect((w.get('[data-test=team-workflow-expiry-save]').element as HTMLButtonElement).disabled).toBe(true);
    w.unmount();
    const old = answers({ v: 3 }) as Record<string, unknown>;
    delete old['teams.workflowExpiry'];
    const w2 = mountIt(fakeApi(old as any).api);
    await flushPromises();
    expect(w2.find('[data-test=team-workflow-expiry]').exists()).toBe(false);
    w2.unmount();
  });
});
