// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import { agentPillText, agentPillTitle } from '../../src/renderer/src/memberModel';
import type { AgentCard, AgentModelShown, YawbleApi } from '../../src/shared/api';

// The member pane's agent pill: "<agent>/<model>" from the core's agentModel, and a tooltip saying where the name comes from.
const member = { id: 7, teamId: 1, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (agentModel?: AgentModelShown | null, cli = 'claude'): AgentCard => ({
  member: { ...member, cli: cli as any }, agentModel, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null,
});
const api = { call: async () => ({}), onEvent: () => () => {}, getInfo: async () => { throw new Error('no'); }, getAvailableBackends: async () => [] } as unknown as YawbleApi;
const Screen = defineComponent({ setup: () => () => h('div') });
const mounted: { unmount(): void }[] = [];
const pill = (c: AgentCard) => {
  const w = mount(MemberPane, { props: { card: c, api } as any, global: { stubs: { TerminalScreen: Screen } } });
  mounted.push(w);
  return w.get('[data-test=agent-cli]');
};
afterEach(() => { vi.useRealTimers(); while (mounted.length) mounted.pop()!.unmount(); });

describe('the agent pill', () => {
  it('names the agent and the model set on the member', () => {
    const p = pill(card({ name: 'claude-opus-5-5', source: 'member', at: null }));
    expect(p.text()).toBe('claude/claude-opus-5-5');
    expect(p.attributes('title')).toBe('claude/claude-opus-5-5\nModel set on this member');
  });

  it("names the agent's default model, and a custom agent by its own name", () => {
    const p = pill(card({ name: 'gpt-5.5-codex', source: 'agent', at: null }, 'codex-fast'));
    expect(p.text()).toBe('codex-fast/gpt-5.5-codex');
    expect(p.attributes('title')).toContain("The agent's default model");
  });

  it("names the model the last run used when the CLI picks its own, with when", () => {
    vi.useFakeTimers({ now: new Date('2026-11-18T21:00:00Z') });
    const p = pill(card({ name: 'claude-opus-5-5', source: 'observed', at: '2026-11-18T20:48:00Z' }));
    expect(p.text()).toBe('claude/claude-opus-5-5');
    expect(p.attributes('title')).toBe("claude/claude-opus-5-5\nThe CLI's default; its last run used claude-opus-5-5 (12 min ago)");
  });

  it('says default before the first run', () => {
    const p = pill(card({ name: '', source: 'cli', at: null }, 'grok'));
    expect(p.text()).toBe('grok/default');
    expect(p.attributes('title')).toBe("grok/default\nThe CLI's default model (not run yet)");
  });

  it('is the agent alone when the core sends no model (an older core)', () => {
    expect(pill(card(undefined)).text()).toBe('claude');
    expect(agentPillText('claude', null)).toBe('claude');
    expect(agentPillTitle('claude', null)).toBe('claude');
  });
});
