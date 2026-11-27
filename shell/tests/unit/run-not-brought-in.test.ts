// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { defineComponent, h } from 'vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import { memberStateLabel, memberStateOf } from '../../src/renderer/src/faces/faces';
import type { AgentCard, YawbleApi } from '../../src/shared/api';

/**
 * A run whose agent finished, but whose branch the app could not bring into the team repository (the core's failure
 * class "not_brought_in"): the card still wants the person's eye, and says the run finished and what was not brought
 * in, not "Last run failed". Any other failed run keeps its wording.
 */
const member = { id: 7, teamId: 1, team: 'payments', name: 'Manager', role: 'manager', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (over: Partial<AgentCard> = {}): AgentCard => ({
  member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: 'failed', lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [], attention: 'failed', ...over,
} as AgentCard);
const reason = "the member's branch was not brought into the team repository: members/payments/ada does not merge cleanly";
const api = { call: async () => ({}), onEvent: () => () => {}, openTerminal: async () => {}, pickFolder: async () => null } as unknown as YawbleApi;
const FakeScreen = defineComponent({ render: () => h('div') });
const pane = (c: AgentCard) => mount(MemberPane, { props: { card: c, api } as any, global: { stubs: { TerminalScreen: FakeScreen } } });

describe('a run that finished but was not brought in', () => {
  it('is still a state that wants the person, with its own label', () => {
    const c = card({ lastRunFailureClass: 'not_brought_in', lastRunReason: reason });
    expect(memberStateOf(c)).toBe('failed');
    expect(memberStateLabel(c)).toBe('Work not brought in');
    expect(memberStateLabel(card({ lastRunFailureClass: 'timeout', lastRunReason: 'timed out' }))).toBe('Last run failed');
    expect(memberStateLabel(card({ lastRunFailureClass: null, lastRunReason: 'exit 1' }))).toBe('Last run failed');
  });

  it('the member card says the run finished and what was not brought in', () => {
    const w = pane(card({ lastRunFailureClass: 'not_brought_in', lastRunReason: reason }));
    expect(w.get('[data-test=agent-face-state]').text()).toBe('Work not brought in');
    expect(w.text()).toContain(`last run finished, but ${reason}`);
    expect(w.text()).not.toContain('last run failed');
  });

  it('any other failed run keeps "Last run failed" and its reason', () => {
    const w = pane(card({ lastRunFailureClass: 'failed', lastRunReason: 'exit 1' }));
    expect(w.get('[data-test=agent-face-state]').text()).toBe('Last run failed');
    expect(w.text()).toContain('last run failed: exit 1');
  });
});
