// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import McpAddDialog from '../../src/renderer/src/components/McpAddDialog.vue';
import McpServersPanel from '../../src/renderer/src/components/McpServersPanel.vue';
import type { McpServerInfo, YawbleApi } from '../../src/shared/api';

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

const cand = (o: Record<string, unknown> = {}) => ({
  key: 'k1', name: 'files', transport: 'stdio', command: 'npx', args: ['-y', 'files-mcp'], url: null, env: [{ name: 'TOKEN', secret: true, shown: 'ab••••yz' }],
  sources: ['Claude Desktop', 'Cursor'], alreadyAdded: null, ...o,
});
const base = (extra: Record<string, (p: any) => unknown> = {}) => ({
  'integrations.mcpImportScan': () => [],
  'integrations.connections': () => [],
  'integrations.mcpSave': () => ({ ok: true }),
  'integrations.mcpCheck': () => ({ ok: true, tools: 2 }),
  'integrations.mcpSignIn': () => ({ ok: true }),
  ...extra,
});
async function open(answers: Record<string, (p: any) => unknown>, remote = false) {
  const f = fakeApi(answers, remote);
  const w = mount(McpAddDialog, { props: { api: f.api } });
  await flushPromises();
  return { w, ...f };
}
const t = (w: any, id: string) => w.find(`[data-test="${id}"]`);

