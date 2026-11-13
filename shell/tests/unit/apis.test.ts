// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ConnectionDialog from '../../src/renderer/src/components/ConnectionDialog.vue';
import ConnectionsPanel from '../../src/renderer/src/components/ConnectionsPanel.vue';
import IntegrationDialog from '../../src/renderer/src/components/IntegrationDialog.vue';
import IntegrationsPanel from '../../src/renderer/src/components/IntegrationsPanel.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import type { ApiRow, Connection, YawbleApi } from '../../src/shared/api';

function fakeApi(answers: Record<string, (params: any) => unknown>, remote = false) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...(remote ? { remote: true } : {}),
  };
  return { api, calls };
}

const realConfirm = window.confirm;
afterEach(() => { window.confirm = realConfirm; });
/** happy-dom has no confirm to spy on: install one that answers the same every time and records what it was asked. */
function answerConfirm(answer: boolean) {
  const fn = vi.fn(() => answer);
  window.confirm = fn as unknown as typeof window.confirm;
  return fn;
}

const teams = [{ id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 }];
const conn = (o: Partial<Connection> = {}): Connection => ({
  id: 7, name: 'Pets key', kind: 'secret', fields: [{ key: 'token', label: 'API key or token', secret: true, shown: 'pets••••••a9Qz', set: true }],
  status: 'works', statusReason: null, testedAt: null, usedBy: ['pets'], ...o,
});
const apiRow = (o: Partial<ApiRow> = {}): ApiRow => ({
  kind: 'http', name: 'pets', baseUrl: 'https://api.pets.test/v1', docsUrl: null, specUrl: null, title: 'Pets', connection: { id: 7, name: 'Pets key', status: 'works' },
  auth: { type: 'bearer', name: null, description: null }, signIn: { label: 'Token', description: 'Send it as a bearer token.', fields: [{ key: 'token', label: 'Token', secret: true }] },
  status: 'read 14 actions from the OpenAPI spec', all: false, teams: [3], counts: { allowed: 9, ask: 5, off: 0, undecided: 0 }, ...o,
});
const node = (path: string, o: Record<string, unknown> = {}) => ({
  path, parent: '', name: path, depth: 1, summary: `does ${path}`, hasChildren: false, class: 'readonly', source: 'rule', reason: 'GET', state: 'free', ticked: false, tickedFrom: null,
  defaultTick: false, decision: null, gone: false, badge: null, seenVersion: null, suggested: 'allow', defaultDecision: null, teamDecision: null, effective: 'ask', origin: 'discovered', ...o,
});
const checklist = (nodes: unknown[] = [], runs = 1) => ({
  cli: 'pets', teamId: null, version: null, discoveredAt: '2026-11-10', runs, partial: false, partialReason: null, error: null, new: 0, changed: 0, gone: 0, choices: 0,
  kind: 'api', offeredAll: false, offeredTeams: [], counts: { allowed: 0, ask: 0, off: 0, undecided: nodes.length }, nodes, otherRules: [],
});
const stubs = { TasksPanel: true };

