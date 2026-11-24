// @vitest-environment happy-dom
// B134 (card 598): script members on the team page. Add member as "Script member" (the script, its version, a settings
// form made from the script's settings, one binding per requires entry), the tile (script@version, last run, next
// trigger) and the pane (last runs, saved variables with a Reset each), and a Visitor's read-only view. Fakes only.
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { AgentCard, TeamSummary, YawbleApi } from '../../src/shared/api';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import ScriptMemberAdd from '../../src/renderer/src/components/ScriptMemberAdd.vue';
import ScriptMemberPane from '../../src/renderer/src/components/ScriptMemberPane.vue';
import { applyRemoteAccess, READ_ONLY_TITLE } from '../../src/renderer/src/remoteAccess';
import { enumOptions, scriptAtVersion, settingInput, settingText, settingValue } from '../../src/renderer/src/scriptMembers';

const team = { id: 1, name: 'payments', running: 0, queued: 0 } as unknown as TeamSummary;

const document = {
  format: 'yawble-script/1', id: 'inbox-sms-alert', name: 'Inbox SMS alert',
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

const scripts = [
  { id: 5, key: 'inbox-sms-alert', name: 'Inbox SMS alert', description: 'Texts new mail', latestVersion: 3, runnableVersion: 2, draft: true, updatedAt: '', usedBy: [] },
  { id: 6, key: 'only-drafts', name: 'Only drafts', description: '', latestVersion: 1, runnableVersion: null, draft: true, updatedAt: '', usedBy: [] },
];

const run = (id: number, status: string, extra: Record<string, unknown> = {}) => ({
  id, teamId: 1, team: 'payments', memberId: 9, member: 'Poller', scriptId: 5, script: 'inbox-sms-alert', version: 2, trigger: 'trigger:4', status,
  dryRun: false, test: false, input: null, outputs: null, failedStepId: null, reason: null, startedAt: '2026-11-24T08:00:00Z', endedAt: '2026-11-24T08:00:05Z', ...extra,
});

const scriptMember = {
  memberId: 9, teamId: 1, team: 'payments', name: 'Poller', scriptId: 5, script: 'inbox-sms-alert', version: null, runsVersion: 2,
  settings: {}, bindings: { mail: 'Work mail', sms: 'Twilio' }, timeoutMinutes: 10, consecutiveFailures: 0, running: false,
  lastRun: run(31, 'failed', { reason: 'mail: sign-in refused' }), nextTriggerAt: '2026-11-24T09:00:00.0000000Z',
};

type Calls = { method: string; params: Record<string, unknown> }[];

function fakeApi(calls: Calls, over: Record<string, (p: Record<string, unknown>) => unknown> = {}) {
  const answers: Record<string, (p: Record<string, unknown>) => unknown> = {
    'teams.agents': () => [],
    'bus.decisions': () => [],
    'integrations.asks': () => [],
    'scripts.list': () => scripts,
    'scripts.versions': () => [{ version: 3, ok: false, createdAt: '' }, { version: 2, ok: true, createdAt: '' }, { version: 1, ok: true, createdAt: '' }],
    'scripts.get': () => ({ id: 5, key: 'inbox-sms-alert', name: 'Inbox SMS alert', version: 2, ok: true, document }),
    'integrations.emailList': () => [
      { id: 1, name: 'Work mail', kind: 'email', enabledAll: false, teamIds: [1] },
      { id: 2, name: 'Other team mail', kind: 'email', enabledAll: false, teamIds: [7] },
      { id: 3, name: 'Shared mail', kind: 'email', enabledAll: true, teamIds: [] },
    ],
    'integrations.apiList': () => [{ name: 'Twilio', all: false, teams: [1] }, { name: 'Elsewhere', all: false, teams: [2] }],
    'scripts.memberAdd': (p) => ({ ...scriptMember, name: p.name }),
    'scripts.memberGet': () => scriptMember,
    'scripts.runs': () => [run(31, 'failed', { reason: 'mail: sign-in refused' }), run(30, 'succeeded', { trigger: 'tell' })],
    'scripts.memberState': () => ({ memberId: 9, variables: { lastSeen: 'gm:1:2:3', seen: [1, 2] }, consecutiveFailures: 1, updatedAt: '' }),
    'scripts.memberResetVariable': () => ({ reset: true }),
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
  const w = keep(mount(ScriptMemberAdd, { props: { api: fakeApi(calls, over), teamId: 1, name: 'Mail Poller' } }));
  await flushPromises();
  await w.get('select[data-test=script-member-script]').setValue('5');
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
    expect(scriptAtVersion({ script: 'poller', version: null, runsVersion: 3 })).toBe('poller@latest (v3)');
    expect(scriptAtVersion({ script: 'poller', version: 2, runsVersion: 2 })).toBe('poller@2');
  });
});