describe('McpAddDialog', () => {
  it('importSectionListsCandidates_alreadyAddedDisabled', async () => {
    const { w } = await open(base({ 'integrations.mcpImportScan': () => [cand(), cand({ key: 'k2', name: 'git', alreadyAdded: 'git-2', env: [] })] }));
    const text = t(w, 'mcp-import').text();
    expect(text).toContain('Import from this computer');
    expect(text).toContain('npx -y files-mcp');
    expect(text).toContain('from Claude Desktop, Cursor');
    expect(text).toContain('TOKEN (secret ab••••yz)');
    expect(text).toContain('already added as git-2');
    expect((t(w, 'cand-tick-k2').element as HTMLInputElement).disabled).toBe(true);
    expect((t(w, 'cand-tick-k1').element as HTMLInputElement).disabled).toBe(false);
  });

  it('importSelectedCallsMcpImport', async () => {
    const { w, calls } = await open(base({ 'integrations.mcpImport': () => ({ imported: ['files'] }) }));
    // (the scan before the import has a candidate; the one after has none)
    w.unmount();
    let scans = 0;
    const f = fakeApi(base({ 'integrations.mcpImportScan': () => (scans++ === 0 ? [cand(), cand({ key: 'k2', name: 'git', alreadyAdded: 'git' })] : []), 'integrations.mcpImport': () => ({ imported: ['files'] }) }));
    const w2 = mount(McpAddDialog, { props: { api: f.api } });
    await flushPromises();
    await t(w2, 'cand-tick-k1').setValue(true);
    await t(w2, 'import-selected').trigger('click');
    await flushPromises();
    expect(f.calls.find((c) => c.method === 'integrations.mcpImport')!.params).toEqual({ keys: ['k1'] });
    expect(calls.length).toBeGreaterThan(0);
    await t(w2, 'imported-discover-files').trigger('click');
    expect(w2.emitted('discover')![0]).toEqual(['files']);
  });

  it('remoteTokenSavesSecretAndNeverShowsItBack', async () => {
    const { w, calls } = await open(base());
    await t(w, 'mode-remote').setValue(true);
    await t(w, 'f-name').setValue('linear');
    await t(w, 'f-url').setValue('https://mcp.linear.test/mcp');
    await t(w, 'signin-token').setValue(true);
    expect((t(w, 'f-token').element as HTMLInputElement).type).toBe('password');
    await t(w, 'f-token').setValue('lin_secret_value');
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'integrations.mcpSave')!.params;
    expect(save).toEqual({ name: 'linear', transport: 'http', url: 'https://mcp.linear.test/mcp', auth: { type: 'bearer' }, secrets: { token: 'lin_secret_value' } });
    expect(w.html()).not.toContain('lin_secret_value');
    expect((t(w, 'f-token').element as HTMLInputElement).value).toBe('');
    expect(w.emitted('discover')![0]).toEqual(['linear']);
  });

  it('localSaveSendsNoTransport', async () => {
    const { w, calls } = await open(base());
    await t(w, 'f-name').setValue('files');
    await t(w, 'f-command').setValue('npx');
    await t(w, 'f-args').setValue('-y\nfiles-mcp');
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'integrations.mcpSave')!.params;
    expect('transport' in save).toBe(false);
    expect(save).toMatchObject({ name: 'files', command: 'npx', args: ['-y', 'files-mcp'], env: [] });
  });

  it('serverSignsMeInCallsMcpSignInAfterSave', async () => {
    const { w, calls } = await open(base());
    await t(w, 'mode-remote').setValue(true);
    await t(w, 'f-name').setValue('notion');
    await t(w, 'f-url').setValue('https://mcp.notion.test/mcp');
    await t(w, 'signin-oauth').setValue(true);
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    const order = calls.map((c) => c.method).filter((m) => m !== 'integrations.mcpImportScan' && m !== 'integrations.connections' && m !== 'integrations.mcpList');
    expect(order).toEqual(['integrations.mcpSave', 'integrations.mcpSignIn', 'integrations.mcpCheck']);
    expect(calls.find((c) => c.method === 'integrations.mcpSave')!.params.auth).toEqual({ type: 'oauth' });
    expect(calls.find((c) => c.method === 'integrations.mcpSignIn')!.params).toEqual({ name: 'notion' });
  });

  it('signInShowsFinishInBrowserWhileWaiting', async () => {
    let release!: () => void;
    const { w } = await open(base({ 'integrations.mcpSignIn': () => new Promise((r) => { release = () => r({ ok: true }); }) }));
    await t(w, 'mode-remote').setValue(true);
    await t(w, 'f-name').setValue('notion');
    await t(w, 'f-url').setValue('https://mcp.notion.test/mcp');
    await t(w, 'signin-oauth').setValue(true);
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    expect(t(w, 'f-busy').text()).toBe('Finish signing in in your browser…');
    release();
    await flushPromises();
  });

  it('pasteFillsTheForm', async () => {
    const draft = { name: 'files', transport: 'stdio', command: 'npx', args: ['-y', 'files-mcp'], url: null, auth: null, env: [{ name: 'API_KEY', value: '', secret: true, needed: true }], note: null };
    const { w, calls } = await open(base({ 'integrations.mcpParse': () => ({ drafts: [draft] }) }));
    await t(w, 'paste-toggle').trigger('click');
    await t(w, 'paste-text').setValue('claude mcp add files -- npx -y files-mcp');
    await t(w, 'paste-fill').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.mcpParse')!.params.text).toContain('claude mcp add');
    expect((t(w, 'f-name').element as HTMLInputElement).value).toBe('files');
    expect((t(w, 'f-command').element as HTMLInputElement).value).toBe('npx');
    expect((t(w, 'f-args').element as HTMLTextAreaElement).value).toBe('-y\nfiles-mcp');
    expect(t(w, 'env-row-API_KEY').exists()).toBe(true);
  });

  it('pasteWithNothingSaysSo', async () => {
    const { w } = await open(base({ 'integrations.mcpParse': () => ({ drafts: [] }) }));
    await t(w, 'paste-toggle').trigger('click');
    await t(w, 'paste-text').setValue('hello');
    await t(w, 'paste-fill').trigger('click');
    await flushPromises();
    expect(t(w, 'mcp-add-info').text()).toBe('That doesn’t look like an MCP config');
  });

  it('pasteWithSeveralServersLetsThePersonPick', async () => {
    const d = (name: string, url: string) => ({ name, transport: 'http', command: null, args: [], url, auth: { type: 'bearer', header: null }, env: [], note: null });
    const { w } = await open(base({ 'integrations.mcpParse': () => ({ drafts: [d('a', 'https://a.test/mcp'), d('b', 'https://b.test/mcp')] }) }));
    await t(w, 'paste-toggle').trigger('click');
    await t(w, 'paste-text').setValue('{}');
    await t(w, 'paste-fill').trigger('click');
    await flushPromises();
    expect(t(w, 'paste-drafts').exists()).toBe(true);
    expect((t(w, 'f-name').element as HTMLInputElement).value).toBe('');
    await t(w, 'paste-draft-1').trigger('click');
    expect((t(w, 'f-name').element as HTMLInputElement).value).toBe('b');
    expect((t(w, 'f-url').element as HTMLInputElement).value).toBe('https://b.test/mcp');
    expect((t(w, 'signin-token').element as HTMLInputElement).checked).toBe(true);
  });

  it('neededSecretBlocksSave', async () => {
    const draft = { name: 'files', transport: 'stdio', command: 'npx', args: [], url: null, auth: null, env: [{ name: 'API_KEY', value: '', secret: true, needed: true }], note: null };
    const { w, calls } = await open(base({ 'integrations.mcpParse': () => ({ drafts: [draft] }) }));
    await t(w, 'paste-toggle').trigger('click');
    await t(w, 'paste-text').setValue('x');
    await t(w, 'paste-fill').trigger('click');
    await flushPromises();
    expect(t(w, 'env-row-API_KEY').classes()).toContain('needed');
    expect((t(w, 'save-discover').element as HTMLButtonElement).disabled).toBe(true);
    await t(w, 'env-value').setValue('abc');
    expect((t(w, 'save-discover').element as HTMLButtonElement).disabled).toBe(false);
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.mcpSave')!.params.secrets).toEqual({ API_KEY: 'abc' });
  });

  it('missingVariableFromDiscoverAddsARow', async () => {
    const { w } = await open(base({ 'integrations.mcpCheck': () => { throw new Error('files exited: GITHUB_TOKEN is required'); } }));
    await t(w, 'f-name').setValue('files');
    await t(w, 'f-command').setValue('npx');
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    const row = t(w, 'env-row-GITHUB_TOKEN');
    expect(row.exists()).toBe(true);
    expect(row.classes()).toContain('needed');
    expect((row.find('[data-test=env-secret]').element as HTMLInputElement).checked).toBe(true);
    expect(w.emitted('discover')).toBeUndefined();
  });

  it('remoteViewHidesImportAndSignIn', async () => {
    const { w, calls } = await open(base({ 'integrations.mcpImportScan': () => [cand()] }), true);
    expect(calls.some((c) => c.method === 'integrations.mcpImportScan')).toBe(false);
    expect(t(w, 'mcp-import').exists()).toBe(false);
    expect(t(w, 'mcp-import-remote').text()).toBe('Sign-ins and imports are managed on the computer itself.');
    await t(w, 'mode-remote').setValue(true);
    expect(t(w, 'f-signin').exists()).toBe(false);
    expect(t(w, 'f-signin-remote').text()).toContain('managed on the computer itself');
    await t(w, 'mode-local').setValue(true);
    await t(w, 'env-add').trigger('click');
    await t(w, 'env-secret').setValue(true);
    expect((t(w, 'env-value').element as HTMLInputElement).disabled).toBe(true);
  });
});

