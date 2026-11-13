// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import GuardrailPanel from '../../src/renderer/src/components/GuardrailPanel.vue';
import type { AgentCard, GuardrailReport, GuardrailResult, GuardrailState, YawbleApi } from '../../src/shared/api';
import { isUiMethod } from '../../src/shared/api';

// The guardrail check's button and panel against a scripted core (guardrail.start/get/stop).
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

async function until(w: VueWrapper, what: string, cond: () => boolean) {
  for (let i = 0; i < 200; i++) {
    if (cond()) return;
    await new Promise((r) => setTimeout(r, 5));
    await flushPromises();
  }
  throw new Error(`timed out waiting for ${what}:\n${w.html()}`);
}

const result = (over: Partial<GuardrailResult>): GuardrailResult => ({
  id: 'x', category: 'filesystem', verdict: 'BLOCKED', expected: 'BLOCKED', asExpected: true, evidence: 'EACCES',
  sandboxOnly: false, logged: null, ...over,
});

const report = (over: Partial<GuardrailReport> = {}): GuardrailReport => ({
  id: 'r1', subject: { team: 'payments', member: 'Ben', cli: 'grok', synthetic: false }, os: 'linux',
  startedAt: '2026-11-06T10:00:00Z', durationMs: 3300, passed: true,
  counts: { blocked: 3, allowed: 1, unexpected: 0, skipped: 0, info: 0 },
  sandbox: { backend: 'bubblewrap', status: 0, exitCode: 0, timedOut: false, refusal: null, stderr: null },
  allowHosts: [], loosened: [], notes: ['The sandbox masks denied folders with a writable tmpfs.'],
  results: [
    result({ id: 'write.host-home', evidence: 'created in the sandbox only; not on the host', sandboxOnly: true }),
    result({ id: 'app.grok-home', category: 'app-data', evidence: 'ENOENT' }),
    result({ id: 'gateway.connect-denied-host', category: 'gateway', evidence: '403 for example.com', logged: true }),
    result({ id: 'write.own-clone', verdict: 'ALLOWED', expected: 'ALLOWED', evidence: 'reached the host' }),
  ],
  run: { runKey: 'k1', runNumber: 7, end: 'completed', reason: null, backend: 'yawble-ffi', credentialMode: 'GrokLogin', credentialConfigured: false, killLatencyMs: null },
  ...over,
});

describe('guardrail check: wiring', () => {
  it('lets the window reach the guardrail methods and nothing new besides', () => {
    expect(isUiMethod('guardrail.start')).toBe(true);
    expect(isUiMethod('guardrail.get')).toBe(true);
    expect(isUiMethod('guardrail.stop')).toBe(true);
    expect(isUiMethod('runs.stop')).toBe(false);
  });

  it("opens from an agent card's shield into the agent's Guardrails tab with its last report", async () => {
    const member = { id: 2, teamId: 1, team: 'payments', name: 'Ben', role: 'member', cli: 'grok', instructions: '', model: '', credential: 'default' } as const;
    const card: AgentCard = {
      member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
      runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
    };
    const { api, calls } = fakeApi({
      'app.overview': () => ({ slots: { running: 0, limit: 3, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 }] }),
      'teams.agents': () => [card],
      'bus.decisions': () => [],
      'members.get': () => ({ ...member }),
      'policy.getMember': () => ({
        memberId: 2, override: {}, loosen: {}, loosenSummary: [], loosenApprovedAt: null, widening: [], violations: [], backend: 'bubblewrap', machineMemoryMb: null,
        global: { filesystem: { grantableRoots: [], readonly: [], readwrite: [], denied: [] }, network: { allowHosts: [] }, compute: { idleTimeoutSec: 1800, wallTimeoutSec: 7200 }, ui: { clipboard: 'none' }, app: { maxConcurrentAgents: 3, backend: 'default' } },
        effective: { readonly: [], readwrite: [], denied: [], allowHosts: [], compute: { idleTimeoutSec: 1800, wallTimeoutSec: 7200, memoryMb: null, cpuPercent: null, maxProcesses: null }, clipboard: 'none', loosened: [], notes: [] },
        implicit: { readwrite: [], readonly: [], denied: [] },
      }),
      'credentials.status': () => ({
        implementation: 'fake', storage: 'none', storageWarning: null,
        claude: { mode: 'subscription', subscription: { connected: false }, apiKey: { connected: false } },
        grok: { mode: 'login', login: { signedIn: false }, apiKey: { connected: false } },
      }),
      'guardrail.get': (): GuardrailState => ({ state: 'done', report: report() }),
    });
    const w = mount(App, { props: { api, pollMs: 60_000 } });
    await until(w, 'the team in the sidebar', () => w.find('[data-test=team-item]').exists());
    await w.get('[data-test=team-item]').trigger('click');
    await until(w, 'the card', () => w.find('[data-test=agent-Ben] [data-test=open-guardrail]').exists());
    await w.get('[data-test=agent-Ben] [data-test=open-guardrail]').trigger('click');
    await until(w, 'the report', () => w.find('[data-test=guardrail-summary]').exists());
    expect(w.get('[data-test=tab-guardrails]').classes()).toContain('on');
    expect(calls.find((c) => c.method === 'guardrail.get')!.params).toEqual({ team: 'payments', member: 'Ben' });
    expect(w.get('[data-test=guardrail-verdict]').text()).toBe('PASSED');
    expect(w.get('[data-test=guardrail-summary]').text()).toContain('3 blocked · 1 allowed · 0 unexpected');
    expect(w.get('[data-test=guardrail-summary]').text()).toContain('run completed on yawble-ffi');
    expect(w.get('[data-test=guardrail-notes]').text()).toContain('writable tmpfs');
    const home = w.get('[data-test=guardrail-result-write\\.host-home]');
    expect(home.text()).toContain('BLOCKED');
    expect(home.text()).toContain('sandbox only');
    expect(w.get('[data-test=guardrail-result-gateway\\.connect-denied-host]').text()).toContain('logged by the gateway');
    w.unmount();
  });
});

