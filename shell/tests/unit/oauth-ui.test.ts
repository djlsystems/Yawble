// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import ConnectionDialog from '../../src/renderer/src/components/ConnectionDialog.vue';
import IntegrationDialog from '../../src/renderer/src/components/IntegrationDialog.vue';
import ApisPanel from '../../src/renderer/src/components/ApisPanel.vue';
import ConnectionsPanel from '../../src/renderer/src/components/ConnectionsPanel.vue';
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
const oauthConn = (o: Partial<Connection> = {}): Connection => ({
  id: 9, name: 'acme sign-in', kind: 'oauth', status: 'works', statusReason: null, testedAt: null, usedBy: ['acme'],
  fields: [
    { key: 'client_id', label: 'Client ID', secret: false, shown: 'cid-1', set: true },
    { key: 'client_secret', label: 'Client secret', secret: true, shown: '••••', set: true },
    { key: 'authorize_endpoint', label: 'Sign-in page', secret: false, shown: 'https://acme.test/authorize', set: true },
    { key: 'token_endpoint', label: 'Token', secret: false, shown: 'https://acme.test/token', set: true },
    { key: 'granted_scopes', label: 'Scopes', secret: false, shown: 'read write', set: true },
  ], ...o,
});
const apiRow = (oauth: Partial<NonNullable<ApiRow['oauth']>> = {}): ApiRow => ({
  kind: 'http', name: 'acme', baseUrl: 'https://api.acme.test/', docsUrl: null, specUrl: null, title: null,
  connection: { id: 9, name: 'acme sign-in', status: 'works' },
  auth: { type: 'oauth2', name: null, description: null }, signIn: null, status: null, all: false, teams: [3],
  counts: { allowed: 2, ask: 0, off: 0, undecided: 0 },
  oauth: { authorizeUrl: 'https://login.acme.test/authorize', tokenUrl: 'https://login.acme.test/token', neededScopes: ['read', 'write'], missingScopes: [], ...oauth },
});
const apiDialogAnswers = (row: ApiRow) => ({
  'integrations.apiList': () => [row],
  'integrations.connections': () => [oauthConn()],
  'integrations.discoverStatus': () => ({ state: 'idle' }),
  'integrations.checklist': () => { throw new Error('not set up yet'); },
});
const signedIn = () => ({ ok: true, connection: { id: 9, name: 'acme sign-in', status: 'works', statusReason: null } });