describe('Add member as a script member', () => {
  it('offers only runnable scripts, latest or a clean pinned version, a field per setting with its default, and a binding per requires entry', async () => {
    const calls: Calls = [];
    const w = await openForm(calls);
    const scriptOptions = w.findAll('[data-test=script-member-script] option').map((o) => o.text());
    expect(scriptOptions).toContain('Inbox SMS alert (inbox-sms-alert)');
    expect(scriptOptions.join()).not.toContain('Only drafts');

    // Latest runs the runnable version; the draft v3 is not offered.
    expect(w.findAll('[data-test=script-member-version] option').map((o) => o.text())).toEqual(['Latest (runs v2)', 'v2', 'v1']);
    expect(calls.find((c) => c.method === 'scripts.get')!.params).toEqual({ script: 5, version: 2 });

    // Settings: types and defaults from the document.
    expect(w.findAll('[data-test=script-member-settings] > label').length).toBe(6);
    expect((w.get('[data-test=script-setting-folder] input').element as HTMLInputElement).value).toBe('INBOX');
    expect(w.get('[data-test=script-setting-recipients] textarea').exists()).toBe(true);
    expect((w.get('[data-test=script-setting-max] input').element as HTMLInputElement).type).toBe('number');
    expect((w.get('[data-test=script-setting-quiet] input').element as HTMLInputElement).type).toBe('checkbox');
    expect(w.findAll('[data-test=script-setting-mode] option').map((o) => o.text())).toEqual(['all', 'unread']);
    expect(w.get('[data-test=script-setting-recipients]').text()).toContain('*'); // no default: needs a value

    // Bindings: the team's own integrations of the entry's kind (one offered to every team counts, another team's does not).
    expect(w.findAll('[data-test=script-binding-mail] option').map((o) => o.text()).slice(1)).toEqual(['Work mail', 'Shared mail']);
    expect(w.findAll('[data-test=script-binding-sms] option').map((o) => o.text()).slice(1)).toEqual(['Twilio']);
    // A single choice is taken; with two the person picks, and Add waits for it.
    expect((w.get('[data-test=script-binding-sms] select').element as HTMLSelectElement).value).toBe('Twilio');
    expect((w.get('[data-test=script-member-add]').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('calls scripts.memberAdd with typed settings and the bindings, leaving out what was left empty', async () => {
    const calls: Calls = [];
    const w = await openForm(calls);
    await w.get('[data-test=script-setting-recipients] textarea').setValue('+15550001\n+15550002');
    await w.get('[data-test=script-setting-max] input').setValue('30');
    await w.get('[data-test=script-setting-quiet] input').setValue(true);
    await w.get('[data-test=script-binding-mail] select').setValue('Work mail');
    await w.get('[data-test=script-member-version]').setValue('1');
    await flushPromises();
    await w.get('[data-test=script-member-add]').trigger('click');
    await flushPromises();
    const add = calls.find((c) => c.method === 'scripts.memberAdd')!.params;
    expect(add).toEqual({
      teamId: 1, name: 'Mail Poller', script: 5, version: 1,
      settings: { recipients: ['+15550001', '+15550002'], folder: 'INBOX', max: 30, quiet: true, mode: 'unread' },
      bindings: { mail: 'Work mail', sms: 'Twilio' },
    });
    expect(w.emitted('added')).toEqual([[9]]);
  });

  it('shows a bad_script refusal and keeps the form', async () => {
    const calls: Calls = [];
    const w = await openForm(calls, { 'scripts.memberAdd': () => { throw Object.assign(new Error("setting 'fromNumber' needs a value"), { code: 'bad_script' }); } });
    await w.get('[data-test=script-binding-mail] select').setValue('Work mail');
    await w.get('[data-test=script-member-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=script-member-error]').text()).toBe("setting 'fromNumber' needs a value");
    expect(w.emitted('added')).toBeUndefined();
    expect((w.get('[data-test=script-setting-folder] input').element as HTMLInputElement).value).toBe('INBOX');
  });

  it('a value that does not fit its type is refused before anything is sent', async () => {
    const calls: Calls = [];
    const w = await openForm(calls);
    await w.get('[data-test=script-setting-max] input').setValue('2.5');
    await w.get('[data-test=script-binding-mail] select').setValue('Work mail');
    await w.get('[data-test=script-member-add]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=script-member-error]').text()).toBe('max must be a whole number');
    expect(calls.some((c) => c.method === 'scripts.memberAdd')).toBe(false);
  });

  it('the team page offers Script member next to the agent CLIs, and the script form replaces the CLI add', async () => {
    const calls: Calls = [];
    const w = keep(mount(TeamView, { props: { team, api: fakeApi(calls), pollMs: 60_000 } }));
    await flushPromises();
    await w.get('[data-test=member-card-new]').trigger('click');
    expect(w.get('[data-test=add-agent-cli-script]').text()).toBe('Script member');
    expect(w.find('[data-test=script-member-form]').exists()).toBe(false);
    await w.get('[data-test=add-agent-name]').setValue('Mail Poller');
    await w.get('[data-test=add-agent-cli]').setValue('script');
    await flushPromises();
    expect(w.find('[data-test=script-member-form]').exists()).toBe(true);
    expect(w.find('[data-test=add-agent]').exists()).toBe(false);
    expect(calls.some((c) => c.method === 'scripts.list')).toBe(true);
    expect(calls.some((c) => c.method === 'members.add')).toBe(false);
  });
});

const card = {
  member: { id: 9, teamId: 1, team: 'payments', name: 'Poller', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  kind: 'script', state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null,
} as unknown as AgentCard;

describe('a script member on the team page', () => {
  it('its tile shows script@version (latest: the version it runs), the last run and the next trigger', async () => {
    const calls: Calls = [];
    const w = keep(mount(ScriptMemberPane, { props: { card, api: fakeApi(calls), pollMs: 60_000 } }));
    await flushPromises();
    expect(w.get('[data-test=script-member-version]').text()).toBe('inbox-sms-alert@latest (v2)');
    expect(w.get('[data-test=script-member-last-status]').text()).toBe('Failed');
    expect(w.get('[data-test=script-member-last-run]').text()).toContain(new Date('2026-11-24T08:00:05Z').toLocaleString());
    expect(w.get('[data-test=script-member-next]').text()).toBe(`Next trigger: ${new Date('2026-11-24T09:00:00Z').toLocaleString()}`);
    expect(calls.find((c) => c.method === 'scripts.runs')!.params).toEqual({ memberId: 9, limit: 10 });
  });

  it('a member with no scheduled trigger and no run says so', async () => {
    const w = keep(mount(ScriptMemberPane, { props: { card, api: fakeApi([], {
      'scripts.memberGet': () => ({ ...scriptMember, version: 1, lastRun: null, nextTriggerAt: null }),
      'scripts.runs': () => [], 'scripts.memberState': () => ({ memberId: 9, variables: {}, consecutiveFailures: 0, updatedAt: null }),
    }), pollMs: 60_000 } }));
    await flushPromises();
    expect(w.get('[data-test=script-member-version]').text()).toBe('inbox-sms-alert@1');
    expect(w.get('[data-test=script-member-last-run]').text()).toBe('Last run: never');
    expect(w.get('[data-test=script-member-next]').text()).toBe('Next trigger: none scheduled');
    expect(w.find('[data-test=script-member-no-runs]').exists()).toBe(true);
    expect(w.find('[data-test=script-member-no-variables]').exists()).toBe(true);
  });

  it('its pane lists the last runs and the saved variables; Reset forgets one and reloads them', async () => {
    const calls: Calls = [];
    let vars: Record<string, unknown> = { lastSeen: 'gm:1:2:3', seen: [1, 2] };
    const w = keep(mount(ScriptMemberPane, { props: { card, api: fakeApi(calls, {
      'scripts.memberState': () => ({ memberId: 9, variables: vars, consecutiveFailures: 1, updatedAt: '' }),
      'scripts.memberResetVariable': (p) => { const { [p.name as string]: _gone, ...rest } = vars; vars = rest; return { reset: true }; },
    }), pollMs: 60_000 } }));
    await flushPromises();
    const runs = w.findAll('[data-test=script-member-run]');
    expect(runs.length).toBe(2);
    expect(runs[0].text()).toContain('Failed');
    expect(runs[0].text()).toContain('trigger 4');
    expect(runs[0].text()).toContain('mail: sign-in refused');
    expect(runs[1].text()).toContain('tell');
    expect(w.get('[data-test=script-variable-lastSeen]').text()).toContain('gm:1:2:3');
    expect(w.get('[data-test=script-variable-seen]').text()).toContain('[1,2]');

    await w.get('[data-test=script-variable-reset-lastSeen]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'scripts.memberResetVariable')!.params).toEqual({ memberId: 9, name: 'lastSeen' });
    expect(w.find('[data-test=script-variable-lastSeen]').exists()).toBe(false);
    expect(w.find('[data-test=script-variable-seen]').exists()).toBe(true);
  });

  it('a Visitor sees the runs and variables but no Reset, and Remove is disabled read-only', async () => {
    applyRemoteAccess('read');
    const calls: Calls = [];
    const w = keep(mount(ScriptMemberPane, { props: { card, api: fakeApi(calls), pollMs: 60_000 } }));
    await flushPromises();
    expect(w.findAll('[data-test=script-member-run]').length).toBe(2);
    expect(w.find('[data-test=script-variable-lastSeen]').exists()).toBe(true);
    expect(w.find('[data-test=script-variable-reset-lastSeen]').exists()).toBe(false);
    const remove = w.get('[data-test=script-member-remove]').element as HTMLButtonElement;
    expect(remove.disabled).toBe(true);
    expect(remove.title).toBe(READ_ONLY_TITLE);
    expect(calls.every((c) => !['scripts.memberResetVariable', 'members.remove'].includes(c.method))).toBe(true);
  });

  it('a Visitor cannot add a script member: Add is disabled read-only', async () => {
    applyRemoteAccess('read');
    const calls: Calls = [];
    const w = await openForm(calls);
    await w.get('[data-test=script-binding-mail] select').setValue('Work mail');
    const add = w.get('[data-test=script-member-add]').element as HTMLButtonElement;
    expect(add.disabled).toBe(true);
    expect(add.title).toBe(READ_ONLY_TITLE);
  });

  it('the team page shows a script member card as its script tile, an agent as before', async () => {
    const agent = { ...card, kind: 'agent', member: { ...card.member, id: 2, name: 'DeveloperAda' } };
    const calls: Calls = [];
    const w = keep(mount(TeamView, { props: { team, api: fakeApi(calls, { 'teams.agents': () => [card, agent] }), pollMs: 60_000 },
      global: { stubs: { MemberPane: { template: '<div data-test="agent-pane-stub" />' } } } }));
    await flushPromises();
    expect(w.find('[data-test=script-member-Poller]').exists()).toBe(true);
    expect(w.findAll('[data-test=agent-pane-stub]').length).toBe(1);
    expect(w.get('[data-test=script-member-version]').text()).toBe('inbox-sms-alert@latest (v2)');
  });
});
