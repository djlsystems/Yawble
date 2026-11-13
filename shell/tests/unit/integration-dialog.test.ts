// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import IntegrationsPanel from '../../src/renderer/src/components/IntegrationsPanel.vue';
import IntegrationDialog from '../../src/renderer/src/components/IntegrationDialog.vue';
import type { IntegrationInfo, YawbleApi } from '../../src/shared/api';

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
const cli = (o: Partial<IntegrationInfo> = {}): IntegrationInfo => ({
  name: 'gh', label: 'GitHub', description: 'GitHub CLI', known: true, configured: true, enabled: true, all: false, teams: [3], path: 'C:\\bin\\gh.exe', version: '2.45.0',
  status: 'signed in as me', floors: [], login: 'gh auth login', counts: { allowed: 4, ask: 2, off: 1, undecided: 0 }, preset: [], rules: [], ...o,
});
const checklist = (runs = 1) => ({
  cli: 'gh', teamId: null, version: '2.45.0', discoveredAt: '2026-11-09', runs, partial: false, partialReason: null, error: null, new: 0, changed: 0, gone: 0, choices: 0,
  kind: 'cli', offeredAll: false, offeredTeams: [3], counts: { allowed: 0, ask: 0, off: 0, undecided: 0 }, nodes: [], otherRules: [],
});
const base = (extra: Record<string, (p: any) => unknown> = {}) => ({
  'integrations.list': () => ({ integrations: [cli()], pendingAsks: 0 }),
  'integrations.mcpList': () => [{ name: 'files', command: 'npx', args: ['-y', 'x'], status: 'running', all: false, teams: [], tools: [], rules: [], counts: { allowed: 1, ask: 0, off: 0, undecided: 2 } }],
  'integrations.checklist': () => checklist(),
  'integrations.discoverStatus': () => ({ state: 'idle' }),
  'integrations.tasks': () => [],
  ...extra,
});
const stubs = { TasksPanel: true };

describe('integrations page and dialog', () => {
  it('the integrations page shows CLIs and MCP servers sub-tabs with counts', async () => {
    const { api } = fakeApi(base());
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    expect(w.find('[data-test=tab-cli]').text()).toBe('CLIs (1)');
    expect(w.find('[data-test=tab-mcp]').text()).toBe('MCP servers (1)');
    expect(w.find('[data-test=integration-gh]').text()).toContain('7 actions: 4 allowed, 2 ask first');
    await w.find('[data-test=tab-mcp]').trigger('click');
    expect(w.find('[data-test=mcp-files]').isVisible()).toBe(true);
    expect((w.find('[data-test=cli-tab]').element as HTMLElement).style.display).toBe('none');
  });

  it('edit opens the dialog with the checklist scoped to every team', async () => {
    const { api, calls } = fakeApi(base());
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    await w.find('[data-test=integration-edit-gh]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=integration-dialog]').exists()).toBe(true);
    const c = calls.find((x) => x.method === 'integrations.checklist')!;
    expect(c.params).toMatchObject({ kind: 'cli', name: 'gh', teamId: null });
  });

  it('opening an undiscovered CLI starts discover', async () => {
    const { api, calls } = fakeApi(base({
      'integrations.checklist': () => { throw new Error('gh is not set up yet'); },
      'integrations.discover': () => ({ state: 'running', found: 0, seconds: 0 }),
    }));
    mount(IntegrationDialog, { props: { api, kind: 'cli', name: 'gh', teams } });
    await flushPromises();
    expect(calls.some((c) => c.method === 'integrations.discover')).toBe(true);
  });

  it('re-reads the sign-in status when a discover finishes, and tells the page', async () => {
    let state = 'running';
    let status = 'not signed in: run gh auth login';
    const { api, calls } = fakeApi(base({
      'integrations.list': () => ({ integrations: [cli({ status })], pendingAsks: 0 }),
      'integrations.discoverStatus': () => ({ state }),
    }));
    const w = mount(IntegrationDialog, { props: { api, kind: 'cli', name: 'gh', teams } });
    await flushPromises();
    expect(w.find('[data-test=dialog-status]').text()).toContain('not signed in');
    status = 'signed in: me';
    state = 'done';
    await new Promise((r) => setTimeout(r, 800));
    await flushPromises();
    expect(w.find('[data-test=dialog-status]').text()).toContain('signed in: me');
    expect(calls.filter((c) => c.method === 'integrations.list').length).toBe(2);
    expect(w.emitted('saved')).toHaveLength(1);
    w.unmount();
  });

  it('login via terminal calls openLogin, and is replaced by the command text in a remote view', async () => {
    const local = fakeApi(base({ 'integrations.openLogin': () => ({ ok: true, command: 'gh auth login' }) }));
    const w = mount(IntegrationDialog, { props: { api: local.api, kind: 'cli', name: 'gh', teams } });
    await flushPromises();
    await w.find('[data-test=dialog-login]').trigger('click');
    expect(local.calls.find((c) => c.method === 'integrations.openLogin')!.params).toMatchObject({ name: 'gh' });
    const remote = fakeApi(base(), true);
    const r = mount(IntegrationDialog, { props: { api: remote.api, kind: 'cli', name: 'gh', teams } });
    await flushPromises();
    expect(r.find('[data-test=dialog-login]').exists()).toBe(false);
    expect(r.find('[data-test=dialog-login-remote]').text()).toContain('Sign in on the computer: gh auth login');
  });

  it('a CLI offered to no team says so', async () => {
    const { api } = fakeApi(base({ 'integrations.list': () => ({ integrations: [cli({ teams: [], all: false, enabled: false })], pendingAsks: 0 }) }));
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    expect(w.find('[data-test=integration-gh]').text()).toContain('not offered to any team');
  });

  it('remove makes the card disappear, and an added CLI that is not installed shows as not installed', async () => {
    let items = [cli(), cli({ name: 'vercel', label: 'vercel', path: null, status: null, login: null, counts: null, teams: [], enabled: false })];
    const { api, calls } = fakeApi(base({
      'integrations.list': () => ({ integrations: items, pendingAsks: 0 }),
      'integrations.remove': (p) => { items = items.filter((i) => i.name !== p.name); return { ok: true }; },
    }));
    window.confirm = () => true;
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    expect(w.find('[data-test=tab-cli]').text()).toBe('CLIs (2)');
    expect(w.find('[data-test=integration-vercel]').text()).toContain('not installed on this computer');
    await w.find('[data-test=integration-remove-gh]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'integrations.remove' && c.params.name === 'gh')).toBe(true);
    expect(w.find('[data-test=integration-gh]').exists()).toBe(false);
    expect(w.find('[data-test=tab-cli]').text()).toBe('CLIs (1)');
  });

  it('login on the card says a terminal opened', async () => {
    const { api } = fakeApi(base({ 'integrations.openLogin': () => ({ ok: true, command: 'gh auth login' }) }));
    const w = mount(IntegrationsPanel, { props: { api, teams }, global: { stubs } });
    await flushPromises();
    await w.find('[data-test=integration-login-gh]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=integrations-note]').text()).toContain('A terminal opened');
  });
});
