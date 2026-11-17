// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { BoardCard, Decision, TeamSummary, WorkflowSummary, YawbleApi } from '../../src/shared/api';
import { TEAM_SET_WORKFLOW_EXPIRY, workflowTime } from '../../src/shared/api';
import WorkflowsDialog from '../../src/renderer/src/components/WorkflowsDialog.vue';
import TeamWorkflows from '../../src/renderer/src/components/TeamWorkflows.vue';
import WorkflowExpiryAsks from '../../src/renderer/src/components/WorkflowExpiryAsks.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import { entryTime, workflowEnds, workflowLook, WorkflowLooks } from '../../src/renderer/src/monitor/workflows';
import { MonitorModel, workflowKey } from '../../src/renderer/src/monitor/model';
import { expiresSoon, expiryWarningText, extendErrorText, until } from '../../src/renderer/src/workflowExpiry';
import { applyRemoteAccess, READ_ONLY_TITLE } from '../../src/renderer/src/remoteAccess';

// B085 (card 389): an expired workflow reads "expired" (not completed, not closed) with its time from endedAt; a
// bus.decisions item the core warns about (expiryWarning, expiresAt) is in Needs you with Extend (bus.workflowExtend;
// an ended workflow is refused with already_ended); Team settings edits workflowExpiryDays (read from teams.list, set
// with teams.setWorkflowExpiry, 1..90). Against card 388's core (DeveloperMarlo's d0adff4); fakes only.

const hours = (h: number) => new Date(Date.now() + h * 3600_000).toISOString();
const team = { id: 1, name: 'payments' } as TeamSummary;
const base = { owner: 'Manager', undeclared: false, busy: [] as string[], ending: null, startedAt: hours(-200), messages: 3, endedAt: null, expiredAt: null, expiresAt: null };
const expired: WorkflowSummary = { ...base, id: 9, title: 'Old export', state: 'expired', ending: 'expired: no activity for 3 days',
  lastAt: hours(-2), endedAt: hours(-96), expiredAt: hours(-24) };
const completed: WorkflowSummary = { ...base, id: 8, title: 'Done thing', state: 'completed', lastAt: hours(-50), endedAt: hours(-50) };
const closed: WorkflowSummary = { ...base, id: 7, title: 'Dropped thing', state: 'closed', lastAt: hours(-60), endedAt: hours(-60) };
const expiring: WorkflowSummary = { ...base, id: 12, title: 'Refund limits', state: 'open', undeclared: true, lastAt: hours(-52), expiresAt: hours(20),
  expiryWarning: '#12 will expire tomorrow; it waits on your answer to question 3' };
/** The core's bus.decisions items: question 3 of #12 is warned; question 5 of #10 is not. */
const warned: Decision = { seq: 3, from: 'Manager', question: 'Which countries?', workflow: 12, at: hours(-52),
  expiresAt: hours(20), expiryWarning: '#12 will expire tomorrow; it waits on your answer to question 3' };
const plain: Decision = { seq: 5, from: 'Manager', question: 'Which bank?', workflow: 10, at: hours(-1), expiresAt: null, expiryWarning: null };

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
const refusal = (code: string, message: string) => Object.assign(new Error(message), { code });

afterEach(() => { applyRemoteAccess('full'); document.body.innerHTML = ''; });

