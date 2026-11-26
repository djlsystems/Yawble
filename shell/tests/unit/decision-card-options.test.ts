// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import type { Decision, YawbleApi } from '../../src/shared/api';

// A decision with a summary and two options renders summary, the two option buttons,
// Discuss with the Concierge and a collapsed Details, with no Approve / Always approve. Fakes only.
const decision: Decision = {
  seq: 77, from: 'Manager', deliverable: 9, at: '',
  question: 'Tests can\'t run: the sandbox blocks localhost.',
  summary: 'Tests can\'t run: the sandbox blocks localhost.',
  details: '1. Build: succeeded.\n2. dotnet test: SocketException (13).',
  options: [
    { label: 'Allow localhost & re-run tests', reply: 'Allow localhost is on. Re-run the tests.', action: 'team.allowLocalhostMac', actionAvailable: true, actionUnavailableReason: null },
    { label: 'Skip tests', reply: 'Skip the tests.', action: null },
  ],
};

function view(d: Decision) {
  const calls: { method: string; params: any }[] = [];
  const answers: Record<string, () => unknown> = { 'teams.agents': () => [], 'bus.decisions': () => [d], 'integrations.asks': () => [], 'bus.answer': () => ({ seq: 1 }) };
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a() as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  const team = { id: 5, name: 't', repoPath: null, members: 2, running: 0, queued: 0, paused: false, archived: false } as any;
  const w = mount(TeamView, { props: { team, api, pollMs: 100000 }, global: { stubs: { TaskAskCard: true, TeamSyncBanner: true } } });
  return { w, calls };
}

describe('a decision with a summary and two options', () => {
  it('shows the summary, [Allow localhost & re-run tests] [Skip tests] [Discuss with the Concierge] and collapsed Details', async () => {
    const { w, calls } = view(decision);
    await flushPromises();
    const card = w.get('[data-test=decision]');
    expect(card.get('[data-test=decision-question]').text()).toBe('Tests can\'t run: the sandbox blocks localhost.');
    const labels = card.findAll('button').map((b) => b.text().trim());
    expect(labels.slice(0, 3)).toEqual(['Allow localhost & re-run tests', 'Skip tests', 'Discuss with the Concierge']);
    expect(card.find('[data-test=decision-approve]').exists()).toBe(false);
    expect(card.find('[data-test=decision-always]').exists()).toBe(false);
    const details = card.get('[data-test=decision-details]');
    expect((details.element as HTMLDetailsElement).open).toBe(false);
    expect(details.text()).toContain('SocketException (13)');
    // Skip sends its index and no text: the core sends its reply, once.
    await card.get('[data-test=decision-choice-1]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'bus.answer')?.params).toEqual({ teamId: 5, seq: 77, optionIndex: 1, text: '' });
  });
});
