// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import ApisPanel from '../../src/renderer/src/components/ApisPanel.vue';
import ConnectionDialog from '../../src/renderer/src/components/ConnectionDialog.vue';
import ConnectionsPanel from '../../src/renderer/src/components/ConnectionsPanel.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import IntegrationDialog from '../../src/renderer/src/components/IntegrationDialog.vue';
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

const teams = [{ id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 }];
const login = (o: Partial<Connection> = {}): Connection => ({
  id: 5, name: 'intranet.test login', kind: 'website', fields: [{ key: 'site', label: 'Site', secret: false, shown: 'https://intranet.test/', set: true }],
  status: 'works', statusReason: null, testedAt: null, usedBy: [], ...o,
});
const siteRow = (o: Partial<ApiRow> = {}): ApiRow => ({
  kind: 'website', name: 'intranet.test', baseUrl: 'https://intranet.test/', docsUrl: null, specUrl: null, title: null,
  connection: { id: 5, name: 'intranet.test login', status: 'needs_reconnect', statusReason: 'signed out' },
  auth: null, signIn: null, status: null, all: false, teams: [3], counts: { allowed: 6, ask: 0, off: 0, undecided: 0 }, ...o,
});

describe('Website login and Add website', () => {
  it('NewConnectionOffersWebsiteLogin', async () => {
    const { api, calls } = fakeApi({ 'integrations.connectionSave': () => login() });
    const w = mount(ConnectionDialog, { props: { api } });
    expect(w.find('[data-test=connection-kind]').text()).toContain('API key or token');
    expect(w.find('[data-test=connection-kind]').text()).toContain('Website login');
    expect(w.find('[data-test=connection-field-0]').exists()).toBe(true);
    await w.find('[data-test=connection-kind-website]').setValue(true);
    expect(w.find('[data-test=connection-field-0]').exists()).toBe(false);
    expect(w.find('[data-test=connection-site]').exists()).toBe(true);
    await w.find('[data-test=connection-site]').setValue('https://intranet.test');
    await w.find('[data-test=connection-save]').trigger('click');
    await flushPromises();
    expect(calls[0]).toEqual({ method: 'integrations.connectionSave', params: { kind: 'website', site: 'https://intranet.test' } });
    expect(w.emitted('saved')).toBeTruthy();
  });

  it('WebsiteLoginSignInThenDone', async () => {
    const { api, calls } = fakeApi({
      'integrations.connectionSave': () => login(),
      'integrations.websiteSignIn': () => ({ ok: true }),
      'integrations.websiteSignInDone': () => ({ ok: true }),
    });
    const w = mount(ConnectionDialog, { props: { api, startKind: 'website' } });
    expect(w.find('[data-test=connection-signin]').exists()).toBe(false); // Save first
    await w.find('[data-test=connection-site]').setValue('https://intranet.test');
    await w.find('[data-test=connection-save]').trigger('click');
    await flushPromises();
    await w.find('[data-test=connection-signin]').trigger('click');
    await flushPromises();
    expect(calls.at(-1)).toEqual({ method: 'integrations.websiteSignIn', params: { connectionId: 5 } });
    expect(w.find('[data-test=connection-signin-note]').text()).toBe('A sign-in window is open. Sign in there, then press Done.');
    await w.find('[data-test=connection-signin-done]').trigger('click');
    await flushPromises();
    expect(calls.at(-1)).toEqual({ method: 'integrations.websiteSignInDone', params: { connectionId: 5 } });
    expect(w.find('[data-test=connection-signin-note]').exists()).toBe(false);
  });

  it('PanelReconnectIsSignInAgain', async () => {
    const { api, calls } = fakeApi({
      'integrations.connections': () => [login({ status: 'needs_reconnect', statusReason: 'signed out' })],
      'integrations.websiteSignIn': () => ({ ok: true }),
      'integrations.websiteSignInDone': () => ({ ok: true }),
    });
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=connection-kind]').text()).toBe('Website login');
    expect(w.find('[data-test=connection-signin-5]').text()).toBe('Reconnect');
    await w.find('[data-test=connection-signin-5]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'integrations.websiteSignIn' && c.params.connectionId === 5)).toBe(true);
    expect(w.find('[data-test=connection-signin-note]').exists()).toBe(true);
    await w.find('[data-test=connection-signin-done-5]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'integrations.websiteSignInDone')).toBe(true);
  });

  it('AddWebsiteSavesKindWebsite', async () => {
    const calls: { method: string; params: any }[] = [];
    let created = false;
    const { api } = fakeApi({
      'integrations.apiSave': (p) => { calls.push({ method: 'apiSave', params: p }); created = true; return { ok: true, name: p.name ?? 'intranet.test' }; },
      'integrations.apiList': () => (created ? [siteRow({ connection: null })] : []),
      'integrations.connections': () => [login(), login({ id: 6, name: 'secret one', kind: 'secret' })],
      'integrations.discover': () => ({ state: 'done', found: 6 }),
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => { throw new Error('not set up yet'); },
      'integrations.saveChecklist': () => ({ ok: true }),
    });
    const w = mount(ApisPanel, { props: { api, teams } });
    await flushPromises();
    expect(w.find('[data-test=website-add]').text()).toBe('+ Add website');
    await w.find('[data-test=website-add]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=api-docs-url]').exists()).toBe(false);
    const options = w.find('[data-test=website-login-pick]').findAll('option').map((o) => o.text());
    expect(options).toEqual(['None (public site)', 'intranet.test login', 'New website login…']);
    await w.find('[data-test=api-base-url]').setValue('https://intranet.test');
    await w.find('[data-test=website-login-pick]').setValue('5');
    await w.find('[data-test=api-discover]').trigger('click');
    await flushPromises();
    expect(calls[0]!.params).toMatchObject({ kind: 'website', url: 'https://intranet.test', connectionId: 5, create: true });
    await w.find('[data-test=api-save]').trigger('click');
    await flushPromises();
    expect(calls[1]!.params).toMatchObject({ kind: 'website', name: 'intranet.test', url: 'https://intranet.test', connectionId: 5 });
  });

  it('WebsiteCardShowsLoginStatus', async () => {
    const { api } = fakeApi({
      'integrations.apiList': () => [siteRow(), siteRow({ name: 'public.test', baseUrl: 'https://public.test/', connection: null })],
      'integrations.connections': () => [login({ status: 'needs_reconnect', statusReason: 'signed out' })],
    });
    const w = mount(ApisPanel, { props: { api, teams } });
    await flushPromises();
    const card = w.find('[data-test="api-intranet.test"]');
    expect(card.text()).toContain('https://intranet.test/');
    expect(card.text()).toContain('Website');
    expect(card.text()).toContain('intranet.test login');
    expect(card.text()).toContain('needs reconnect');
    expect(card.text()).toContain('signed out');
    expect(w.find('[data-test="api-public.test"]').text()).toContain('public site, no login');
  });

  it('RemoteViewHidesSignIn', async () => {
    const { api } = fakeApi({ 'integrations.apiList': () => [siteRow()] }, true);
    const panel = mount(ApisPanel, { props: { api, teams } });
    await flushPromises();
    expect(panel.find('[data-test=website-add]').exists()).toBe(false);
    const remoteDialog = fakeApi({ 'integrations.apiList': () => [siteRow()], 'integrations.connections': () => [] }, true);
    const dlg = mount(IntegrationDialog, { props: { api: remoteDialog.api, kind: 'api', name: 'intranet.test', teams, website: true } });
    await flushPromises();
    expect(dlg.find('[data-test=website-login-pick]').exists()).toBe(false);
    expect(dlg.find('[data-test=website-login-remote]').exists()).toBe(true);
    const cd = fakeApi({ 'integrations.connectionSave': () => login() }, true);
    const c = mount(ConnectionDialog, { props: { api: cd.api, connection: login() } });
    expect(c.find('[data-test=connection-signin]').exists()).toBe(false);
    expect(c.find('[data-test=connection-signin-done]').exists()).toBe(false);
  });

  it('TeamDialogOpensAWebsiteAsAWebsite', async () => {
    const { api } = fakeApi({
      'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
      'teams.push': () => ({ on: false, origin: null }), 'teams.repos': () => ({ main: null, extras: [] }), 'teams.approvals': () => [],
      'integrations.list': () => ({ integrations: [], pendingAsks: 0 }), 'integrations.mcpList': () => [],
      'integrations.apiList': () => [siteRow()],
    });
    const team = { id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0, paused: false, archived: false } as any;
    const w = mount(TeamManageDialog, { props: { team, api }, global: { stubs: { IntegrationDialog: true, Modal: { template: '<div><slot /></div>' } } } });
    await flushPromises();
    await w.find('[data-test=manage-tab-integrations]').trigger('click');
    expect(w.find('[data-test="team-int-intranet.test"]').text()).toContain('Website');
    await w.find('[data-test="team-int-edit-intranet.test"]').trigger('click');
    expect(w.findComponent(IntegrationDialog).props()).toMatchObject({ kind: 'api', website: true, name: 'intranet.test' });
  });

  it('SignInNoteClearsWhenTheConnectionChanges', async () => {
    let listener: ((e: { event: string; data: any }) => void) | null = null;
    const { api } = fakeApi({
      'integrations.connections': () => [login()],
      'integrations.websiteSignIn': () => ({ ok: true }),
    });
    api.onEvent = (l) => { listener = l; return () => {}; };
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=connection-signin-5]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=connection-signin-note]').exists()).toBe(true);
    listener!({ event: 'connections.changed', data: {} });
    await flushPromises();
    expect(w.find('[data-test=connection-signin-note]').exists()).toBe(false);
    expect(w.find('[data-test=connection-signin-5]').exists()).toBe(true);
  });

  it('WebsiteTestShowsCheckingUntilTheConnectionChanges', async () => {
    let listener: ((e: { event: string; data: any }) => void) | null = null;
    let status: Connection['status'] = 'works';
    let reason: string | null = null;
    const { api } = fakeApi({
      'integrations.connections': () => [login({ status, statusReason: reason })],
      'integrations.connectionTest': () => ({ status: 'works', reason: 'checking the site', changed: false, checking: true }),
    });
    api.onEvent = (l) => { listener = l; return () => {}; };
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    await w.find('[data-test=connection-test-5]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=connection-status]').text()).toBe('checking…');
    expect((w.find('[data-test=connection-test-5]').element as HTMLButtonElement).disabled).toBe(true);
    status = 'needs_reconnect';
    reason = 'the site sent the page to idp.test, outside intranet.test: sign in again';
    listener!({ event: 'connections.changed', data: {} });
    await flushPromises();
    expect(w.find('[data-test=connection-status]').text()).toBe(`needs reconnect: ${reason}`);
  });

  it('UntestedShowsItsReasonAndNeverReadsNeedsReconnect', async () => {
    const { api } = fakeApi({
      'integrations.connections': () => [login({ status: 'untested', statusReason: 'could not check the site: the page took too long to load' })],
      'integrations.connectionTest': () => ({ status: 'untested', reason: 'nothing proved', changed: true }),
    });
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=connection-status]').text()).toBe('untested: could not check the site: the page took too long to load');
    await w.find('[data-test=connection-test-5]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=connections-note]').text()).toBe('intranet.test login: untested (nothing proved)');
  });

  it('WebsiteCardShowsAnUntestedReason', async () => {
    const { api } = fakeApi({
      'integrations.apiList': () => [siteRow({ connection: { id: 5, name: 'intranet.test login', status: 'untested', statusReason: 'could not check the site' } })],
      'integrations.connections': () => [login({ status: 'untested', statusReason: 'could not check the site' })],
    });
    const w = mount(ApisPanel, { props: { api, teams } });
    await flushPromises();
    const card = w.find('[data-test="api-intranet.test"]');
    expect(card.text()).toContain('untested');
    expect(card.text()).toContain('could not check the site');
  });

  it('NewWebsiteLoginFromAddWebsitePrefillsTheSite', async () => {
    const { api } = fakeApi({
      'integrations.apiList': () => [],
      'integrations.connections': () => [login()],
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => { throw new Error('not set up yet'); },
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: '', teams, website: true } });
    await flushPromises();
    await w.find('[data-test=api-base-url]').setValue('https://shop.example.test/');
    await w.find('[data-test=website-login-pick]').setValue('new');
    await flushPromises();
    const dlg = w.findComponent(ConnectionDialog);
    expect(dlg.props()).toMatchObject({ startKind: 'website', startSite: 'https://shop.example.test/' });
    expect((dlg.find('[data-test=connection-site]').element as HTMLInputElement).value).toBe('https://shop.example.test/');
  });

  it('WebApiDropdownOffersNoWebsiteLoginAndNewIsSecretOnly', async () => {
    const http: ApiRow = {
      ...siteRow(), kind: 'http', name: 'pets', baseUrl: 'https://api.pets.test/', connection: null,
      auth: { type: 'bearer', name: null, description: null }, signIn: { label: 'Token', description: null, fields: [{ key: 'token', label: 'Token', secret: true }] },
    };
    const { api } = fakeApi({
      'integrations.apiList': () => [http],
      'integrations.connections': () => [login(), login({ id: 6, name: 'pets key', kind: 'secret', fields: [] })],
      'integrations.discoverStatus': () => ({ state: 'idle' }),
      'integrations.checklist': () => { throw new Error('not set up yet'); },
    });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'pets', teams } });
    await flushPromises();
    const options = w.find('[data-test=api-connection]').findAll('option').map((o) => o.text());
    expect(options).toEqual(['Paste a new one above', 'pets key', 'New connection…']);
    await w.find('[data-test=api-connection]').setValue('new');
    await flushPromises();
    const dlg = w.findComponent(ConnectionDialog);
    expect(dlg.props('secretOnly')).toBe(true);
    expect(dlg.find('[data-test=connection-kind]').exists()).toBe(false);
  });

  it('McpAddConnectionDialogOffersNoWebsiteKind', async () => {
    const w = mount(ConnectionDialog, { props: { api: fakeApi({}).api, secretOnly: true } });
    expect(w.find('[data-test=connection-kind]').exists()).toBe(false);
    expect(w.find('[data-test=connection-field-0]').exists()).toBe(true);
  });
});
