// @vitest-environment happy-dom
// Workflow members on the team page. Add member as "Workflow member" (the workflow, its version, a settings
// form made from the workflow's settings, one binding per requires entry), the tile (workflow@version, last run, next
// trigger) and the pane (last runs, saved variables with a Reset each), and a Visitor's read-only view. Fakes only.
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { AgentCard, TeamSummary, YawbleApi } from '../../src/shared/api';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import WorkflowMemberAdd from '../../src/renderer/src/components/WorkflowMemberAdd.vue';
import WorkflowMemberPane from '../../src/renderer/src/components/WorkflowMemberPane.vue';
import { applyRemoteAccess, READ_ONLY_TITLE } from '../../src/renderer/src/remoteAccess';
import { enumOptions, workflowAtVersion, settingInput, settingText, settingValue } from '../../src/renderer/src/workflowMembers';

const team = { id: 1, name: 'payments', running: 0, queued: 0 } as unknown as TeamSummary;

const document = {
  format: 'yawble-workflow/1', id: 'inbox-sms-alert', name: 'Inbox SMS alert',
  settings: [
    { name: 'recipients', type: 'list<string>', label: 'Phones to text' },
    { name: 'fromNumber', type: 'string', label: 'Number that sends' },
    { name: 'folder', type: 'string', label: 'Mail folder', default: 'INBOX' },
    { name: 'max', type: 'int', default: 20 },
    { name: 'quiet', type: 'bool', default: false },
    { name: 'mode', type: 'enum(all, unread)', default: 'unread' },
  ],
  requires: [
    { name: 'mail', kind: 'email', actions: ['list'] },
    { name: 'sms', kind: 'api', actions: ['POST /Messages.json'] },
  ],
};

const workflows = [
  { id: 5, key: 'inbox-sms-alert', name: 'Inbox SMS alert', description: 'Texts new mail', latestVersion: 3, runnableVersion: 2, draft: true, updatedAt: '', usedBy: [] },
  { id: 6, key: 'only-drafts', name: 'Only drafts', description: '', latestVersion: 1, runnableVersion: null, draft: true, updatedAt: '', usedBy: [] },
];

const run = (id: number, status: string, extra: Record<string, unknown> = {}) => ({
  id, teamId: 1, team: 'payments', memberId: 9, member: 'Poller', workflowId: 5, workflow: 'inbox-sms-alert', version: 2, trigger: 'trigger:4', status,
  dryRun: false, test: false, input: null, outputs: null, failedStepId: null, reason: null, startedAt: '2026-11-24T08:00:00Z', endedAt: '2026-11-24T08:00:05Z', ...extra,
});

const workflowMember = {
  memberId: 9, teamId: 1, team: 'payments', name: 'Poller', workflowId: 5, workflow: 'inbox-sms-alert', version: null, runsVersion: 2,
  settings: {}, bindings: { mail: 'Work mail', sms: 'Twilio' }, timeoutMinutes: 10, consecutiveFailures: 0, running: false,
  lastRun: run(31, 'failed', { reason: 'mail: sign-in refused' }), nextTriggerAt: '2026-11-24T09:00:00.0000000Z',
};

type Calls = { method: string; params: Record<string, unknown> }[];

function fakeApi(calls: Calls, over: Record<string, (p: Record<string, unknown>) => unknown> = {}) {
  const answers: Record<string, (p: Record<string, unknown>) => unknown> = {
    'teams.agents': () => [],
    'bus.decisions': () => [],
    'integrations.asks': () => [],
    'workflows.list': () => workflows,
    'workflows.versions': () => [{ version: 3, ok: false, createdAt: '' }, { version: 2, ok: true, createdAt: '' }, { version: 1, ok: true, createdAt: '' }],
    'workflows.get': () => ({ id: 5, key: 'inbox-sms-alert', name: 'Inbox SMS alert', version: 2, ok: true, document }),
    'integrations.emailList': () => [
      { id: 1, name: 'Work mail', kind: 'email', enabledAll: false, teamIds: [1] },
      { id: 2, name: 'Other team mail', kind: 'email', enabledAll: false, teamIds: [7] },
      { id: 3, name: 'Shared mail', kind: 'email', enabledAll: true, teamIds: [] },
    ],
    'integrations.apiList': () => [{ name: 'Twilio', all: false, teams: [1] }, { name: 'Elsewhere', all: false, teams: [2] }],
    'workflows.memberAdd': (p) => ({ ...workflowMember, name: p.name }),
    'workflows.memberGet': () => workflowMember,
    'workflows.runs': () => [run(31, 'failed', { reason: 'mail: sign-in refused' }), run(30, 'succeeded', { trigger: 'tell' })],
    'workflows.memberState': () => ({ memberId: 9, variables: { lastSeen: 'gm:1:2:3', seen: [1, 2] }, consecutiveFailures: 1, updatedAt: '' }),
    'workflows.memberResetVariable': () => ({ reset: true }),
    ...over,
  };
  return {
    getInfo: async () => ({}),
    getAvailableBackends: async () => [],
    onEvent: () => () => {},
    call: async (method: string, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      const a = answers[method];
      if (a) return a(params);
      if (method.endsWith('.list') || method.endsWith('s')) return [];
      return {};
    },
  } as unknown as YawbleApi;
}