describe('OAuth sign-in Connection', () => {
  it('OAuthKindFindsTheProviderAndListsScopes', async () => {
    const { api, calls } = fakeApi({
      'integrations.oauthDiscover': () => ({ found: true, issuer: 'https://accounts.acme.test', authorizeUrl: 'https://accounts.acme.test/a', tokenUrl: 'https://accounts.acme.test/t', scopes: ['read', 'write'] }),
    });
    const w = mount(ConnectionDialog, { props: { api } });
    await w.find('[data-test=connection-kind-oauth]').setValue(true);
    await w.find('[data-test=oauth-provider]').setValue('https://accounts.acme.test');
    await w.find('[data-test=oauth-find]').trigger('click');
    await flushPromises();
    expect(calls[0]).toEqual({ method: 'integrations.oauthDiscover', params: { provider: 'https://accounts.acme.test' } });
    expect(w.find('[data-test=oauth-found]').text()).toBe('Found the sign-in at accounts.acme.test');
    expect(w.find('[data-test=oauth-scope-read]').exists()).toBe(true);
    expect(w.find('[data-test=oauth-scope-filter]').exists()).toBe(false); // the filter box is only over 30 scopes
    expect(w.find('[data-test=oauth-redirect]').text()).toContain('Register this redirect address in your app: http://127.0.0.1/callback (any port)');
  });

  it('ScopeFilterAppearsOverThirtyAndFilters', async () => {
    const scopes = Array.from({ length: 31 }, (_, i) => (i === 5 ? 'special.read' : `scope${i}`));
    const { api } = fakeApi({ 'integrations.oauthDiscover': () => ({ found: true, issuer: 'https://a.test', authorizeUrl: 'https://a.test/a', tokenUrl: 'https://a.test/t', scopes }) });
    const w = mount(ConnectionDialog, { props: { api, startKind: 'oauth' } });
    await w.find('[data-test=oauth-provider]').setValue('https://a.test');
    await w.find('[data-test=oauth-find]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test^=oauth-scope-]').length).toBe(32); // 31 scopes + the filter box
    await w.find('[data-test=oauth-scope-filter]').setValue('special');
    expect(w.findAll('[data-test^=oauth-scope-]').length).toBe(2);
    expect(w.find('[data-test="oauth-scope-special.read"]').exists()).toBe(true);
  });

  it('NotFoundOpensAdvanced', async () => {
    const { api } = fakeApi({ 'integrations.oauthDiscover': () => ({ found: false }) });
    const w = mount(ConnectionDialog, { props: { api, startKind: 'oauth' } });
    await w.find('[data-test=oauth-provider]').setValue('https://nowhere.test');
    await w.find('[data-test=oauth-find]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=oauth-notfound]').text()).toBe('This provider does not describe itself');
    expect((w.find('[data-test=oauth-advanced]').element as HTMLDetailsElement).open).toBe(true);
    expect(w.find('[data-test=oauth-authorize]').exists()).toBe(true);
  });

  it('SignInSendsTickedScopes', async () => {
    const { api, calls } = fakeApi({
      'integrations.oauthDiscover': () => ({ found: true, issuer: 'https://accounts.acme.test', authorizeUrl: 'https://accounts.acme.test/a', tokenUrl: 'https://accounts.acme.test/t', scopes: ['read', 'write', 'admin'] }),
      'integrations.oauthSignIn': signedIn,
      'integrations.connections': () => [oauthConn()],
    });
    const w = mount(ConnectionDialog, { props: { api, startKind: 'oauth' } });
    await w.find('[data-test=oauth-provider]').setValue('https://accounts.acme.test');
    await w.find('[data-test=oauth-find]').trigger('click');
    await flushPromises();
    await w.find('[data-test=oauth-scope-read]').setValue(true);
    await w.find('[data-test=oauth-scope-admin]').setValue(true);
    await w.find('[data-test=oauth-client-id]').setValue('cid-1');
    await w.find('[data-test=oauth-client-secret]').setValue('s3cret');
    await w.find('[data-test=oauth-signin]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.oauthSignIn')!.params).toEqual({
      provider: 'https://accounts.acme.test', authorizeUrl: 'https://accounts.acme.test/a', tokenUrl: 'https://accounts.acme.test/t',
      clientId: 'cid-1', clientSecret: 's3cret', scopes: ['read', 'admin'],
    });
    expect(w.find('[data-test=oauth-result]').text()).toBe('works, 2 scopes granted');
    expect(w.emitted('saved')).toBeTruthy();
  });

  it('EditKeepsTheSecretWhenEmpty', async () => {
    const { api, calls } = fakeApi({ 'integrations.oauthSignIn': signedIn, 'integrations.connections': () => [oauthConn()] });
    const w = mount(ConnectionDialog, { props: { api, connection: oauthConn() } });
    const secret = w.find('[data-test=oauth-client-secret]');
    expect((secret.element as HTMLInputElement).value).toBe('');
    expect(secret.attributes('placeholder')).toContain('leave empty to keep');
    expect((w.find('[data-test=oauth-client-id]').element as HTMLInputElement).value).toBe('cid-1');
    await w.find('[data-test=oauth-signin]').trigger('click');
    await flushPromises();
    const p = calls.find((c) => c.method === 'integrations.oauthSignIn')!.params;
    expect(p.connectionId).toBe(9);
    expect('clientSecret' in p).toBe(false);
  });
});

