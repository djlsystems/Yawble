// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import MonitorView from '../../src/renderer/src/monitor/MonitorView.vue';
import AgentCardView from '../../src/renderer/src/components/AgentCard.vue';
import GlobalPolicyDialog from '../../src/renderer/src/components/GlobalPolicyDialog.vue';
import { clockTime, heavyLine, leaseText } from '../../src/renderer/src/heavyLease';
import { setOutputHeight } from '../../src/renderer/src/agentOutputHeight';
import type { AgentCard, GlobalPolicyDoc, LeaseSnapshot, YawbleApi } from '../../src/shared/api';

const since = '2026-11-12T09:05:00.0000000+00:00';
const holding: LeaseSnapshot = {
  limit: 1,
  holders: [{ team: 'payments', member: 'DeveloperPriya', what: 'full test suite', since }],
  queue: [{ team: 'payments', member: 'TesterWren', what: 'build', since, position: 1 }, { team: 'search', member: 'DeveloperInes', what: 'image build', since, position: 2 }],
};
const free: LeaseSnapshot = { limit: 1, holders: [], queue: [] };

function monitorApi(heavy: () => LeaseSnapshot | undefined): YawbleApi {
  return {
    call: async (method: string) => {
      if (method === 'app.overview') return { slots: { running: 1, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 1, running: 1, queued: 0 }], heavy: heavy() };
      if (method === 'teams.agents') return [{ member: { id: 2, teamId: 1, team: 'payments', name: 'DeveloperPriya', role: 'member', cli: 'claude' }, state: 'running', attention: null, queuedInstructions: 0, statusLine: null }];
      if (method === 'monitor.messages') return { end: 0, messages: [] };
      if (method === 'monitor.steps') return { end: 0, pending: false, steps: [] };
      if (method === 'bus.workflows') return [];
      throw new Error(method);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
}

describe('the heavy-work line', () => {
  it('names the holder, what and since when, and how many wait; nothing when free and nobody waits', () => {
    expect(heavyLine(holding)).toBe(`Heavy work: payments / DeveloperPriya — full test suite (since ${clockTime(since)}) · 2 waiting`);
    expect(heavyLine({ ...holding, queue: [] })).toBe(`Heavy work: payments / DeveloperPriya — full test suite (since ${clockTime(since)})`);
    expect(heavyLine({ limit: 1, holders: [], queue: holding.queue })).toBe('Heavy work: free · 2 waiting');
    expect(heavyLine(free)).toBeNull();
    expect(heavyLine(undefined)).toBeNull(); // an older core sends none
  });

  it('shows on the Monitor near the counts while held, and hides once the lease is free', async () => {
    let heavy: LeaseSnapshot = holding;
    const w = mount(MonitorView, { props: { api: monitorApi(() => heavy), pollMs: 30 } });
    for (let i = 0; i < 5; i++) await flushPromises();
    const line = w.get('[data-test=monitor-heavy]');
    expect(line.text()).toContain('Heavy work: payments / DeveloperPriya — full test suite');
    expect(line.text()).toContain('· 2 waiting');
    expect(line.element.parentElement?.querySelector('[data-test=monitor-counts]')).not.toBeNull(); // in the HUD with the counts
    heavy = free;
    await new Promise((r) => setTimeout(r, 80));
    for (let i = 0; i < 5; i++) await flushPromises();
    expect(w.find('[data-test=monitor-heavy]').exists()).toBe(false);
    w.unmount();
  });

  it('is not shown by a core that sends no lease', async () => {
    const w = mount(MonitorView, { props: { api: monitorApi(() => undefined), pollMs: 60_000 } });
    for (let i = 0; i < 5; i++) await flushPromises();
    expect(w.find('[data-test=monitor-heavy]').exists()).toBe(false);
    w.unmount();
  });
});

const member = { id: 7, teamId: 1, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (over: Partial<AgentCard> = {}): AgentCard => ({
  member: { ...member }, state: 'running', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: 3,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [], ...over,
});

describe("a member's card and the heavy-work lease", () => {
  beforeEach(() => { localStorage.clear(); setOutputHeight(120); });

  it('says it holds the lease while holding it, and that it waits while queued', () => {
    const held = mount(AgentCardView, { props: { card: card({ lease: 'holding', leaseWhat: 'full test suite' }) } });
    expect(held.get('[data-test=agent-lease]').text()).toContain('holds the heavy-work lease: full test suite');
    const waiting = mount(AgentCardView, { props: { card: card({ lease: 'waiting', leaseWhat: 'build', leasePosition: 2 }) } });
    expect(waiting.get('[data-test=agent-lease]').text()).toContain('waiting for the heavy-work lease (position 2): build');
    const neither = mount(AgentCardView, { props: { card: card() } });
    expect(neither.find('[data-test=agent-lease]').exists()).toBe(false);
    expect(leaseText({ lease: null })).toBeNull();
  });
});

describe('Settings > Guardrails: Heavy work at once', () => {
  it('is in the Limits section, loads the saved number (1 when absent) and saves what the person typed', async () => {
    const doc: GlobalPolicyDoc = {
      filesystem: { grantableRoots: [], readonly: [], readwrite: [], denied: [] }, network: { allowHosts: [] },
      compute: { idleTimeoutSec: 1800, wallTimeoutSec: 7200 }, ui: { clipboard: 'none' }, app: { maxConcurrentAgents: 4, backend: 'default' },
    };
    const calls: { method: string; params: any }[] = [];
    const api = {
      call: async (method: string, params: any = {}) => {
        calls.push({ method, params });
        if (method === 'policy.getGlobal') return doc;
        if (method === 'policy.saveGlobal') return { ...doc, ...params.policy };
        if (method === 'guardrail.get') return { state: 'none', error: null };
        return {};
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = mount(GlobalPolicyDialog, { props: { api, inline: true } });
    for (let i = 0; i < 4; i++) await flushPromises();
    const input = w.get('[data-test=global-section-limits]').get('[data-test=global-heavyWorkAtOnce]');
    expect((input.element as HTMLInputElement).value).toBe('1');
    await input.setValue('2');
    await w.get('[data-test=save-global]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'policy.saveGlobal').at(-1)!.params.policy.app).toEqual({ maxConcurrentAgents: 4, backend: 'default', heavyWorkAtOnce: 2 });
    w.unmount();
  });
});