const wrappers: VueWrapper[] = [];
const keep = <T extends VueWrapper>(w: T) => { wrappers.push(w); return w; };
afterEach(() => { applyRemoteAccess('full'); for (const w of wrappers.splice(0)) w.unmount(); });

async function openForm(calls: Calls, over = {}) {
  const w = keep(mount(WorkflowMemberAdd, { props: { api: fakeApi(calls, over), teamId: 1, name: 'Mail Poller' } }));
  await flushPromises();
  await w.get('select[data-test=workflow-member-workflow]').setValue('5');
  await flushPromises();
  return w;
}

describe('the settings form helpers', () => {
  it('picks the field for each type and reads it back as the type', () => {
    expect(['string', 'int', 'float', 'bool', 'enum(a, b)', 'list<string>', 'Triage', 'datetime'].map(settingInput))
      .toEqual(['text', 'number', 'number', 'bool', 'enum', 'list', 'json', 'text']);
    expect(enumOptions('enum(all, unread)')).toEqual(['all', 'unread']);
    expect(settingValue({ name: 'r', type: 'list<string>' }, '+1555\n\n +1666 ')).toEqual(['+1555', '+1666']);
    expect(settingValue({ name: 'n', type: 'list<int>' }, '1\n2')).toEqual([1, 2]);
    expect(settingValue({ name: 'm', type: 'int' }, '20')).toBe(20);
    expect(() => settingValue({ name: 'm', type: 'int', label: 'Max' }, '2.5')).toThrow('Max must be a whole number');
    expect(settingValue({ name: 'q', type: 'bool' }, true)).toBe(true);
    expect(settingValue({ name: 's', type: 'string' }, '  ')).toBeUndefined();
    expect(settingValue({ name: 'o', type: 'Thing' }, '{"a":1}')).toEqual({ a: 1 });
    expect(settingText({ name: 'r', type: 'list<string>' }, ['a', 'b'])).toBe('a\nb');
    expect(workflowAtVersion({ workflow: 'poller', version: null, runsVersion: 3 })).toBe('poller@latest (v3)');
    expect(workflowAtVersion({ workflow: 'poller', version: 2, runsVersion: 2 })).toBe('poller@2');
  });
});

