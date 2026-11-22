// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import { decisionLayout } from '../../src/renderer/src/questionFormat';
import { memberReport } from '../../src/renderer/src/reportFormat';
import { memberStateOf } from '../../src/renderer/src/faces/faces';
import type { AgentCard, Decision, DecisionOption, YawbleApi } from '../../src/shared/api';

// B123: the decision card (plain ask first, the asker's answers as buttons, Details collapsed), the one-click action
// option, and the member tile's "Waiting on you". Fakes only.
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

const team = { id: 3, name: 'hello-world', repoPath: null, members: 2, running: 0, queued: 0, paused: false, archived: false } as any;

// The B123 card's question as the Manager wrote it (findings list, then the closing question).
const oldQuestion = 'B119 part 2 verify: build, isolation and outbound checks behaved as expected, but `dotnet test` cannot run on macOS '
  + 'in the sandbox: SocketException (13) Permission denied.\n\nResults:\n\n1. Build: succeeded.\n2. dotnet test (VSTest): failed.\n'
  + '3. MTP variant (dotnet exec): failed.\n4. Isolation and outbound (writes, api.nuget.org): blocked as expected.\n5. Loopback to host port 9999: refused.\n\n'
  + 'Do you want to turn on **Allow localhost (macOS)** in Team settings > Network? Reply **approve** once it is on and I will have the '
  + 'test run repeated, or tell me to skip it and close without the test run.';

const newDecision = (over: Partial<DecisionOption> = {}): Decision => ({
  seq: 41, from: 'Manager', workflow: 7, at: '', question: oldQuestion,
  summary: 'Tests can\'t run: the sandbox blocks localhost. Turn it on for this team?',
  details: 'Results:\n\n1. Build: succeeded.\n2. dotnet test (VSTest): failed.',
  options: [
    { label: 'Allow localhost & re-run tests', reply: 'Allow localhost is on now. Repeat the test run.', action: 'team.allowLocalhostMac', actionAvailable: true, ...over },
    { label: 'Skip tests', reply: 'Skip the test run and close without it.' },
  ],
});

describe('decisionLayout (B123)', () => {
  it('a decision with a summary: summary first, its options in order with their index, details apart', () => {
    const l = decisionLayout(newDecision());
    expect(l.summary).toBe('Tests can\'t run: the sandbox blocks localhost. Turn it on for this team?');
    expect(l.buttons.map((b) => [b.label, b.optionIndex])).toEqual([['Allow localhost & re-run tests', 0], ['Skip tests', 1]]);
    expect(l.buttons[0].actionNote).toContain('localhost');
    expect(l.buttons[0].actionNote).toContain('Off by default');
    expect(l.buttons[0].disabled).toBe(false);
    expect(l.buttons[1].action).toBeUndefined();
    expect(l.details.startsWith('Results:')).toBe(true);
  });

  it('an unavailable action is disabled with core\'s reason', () => {
    const l = decisionLayout(newDecision({ actionAvailable: false, actionUnavailableReason: 'A Visitor cannot change team settings.' }));
    expect(l.buttons[0]).toMatchObject({ disabled: true, disabledReason: 'A Visitor cannot change team settings.' });
  });

  it('an old question (no summary): the paragraph that asks first, the rest under Details, Approve + Skip', () => {
    const l = decisionLayout({ question: oldQuestion });
    expect(l.summary.startsWith('Do you want to turn on **Allow localhost (macOS)**')).toBe(true);
    expect(l.details.startsWith('B119 part 2 verify')).toBe(true);
    expect(l.details).toContain('5. Loopback to host port 9999: refused.');
    expect(l.buttons.map((b) => b.kind)).toEqual(['approve', 'skip']);
  });

  it('an old one-paragraph question keeps all of it as the ask and has no Details', () => {
    expect(decisionLayout({ question: 'Use Postgres?' })).toMatchObject({ summary: 'Use Postgres?', details: '', buttons: [] });
  });
});

