// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import CredentialsDialog from '../../src/renderer/src/components/CredentialsDialog.vue';
import MemberSettingsDialog from '../../src/renderer/src/components/MemberSettingsDialog.vue';
import type { AgentCard, CredentialStatus, MemberPolicyView, YawbleApi } from '../../src/shared/api';
import { isUiMethod } from '../../src/shared/api';

// The screens against a scripted core: what each sends, and what it shows.
function fakeApi(answers: Record<string, (params: any) => unknown>) {
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
  };
  return { api, calls };
}

const member = { id: 2, teamId: 1, team: 'payments', name: 'Ben', role: 'member', cli: 'grok', instructions: '', model: '', credential: 'default' } as const;
const card = (over: Partial<AgentCard> = {}): AgentCard => ({
  member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [], ...over,
});
const secret = { connected: false, addedAt: null, expiresAt: null, lastUsedAt: null, renewSoon: false, problem: null };
const creds = (over: Partial<CredentialStatus['claude']['subscription']> = {}): CredentialStatus => ({
  implementation: 'fake', storage: 'none', storageWarning: 'Fake credential service',
  claude: { mode: 'subscription', subscription: { ...secret, ...over }, apiKey: { ...secret } },
  grok: { mode: 'login', login: { signedIn: false, expiresAt: null, lastRefreshAt: null, problem: null }, apiKey: { ...secret } },
});

describe('main window', () => {
  it('shows global run slots, the teams with counts and the agent cards', async () => {
    const { api } = fakeApi({
      'app.overview': () => ({ slots: { running: 3, limit: 3, queued: 1 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 2, running: 1, queued: 1 }] }),
      'teams.agents': () => [
        card({ member: { ...member, id: 1, name: 'Manager', role: 'manager', cli: 'claude' }, state: 'running', statusLine: 'Split work' }),
        card({ state: 'queued', queuePosition: 1, queueLength: 2, queuedInstructions: 2, loosened: ['network.allowHosts + registry.npmjs.org'], net: 'model + bus + registry.npmjs.org' }),
      ],
      'bus.decisions': () => [{ seq: 9, from: 'Ben', question: 'Use Postgres?', workflow: 3, at: '' },
        { seq: 10, from: 'Manager', question: 'OK to hire a second tester?', workflow: 4, at: '' }],
    });
    const w = mount(App, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    await flushPromises();
    expect(w.get('[data-test=slots]').text()).toBe('Run slots: 3 / 3 in use · 1 queued');
    expect(w.get('[data-test=team-counts]').text()).toBe('1 running · 1 queued');
    // The window opens on the Concierge; the team is one click away in the sidebar.
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
    await w.get('[data-test=team-item]').trigger('click');
    await flushPromises();
    await flushPromises();
    expect(w.get('[data-test=agent-Manager] [data-test=agent-face-state]').text()).toBe('Running');
    expect(w.get('[data-test=agent-Manager] [data-test=agent-output]').text()).toBe('Split work');
    const ben = w.get('[data-test=agent-Ben]');
    expect(ben.get('[data-test=agent-face-state]').text()).toBe('Waiting to start');
    expect(ben.get('[data-test=agent-state]').text()).toContain('position 1 of 2');
    expect(ben.get('[data-test=loosened-chip]').text()).toContain('registry.npmjs.org');
    expect(ben.get('[data-test=agent-cli]').text()).toBe('grok');
    const [bensQuestion, hire] = w.findAll('[data-test=decision]');
    expect(bensQuestion.text()).toContain('Ben needs a decision');
    expect(bensQuestion.get('[data-test=decision-question]').text()).toContain('Use Postgres?');
    // Approve only where approval is asked for; every question keeps its answer box and Discuss.
    expect(bensQuestion.find('[data-test=decision-approve]').exists()).toBe(false);
    expect(bensQuestion.find('[data-test=decision-discuss]').exists()).toBe(true);
    expect(hire.find('[data-test=decision-approve]').exists()).toBe(true);
    expect(hire.find('[data-test=decision-always]').exists()).toBe(true);
    expect(w.find('[data-test=decision-discuss]').exists()).toBe(true);
    expect(w.findAll('[data-test=open-terminal]')).toHaveLength(2);
    expect(w.findAll('[data-test=open-settings]')).toHaveLength(2);
  });
});

