// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import AgentCardView from '../../src/renderer/src/components/AgentCard.vue';
import { setOutputHeight } from '../../src/renderer/src/agentOutputHeight';
import type { AgentCard } from '../../src/shared/api';

const member = { id: 7, teamId: 1, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (over: Partial<AgentCard> = {}): AgentCard => ({
  member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [], ...over,
});
const report = 'handed back: Card 42 is done.\n\n**Delivered**\n1. **Settings > Health tab** (new `HealthPanel.vue`)\n2. Backups section';

describe("a member's card", () => {
  beforeEach(() => { localStorage.clear(); setOutputHeight(120); });

  it('has its action buttons in its title bar, next to its name and state', () => {
    const w = mount(AgentCardView, { props: { card: card({ state: 'running', statusLine: 'Split work' }) } });
    const title = w.get('[data-test=agent-title]');
    expect(title.find('[data-test=agent-name]').exists()).toBe(true);
    expect(title.get('[data-test=agent-face-state]').text()).toBe('Running');
    for (const b of ['open-terminal', 'open-settings', 'open-guardrail', 'stop', 'remove-agent']) expect(title.find(`[data-test=${b}]`).exists()).toBe(true);
  });

  it("shows what the agent said as formatted text in its own box, not squeezed into the state line", () => {
    const w = mount(AgentCardView, { props: { card: card({ lastRunState: 'completed', statusLine: report }) } });
    const out = w.get('[data-test=agent-output]');
    expect(out.find('strong').text()).toBe('Delivered');
    expect(out.findAll('ol li').map((li) => li.text())).toEqual(['Settings > Health tab (new HealthPanel.vue)', 'Backups section']);
    expect(out.find('code').text()).toBe('HealthPanel.vue');
    expect(w.get('[data-test=agent-state]').text()).not.toContain('Delivered');
    expect(w.get('[data-test=agent-state]').text()).toContain('last run completed');
  });

  it('never renders HTML an agent wrote', () => {
    const w = mount(AgentCardView, { props: { card: card({ state: 'running', statusLine: '<img src=x onerror=alert(1)> hi' }) } });
    expect(w.get('[data-test=agent-output]').find('img').exists()).toBe(false);
  });

  it('keeps the output at a fixed height that scrolls inside, and keeps the box when there is nothing said', () => {
    const w = mount(AgentCardView, { props: { card: card({ state: 'running', statusLine: report }) } });
    const out = w.get('[data-test=agent-output]');
    expect((out.element as HTMLElement).style.height).toBe('120px');
    expect(out.classes()).toContain('scrolls');
    expect(out.find('[data-test=agent-output-empty]').exists()).toBe(false);
    // Nothing said (a new member, or after the app restarted): the same box at the shared height, a muted line in it.
    const quiet = mount(AgentCardView, { props: { card: card() } });
    const box = quiet.get('[data-test=agent-output]');
    expect((box.element as HTMLElement).style.height).toBe('120px');
    expect(box.get('[data-test=agent-output-empty]').text()).toBe('Nothing said yet.');
    expect(box.get('[data-test=agent-output-empty]').classes()).toContain('muted');
    const ranBefore = mount(AgentCardView, { props: { card: card({ lastRunState: 'completed', statusLine: '  ' }) } });
    expect(ranBefore.get('[data-test=agent-output-empty]').text()).toBe('No report since the app started.');
  });

  it('has no resize affordance of its own: the gap handles on the team board resize it (member-gap-handles.test.ts)', () => {
    const w = mount(AgentCardView, { props: { card: card({ state: 'running', statusLine: report }) } });
    expect(w.find('[data-test=agent-resize]').exists()).toBe(false);
    expect(w.find('[role=slider],[role=separator]').exists()).toBe(false);
  });

  it('keeps the shared height for next time', () => {
    setOutputHeight(240);
    const again = mount(AgentCardView, { props: { card: card({ state: 'running', statusLine: report }) } });
    expect((again.get('[data-test=agent-output]').element as HTMLElement).style.height).toBe('240px');
    expect(localStorage.getItem('yawble.agentOutput')).toBe('240');
  });
});

describe("a member's card: what waits, and no policy summary", () => {
  beforeEach(() => { localStorage.clear(); setOutputHeight(120); });

  it('says how many tells wait behind a running member, with why in its tooltip', () => {
    const w = mount(AgentCardView, { props: { card: card({ state: 'running', queuedInstructions: 2 }) } });
    const more = w.get('[data-test=agent-title]').get('[data-test=agent-more-waiting]');
    expect(more.text()).toBe('· 2 more waiting');
    expect(more.attributes('title')).toBe('2 instructions waiting; they run after this one (oldest workflow first)');
    const one = mount(AgentCardView, { props: { card: card({ state: 'running', queuedInstructions: 1 }) } });
    expect(one.get('[data-test=agent-more-waiting]').attributes('title')).toBe('1 instruction waiting; they run after this one (oldest workflow first)');
    expect(mount(AgentCardView, { props: { card: card({ state: 'running', queuedInstructions: 0 }) } }).find('[data-test=agent-more-waiting]').exists()).toBe(false);
    // A queued member already says its instructions in the state line.
    expect(mount(AgentCardView, { props: { card: card({ state: 'queued', queuePosition: 1, queueLength: 1, queuedInstructions: 2 }) } })
      .find('[data-test=agent-more-waiting]').exists()).toBe(false);
  });

  it('shows no network / timeout summary, but keeps policy problems', () => {
    const w = mount(AgentCardView, { props: { card: card({ net: 'model + bus + 3 hosts', wallTimeout: '2 h', lastRunState: 'completed' }) } });
    const state = w.get('[data-test=agent-state]').text();
    expect(state).not.toContain('net:');
    expect(state).not.toContain('model + bus');
    expect(state).not.toContain('timeout');
    expect(state).toBe('last run completed');
    const bad = mount(AgentCardView, { props: { card: card({ policyProblems: ['global compute.idleTimeoutSec must be 60..86400'] }) } });
    expect(bad.get('[data-test=agent-policy-problems]').text()).toBe('policy: global compute.idleTimeoutSec must be 60..86400');
  });
});
