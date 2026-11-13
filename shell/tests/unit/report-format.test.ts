// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import AgentCardView from '../../src/renderer/src/components/AgentCard.vue';
import { splitReport } from '../../src/renderer/src/reportFormat';
import type { AgentCard } from '../../src/shared/api';

const member = { id: 7, teamId: 1, team: 'payments', name: 'TesterWren', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (statusLine: string): AgentCard => ({
  member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine, runId: null,
  runStartedAt: null, lastRunState: 'completed', lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
});
const wall = 'Card 50 committed on members/testerwren @ 6f280bc (no migration added). PART 1: compiles, no regressions. Full suite on main 90 failed / 1648.';

describe('reports are easier to read', () => {
  it('turns the kind prefix into a label and keeps the rest as the report', () => {
    expect(splitReport('handed back: Done.')).toEqual({ kind: 'handback', label: 'Handed back', body: 'Done.' });
    expect(splitReport('declared complete: All in.')).toMatchObject({ kind: 'complete', label: 'Declared complete', body: 'All in.' });
    expect(splitReport('blocked: No access.')).toMatchObject({ kind: 'blocked', label: 'Blocked' });
    expect(splitReport('needs decision: Which?')).toMatchObject({ kind: 'decision', label: 'Needs decision' });
    expect(splitReport('Running the tests')).toEqual({ kind: null, label: null, body: 'Running the tests' });
  });

  it('makes the first sentence of a one-line report its own summary paragraph, and leaves formatted reports as written', () => {
    expect(splitReport('handed back: ' + wall).body).toBe(
      'Card 50 committed on members/testerwren @ 6f280bc (no migration added).\n\nPART 1: compiles, no regressions. Full suite on main 90 failed / 1648.');
    const md = 'Summary line.\n\n- one\n- two';
    expect(splitReport('handed back: ' + md).body).toBe(md);
    expect(splitReport('handed back: Short one.').body).toBe('Short one.');
  });

  it('shows the label and a bold summary paragraph on the card', () => {
    const w = mount(AgentCardView, { props: { card: card('handed back: ' + wall) } });
    expect(w.get('[data-test=agent-report-kind]').text()).toBe('Handed back');
    const out = w.get('[data-test=agent-output]');
    expect(out.text()).not.toContain('handed back:');
    expect(out.findAll('p')[0].text()).toBe('Card 50 committed on members/testerwren @ 6f280bc (no migration added).');
  });
});