describe('APIs and Connections', () => {
  it('ApisPanel_ShowsCountsAndSignInStatus', async () => {
    const { api } = fakeApi({
      'integrations.list': () => ({ integrations: [], pendingAsks: 0 }),
      'integrations.mcpList': () => [],
      'integrations.apiList': () => [apiRow(), apiRow({ name: 'bare', baseUrl: 'https://bare.test', connection: null, counts: null, teams: [], status: null })],
      'integrations.connections': () => [conn(), conn({ id: 8, name: 'Other', usedBy: [], status: 'needs_reconnect', statusReason: 'the service said no' })],
      'integrations.tasks': () => [],
    });
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    expect(w.find('[data-test=tab-cli]').text()).toBe('CLIs (0)');
    expect(w.find('[data-test=tab-api]').text()).toBe('APIs (2)');
    expect(w.find('[data-test=tab-mcp]').text()).toBe('MCP servers (0)');
    expect(w.find('[data-test=tab-connections]').text()).toBe('Connections (2)');
    const card = w.find('[data-test=api-pets]');
    expect(card.text()).toContain('https://api.pets.test/v1');
    expect(card.text()).toContain('Pets key');
    expect(card.text()).toContain('works');
    expect(card.text()).toContain('14 actions: 9 allowed, 5 ask first');
    expect(card.text()).toContain('Offered to ops');
    expect(w.find('[data-test=api-bare]').text()).toContain('needs a connection');
    expect(w.find('[data-test=api-add]').text()).toBe('+ Add API');
  });

  it('ApiDialog_DiscoverThenSignInThenSave_CallsMethodsInOrder', async () => {
    let discovered = false;
    let created = false;
    const { api, calls } = fakeApi({
      'integrations.apiSave': (p) => { created = true; return { ok: true, name: p.name ?? 'api.pets.test' }; },
      'integrations.apiList': () => !created ? [] : [apiRow(discovered ? { connection: null, status: 'read 2 actions from the OpenAPI spec' } : { connection: null, signIn: null, status: null, counts: null })],
      'integrations.connections': () => [],
      'integrations.discover': () => { discovered = true; return { state: 'done', found: 2 }; },
      'integrations.discoverStatus': () => ({ state: discovered ? 'done' : 'idle' }),
      'integrations.checklist': () => checklist(discovered ? [node('listPets'), node('getPet')] : []),
      'integrations.connectionSave': () => conn({ id: 9, name: 'pets key' }),
      'integrations.saveChecklist': () => ({ ok: true, allowed: 2, asked: 0, denied: 0, removed: 0 }),
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: '', teams } });
    await flushPromises();
    expect(w.find('[data-test=api-signin]').exists()).toBe(false);
    await w.find('[data-test=api-base-url]').setValue('https://api.pets.test/v1');
    await w.find('[data-test=api-name]').setValue('pets');
    await w.find('[data-test=api-discover]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=api-result]').text()).toBe('read 2 actions from the OpenAPI spec');
    expect(w.find('[data-test=api-signin]').text()).toContain('Paste your Pets API key');
    expect(w.find('[data-test=api-signin-description]').text()).toContain('bearer token');
    await w.find('[data-test=api-secret-token]').setValue('sk-test-123456789');
    await w.find('[data-test=api-save]').trigger('click');
    await flushPromises();

    const order = calls.map((c) => c.method).filter((m) => ['integrations.apiSave', 'integrations.discover', 'integrations.connectionSave', 'integrations.saveChecklist'].includes(m));
    expect(order).toEqual(['integrations.apiSave', 'integrations.discover', 'integrations.connectionSave', 'integrations.apiSave', 'integrations.saveChecklist']);
    const saves = calls.filter((c) => c.method === 'integrations.apiSave');
    expect(saves[0]!.params).toMatchObject({ name: 'pets', baseUrl: 'https://api.pets.test/v1' });
    expect(saves[0]!.params).not.toHaveProperty('connectionId');
    expect(saves[1]!.params).toMatchObject({ name: 'pets', connectionId: 9 });
    const cs = calls.find((c) => c.method === 'integrations.connectionSave')!;
    expect(cs.params.secrets).toEqual({ token: 'sk-test-123456789' });
    expect(cs.params.name).toBe('Pets key');
    expect(cs.params.reuseIdentical).toBe(true);
    expect(w.emitted('close')).toBeTruthy();
    expect(w.text()).not.toContain('sk-test-123456789');
  });

  it('ApiDialog_NoSpecFound_KeepsUrlsEditableAndShowsTheSentence', async () => {
    const { api } = fakeApi({
      'integrations.apiSave': () => ({ ok: true, name: 'pets' }),
      'integrations.apiList': () => [apiRow({ connection: null, signIn: null, status: 'not working: no OpenAPI description was found', counts: null })],
      'integrations.connections': () => [],
      'integrations.discover': () => ({ state: 'failed', error: 'no OpenAPI description was found' }),
      'integrations.discoverStatus': () => ({ state: 'failed', error: 'no OpenAPI description was found' }),
      'integrations.checklist': () => { throw new Error('x is not set up yet'); },
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: '', teams } });
    await w.find('[data-test=api-docs-url]').setValue('https://x.test/docs');
    await w.find('[data-test=api-discover]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=api-result]').text()).toContain('no OpenAPI description was found');
    expect((w.find('[data-test=api-docs-url]').element as HTMLInputElement).disabled).toBe(false);
    expect((w.find('[data-test=api-base-url]').element as HTMLInputElement).disabled).toBe(false);
  });

  it('DiscoverChecklist_Api_GroupsByTagAndShowsMethodAndRoute', async () => {
    const { api } = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.connections': () => [],
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => checklist([
        node('listPets', { tag: 'pets', method: 'GET', route: '/pets' }),
        node('GET /owners', { tag: 'owners', method: 'GET', route: '/owners' }),
        node('ping', { tag: null, method: 'GET', route: '/ping' }),
      ]),
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    const list = w.find('[data-test=discover-list]');
    expect(list.text()).toContain('owners');
    expect(list.text()).toContain('Other');
    expect(w.find('[data-test=node-listPets]').text()).toContain('GET /pets');
    const owners = w.findAll('[data-test]').find((e) => e.attributes('data-test') === 'node-GET_/owners')!;
    expect(owners.find('[data-test=route]').exists()).toBe(false); // the action id already reads "GET /owners": not repeated
    expect(w.find('[data-test=node-ping]').text()).toContain('GET /ping');
  });

  it('ApiDialog_Cancel_RemovesAnApiThisSessionCreated_ButNeverOnEdit', async () => {
    const mk = (existing = false) => {
      let saved = existing;
      return fakeApi({
      'integrations.apiSave': () => { saved = true; return { ok: true, name: 'pets' }; },
      'integrations.apiList': () => (saved ? [apiRow({ connection: null })] : []),
      'integrations.apiRemove': () => ({ ok: true, soleConnection: null, connectionRemoved: false }),
      'integrations.connections': () => [],
      'integrations.discover': () => ({ state: 'done' }),
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => checklist(),
      });
    };
    const a = mk();
    const w = mount(IntegrationDialog, { props: { api: a.api, kind: 'api', name: '', teams } });
    await flushPromises();
    await w.find('[data-test=dialog-close]').trigger('click');
    await flushPromises();
    expect(a.calls.some((c) => c.method === 'integrations.apiRemove')).toBe(false); // nothing was created yet
    await w.find('[data-test=api-base-url]').setValue('https://api.pets.test/v1');
    await w.find('[data-test=api-name]').setValue('pets');
    await w.find('[data-test=api-discover]').trigger('click');
    await flushPromises();
    await w.find('[data-test=dialog-close]').trigger('click');
    await flushPromises();
    expect(a.calls.find((c) => c.method === 'integrations.apiRemove')!.params).toEqual({ name: 'pets', removeConnection: false });
    expect(w.emitted('close')).toBeTruthy();

    const e = mk(true);
    const ew = mount(IntegrationDialog, { props: { api: e.api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    await ew.find('[data-test=dialog-close]').trigger('click');
    await flushPromises();
    expect(e.calls.some((c) => c.method === 'integrations.apiRemove')).toBe(false);
  });

  it('DiscoverChecklist_Api_OtherRulesRemoveUsesApiDeleteRule', async () => {
    const { api, calls } = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.connections': () => [],
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => ({ ...checklist([node('listPets')]), otherRules: [{ id: 41, teamId: null, pattern: 'odd*', decision: 'allow' }] }),
      'integrations.apiDeleteRule': () => ({ ok: true }),
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    await w.find('[data-test=other-remove-41]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.apiDeleteRule')!.params).toEqual({ id: 41 });
  });

  it('ApiDialog_FailedRulesSave_KeepsTheDialogOpenAndShowsTheError', async () => {
    let discovered = false;
    const { api } = fakeApi({
      'integrations.apiSave': () => ({ ok: true, name: 'pets' }),
      'integrations.apiList': () => [apiRow({ connection: null })],
      'integrations.connections': () => [conn()],
      'integrations.discover': () => { discovered = true; return { state: 'done' }; },
      'integrations.discoverStatus': () => ({ state: discovered ? 'done' : 'idle' }),
      'integrations.checklist': () => checklist(discovered ? [node('listPets')] : []),
      'integrations.saveChecklist': () => { throw new Error('the rules could not be saved'); },
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: '', teams } });
    await flushPromises();
    await w.find('[data-test=api-base-url]').setValue('https://api.pets.test/v1');
    await w.find('[data-test=api-discover]').trigger('click');
    await flushPromises();
    await w.find('[data-test=api-save]').trigger('click');
    await flushPromises();
    expect(w.emitted('close')).toBeFalsy();
    expect(w.text()).toContain('the rules could not be saved');
  });

  it('ApiDialog_AddWithAnExistingName_IsRefusedBeforeSaving', async () => {
    const { api, calls } = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.connections': () => [],
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: '', teams } });
    await flushPromises();
    await w.find('[data-test=api-base-url]').setValue('https://other.test');
    await w.find('[data-test=api-name]').setValue('Pets');
    await w.find('[data-test=api-discover]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=dialog-error]').text()).toBe('an API named Pets already exists; edit it instead');
    expect(calls.some((c) => c.method === 'integrations.apiSave')).toBe(false);
  });

  it('DiscoverChecklist_Api_ATagNamedOtherIsNotMergedWithTheUntaggedGroup', async () => {
    const { api } = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.connections': () => [],
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => checklist([node('a', { tag: 'Other' }), node('b', { tag: null }), node('c', { tag: 'pets' })]),
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    const groups = w.findAll('[data-test]').filter((e) => (e.attributes('data-test') ?? '').startsWith('tick-reads-tag'));
    expect(groups).toHaveLength(3);
  });

  it('ConnectionDialog_EditKeepsEmptySecret', async () => {
    const c = conn({ fields: [
      { key: 'id', label: 'Account id', secret: true, shown: '••••••', set: true },
      { key: 'token', label: 'API key or token', secret: true, shown: 'sk-t••••••a9Qz', set: true },
    ] });
    const { api, calls } = fakeApi({ 'integrations.connectionSave': () => c });
    const w = mount(ConnectionDialog, { props: { api, connection: c } });
    const box = w.find('[data-test=connection-field-value-1]');
    expect((box.element as HTMLInputElement).value).toBe('');
    expect((box.element as HTMLInputElement).placeholder).toBe('sk-t••••••a9Qz');
    expect((box.element as HTMLInputElement).disabled).toBe(true);
    await w.find('[data-test=connection-field-replace-0]').trigger('click');
    await w.find('[data-test=connection-field-value-0]').setValue('new-id-value');
    await w.find('[data-test=connection-save]').trigger('click');
    await flushPromises();
    const p = calls.find((x) => x.method === 'integrations.connectionSave')!.params;
    expect(p.id).toBe(7);
    expect(p.secrets).toEqual({ id: 'new-id-value' });
    expect(p.secrets).not.toHaveProperty('token');
    expect(p.fields.map((f: any) => f.key)).toEqual(['id', 'token']);
  });

  it('ConnectionDialog_NewStartsWithOneSecretAndAddsFields', async () => {
    const { api, calls } = fakeApi({ 'integrations.connectionSave': () => conn() });
    const w = mount(ConnectionDialog, { props: { api } });
    await w.find('[data-test=connection-name]').setValue('Mine');
    await w.find('[data-test=connection-field-value-0]').setValue('abc-secret-123');
    await w.find('[data-test=connection-add-field]').trigger('click');
    await w.find('[data-test=connection-field-label-1]').setValue('Account id');
    await w.find('[data-test=connection-field-secret-1]').setValue(false);
    await w.find('[data-test=connection-field-value-1]').setValue('acct_1');
    await w.find('[data-test=connection-save]').trigger('click');
    await flushPromises();
    const p = calls[0]!.params;
    expect(p).not.toHaveProperty('id');
    expect(p.secrets).toEqual({ token: 'abc-secret-123' });
    expect(p.fields).toEqual([
      { key: 'token', label: 'API key or token', secret: true },
      { key: 'account_id', label: 'Account id', secret: false, value: 'acct_1' },
    ]);
  });

  it('ConnectionsPanel_RemoveInUseConfirms', async () => {
    const confirm = answerConfirm(true);
    let removed = false;
    const { api, calls } = fakeApi({
      'integrations.connections': () => (removed ? [] : [conn({ usedBy: ['pets', 'bikes'] })]),
      'integrations.connectionRemove': () => { removed = true; return { ok: true }; },
    });
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=connection-usedby]').text()).toContain('pets, bikes');
    expect(w.text()).toContain('pets••••••a9Qz');
    await w.find('[data-test=connection-remove-7]').trigger('click');
    await flushPromises();
    expect(confirm.mock.calls[0]![0]).toContain('pets, bikes');
    expect(calls.find((c) => c.method === 'integrations.connectionRemove')!.params).toEqual({ id: 7, confirm: true });
    expect(w.find('[data-test=connection-7]').exists()).toBe(false);
  });

  it('ConnectionsPanel_RemoveDeclined_DoesNothing', async () => {
    answerConfirm(false);
    const { api, calls } = fakeApi({ 'integrations.connections': () => [conn()] });
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=connection-remove-7]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'integrations.connectionRemove')).toBe(false);
  });

  it('ConnectionsPanel_Test_ShowsTheResult', async () => {
    const { api, calls } = fakeApi({
      'integrations.connections': () => [conn({ status: 'untested' })],
      'integrations.connectionTest': () => ({ status: 'needs_reconnect', reason: 'the service said the key is not valid' }),
    });
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=connection-test-7]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'integrations.connectionTest')).toBe(true);
    expect(w.find('[data-test=connections-note]').text()).toContain('needs reconnect');
  });

  it('ApisPanel_Remove_AsksAboutTheSoleConnection', async () => {
    const confirm = answerConfirm(true);
    const { api, calls } = fakeApi({
      'integrations.list': () => ({ integrations: [], pendingAsks: 0 }),
      'integrations.mcpList': () => [],
      'integrations.apiList': () => [apiRow()],
      'integrations.connections': () => [conn({ usedBy: ['pets'] })],
      'integrations.apiRemove': () => ({ ok: true, soleConnection: { id: 7, name: 'Pets key' }, connectionRemoved: true }),
      'integrations.tasks': () => [],
    });
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    await w.find('[data-test=api-remove-pets]').trigger('click');
    await flushPromises();
    expect(confirm.mock.calls.map((c) => c[0])).toEqual(['Remove pets? Agents can no longer use it.', "Also delete the connection 'Pets key'?"]);
    expect(calls.find((c) => c.method === 'integrations.apiRemove')!.params).toEqual({ name: 'pets', removeConnection: true });
  });

  it('ApisPanel_Remove_SharedConnectionIsNotOffered', async () => {
    const confirm = answerConfirm(true);
    const { api, calls } = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.connections': () => [conn({ usedBy: ['pets', 'bikes'] })],
      'integrations.apiRemove': () => ({ ok: true, soleConnection: null, connectionRemoved: false }),
    });
    const ApisPanel = (await import('../../src/renderer/src/components/ApisPanel.vue')).default;
    const w = mount(ApisPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=api-remove-pets]').trigger('click');
    await flushPromises();
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(calls.find((c) => c.method === 'integrations.apiRemove')!.params).toEqual({ name: 'pets', removeConnection: false });
  });

  it('Remote_ConnectionsHidden', async () => {
    const { api, calls } = fakeApi({
      'integrations.list': () => ({ integrations: [], pendingAsks: 0 }),
      'integrations.mcpList': () => [],
      'integrations.apiList': () => [apiRow()],
      'integrations.tasks': () => [],
    }, true);
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    expect(w.find('[data-test=connections-remote]').text()).toBe('Connections are managed on the computer itself.');
    expect(w.find('[data-test=connection-add]').exists()).toBe(false);
    expect(calls.some((c) => c.method.startsWith('integrations.connection'))).toBe(false);

    const d = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => checklist([node('listPets')]),
    }, true);
    const dw = mount(IntegrationDialog, { props: { api: d.api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    expect((dw.find('[data-test=api-discover]').element as HTMLButtonElement).disabled).toBe(true);
    expect((dw.find('[data-test=api-save]').element as HTMLButtonElement).disabled).toBe(true);
    expect(dw.find('[data-test=api-remote]').text()).toContain('managed on the computer itself');
    expect(dw.find('[data-test=api-secret-token]').exists()).toBe(false);
    expect(dw.find('[data-test=discover-list]').exists()).toBe(true); // the checklist stays usable
    expect(d.calls.some((c) => c.method.startsWith('integrations.connection'))).toBe(false);
  });

  it('TeamIntegrationsTab_ListsApisOfferedToTheTeam', async () => {
    const team = { id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0, paused: false, archived: false } as any;
    const { api } = fakeApi({
      'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [], 'teams.push': () => ({ on: false, origin: null }),
      'teams.repos': () => ({ main: null, extras: [] }), 'teams.approvals': () => [],
      'integrations.list': () => ({ integrations: [], pendingAsks: 0 }),
      'integrations.mcpList': () => [],
      'integrations.apiList': () => [apiRow({ teams: [3] }), apiRow({ name: 'other', teams: [9] }), apiRow({ name: 'everyone', all: true, teams: [], counts: null })],
    });
    const w = mount(TeamManageDialog, { props: { team, api }, global: { stubs: { IntegrationDialog: true, Modal: { template: '<div><slot /></div>' } } } });
    await flushPromises();
    await w.find('[data-test=manage-tab-integrations]').trigger('click');
    expect(w.find('[data-test=team-int-pets]').text()).toContain('API');
    expect(w.find('[data-test=team-int-pets]').text()).toContain('14 actions: 9 allowed, 5 ask first');
    expect(w.find('[data-test=team-int-everyone]').exists()).toBe(true);
    expect(w.find('[data-test=team-int-other]').exists()).toBe(false);
    await w.find('[data-test=team-int-edit-pets]').trigger('click');
    const dlg = w.findComponent(IntegrationDialog);
    expect(dlg.props()).toMatchObject({ kind: 'api', name: 'pets', initialScope: 3 });
  });
});

