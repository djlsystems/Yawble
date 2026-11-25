// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { defineComponent, h } from 'vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import { resetMemberViews } from '../../src/renderer/src/memberPaneView';
import { workflowsRequest } from '../../src/renderer/src/workflows/page';
import type { AgentCard, YawbleApi } from '../../src/shared/api';

// The member card's "#N" links next to "Running": one per workflow the run works on (teams.agents runWorkflows),
// each opening that workflow on the Workflows page. Fakes only: a scripted core, a stand-in terminal screen.
const member = { id: 7, teamId: 3, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
/** A teams.agents card in the core's shape; runWorkflows as the core sends it ([] when not running). */
const card = (over: Partial<AgentCard> = {}): AgentCard => ({
  member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
  runWorkflows: [], ...over,
});
const api = {
  getInfo: async () => { throw new Error('no'); }, getAvailableBackends: async () => [],
  call: async () => ({}), onEvent: () => () => {},
} as unknown as YawbleApi;
const FakeScreen = defineComponent({ setup: () => () => h('div') });
const mounted: { unmount(): void }[] = [];
const pane = (c: AgentCard) => {
  const w = mount(MemberPane, { props: { card: c, api } as any, global: { stubs: { TerminalScreen: FakeScreen } } });
  mounted.push(w);
  return w;
};

beforeEach(() => { localStorage.clear(); resetMemberViews(); workflowsRequest.value = null; });
afterEach(() => { while (mounted.length) { try { mounted.pop()!.unmount(); } catch { /* already */ } } });

describe('member card: the workflows its run works on', () => {
  it('shows one "#N" link per workflow while running, oldest first, joined with commas, each with its tooltip', () => {
    const w = pane(card({ state: 'running', runId: 90, runWorkflows: [12, 15] }));
    const links = w.findAll('[data-test=agent-run-workflow]');
    expect(links.map((l) => l.text())).toEqual(['#12', '#15']);
    expect(links.map((l) => l.attributes('data-id'))).toEqual(['12', '15']);
    expect(links.map((l) => l.attributes('title'))).toEqual(['Open workflow #12 on the Workflows page', 'Open workflow #15 on the Workflows page']);
    expect(w.get('[data-test=agent-run-workflows]').text()).toBe('#12, #15');
  });

  it('sits right after the state word and before the "more waiting" note', () => {
    const w = pane(card({ state: 'running', runId: 90, runWorkflows: [12], queuedInstructions: 2 }));
    const order = [...w.get('[data-test=agent-pills]').element.children].map((e) => (e as HTMLElement).dataset.test);
    const at = (t: string) => order.indexOf(t);
    expect(at('agent-run-workflows')).toBe(at('agent-face-state') + 1);
    expect(at('agent-more-waiting')).toBe(at('agent-run-workflows') + 1);
  });

  it('a click opens that workflow on the Workflows page for the member\'s team, no dialog', async () => {
    const w = pane(card({ state: 'running', runId: 90, runWorkflows: [12, 15] }));
    await w.findAll('[data-test=agent-run-workflow]')[1].trigger('click');
    expect(workflowsRequest.value).toMatchObject({ teamId: 3, workflow: 15 });
    expect(w.find('[role=dialog]').exists()).toBe(false);
  });

  it('shows no link when not running, when the list is empty, or when an older core omits the field', () => {
    expect(pane(card({ state: 'idle', runWorkflows: [12] })).find('[data-test=agent-run-workflow]').exists()).toBe(false);
    expect(pane(card({ state: 'queued', queuePosition: 1, queueLength: 2, runWorkflows: [12] })).find('[data-test=agent-run-workflows]').exists()).toBe(false);
    expect(pane(card({ state: 'running', runId: 90, runWorkflows: [] })).find('[data-test=agent-run-workflows]').exists()).toBe(false);
    const older = card({ state: 'running', runId: 90 });
    delete older.runWorkflows;
    const w = pane(older);
    expect(w.find('[data-test=agent-run-workflows]').exists()).toBe(false);
    expect(w.get('[data-test=agent-face-state]').text()).toBe('Running');
  });
});