describe('the decision card (B123)', () => {
  const mountView = (d: Decision) => {
    const { api, calls } = fakeApi({
      'teams.agents': () => [], 'bus.decisions': () => [d], 'integrations.asks': () => [], 'bus.answer': () => ({}),
    });
    const w = mount(TeamView, { props: { team, api, pollMs: 100000 }, global: { stubs: { TaskAskCard: true, TeamSyncBanner: true } } });
    return { w, calls };
  };

  it('shows header, summary, the options, Discuss, the answer box, then a collapsed Details, in that order', async () => {
    const { w } = mountView(newDecision());
    await flushPromises();
    const card = w.get('[data-test=decision]');
    expect(card.text().startsWith('Manager needs a decision')).toBe(true);
    expect(card.get('[data-test=decision-question]').text()).toBe('Tests can\'t run: the sandbox blocks localhost. Turn it on for this team?');
    expect(card.get('[data-test=decision-choice-0]').text()).toBe('Allow localhost & re-run tests');
    expect(card.get('[data-test=decision-choice-1]').text()).toBe('Skip tests');
    expect(card.get('[data-test=decision-choice-note]').text()).toContain('this Mac\'s localhost');
    expect(card.find('[data-test=decision-option-1]').exists()).toBe(false);
    const html = card.html();
    const order = ['decision-question', 'decision-choice-0', 'decision-discuss', 'decision-answer', 'decision-details'].map((t) => html.indexOf(`data-test="${t}"`));
    expect(order.every((x, i) => x > 0 && (i === 0 || x > order[i - 1]))).toBe(true);
    const details = card.get('[data-test=decision-details]');
    expect((details.element as HTMLDetailsElement).open).toBe(false);
    expect(details.get('summary').text()).toBe('Details');
  });

  it('clicking an option answers with its reply and optionIndex', async () => {
    const { w, calls } = mountView(newDecision());
    await flushPromises();
    await w.get('[data-test=decision-choice-0]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'bus.answer')?.params).toEqual({
      teamId: 3, seq: 41, optionIndex: 0, text: 'Allow localhost is on now. Repeat the test run.',
    });
  });

  it('an action core says is not available is a disabled button with the reason under it', async () => {
    const { w, calls } = mountView(newDecision({ actionAvailable: false, actionUnavailableReason: 'Only on macOS.' }));
    await flushPromises();
    const b = w.get('[data-test=decision-choice-0]');
    expect(b.attributes('disabled')).toBeDefined();
    expect(b.attributes('title')).toBe('Only on macOS.');
    expect(w.get('[data-test=decision-choice-unavailable]').text()).toBe('Only on macOS.');
    await b.trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'bus.answer')).toBe(false);
  });

  it('the old B123 question: the ask first, Approve and Skip (no "1. Build" buttons), findings under Details', async () => {
    const { w, calls } = mountView({ seq: 40, from: 'Manager', workflow: 7, at: '', question: oldQuestion });
    await flushPromises();
    expect(w.get('[data-test=decision-question]').text().startsWith('Do you want to turn on Allow localhost (macOS)')).toBe(true);
    expect(w.findAll('[data-test^=decision-option-]')).toHaveLength(0);
    expect(w.get('[data-test=decision-approve]').text()).toBe('Approve');
    expect(w.get('[data-test=decision-details-body]').text()).toContain('Loopback to host port 9999');
    await w.get('[data-test=decision-skip]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'bus.answer')?.params).toEqual({ teamId: 3, seq: 40, text: 'Skip it.' });
  });
});

describe('"Waiting on you" on the member tile (B123)', () => {
  const member = { id: 1, teamId: 3, team: 'hello-world', name: 'Manager', role: 'manager', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
  const card = (over: Partial<AgentCard> = {}): AgentCard => ({
    member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
    runStartedAt: null, lastRunState: 'completed', lastRunReason: null, ...over,
  });

  it('memberReport: an open decision wins over an earlier "Declared complete"', () => {
    expect(memberReport({ statusLine: 'declared complete: Nothing worth keeping.', decisionId: 41 }))
      .toMatchObject({ kind: 'waiting', label: 'Waiting on you', decisionId: 41, body: 'A decision is waiting for your answer on the team page.' });
    expect(memberReport({ statusLine: 'needs decision: Turn on localhost?', attention: 'decision' })).toMatchObject({ kind: 'waiting', body: 'Turn on localhost?' });
    expect(memberReport({ statusLine: 'declared complete: All in.' })).toMatchObject({ kind: 'complete', label: 'Declared complete', decisionId: null });
    expect(memberStateOf({ state: 'idle', attention: null, decisionId: 41 })).toBe('decision');
  });

  it('a member that declared complete and then asked shows "Waiting on you" linking to the card, until answered', async () => {
    const w = mount(MemberPane, { props: { card: card({ statusLine: 'declared complete: Nothing worth keeping.', attention: 'decision', decisionId: 41 }) }, global: { stubs: { FolderAsks: true } } });
    expect(w.get('[data-test=agent-face-state]').text()).toBe('Waiting on you');
    expect(w.get('[data-test=agent-report-kind]').text()).toBe('Waiting on you');
    expect(w.get('[data-test=agent-output]').text()).not.toContain('Nothing worth keeping');
    expect(w.get('[data-test=agent-open-decision]').text()).toContain('Waiting on you');
    const target = document.createElement('div');
    target.setAttribute('data-decision-seq', '41');
    let scrolled = false;
    target.scrollIntoView = () => { scrolled = true; };
    document.body.appendChild(target);
    await w.get('[data-test=agent-open-decision]').trigger('click');
    expect(scrolled).toBe(true);
    target.remove();
    await w.setProps({ card: card({ statusLine: 'declared complete: Nothing worth keeping.', attention: null, decisionId: null }) });
    expect(w.get('[data-test=agent-report-kind]').text()).toBe('Declared complete');
    expect(w.get('[data-test=agent-face-state]').text()).not.toBe('Waiting on you');
  });
});