describe('APIs and Connections: final-review fixes', () => {
  it('ApiDialog_AddSendsCreate_SoAHostThatIsAlreadyAnApiIsRefused', async () => {
    const { api, calls } = fakeApi({
      'integrations.apiList': () => [apiRow({ name: 'api.pets.test' })],
      'integrations.connections': () => [],
      'integrations.apiSave': () => { throw new Error('an API named api.pets.test already exists; edit it instead'); },
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: '', teams } });
    await flushPromises();
    await w.find('[data-test=api-base-url]').setValue('https://api.pets.test/v2');
    await w.find('[data-test=api-discover]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.apiSave')!.params).toMatchObject({ create: true, baseUrl: 'https://api.pets.test/v2' });
    expect(w.find('[data-test=dialog-error]').text()).toBe('an API named api.pets.test already exists; edit it instead');
    expect(calls.some((c) => c.method === 'integrations.discover')).toBe(false);
    await w.find('[data-test=dialog-close]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'integrations.apiRemove')).toBe(false); // nothing was created, nothing is removed
  });

  it('ApiDialog_EditNeverSendsCreate', async () => {
    const { api, calls } = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.connections': () => [conn()],
      'integrations.apiSave': () => ({ ok: true, name: 'pets' }),
      'integrations.discover': () => ({ state: 'done' }),
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => checklist([node('listPets')]),
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    await w.find('[data-test=api-discover]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.apiSave')!.params).not.toHaveProperty('create');
  });

  it('ApiDialog_NoSignInInTheSpec_OffersTheChoice_AndSavesItAsAuth', async () => {
    let auth: ApiRow['auth'] = null;
    const signInOf = (a: ApiRow['auth']): ApiRow['signIn'] => !a ? null
      : { label: 'API key', description: null, fields: [{ key: a.type === 'bearer' ? 'token' : a.name ?? 'key', label: a.type === 'bearer' ? 'Token' : 'API key', secret: true }] };
    const { api, calls } = fakeApi({
      'integrations.apiList': () => [apiRow({ connection: null, auth, signIn: signInOf(auth) })],
      'integrations.connections': () => [conn()],
      'integrations.apiSave': (p) => { if ('auth' in p) auth = p.auth ? { ...p.auth, name: p.auth.name ?? null, description: null, chosen: true } : null; return { ok: true, name: 'pets' }; },
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => checklist([node('listPets')]),
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    const choice = w.find('[data-test=api-auth-choice]');
    expect(choice.text()).toContain('How does this API sign in?');
    expect((w.find('[data-test=api-auth-none]').element as HTMLInputElement).checked).toBe(true); // the default when the spec says nothing
    expect(w.find('[data-test=api-signin]').exists()).toBe(false);

    await w.find('[data-test=api-auth-header]').setValue(true);
    await flushPromises();
    expect((w.find('[data-test=api-auth-header-name]').element as HTMLInputElement).value).toBe('X-API-Key');
    expect(calls.filter((c) => c.method === 'integrations.apiSave').at(-1)!.params).toEqual({ name: 'pets', auth: { type: 'header', name: 'X-API-Key' } });
    expect(w.find('[data-test=api-signin]').exists()).toBe(true); // then the normal sign-in box and connection picker
    expect(w.find('[data-test=api-secret-X-API-Key]').exists()).toBe(true);
    expect(w.find('[data-test=api-auth-choice]').exists()).toBe(true); // still changeable: the person chose it

    await w.find('[data-test=api-auth-query]').setValue(true);
    await flushPromises();
    expect((w.find('[data-test=api-auth-query-name]').element as HTMLInputElement).value).toBe('api_key');
    expect(calls.filter((c) => c.method === 'integrations.apiSave').at(-1)!.params).toEqual({ name: 'pets', auth: { type: 'query', name: 'api_key' } });

    await w.find('[data-test=api-auth-bearer]').setValue(true);
    await flushPromises();
    expect(calls.filter((c) => c.method === 'integrations.apiSave').at(-1)!.params).toEqual({ name: 'pets', auth: { type: 'bearer' } });

    await w.find('[data-test=api-auth-none]').setValue(true);
    await flushPromises();
    expect(calls.filter((c) => c.method === 'integrations.apiSave').at(-1)!.params).toEqual({ name: 'pets', auth: null });
    expect(w.find('[data-test=api-signin]').exists()).toBe(false);
  });

  it('ApiDialog_SpecSignIn_DoesNotAskHowItSignsIn', async () => {
    const { api } = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.connections': () => [conn()],
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => checklist([node('listPets')]),
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    expect(w.find('[data-test=api-auth-choice]').exists()).toBe(false);
    expect(w.find('[data-test=api-signin]').exists()).toBe(true);
  });

  it('ConnectionsPanel_UnclearTest_SaysTheStatusIsUnchanged', async () => {
    const { api } = fakeApi({
      'integrations.connections': () => [conn({ status: 'needs_reconnect', statusReason: 'the service answered 401' })],
      'integrations.connectionTest': () => ({ status: 'needs_reconnect', reason: 'the service did not answer clearly (pets answered 503 Service Unavailable); status unchanged', changed: false }),
    });
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=connection-test-7]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=connections-note]').text()).toBe('Pets key: the service did not answer clearly (pets answered 503 Service Unavailable); status unchanged');
  });

  it('ConnectionsPanel_ReloadsOnConnectionsChanged', async () => {
    let listener: ((e: { event: string; data: unknown }) => void) | null = null;
    const { api, calls } = fakeApi({ 'integrations.connections': () => [conn()] });
    api.onEvent = (l) => { listener = l as typeof listener; return () => { listener = null; }; };
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    const before = calls.filter((c) => c.method === 'integrations.connections').length;
    listener!({ event: 'connections.changed', data: null });
    await flushPromises();
    expect(calls.filter((c) => c.method === 'integrations.connections').length).toBe(before + 1);
    w.unmount();
    expect(listener).toBeNull();
  });

  it('Remote_HidesTheConnectionsCount_AndApiAddRemoveDiscover', async () => {
    const { api, calls } = fakeApi({
      'integrations.list': () => ({ integrations: [], pendingAsks: 0 }),
      'integrations.mcpList': () => [],
      'integrations.apiList': () => [apiRow()],
      'integrations.tasks': () => [],
    }, true);
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    expect(w.find('[data-test=tab-connections]').text()).toBe('Connections');
    expect(w.find('[data-test=api-add]').exists()).toBe(false);
    expect(w.find('[data-test=api-remove-pets]').exists()).toBe(false);
    expect(w.find('[data-test=apis-remote]').text()).toBe('APIs are added, removed and discovered on the computer itself.');
    expect(w.find('[data-test=api-edit-pets]').exists()).toBe(true); // the checklist stays usable remotely
    expect(calls.some((c) => c.method.startsWith('integrations.connection'))).toBe(false);

    const d = fakeApi({
      'integrations.apiList': () => [apiRow()],
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => checklist([node('listPets')]),
    }, true);
    const dw = mount(IntegrationDialog, { props: { api: d.api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    expect(dw.find('[data-test=discover-start-pets]').exists()).toBe(false);
    expect(dw.find('[data-test=discover-remote]').text()).toBe('Discover runs on the computer itself.');
    expect(dw.find('[data-test=api-auth-choice]').exists()).toBe(false);
  });
});
