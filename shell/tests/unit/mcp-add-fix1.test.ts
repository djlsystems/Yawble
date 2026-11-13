// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import IntegrationDialog from '../../src/renderer/src/components/IntegrationDialog.vue';
import McpAddDialog from '../../src/renderer/src/components/McpAddDialog.vue';
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
const server = (o: Partial<McpServerInfo> = {}): McpServerInfo => ({
  name: 'linear', command: '', args: [], status: 'running: 4 tools', all: false, teams: [], transport: 'http', url: 'https://mcp.linear.test/mcp',
  auth: { type: 'oauth' }, env: [], connection: { id: 5, name: 'linear sign-in', status: 'needs_reconnect', statusReason: 'refused' }, sources: [],
  counts: null, tools: [], rules: [], ...o,
});
const base = (extra: Record<string, (p: any) => unknown> = {}) => ({
  'integrations.mcpImportScan': () => [], 'integrations.connections': () => [], 'integrations.mcpSave': () => ({ ok: true }),
  'integrations.mcpCheck': () => ({ ok: true, tools: 2 }), 'integrations.mcpSignIn': () => ({ ok: true }), ...extra,
});
const t = (w: any, id: string) => w.find(`[data-test="${id}"]`);
const disabled = (w: any, id: string) => (t(w, id).element as HTMLButtonElement).disabled;
async function openAdd(answers: Record<string, (p: any) => unknown>) {
  const f = fakeApi(answers);
  const w = mount(McpAddDialog, { props: { api: f.api } });
  await flushPromises();
  return { w, ...f };
}
async function openEdit(s: McpServerInfo, remote = false) {
  const f = fakeApi({ 'integrations.mcpList': () => [s], 'integrations.mcpSave': () => ({ ok: true }), 'integrations.mcpSignIn': () => ({ ok: true }),
    'integrations.checklist': () => { throw new Error('none'); }, 'integrations.discoverStatus': () => ({ state: 'idle' }) }, remote);
  const w = mount(IntegrationDialog, { props: { api: f.api, kind: 'mcp', name: s.name, teams: [] } });
  await flushPromises();
  return { w, ...f };
}

describe('Add dialog fix round 1', () => {
  it('existingNameIsRefused', async () => {
    const { w, calls } = await openAdd(base({ 'integrations.mcpList': () => [server({ name: 'Linear' })] }));
    await t(w, 'f-name').setValue('linear');
    await t(w, 'f-command').setValue('npx');
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    expect(t(w, 'mcp-add-error').text()).toBe('A server called linear already exists; edit it instead.');
    expect(calls.some((c) => c.method === 'integrations.mcpSave')).toBe(false);
  });

  it('signInFailureKeepsDialogUsableAndLocksName', async () => {
    const { w } = await openAdd(base({ 'integrations.mcpSignIn': () => { throw new Error('closed the tab'); } }));
    await t(w, 'mode-remote').setValue(true);
    await t(w, 'f-name').setValue('notion');
    await t(w, 'f-url').setValue('https://mcp.notion.test/mcp');
    await t(w, 'signin-oauth').setValue(true);
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    expect(t(w, 'mcp-add-error').text()).toBe("Saved, but signing in didn't finish: closed the tab. Press Save and discover to try again.");
    expect(disabled(w, 'save-discover')).toBe(false);
    expect((t(w, 'f-name').element as HTMLInputElement).disabled).toBe(true);
    expect(w.emitted('discover')).toBeUndefined();
  });

  it('websiteLoginsAreNotOffered', async () => {
    const { w } = await openAdd(base({ 'integrations.connections': () => [
      { id: 9, name: 'k', kind: 'secret', fields: [], status: 'works', statusReason: null, testedAt: null, usedBy: [] },
      { id: 10, name: 'intranet login', kind: 'website', fields: [], status: 'works', statusReason: null, testedAt: null, usedBy: [] },
    ] }));
    await t(w, 'mode-remote').setValue(true);
    await t(w, 'signin-token').setValue(true);
    const names = t(w, 'f-connection').findAll('option').map((o: any) => o.text());
    expect(names).toContain('k');
    expect(names).not.toContain('intranet login');
  });

  it('connectionOnlySentWhenNeededAndResetOnChange', async () => {
    const { w, calls } = await openAdd(base({ 'integrations.connections': () => [{ id: 9, name: 'k', kind: 'secret', fields: [], status: 'works', statusReason: null, testedAt: null, usedBy: [] }] }));
    await t(w, 'mode-remote').setValue(true);
    await t(w, 'signin-token').setValue(true);
    const sel = t(w, 'f-connection');
    (sel.element as HTMLSelectElement).value = '9';
    await sel.trigger('change');
    await t(w, 'signin-none').setValue(true);
    await t(w, 'f-name').setValue('x');
    await t(w, 'f-url').setValue('https://x.test/mcp');
    await t(w, 'save-discover').trigger('click');
    await flushPromises();
    expect('connectionId' in calls.find((c) => c.method === 'integrations.mcpSave')!.params).toBe(false);
  });

  it('emptyTokenOrSecretBlocksSave', async () => {
    const { w } = await openAdd(base());
    await t(w, 'f-name').setValue('x');
    await t(w, 'mode-remote').setValue(true);
    await t(w, 'f-url').setValue('https://x.test/mcp');
    await t(w, 'signin-token').setValue(true);
    expect(disabled(w, 'save-discover')).toBe(true);
    await t(w, 'f-token').setValue('abc');
    expect(disabled(w, 'save-discover')).toBe(false);
    await t(w, 'mode-local').setValue(true);
    await t(w, 'f-command').setValue('npx');
    await t(w, 'env-add').trigger('click');
    await t(w, 'env-name').setValue('K_EY');
    await t(w, 'env-secret').setValue(true);
    expect(disabled(w, 'save-discover')).toBe(true);
  });

  it('pasteTextClearedEscapeClosesAndFieldsAreLabelled', async () => {
    const { w } = await openAdd(base({ 'integrations.mcpParse': () => ({ drafts: [] }) }));
    await t(w, 'paste-toggle').trigger('click');
    await t(w, 'paste-text').setValue('secret=abc');
    await t(w, 'paste-fill').trigger('click');
    await flushPromises();
    expect((t(w, 'paste-text').element as HTMLTextAreaElement).value).toBe('');
    expect(t(w, 'paste-text').attributes('aria-label')).toBeTruthy();
    expect(w.find('fieldset legend').exists()).toBe(true);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(w.emitted('close')).toBeTruthy();
  });
});