const remoteServer = (o: Partial<McpServerInfo> = {}): McpServerInfo => ({
  name: 'linear', command: '', args: [], status: 'running: 4 tools', all: false, teams: [], transport: 'http', url: 'https://mcp.linear.test/mcp',
  auth: { type: 'oauth' }, env: [], connection: { id: 5, name: 'linear sign-in', status: 'needs_reconnect', statusReason: 'refused' }, sources: ['Cursor'],
  counts: null, tools: [], rules: [], ...o,
});

describe('McpServersPanel remote cards', () => {
  it('remoteCardShowsUrlAndSignInAgain', async () => {
    const { api, calls } = fakeApi({ 'integrations.mcpList': () => [remoteServer()], 'integrations.mcpSignIn': () => ({ ok: true }) });
    const w = mount(McpServersPanel, { props: { api, teams: [] } });
    await flushPromises();
    expect(w.find('[data-test=mcp-url]').text()).toBe('https://mcp.linear.test/mcp');
    expect(w.find('[data-test=mcp-signin]').text()).toContain('needs you to sign in again');
    expect(w.find('[data-test=mcp-sources]').text()).toBe('imported from Cursor');
    await w.find('[data-test=mcp-signin-again]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.mcpSignIn')!.params).toEqual({ name: 'linear' });
  });

  it('signedInServerHasNoSignInAgain', async () => {
    const { api } = fakeApi({ 'integrations.mcpList': () => [remoteServer({ connection: { id: 5, name: 'x', status: 'works', statusReason: null } })] });
    const w = mount(McpServersPanel, { props: { api, teams: [] } });
    await flushPromises();
    expect(w.find('[data-test=mcp-signin]').text()).toContain('signed in');
    expect(w.find('[data-test=mcp-signin-again]').exists()).toBe(false);
  });

  it('removeAsksAboutAnUnusedConnection', async () => {
    const ask = vi.fn(() => true);
    window.confirm = ask as unknown as typeof window.confirm;
    const { api, calls } = fakeApi({
      'integrations.mcpList': () => [remoteServer()],
      'integrations.connections': () => [{ id: 5, name: 'linear sign-in', kind: 'oauth', fields: [], status: 'works', statusReason: null, testedAt: null, usedBy: ['MCP: linear'] }],
      'integrations.mcpRemove': () => ({ ok: true }),
    });
    const w = mount(McpServersPanel, { props: { api, teams: [] } });
    await flushPromises();
    await w.find('[data-test=mcp-remove-linear]').trigger('click');
    await flushPromises();
    expect(ask).toHaveBeenCalledWith("Also delete the connection 'linear sign-in'?");
    expect(calls.find((c) => c.method === 'integrations.mcpRemove')!.params).toEqual({ name: 'linear', removeConnection: true });
  });

  it('removeSendsNoRemoveConnectionKeyUnlessTrueAndNeverRemotely', async () => {
    window.confirm = vi.fn((m: string) => m.startsWith('Remove ')) as unknown as typeof window.confirm;
    const mk = (remote: boolean) => fakeApi({
      'integrations.mcpList': () => [remoteServer()],
      'integrations.connections': () => [{ id: 5, name: 'linear sign-in', kind: 'oauth', fields: [], status: 'works', statusReason: null, testedAt: null, usedBy: ['MCP: linear'] }],
      'integrations.mcpRemove': () => ({ ok: true }),
    }, remote);
    for (const remote of [false, true]) {
      const f = mk(remote);
      const w = mount(McpServersPanel, { props: { api: f.api, teams: [] } });
      await flushPromises();
      await w.find('[data-test=mcp-remove-linear]').trigger('click');
      await flushPromises();
      expect(f.calls.find((c) => c.method === 'integrations.mcpRemove')!.params).toEqual({ name: 'linear' });
    }
  });

  it('addButtonOpensTheDialog', async () => {
    const { api } = fakeApi(base({ 'integrations.mcpList': () => [] }));
    const w = mount(McpServersPanel, { props: { api, teams: [] } });
    await flushPromises();
    await w.find('[data-test=mcp-add-open]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=mcp-add-dialog]').exists()).toBe(true);
  });
});