describe('guardrail check: the panel', () => {
  it('starts, shows queued with its position, then running, stops, and shows the stopped report', async () => {
    let state: GuardrailState = { state: 'none', error: null };
    const { api, calls } = fakeApi({
      'guardrail.get': () => state,
      'guardrail.start': () => {
        state = { state: 'running', phase: 'queued', position: 2 };
        return { state: 'running', subject: { team: 'payments', member: 'Ben' } };
      },
      'guardrail.stop': () => {
        state = { state: 'done', report: report({ passed: false, run: { ...report().run!, end: 'stopped' }, results: [], counts: { blocked: 0, allowed: 0, unexpected: 0, skipped: 0, info: 0 }, notes: ['The check was stopped before it finished, so this report is incomplete; run it again.'] }) };
        return { stopped: true };
      },
    });
    const w = mount(GuardrailPanel, { props: { api, team: 'payments', member: 'Ben', pollMs: 5 } });
    await until(w, 'none', () => w.find('[data-test=guardrail-none]').exists());
    expect(w.find('[data-test=guardrail-stop]').exists()).toBe(false);

    await w.get('[data-test=guardrail-run]').trigger('click');
    await until(w, 'queued', () => w.find('[data-test=guardrail-progress]').exists());
    expect(calls.find((c) => c.method === 'guardrail.start')!.params).toEqual({ team: 'payments', member: 'Ben' });
    expect(w.get('[data-test=guardrail-progress]').text()).toBe('Queued · position 2 in the global run queue · waiting for a run slot');
    expect(w.get('[data-test=guardrail-run]').attributes('disabled')).toBeDefined();

    state = { state: 'running', phase: 'running', position: null };
    await until(w, 'running', () => w.get('[data-test=guardrail-progress]').text().startsWith('Running'));

    await w.get('[data-test=guardrail-stop]').trigger('click');
    await until(w, 'the stopped report', () => w.find('[data-test=guardrail-verdict]').exists());
    expect(calls.filter((c) => c.method === 'guardrail.stop')).toHaveLength(1);
    expect(calls.find((c) => c.method === 'guardrail.stop')!.params).toEqual({ team: 'payments', member: 'Ben' });
    expect(w.get('[data-test=guardrail-verdict]').text()).toBe('STOPPED');
    expect(w.get('[data-test=guardrail-notes]').text()).toContain('stopped before it finished');
    expect(w.find('[data-test=guardrail-stop]').exists()).toBe(false);
    expect(w.get('[data-test=guardrail-run]').text()).toBe('Run the check again');

    // No more polling once done.
    const gets = calls.filter((c) => c.method === 'guardrail.get').length;
    await new Promise((r) => setTimeout(r, 40));
    expect(calls.filter((c) => c.method === 'guardrail.get').length).toBe(gets);
    w.unmount();
  });

  it('shows a busy refusal, an unexpected result first, and the global policy subject', async () => {
    const failing = report({
      passed: false, counts: { blocked: 2, allowed: 2, unexpected: 1, skipped: 0, info: 0 },
      results: [
        result({ id: 'app.grok-home', evidence: 'ENOENT' }),
        result({ id: 'home.ssh', category: 'home', verdict: 'ALLOWED', asExpected: false, evidence: 'read 411 bytes of ~/.ssh/id_ed25519' }),
      ],
    });
    const { api } = fakeApi({
      'guardrail.get': () => ({ state: 'done', report: failing }),
      'guardrail.start': () => { throw new Error("Error invoking remote method 'core:call': CoreRpcError: busy: Ben is running; run the check when the member is idle"); },
    });
    const w = mount(GuardrailPanel, { props: { api, team: 'payments', member: 'Ben', pollMs: 5 } });
    await until(w, 'report', () => w.find('[data-test=guardrail-results]').exists());
    expect(w.get('[data-test=guardrail-verdict]').text()).toBe('FAILED');
    const rows = w.findAll('[data-test=guardrail-results] tbody tr');
    expect(rows[0].attributes('data-test')).toBe('guardrail-result-home.ssh');
    expect(rows[0].classes()).toContain('bad');
    expect(rows[0].text()).toContain('expected BLOCKED');
    await w.get('[data-test=guardrail-run]').trigger('click');
    await until(w, 'busy', () => w.find('[data-test=guardrail-error]').exists());
    expect(w.get('[data-test=guardrail-error]').text()).toBe('busy: Ben is running; run the check when the member is idle');
    w.unmount();

    const global = fakeApi({ 'guardrail.get': () => ({ state: 'none', error: 'the probe is not installed: /opt/yawble/bin/yawble-guardcheck' }) });
    const g = mount(GuardrailPanel, { props: { api: global.api, pollMs: 5 } });
    await until(g, 'unavailable', () => g.find('[data-test=guardrail-unavailable]').exists());
    expect(global.calls[0].params).toEqual({});
    expect(g.text()).toContain('the global policy alone');
    expect(g.get('[data-test=guardrail-unavailable]').text()).toContain('probe is not installed');
    expect(g.get('[data-test=guardrail-run]').attributes('disabled')).toBeDefined();
    g.unmount();
  });
});