describe('agent settings: policy', () => {
  const view = (): MemberPolicyView => ({
    memberId: 2,
    global: { filesystem: { grantableRoots: ['~/src'], readonly: [], readwrite: [], denied: ['~/.ssh'] }, network: { allowHosts: [] },
      compute: { idleTimeoutSec: 1800, wallTimeoutSec: 7200 }, ui: { clipboard: 'none' }, app: { maxConcurrentAgents: 4, backend: 'default' } },
    override: {}, loosen: {}, loosenSummary: [], loosenApprovedAt: null,
    effective: { readonly: [], readwrite: [], denied: ['/home/pat/.ssh'], allowHosts: [], compute: { idleTimeoutSec: 1800, wallTimeoutSec: 7200, memoryMb: null, cpuPercent: null, maxProcesses: null }, clipboard: 'none', loosened: [], notes: [] },
    implicit: { readwrite: ['/r'], readonly: ['/t'], denied: ['/d'] }, widening: [], violations: [], backend: 'bubblewrap', machineMemoryMb: 16000,
  });

  it('saves an override from the un-inherited fields only, and confirms a loosening with the exact preview', async () => {
    const { api, calls } = fakeApi({
      'members.get': () => ({ ...member }),
      'policy.getMember': view,
      'credentials.status': () => creds(),
      'policy.saveOverride': (p) => ({ ...view(), override: p.override }),
      'policy.previewLoosen': () => ({ loosened: ['network.allowHosts + registry.npmjs.org'] }),
      'policy.saveLoosen': (p) => ({ ...view(), loosen: p.loosen, loosenSummary: p.confirmed, loosenApprovedAt: '2026-11-06T00:00:00Z' }),
    });
    const w = mount(MemberSettingsDialog, { props: { memberId: 2, api, initialTab: 'policy' } });
    await flushPromises();
    await w.get('[data-test=inherit-compute-wallTimeoutSec]').setValue(false);
    await w.get('[data-test=override-compute-wallTimeoutSec]').setValue('900');
    await w.get('[data-test=save-override]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'policy.saveOverride')!.params.override).toEqual({ compute: { wallTimeoutSec: 900 } });

    await w.get('[data-test=loosen-network-allowHosts]').setValue('registry.npmjs.org');
    await w.get('[data-test=preview-loosen]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=loosen-preview]').text()).toContain('network.allowHosts + registry.npmjs.org');
    await w.get('[data-test=confirm-loosen]').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'policy.saveLoosen')!.params;
    expect(save.loosen).toEqual({ network: { allowHosts: ['registry.npmjs.org'] } });
    expect(save.confirmed).toEqual(['network.allowHosts + registry.npmjs.org']);
    expect(w.get('[data-test=loosen-current]').text()).toContain('registry.npmjs.org');
  });

  it('shows the effective network in one read-only line (it left the member card)', async () => {
    const { api } = fakeApi({
      'members.get': () => ({ ...member }),
      'policy.getMember': () => ({ ...view(), net: 'model + bus + 3 hosts' }),
      'credentials.status': () => creds(),
    });
    const w = mount(MemberSettingsDialog, { props: { memberId: 2, api, initialTab: 'policy' } });
    await flushPromises();
    expect(w.get('[data-test=policy-net]').text()).toBe('Network: model + bus + 3 hosts');
  });

  it('shows the core\'s refusal as it is', async () => {
    const { api } = fakeApi({
      'members.get': () => ({ ...member }),
      'policy.getMember': view,
      'credentials.status': () => creds(),
      'policy.saveOverride': () => { throw new Error("Error invoking remote method 'core:call': CoreRpcError: override network.allowHosts: 'x.org' is not in the global list"); },
    });
    const w = mount(MemberSettingsDialog, { props: { memberId: 2, api, initialTab: 'policy' } });
    await flushPromises();
    await w.get('[data-test=save-override]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=settings-error]').text()).toBe("override network.allowHosts: 'x.org' is not in the global list");
  });
});

describe('model credentials', () => {
  it('prompts to re-run claude setup-token by hand within 30 days of expiry', async () => {
    const { api } = fakeApi({ 'credentials.status': () => creds({ connected: true, renewSoon: true, expiresAt: '2026-12-01T00:00:00Z' }) });
    const w = mount(CredentialsDialog, { props: { api } });
    await flushPromises();
    expect(w.get('[data-test=claude-sub-status]').text()).toContain('Connected');
    expect(w.get('[data-test=claude-renew]').text()).toContain('claude setup-token');
    expect(w.get('[data-test=cred-fake-note]').text()).toContain('FAKE');
  });

  it('never shows a pasted key again: the input clears and only "Saved" comes back', async () => {
    const after = creds();
    after.claude.apiKey = { ...secret, connected: true, addedAt: '2026-11-06T00:00:00Z' };
    const { api, calls } = fakeApi({ 'credentials.status': () => creds(), 'credentials.setAnthropicApiKey': () => after });
    const w = mount(CredentialsDialog, { props: { api } });
    await flushPromises();
    await w.get('[data-test=anthropic-key-input]').setValue('fake-key-123456');
    await w.get('[data-test=save-anthropic-key]').trigger('click');
    await flushPromises();
    expect(calls.at(-1)).toEqual({ method: 'credentials.setAnthropicApiKey', params: { key: 'fake-key-123456' } });
    expect((w.get('[data-test=anthropic-key-input]').element as HTMLInputElement).value).toBe('');
    expect(w.get('[data-test=anthropic-key-status]').text()).toContain('Saved');
    expect(w.html()).not.toContain('fake-key-123456');
  });
});

describe('the renderer may reach only the screens\' methods', () => {
  it('allows the UI prefixes and refuses everything else', () => {
    expect(isUiMethod('teams.create')).toBe(true);
    expect(isUiMethod('terminal.attach')).toBe(true);
    // Every call the Logs dialog makes.
    for (const m of ['logs.sources', 'logs.who', 'logs.query', 'logs.values', 'logs.prunePreview', 'logs.prune', 'logs.setRetention']) expect(isUiMethod(m)).toBe(true);
    expect(isUiMethod('skills.list')).toBe(true);
    expect(isUiMethod('triggers.save')).toBe(true);
    expect(isUiMethod('git.merge')).toBe(true);
    expect(isUiMethod('auth')).toBe(false);
    expect(isUiMethod('core.getInfo')).toBe(false);
    expect(isUiMethod('teams.list; drop')).toBe(false);
    expect(isUiMethod(42)).toBe(false);
  });
});