describe('Replace secrets on an existing server', () => {
  it('replaceTokenSavesOnlyTheSecretAndHighlightsWhenNeeded', async () => {
    const { w, calls } = await openEdit(server({ auth: { type: 'bearer' } }));
    expect(t(w, 'needs-new-token').text()).toBe('needs a new token');
    await t(w, 'replace-token').setValue('newtok');
    await t(w, 'save-signin').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.mcpSave')!.params).toEqual({ name: 'linear', secrets: { token: 'newtok' } });
    expect(w.html()).not.toContain('newtok');
  });

  it('replaceEnvValueOmitsEnvAndTransport', async () => {
    const { w, calls } = await openEdit(server({ name: 'files', transport: 'stdio', url: null, auth: null, command: 'npx', args: ['-y', 'f'], connection: null,
      env: [{ name: 'API_KEY', secret: true, set: true, shown: 'ab••yz' }] }));
    await t(w, 'replace-value-API_KEY').setValue('zzz');
    await t(w, 'replace-save-API_KEY').trigger('click');
    await flushPromises();
    const p = calls.find((c) => c.method === 'integrations.mcpSave')!.params;
    expect(p).toEqual({ name: 'files', command: 'npx', secrets: { API_KEY: 'zzz' } });
  });

  it('switchingToServerSignsMeOnlySignsInAndSavesNothingFirst', async () => {
    const { w, calls } = await openEdit(server({ auth: { type: 'none' }, connection: null }));
    await t(w, 'edit-signin-oauth').setValue(true);
    await t(w, 'save-signin').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'integrations.mcpSave' || c.method === 'integrations.mcpSignIn').map((c) => [c.method, c.params])).toEqual([
      ['integrations.mcpSignIn', { name: 'linear' }]]);
  });

  it('abandonedSignInSavesNoOauthAuth', async () => {
    const f = fakeApi({ 'integrations.mcpList': () => [server({ auth: { type: 'none' }, connection: null })], 'integrations.mcpSave': () => ({ ok: true }),
      'integrations.mcpSignIn': () => { throw new Error('sign-in cancelled'); },
      'integrations.checklist': () => { throw new Error('none'); }, 'integrations.discoverStatus': () => ({ state: 'idle' }) });
    const w = mount(IntegrationDialog, { props: { api: f.api, kind: 'mcp', name: 'linear', teams: [] } });
    await flushPromises();
    await t(w, 'edit-signin-oauth').setValue(true);
    await t(w, 'save-signin').trigger('click');
    await flushPromises();
    expect(f.calls.some((c) => c.method === 'integrations.mcpSave')).toBe(false);
  });

  it('remoteViewHidesReplaceBoxes', async () => {
    const { w } = await openEdit(server({ auth: { type: 'bearer' } }), true);
    expect(w.find('[data-test=mcp-secrets]').exists()).toBe(false);
  });
});