describe('OAuth API', () => {
  it('ApiOAuthShowsNeededScopes', async () => {
    const { api, calls } = fakeApi({ ...apiDialogAnswers(apiRow()), 'integrations.oauthSignIn': signedIn });
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'acme', teams } });
    await flushPromises();
    expect(w.find('[data-test=api-oauth-title]').text()).toBe('Sign in with login.acme.test');
    expect(w.find('[data-test=api-oauth-scopes]').text()).toContain('read');
    expect(w.find('[data-test=api-oauth-scopes]').text()).toContain('write');
    expect(w.find('[data-test=api-oauth-missing]').exists()).toBe(false);
    expect((w.find('[data-test=api-oauth-client-id]').element as HTMLInputElement).value).toBe('cid-1');
    expect(w.find('[data-test=api-oauth-client-secret]').attributes('placeholder')).toContain('leave empty to keep');
    await w.find('[data-test=api-oauth-signin]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.oauthSignIn')!.params).toEqual({ api: 'acme', clientId: 'cid-1', connectionId: 9 });
  });

  it('MissingScopesOfferSignInAgain', async () => {
    const { api } = fakeApi(apiDialogAnswers(apiRow({ missingScopes: ['write'] })));
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'acme', teams } });
    await flushPromises();
    expect(w.find('[data-test=api-oauth-missing]').text()).toBe('Sign in again to allow the new actions');
    expect(w.find('[data-test=api-oauth-signin]').text()).toBe('Sign in again');
    const list = fakeApi({ 'integrations.apiList': () => [apiRow({ missingScopes: ['write'] })], 'integrations.connections': () => [] });
    const panel = mount(ApisPanel, { props: { api: list.api, teams } });
    await flushPromises();
    expect(panel.find('[data-test=api-missing-scopes]').text()).toContain('Sign in again to allow the new actions');
  });

  it('RemoteViewHasNoSignIn', async () => {
    const { api } = fakeApi(apiDialogAnswers(apiRow()), true);
    const w = mount(IntegrationDialog, { props: { api, kind: 'api', name: 'acme', teams } });
    await flushPromises();
    expect(w.find('[data-test=api-oauth-signin]').exists()).toBe(false);
    expect(w.find('[data-test=api-oauth-remote]').text()).toContain('acme sign-in: works');
    const c = mount(ConnectionDialog, { props: { api, connection: oauthConn() } });
    expect(c.find('[data-test=oauth-signin]').exists()).toBe(false);
    expect(c.find('[data-test=oauth-remote]').exists()).toBe(true);
  });
});

describe('OAuth Connection final fixes', () => {
  const serverConn = () => oauthConn({
    id: 12, name: 'notesmcp sign-in', usedBy: ['notesmcp'],
    fields: [...oauthConn().fields, { key: 'resource', label: 'Resource', secret: false, shown: 'https://mcp.notes.test/mcp', set: true }],
  });

  it('AServersSignInIsSignedInAgainFromTheServer', async () => {
    const { api } = fakeApi({ 'integrations.connections': () => [oauthConn(), serverConn()] });
    const w = mount(ConnectionsPanel, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=connection-signin-9]').exists()).toBe(true);
    expect(w.find('[data-test=connection-signin-12]').exists()).toBe(false);
    expect(w.find('[data-test=connection-mcp-note-12]').text()).toBe('Sign in again from Settings → Integrations → MCP servers (Sign in).');
    expect(w.find('[data-test=connection-mcp-note-9]').exists()).toBe(false);
    // and its Edit dialog has no sign-in either
    const d = mount(ConnectionDialog, { props: { api, connection: serverConn() } });
    expect(d.find('[data-test=oauth-signin]').exists()).toBe(false);
    expect(d.find('[data-test=oauth-mcp-note]').text()).toContain('MCP servers');
  });

  it('EditSavesTheNameWithoutSigningIn', async () => {
    const { api, calls } = fakeApi({ 'integrations.connectionSave': (p) => ({ ...oauthConn(), name: p.name }) });
    const w = mount(ConnectionDialog, { props: { api, connection: oauthConn() } });
    await w.find('[data-test=connection-name]').setValue('Acme login');
    await w.find('[data-test=oauth-save]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'integrations.connectionSave', params: { id: 9, name: 'Acme login', secrets: {} } }]);
    expect(w.emitted('close')).toBeTruthy();

    // a new client secret typed: it is sent; nothing else changes
    const two = fakeApi({ 'integrations.connectionSave': () => oauthConn() });
    const w2 = mount(ConnectionDialog, { props: { api: two.api, connection: oauthConn() } });
    await w2.find('[data-test=oauth-client-secret]').setValue('new-secret');
    await w2.find('[data-test=oauth-save]').trigger('click');
    await flushPromises();
    expect(two.calls[0].params).toEqual({ id: 9, name: 'acme sign-in', secrets: { client_secret: 'new-secret' } });

    // no Save for a new sign-in, nor in the remote view
    expect(mount(ConnectionDialog, { props: { api, startKind: 'oauth' } }).find('[data-test=oauth-save]').exists()).toBe(false);
    const remote = fakeApi({}, true);
    expect(mount(ConnectionDialog, { props: { api: remote.api, connection: oauthConn() } }).find('[data-test=oauth-save]').exists()).toBe(false);
  });
});
