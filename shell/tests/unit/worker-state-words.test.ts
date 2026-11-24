// @vitest-environment happy-dom
// Card 565 (B126 shell): the real waiting state, not always "queued for a run slot". The core's worker states
// (card 564): queued | waits-for-run (waitsFor: the sender) | held | pending, on workflows.list members and teams.agents.
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import type { AgentCard, WorkflowRow, WorkflowsListAnswer, YawbleApi } from '../../src/shared/api';
import { cardState, waitSays, workerSays } from '../../src/renderer/src/waiting';
import { statusTip } from '../../src/renderer/src/workflows/page';
import { memberStateOf } from '../../src/renderer/src/faces/faces';
import WorkflowsView from '../../src/renderer/src/workflows/WorkflowsView.vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';

enableAutoUnmount(afterEach);
afterEach(() => { document.body.innerHTML = ''; localStorage.clear(); });

describe('each worker state in words', () => {
  it.each([
    ['queued', null, 'queued for a run slot'],
    ['waits-for-run', 'DeveloperAda', "waits for Developer Ada's run to end"],
    ['waits-for-run', null, "waits for another member's run to end"],
    ['held', null, 'waits: team paused'],
    ['pending', null, 'has work waiting'],
    ['something-new', null, 'has work waiting'],
  ])('%s (%s): %s', (state, waitsFor, says) => {
    expect(waitSays(state, waitsFor)).toBe(says);
    expect(workerSays({ state, waitsFor })).toBe(says);
  });
  it('running and the lease keep their words', () => {
    expect(workerSays({ state: 'running' })).toBe('running');
    expect(workerSays({ state: 'lease' })).toBe('waiting for the heavy-work lease');
  });
  it('every not-started state rings as waiting', () => {
    for (const state of ['queued', 'waits-for-run', 'held', 'pending'] as const) expect(memberStateOf({ state })).toBe('waiting');
    expect(memberStateOf({ state: 'idle' })).toBe('idle');
  });
});

describe('the Status tooltip of a waiting workflow', () => {
  const row = (members: WorkflowRow['members'], waitingFor: WorkflowRow['waitingFor'] = 'slot') =>
    ({ status: 'waiting', state: 'open', waitingFor, expiresAt: null, working: false, members }) as const;
  it('a queued worker: waiting for a run slot', () => {
    expect(statusTip(row([{ id: 1, name: 'Eve', state: 'queued' }]))).toBe('Waiting for a run slot');
  });
  it('a hand-back held for its sender\'s run: says so, not a run slot', () => {
    expect(statusTip(row([{ id: 1, name: 'Manager', state: 'waits-for-run', waitsFor: 'TesterBen' }])))
      .toBe("Waiting: Manager waits for Tester Ben's run to end");
  });
  it('a paused team and other pending work', () => {
    expect(statusTip(row([{ id: 1, name: 'Manager', state: 'held' }]))).toBe('Waiting: Manager waits: team paused');
    expect(statusTip(row([{ id: 2, name: 'DeveloperAda', state: 'pending' }], null))).toBe('Waiting: Developer Ada has work waiting');
  });
  it('the lease and a wake-up keep their own words', () => {
    expect(statusTip(row([{ id: 1, name: 'Ben', state: 'held' }], 'lease'))).toBe('Waiting for the heavy-work lease');
    expect(statusTip(row([], 'wake'))).toBe('Waiting for a wake-up');
  });
});