describe('Add member as a workflow member', () => {
  it('offers only runnable workflows, latest or a clean pinned version, a field per setting with its default, and a binding per requires entry', async () => {
    const calls: Calls = [];
    const w = await openForm(calls);
    const workflowOptions = w.findAll('[data-test=workflow-member-workflow] option').map((o) => o.text());
    expect(workflowOptions).toContain('Inbox SMS alert (inbox-sms-alert)');
    expect(workflowOptions.join()).not.toContain('Only drafts');

    // Latest runs the runnable version; the draft v3 is not offered.
    expect(w.findAll('[data-test=workflow-member-version] option').map((o) => o.text())).toEqual(['Latest (runs v2)', 'v2', 'v1']);
    expect(calls.find((c) => c.method === 'workflows.get')!.params).toEqual({ workflow: 5, version: 2 });

    // Settings: types and defaults from the document.
    expect(w.findAll('[data-test=workflow-member-settings] > label').length).toBe(6);
    expect((w.get('[data-test=workflow-setting-folder] input').element as HTMLInputElement).value).toBe('INBOX');
    expect(w.find('[data-test=workflow-setting-recipients] textarea').exists()).toBe(true);
    expect((w.get('[data-test=workflow-setting-max] input').element as HTMLInputElement).type).toBe('number');
    expect((w.get('[data-test=workflow-setting-quiet] input').element as HTMLInputElement).type).toBe('checkbox');
    expect(w.findAll('[data-test=workflow-setting-mode] option').map((o) => o.text())).toEqual(['all', 'unread']);
    expect(w.get('[data-test=workflow-setting-recipients]').text()).toContain('*'); // no default: needs a value

    // Bindings: the team's own integrations of the entry's kind (one offered to every team counts, another team's does not).
    expect(w.findAll('[data-test=workflow-binding-mail] option').map((o) => o.text()).slice(1)).toEqual(['Work mail', 'Shared mail']);
    expect(w.findAll('[data-test=workflow-binding-sms] option').map((o) => o.text()).slice(1)).toEqual(['Twilio']);
    // A single choice is taken; with two the person picks, and Add waits for it.
    expect((w.get('[data-test=workflow-binding-sms] select').element as HTMLSelectElement).value).toBe('Twilio');
    expect((w.get('[data-test=workflow-member-add]').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('calls workflows.memberAdd with typed settings and the bindings, leaving out what was left empty', async () => {
    const calls: Calls = [];
    const w = await openForm(calls);
    await w.get('[data-test=workflow-setting-recipients] textarea').setValue('+15550001\n+15550002');
    await w.get('[data-test=workflow-setting-max] input').setValue('30');
    await w.get('[data-test=workflow-setting-quiet] input').setValue(true);
    await w.get('[data-test=workflow-binding-mail] select').setValue('Work mail');
    await w.get('[data-test=workflow-member-version]').setValue('1');
    await flushPromises();
    await w.get('[data-test=workflow-member-add]').trigger('click');
    await flushPromises();
    const add = calls.find((c) => c.method === 'workflows.memberAdd')!.params;
    expect(add).toEqual({
      teamId: 1, name: 'Mail Poller', workflow: 5, version: 1,
      settings: { recipients: ['+15550001', '+15550002'], folder: 'INBOX', max: 30, quiet: true, mode: 'unread' },
      bindings: { mail: 'Work mail', sms: 'Twilio' },
    });
    expect(w.emitted('added')).toEqual([[9]]);
  });

  it('shows a bad_workflow refusal and keeps the form', async () => {
    const calls: Calls = [];
    const w = await openForm(calls, { 'workflows.memberAdd': () => { throw Object.assign(new Error("setting 'fromNumber' needs a value"), { code: 'bad_workflow' }); } });
    await w.get('[data-test=workflow-binding-mail] select').setValue('Work mail');
    await w.get('[data-test=workflow-member-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=workflow-member-error]').text()).toBe("setting 'fromNumber' needs a value");
    expect(w.emitted('added')).toBeUndefined();
    expect((w.get('[data-test=workflow-setting-folder] input').element as HTMLInputElement).value).toBe('INBOX');
  });

  it('a value that does not fit its type is refused before anything is sent', async () => {
    const calls: Calls = [];
    const w = await openForm(calls);
    await w.get('[data-test=workflow-setting-max] input').setValue('2.5');
    await w.get('[data-test=workflow-binding-mail] select').setValue('Work mail');
    await w.get('[data-test=workflow-member-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=workflow-member-error]').text()).toBe('max must be a whole number');
    expect(calls.some((c) => c.method === 'workflows.memberAdd')).toBe(false);
  });

  it('the team page offers Workflow member next to the agent CLIs, and the workflow form replaces the CLI add', async () => {
    const calls: Calls = [];
    const w = keep(mount(TeamView, { props: { team, api: fakeApi(calls), pollMs: 60_000 } }));
    await flushPromises();
    await w.get('[data-test=member-card-new]').trigger('click');
    expect(w.get('[data-test=add-agent-cli-workflow]').text()).toBe('Workflow member');
    expect(w.find('[data-test=workflow-member-form]').exists()).toBe(false);
    await w.get('[data-test=add-agent-name]').setValue('Mail Poller');
    await w.get('[data-test=add-agent-cli]').setValue('workflow');
    await flushPromises();
    expect(w.find('[data-test=workflow-member-form]').exists()).toBe(true);
    expect(w.find('[data-test=add-agent]').exists()).toBe(false);
    expect(calls.some((c) => c.method === 'workflows.list')).toBe(true);
    expect(calls.some((c) => c.method === 'members.add')).toBe(false);
  });
});

const card = {
  member: { id: 9, teamId: 1, team: 'payments', name: 'Poller', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  kind: 'workflow', state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null,
} as unknown as AgentCard;

describe('a workflow member on the team page', () => {
  it('its tile shows workflow@version (latest: the version it runs), the last run and the next trigger', async () => {
    const calls: Calls = [];
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: fakeApi(calls), pollMs: 60_000 } }));
    await flushPromises();
    expect(w.get('[data-test=workflow-member-version]').text()).toBe('inbox-sms-alert@latest (v2)');
    expect(w.get('[data-test=workflow-member-last-status]').text()).toBe('Failed');
    expect(w.get('[data-test=workflow-member-last-run]').text()).toContain(new Date('2026-11-24T08:00:05Z').toLocaleString());
    expect(w.get('[data-test=workflow-member-next]').text()).toBe(`Next trigger: ${new Date('2026-11-24T09:00:00Z').toLocaleString()}`);
    expect(calls.find((c) => c.method === 'workflows.runs')!.params).toEqual({ memberId: 9, limit: 10 });
  });

  it('an enabled trigger lights the Triggers pill; with one trigger, clicking it asks to edit that trigger', async () => {
    const trig = (id: number, memberId: number, enabled = true) => ({ id, teamId: 1, memberId, name: `t${id}`, schedule: { kind: 'every', intervalSec: 300 }, instruction: '', onlyWhenIdle: true, enabled, nextDueAt: null, lastFiredAt: null, wakeManager: 'onHandbackOrFailure' });
    let list = [trig(4, 9), trig(5, 2)];
    const calls: Calls = [];
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: fakeApi(calls, { 'triggers.list': () => list }), pollMs: 60_000 } }));
    await flushPromises();
    expect(calls.find((c) => c.method === 'triggers.list')!.params).toEqual({ teamId: 1 });
    const pill = w.get('[data-test=workflow-member-triggers]');
    expect(pill.text()).toBe('trigger on');
    expect(pill.attributes('title')).toBe('t4: every 5 min');
    await pill.trigger('click');
    expect(w.emitted('triggers')).toEqual([[4]]);

    list = [trig(4, 9), trig(6, 9)];
    const w2 = keep(mount(WorkflowMemberPane, { props: { card, api: fakeApi([], { 'triggers.list': () => list }), pollMs: 60_000 } }));
    await flushPromises();
    expect(w2.get('[data-test=workflow-member-triggers]').text()).toBe('2 triggers on');
    await w2.get('[data-test=workflow-member-triggers]').trigger('click');
    expect(w2.emitted('triggers')).toEqual([[null]]);

    list = [trig(4, 9, false)];
    const w3 = keep(mount(WorkflowMemberPane, { props: { card, api: fakeApi([], { 'triggers.list': () => list }), pollMs: 60_000 } }));
    await flushPromises();
    expect(w3.find('[data-test=workflow-member-triggers]').exists()).toBe(false);
  });

  it('the newest lines of the files its workflow writes show newest first; a file without lines is not shown', async () => {
    const calls: Calls = [];
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: fakeApi(calls, { 'workflows.memberFiles': () => ({ memberId: 9, files: [
      { path: 'run.log', bytes: 40, modifiedAt: '2026-11-24T16:17:04Z', tail: ['16:12 email: Hello (a@b.test)', '16:17 1 new, 1 texts'] },
      { path: 'out/data.bin', bytes: 3, modifiedAt: '2026-11-24T16:00:00Z' },
    ] }) }), pollMs: 60_000 } }));
    await flushPromises();
    expect(calls.find((c) => c.method === 'workflows.memberFiles')!.params).toEqual({ memberId: 9, lines: 30 });
    expect(w.get('[data-test="workflow-member-file-lines-run.log"]').findAll('li').map((l) => l.text())).toEqual(['16:17 1 new, 1 texts', '16:12 email: Hello (a@b.test)']);
    expect(w.find('[data-test="workflow-member-file-out/data.bin"]').exists()).toBe(false);
  });

  it('a member with no scheduled trigger and no run says so', async () => {
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: fakeApi([], {
      'workflows.memberGet': () => ({ ...workflowMember, version: 1, lastRun: null, nextTriggerAt: null }),
      'workflows.runs': () => [], 'workflows.memberState': () => ({ memberId: 9, variables: {}, consecutiveFailures: 0, updatedAt: null }),
    }), pollMs: 60_000 } }));
    await flushPromises();
    expect(w.get('[data-test=workflow-member-version]').text()).toBe('inbox-sms-alert@1');
    expect(w.get('[data-test=workflow-member-last-run]').text()).toBe('Last run: never');
    expect(w.get('[data-test=workflow-member-next]').text()).toBe('Next trigger: none scheduled');
    expect(w.find('[data-test=workflow-member-no-runs]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-member-no-variables]').exists()).toBe(true);
  });

  it('its pane lists the last runs and the saved variables; Reset forgets one and reloads them', async () => {
    const calls: Calls = [];
    let vars: Record<string, unknown> = { lastSeen: 'gm:1:2:3', seen: [1, 2] };
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: fakeApi(calls, {
      'workflows.memberState': () => ({ memberId: 9, variables: vars, consecutiveFailures: 1, updatedAt: '' }),
      'workflows.memberResetVariable': (p) => { const { [p.name as string]: _gone, ...rest } = vars; vars = rest; return { reset: true }; },
    }), pollMs: 60_000 } }));
    await flushPromises();
    const runs = w.findAll('[data-test=workflow-member-run]');
    expect(runs.length).toBe(2);
    expect(runs[0].text()).toContain('Failed');
    expect(runs[0].text()).toContain('trigger 4');
    expect(runs[0].text()).toContain('mail: sign-in refused');
    expect(runs[1].text()).toContain('tell');
    expect(w.get('[data-test=workflow-variable-lastSeen]').text()).toContain('gm:1:2:3');
    expect(w.get('[data-test=workflow-variable-seen]').text()).toContain('[1,2]');

    await w.get('[data-test=workflow-variable-reset-lastSeen]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'workflows.memberResetVariable')!.params).toEqual({ memberId: 9, name: 'lastSeen' });
    expect(w.find('[data-test=workflow-variable-lastSeen]').exists()).toBe(false);
    expect(w.find('[data-test=workflow-variable-seen]').exists()).toBe(true);
  });

  it('a Visitor sees the runs and variables but no Reset, and Remove is disabled read-only', async () => {
    applyRemoteAccess('read');
    const calls: Calls = [];
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: fakeApi(calls), pollMs: 60_000 } }));
    await flushPromises();
    expect(w.findAll('[data-test=workflow-member-run]').length).toBe(2);
    expect(w.find('[data-test=workflow-variable-lastSeen]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-variable-reset-lastSeen]').exists()).toBe(false);
    const remove = w.get('[data-test=workflow-member-remove]').element as HTMLButtonElement;
    expect(remove.disabled).toBe(true);
    expect(remove.title).toBe(READ_ONLY_TITLE);
    expect(calls.every((c) => !['workflows.memberResetVariable', 'members.remove'].includes(c.method))).toBe(true);
  });

  it('a Visitor cannot add a workflow member: Add is disabled read-only', async () => {
    applyRemoteAccess('read');
    const calls: Calls = [];
    const w = await openForm(calls);
    await w.get('[data-test=workflow-binding-mail] select').setValue('Work mail');
    const add = w.get('[data-test=workflow-member-add]').element as HTMLButtonElement;
    expect(add.disabled).toBe(true);
    expect(add.title).toBe(READ_ONLY_TITLE);
  });

  it('the team page shows a workflow member card as its workflow tile, an agent as before', async () => {
    const agent = { ...card, kind: 'agent', member: { ...card.member, id: 2, name: 'DeveloperAda' } };
    const calls: Calls = [];
    const w = keep(mount(TeamView, { props: { team, api: fakeApi(calls, { 'teams.agents': () => [card, agent] }), pollMs: 60_000 },
      global: { stubs: { MemberPane: { template: '<div data-test="agent-pane-stub" />' } } } }));
    await flushPromises();
    expect(w.find('[data-test=workflow-member-Poller]').exists()).toBe(true);
    expect(w.findAll('[data-test=agent-pane-stub]').length).toBe(1);
    expect(w.get('[data-test=workflow-member-version]').text()).toBe('inbox-sms-alert@latest (v2)');
  });
});