describe('the "expired" state (B085)', () => {
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

  it('the workflows dialog reads "Expired" with "ended" at endedAt and when it expired; no actions on it', async () => {
    const { api } = fakeApi({ 'bus.workflows': () => [expired, completed, closed] });
    const w = mount(WorkflowsDialog, { props: { team, api, pollMs: 60_000 }, attachTo: document.body });
    await flushPromises();
    const row = w.get('[data-test=workflow-9]');
    expect(row.get('[data-test=workflow-state]').text()).toBe('Expired');
    expect(row.classes()).toContain('expired');
    expect(row.text()).toContain('ended ');
    expect(row.text()).toContain('expired ');
    expect(row.text()).toContain('Expired: expired: no activity for 3 days');
    expect(row.find('[data-test=workflow-nudge]').exists()).toBe(false);
    expect(w.get('[data-test=workflow-8] [data-test=workflow-state]').text()).toBe('Completed');
    expect(w.get('[data-test=workflow-7] [data-test=workflow-state]').text()).toBe('Closed');
    w.unmount();
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

  it('a workflow node (B080) that expires leaves with an "expired" wfdone effect, not "closed"', () => {
    const open = { ...expired, state: 'open' as const, endedAt: null, expiredAt: null };
    expect(workflowEnds([open, { ...closed, state: 'open' }], [expired, closed])).toEqual([{ id: 9, status: 'expired' }, { id: 7, status: 'closed' }]);
    const m = new MonitorModel();
    m.setWorkflows('payments', [open], 0);
    m.effects.length = 0;
    m.setWorkflows('payments', [expired], 10);
    expect(m.effects).toEqual([{ kind: 'wfdone', key: workflowKey('payments', 9), team: 'payments', status: 'expired', at: 10 }]);
  });
});

describe('Needs you and Extend (B085)', () => {
  const cards = [{ id: 4, team: 'payments', teamId: 1, status: 'needs-decision', workflow: 12 } as BoardCard];

  it('the Needs-you item is the warned decision\'s expiryWarning, and Extend calls bus.workflowExtend { teamId, workflow }', async () => {
    let list = [plain, warned];
    const { api, calls } = fakeApi({
      'bus.decisions': () => list,
      'bus.workflowExtend': (p) => { list = list.map((d) => (d.workflow === p.workflow ? { ...d, expiresAt: null, expiryWarning: null } : d)); return { ...expiring, expiresAt: null }; },
    });
    const w = mount(WorkflowExpiryAsks, { props: { api, cards, pollMs: 60_000 } });
    await flushPromises();
    expect(calls.find((c) => c.method === 'bus.decisions')!.params).toEqual({ teamId: 1 });
    expect(w.emitted('count')!.at(-1)).toEqual([1]);
    expect(w.get('[data-test=workflow-expiry-text]').text()).toBe('#12 will expire tomorrow; it waits on your answer to question 3');
    expect(w.get('[data-test=workflow-expiry-12]').text()).toContain('expires in 19 h');
    expect(w.find('[data-test=workflow-expiry-10]').exists()).toBe(false);
    await w.get('[data-test=workflow-expiry-extend]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'bus.workflowExtend')!.params).toEqual({ teamId: 1, workflow: 12 });
    expect(w.find('[data-test=workflow-expiry-12]').exists()).toBe(false);
    expect(w.emitted('count')!.at(-1)).toEqual([0]);
    w.unmount();
  });

  it('no item without a warning (an older core, or nothing warned), and no call without a needs-decision card', async () => {
    const older = { ...warned, expiryWarning: undefined, expiresAt: undefined };
    const { api, calls } = fakeApi({ 'bus.decisions': () => [plain, older] });
    const w = mount(WorkflowExpiryAsks, { props: { api, cards, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=workflow-expiry-asks]').exists()).toBe(false);
    w.unmount();
    calls.length = 0;
    const w2 = mount(WorkflowExpiryAsks, { props: { api, cards: [], pollMs: 60_000 } });
    await flushPromises();
    expect(calls).toEqual([]);
    w2.unmount();
  });

  it('an Extend refused with already_ended says the workflow has ended and drops its line', async () => {
    const { api } = fakeApi({
      'bus.decisions': () => [warned],
      'bus.workflowExtend': () => { throw refusal('already_ended', 'this workflow has ended; telling its team something in it reopens it'); },
    });
    const w = mount(WorkflowExpiryAsks, { props: { api, cards, pollMs: 60_000 } });
    await flushPromises();
    await w.get('[data-test=workflow-expiry-extend]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=workflow-expiry-error]').text()).toBe('#12 has already ended, so there is nothing to extend. Telling its team something in it reopens it.');
    expect(w.find('[data-test=workflow-expiry-12]').exists()).toBe(false);
    w.unmount();
  });

  it('Extend is read-only for a Visitor: disabled with the read-only title, nothing called', async () => {
    applyRemoteAccess('read');
    const { api, calls } = fakeApi({ 'bus.decisions': () => [warned] });
    const w = mount(WorkflowExpiryAsks, { props: { api, cards, pollMs: 60_000 } });
    await flushPromises();
    const b = w.get('[data-test=workflow-expiry-extend]').element as HTMLButtonElement;
    expect(b.disabled).toBe(true);
    expect(b.title).toBe(READ_ONLY_TITLE);
    expect(calls.some((c) => c.method === 'bus.workflowExtend')).toBe(false);
    w.unmount();
  });

  it('the workflows dialog shows when an expiring workflow expires, with Extend calling bus.workflowExtend', async () => {
    const { api, calls } = fakeApi({ 'bus.workflows': () => [expiring], 'bus.workflowExtend': () => ({}) });
    const w = mount(WorkflowsDialog, { props: { team, api, pollMs: 60_000 }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=workflow-expires]').text()).toContain('Expires in 19 h');
    await w.get('[data-test=workflow-extend]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'bus.workflowExtend')!.params).toEqual({ teamId: 1, workflow: 12 });
    w.unmount();
  });

  it('the workflows dialog: an Extend refused with already_ended reads as ended, not as a raw error', async () => {
    const { api } = fakeApi({ 'bus.workflows': () => [expiring], 'bus.workflowExtend': () => { throw refusal('already_ended', 'this workflow has already ended'); } });
    const w = mount(WorkflowsDialog, { props: { team, api, pollMs: 60_000 }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=workflow-extend]').trigger('click');
    await flushPromises();
    expect(w.text()).toContain('#12 has already ended, so there is nothing to extend.');
    w.unmount();
  });

  it('the helpers: expiresSoon within a day only for open ones, until, the text from seq, and other Extend errors', () => {
    expect(expiresSoon(expiring)).toBe(true);
    expect(expiresSoon({ state: 'open', expiresAt: hours(30) })).toBe(false);
    expect(expiresSoon({ state: 'expired', expiresAt: hours(1) })).toBe(false);
    expect(expiresSoon({ state: 'open', expiresAt: null })).toBe(false);
    const now = Date.parse('2026-11-17T12:00:00Z');
    expect(until('2026-11-17T12:29:30Z', now)).toBe('in 29 min');
    expect(until('2026-11-18T07:59:00Z', now)).toBe('in 19 h');
    expect(until('2026-11-17T11:00:00Z', now)).toBe('within a minute');
    expect(expiryWarningText(warned)).toBe(warned.expiryWarning);
    expect(expiryWarningText({ workflow: 4, seq: 7, expiryWarning: null })).toBe('#4 will expire tomorrow; it waits on your answer to question 7');
    expect(extendErrorText(new Error('boom'), 4)).toBe('boom');
  });
});

describe('the team setting "Expire idle workflows after N days" (B085)', () => {
  /** days.v undefined: an older core, whose teams.list items have no workflowExpiryDays. */
  const answers = (days: { v: number | undefined }) => ({
    'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
    'teams.push': () => ({ on: false, origin: null }), 'teams.approvals': () => [], 'teams.syncToBase': () => ({ on: true, note: null, baseBranch: 'main', behind: null }),
    'teams.repos': () => ({ main: { path: 'C:/r', baseBranch: 'main', managed: true, origin: null, name: null }, extras: [] }),
    'integrations.list': () => ({ integrations: [], pendingAsks: 0 }), 'integrations.mcpList': () => [], 'integrations.apiList': () => [],
    'teams.list': () => [{ ...team, members: 1, running: 0, queued: 0, repoPath: null, ...(days.v === undefined ? {} : { workflowExpiryDays: days.v }) }],
    [TEAM_SET_WORKFLOW_EXPIRY]: (p: any) => {
      const d = p.workflowExpiryDays;
      if (d !== null && (!Number.isInteger(d) || d < 1 || d > 90)) throw refusal('bad_days', 'workflowExpiryDays must be 1 to 90 days');
      days.v = d ?? 3;
      return { workflowExpiryDays: days.v };
    },
  });
  const mountIt = (api: YawbleApi) => mount(TeamManageDialog, { props: { team: { ...team, members: 1, running: 0, queued: 0, repoPath: null }, api },
    global: { stubs: { IntegrationDialog: true, Modal: { template: '<div><slot /></div>' } } } });

  it('shows the teams.list item\'s workflowExpiryDays (3) and saving 5 sends teams.setWorkflowExpiry { teamId, workflowExpiryDays: 5 }', async () => {
    const days = { v: 3 as number | undefined };
    const { api, calls } = fakeApi(answers(days));
    const w = mountIt(api);
    await flushPromises();
    const field = w.get('[data-test=team-workflow-expiry-days]');
    expect((field.element as HTMLInputElement).value).toBe('3');
    expect(field.attributes('max')).toBe('90');
    expect(w.get('[data-test=team-workflow-expiry]').text()).toContain('Expire idle workflows after');
    expect((w.get('[data-test=team-workflow-expiry-save]').element as HTMLButtonElement).disabled).toBe(true); // unchanged
    await field.setValue('5');
    await w.get('[data-test=team-workflow-expiry-save]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'teams.setWorkflowExpiry').at(-1)!.params).toEqual({ teamId: 1, workflowExpiryDays: 5 });
    expect(days.v).toBe(5);
    expect(w.find('[data-test=team-workflow-expiry-saved]').exists()).toBe(true);
    expect(calls.some((c) => c.method === 'teams.workflowExpiry')).toBe(false);
    w.unmount();
  });

  it('allows 90, refuses 0 and 91 (Save disabled, with the 1 to 90 note)', async () => {
    const { api } = fakeApi(answers({ v: 3 }));
    const w = mountIt(api);
    await flushPromises();
    const field = w.get('[data-test=team-workflow-expiry-days]');
    const save = () => (w.get('[data-test=team-workflow-expiry-save]').element as HTMLButtonElement).disabled;
    await field.setValue('90');
    expect(save()).toBe(false);
    await field.setValue('91');
    expect(save()).toBe(true);
    expect(w.get('[data-test=team-workflow-expiry]').text()).toContain('A whole number of days, 1 to 90.');
    await field.setValue('0');
    expect(save()).toBe(true);
    w.unmount();
  });

  it('a refusal from the core (bad_days) is shown, not saved', async () => {
    const a = answers({ v: 3 });
    a[TEAM_SET_WORKFLOW_EXPIRY] = () => { throw refusal('bad_days', 'workflowExpiryDays must be 1 to 90 days'); };
    const w = mountIt(fakeApi(a).api);
    await flushPromises();
    await w.get('[data-test=team-workflow-expiry-days]').setValue('7');
    await w.get('[data-test=team-workflow-expiry-save]').trigger('click');
    await flushPromises();
    expect(w.text()).toContain('workflowExpiryDays must be 1 to 90 days');
    expect(w.find('[data-test=team-workflow-expiry-saved]').exists()).toBe(false);
    w.unmount();
  });

  it('an older core (no workflowExpiryDays on the teams.list item) shows no field', async () => {
    const w = mountIt(fakeApi(answers({ v: undefined })).api);
    await flushPromises();
    expect(w.find('[data-test=team-workflow-expiry]').exists()).toBe(false);
    w.unmount();
  });
});