describe('the Workflows page Who column', () => {
  const real = JSON.parse(readFileSync(path.join(__dirname, 'fixtures', 'workflows', 'list-open.json'), 'utf8')) as WorkflowsListAnswer;
  const EVE = 17;
  async function page(members: WorkflowRow['members']) {
    const answer = structuredClone(real);
    answer.workflows.find((w) => w.id === EVE)!.members = members;
    const api = { call: async (m: string) => (m === 'workflows.list' ? structuredClone(answer) : {}) } as unknown as YawbleApi;
    const w = mount(WorkflowsView, { props: { teams: [{ id: 1, name: 'Web' }, { id: 2, name: 'Cloud' }], api, pollMs: 600_000 }, attachTo: document.body });
    await flushPromises();
    const r = w.find(`[data-test=workflows-row][data-wf="${EVE}"]`);
    return { who: r.findAll('[data-test=workflows-who] > .face').map((f) => f.attributes('title')), tip: r.find('td.st').attributes('title') };
  }
  it.each([
    [{ state: 'queued' }, 'Eve: queued for a run slot', 'Waiting for a run slot'],
    [{ state: 'waits-for-run', waitsFor: 'Dee' }, "Eve: waits for Dee's run to end", "Waiting: Eve waits for Dee's run to end"],
    [{ state: 'held' }, 'Eve: waits: team paused', 'Waiting: Eve waits: team paused'],
    [{ state: 'pending' }, 'Eve: has work waiting', 'Waiting: Eve has work waiting'],
  ] as const)('%o', async (m, says, tip) => {
    const got = await page([{ id: 6, name: 'Eve', ...m }]);
    expect(got.who[0]).toMatch(new RegExp(`${says.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
    expect(got.tip).toBe(tip);
  });
});

describe('the member pane state line', () => {
  const member = { id: 7, teamId: 1, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
  const card = (over: Partial<AgentCard>): AgentCard => ({
    member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 1, statusLine: null, runId: null,
    runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [], ...over,
  } as AgentCard);
  const stateText = (c: AgentCard) => mount(MemberPane, { props: { card: c } as any, global: { stubs: { TerminalScreen: true } } }).get('[data-test=agent-state]').text();
  it.each([
    [{ state: 'queued', queuePosition: 2, queueLength: 3 }, 'position 2 of 3 for a global run slot · 1 instruction'],
    [{ state: 'waits-for-run', waitsFor: 'TesterBen' }, "waits for Tester Ben's run to end · 1 instruction"],
    [{ state: 'held', queuedInstructions: 2 }, 'waits: team paused · 2 instructions'],
    [{ state: 'pending' }, 'has work waiting · 1 instruction'],
  ] as const)('%o', (over, line) => {
    const text = stateText(card(over as Partial<AgentCard>));
    expect(text).toContain(line);
    if (over.state !== 'queued') expect(text).not.toContain('run slot');
  });
});

// Card 571 (B126 shell follow-up): core 564 sends waitingFor slot only for a queued member, else waits-for-run, held or
// pending; and agent cards may carry waitState + waitsFor beside a queued / idle state.
describe('the row waitingFor alone (card 571)', () => {
  const row = (waitingFor: WorkflowRow['waitingFor'], members: WorkflowRow['members'] = []) =>
    ({ status: 'waiting', state: 'open', waitingFor, expiresAt: null, working: false, members }) as const;
  it('slot keeps its words', () => expect(statusTip(row('slot'))).toBe('Waiting for a run slot'));
  it('waits-for-run without a sender: another member\'s run', () => {
    expect(statusTip(row('waits-for-run'))).toBe("Waiting for another member's run to end");
  });
  it('waits-for-run takes the sender from a waiting worker', () => {
    // A running worker keeps the workers' own words out, so the row's waitingFor speaks, with the waiting worker's sender.
    expect(statusTip(row('waits-for-run', [{ id: 1, name: 'Eve', state: 'running' }, { id: 2, name: 'Manager', state: 'waits-for-run', waitsFor: 'TesterBen' }])))
      .toBe("Waiting for Tester Ben's run to end");
  });
  it('held: team paused', () => expect(statusTip(row('held'))).toBe('Waiting: team paused'));
  it('pending: has work waiting', () => expect(statusTip(row('pending'))).toBe('Has work waiting'));
});

describe('an agent card with waitState beside state (card 571)', () => {
  it.each([
    [{ state: 'idle', waitState: 'waits-for-run' }, 'waits-for-run'],
    [{ state: 'queued', waitState: 'held' }, 'held'],
    [{ state: 'idle', waitState: 'pending' }, 'pending'],
    [{ state: 'queued', waitState: 'queued' }, 'queued'],
    [{ state: 'idle', waitState: 'something-new' }, 'pending'],
    [{ state: 'idle', waitState: null }, 'idle'],
    [{ state: 'running', waitState: 'pending' }, 'running'],
    [{ state: 'held' }, 'held'],
  ] as const)('%o reads as %s', (c, want) => {
    expect(cardState(c as Pick<AgentCard, 'state' | 'waitState'>)).toBe(want);
  });
  it('rings waiting and says the wait on the member pane', () => {
    expect(memberStateOf({ state: 'idle', waitState: 'pending' })).toBe('waiting');
    const member = { id: 7, teamId: 1, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
    const card = {
      member, state: 'idle', waitState: 'waits-for-run', waitsFor: 'TesterBen', queuePosition: null, queueLength: 0, queuedInstructions: 1, statusLine: null,
      runId: null, runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
    } as unknown as AgentCard;
    const w = mount(MemberPane, { props: { card } as any, global: { stubs: { TerminalScreen: true } } });
    expect(w.get('[data-test=agent-state]').text()).toContain("waits for Tester Ben's run to end");
    expect(w.get('[data-test=agent-state]').text()).not.toContain('run slot');
  });
});