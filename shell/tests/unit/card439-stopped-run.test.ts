// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import { defineComponent, h } from 'vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import TeamMembers from '../../src/renderer/src/components/TeamMembers.vue';
import BoardCardItem from '../../src/renderer/src/board/BoardCardItem.vue';
import { cardFace, cardStatusLook, cardStopped, trailKind } from '../../src/renderer/src/board/cards';
import { memberStateOf, MemberStateLabel } from '../../src/renderer/src/faces/faces';
import { feedKindOf, MonitorModel, type Effect, type FeedRow, type TeamSnapshot } from '../../src/renderer/src/monitor/model';
import { MonitorSound } from '../../src/renderer/src/monitor/sound';
import { ReplayStates, toneOfRow } from '../../src/renderer/src/monitor/timeline';
import { Palette } from '../../src/renderer/src/monitor/scene';
import { isStoppedRun, type AgentCard, type BoardCard, type TerminalBanner, type YawbleApi } from '../../src/shared/api';

/**
 * Card 439 (B097, window): a run the person stopped shows as "Stopped" in a neutral colour everywhere a member's or a
 * run's state shows, and never as needing the person. The core (card 437) sends member attention "stopped", the member
 * field lastRunFailureClass, the Monitor feed kind "stopped" and failureClass on run rows; a run recorded "failed" with
 * class "stopped" is stopped too. "interrupted" keeps its own wording; timeout, idle, credential, start_failed and
 * failed stay failures.
 */

const member = { id: 7, teamId: 1, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (over: Partial<AgentCard> = {}): AgentCard => ({
  member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [], ...over,
});
const stoppedCard = (over: Partial<AgentCard> = {}) =>
  card({ lastRunState: 'failed', lastRunReason: 'stopped by the person', lastRunFailureClass: 'stopped', attention: 'stopped', ...over });

function fakeApi(remote: boolean, agents: AgentCard[] = []): YawbleApi {
  return {
    ...(remote ? { remote: true } : {}),
    getInfo: async () => { throw new Error('no'); }, getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      if (method === 'terminal.banner') return { memberId: params.memberId, title: 'm', state: 'idle', runId: null, queuePosition: null, queueLength: 0, lastRunState: null, lastRunReason: null, details: [], loosened: [] } as unknown as TerminalBanner;
      if (method === 'teams.agents') return agents;
      return {};
    },
    onEvent: () => () => {},
    openTerminal: async () => {}, pickFolder: async () => null,
  } as unknown as YawbleApi;
}
const FakeScreen = defineComponent({ props: { channel: String }, setup: () => () => h('div') });
const mounted: { unmount(): void }[] = [];
afterEach(() => { while (mounted.length) { try { mounted.pop()!.unmount(); } catch { /* already */ } } });
const pane = (c: AgentCard, remote = false) => {
  const w = mount(MemberPane, { props: { card: c, api: fakeApi(remote) } as any, global: { stubs: { TerminalScreen: FakeScreen } } });
  mounted.push(w);
  return w;
};

describe('the member state: stopped is its own neutral state', () => {
  it('attention "stopped" is Stopped, not Last run failed', () => {
    expect(memberStateOf({ state: 'idle', attention: 'stopped' })).toBe('stopped');
    expect(MemberStateLabel.stopped).toBe('Stopped');
  });
  it('attention "failed" with class "stopped" (a core that still says failed) is Stopped too', () => {
    expect(memberStateOf({ state: 'idle', attention: 'failed', lastRunFailureClass: 'stopped' })).toBe('stopped');
  });
  it.each(['failed', 'timeout', 'idle', 'credential', 'start_failed', undefined])('class %s stays a failure', (cls) => {
    expect(memberStateOf({ state: 'idle', attention: 'failed', lastRunFailureClass: cls ?? null })).toBe('failed');
  });
  it('a stopped member that runs again is running; a question still wins', () => {
    expect(memberStateOf({ state: 'running', attention: 'stopped' })).toBe('running');
    expect(memberStateOf({ state: 'queued', attention: 'stopped' })).toBe('waiting');
    expect(memberStateOf({ state: 'idle', attention: 'decision', lastRunFailureClass: 'stopped' })).toBe('decision');
  });
  it('isStoppedRun reads attention, the class or a "stopped" state', () => {
    expect(isStoppedRun({ state: 'failed', failureClass: 'stopped' })).toBe(true);
    expect(isStoppedRun({ attention: 'stopped' })).toBe(true);
    expect(isStoppedRun({ state: 'failed', failureClass: 'timeout' })).toBe(false);
    expect(isStoppedRun({ state: 'interrupted', failureClass: 'interrupted' })).toBe(false);
    expect(isStoppedRun(null)).toBe(false);
  });
});

describe("MemberPane: the member's last run", () => {
  it('a stopped run reads "last run stopped by you", the label Stopped, the ring neutral (no throb)', () => {
    const w = pane(stoppedCard());
    expect(w.get('[data-test=agent-state]').text()).toContain('last run stopped by you');
    expect(w.get('[data-test=agent-state]').text()).not.toContain('failed');
    expect(w.get('[data-test=agent-face-state]').text()).toBe('Stopped');
    expect(w.get('[data-test=agent-DeveloperMarlo]').classes()).toContain('is-stopped');
    expect(w.get('[data-test=agent-DeveloperMarlo]').classes()).not.toContain('is-failed');
    expect(w.get('[data-test=member-avatar]').attributes('data-state')).toBe('stopped');
  });
  it('a failed run (class failed) still reads "last run failed: <reason>"', () => {
    const w = pane(card({ lastRunState: 'failed', lastRunReason: 'exit 1', lastRunFailureClass: 'failed', attention: 'failed' }));
    expect(w.get('[data-test=agent-state]').text()).toContain('last run failed: exit 1');
    expect(w.get('[data-test=agent-face-state]').text()).toBe('Last run failed');
    expect(w.get('[data-test=agent-DeveloperMarlo]').classes()).toContain('is-failed');
  });
  it('a run recorded failed with class "stopped" but no stopped attention still says stopped by you', () => {
    const w = pane(card({ lastRunState: 'failed', lastRunReason: 'stopped by the person', lastRunFailureClass: 'stopped', attention: 'failed' }));
    expect(w.get('[data-test=agent-state]').text()).toContain('last run stopped by you');
    expect(w.get('[data-test=agent-face-state]').text()).toBe('Stopped');
  });
  it('timeout stays a failure; interrupted keeps its own wording', () => {
    const t = pane(card({ lastRunState: 'failed', lastRunReason: 'wall timeout', lastRunFailureClass: 'timeout', attention: 'failed' }));
    expect(t.get('[data-test=agent-state]').text()).toContain('last run failed: wall timeout');
    const i = pane(card({ lastRunState: 'interrupted', lastRunReason: 'the app closed', lastRunFailureClass: 'interrupted' }));
    expect(i.get('[data-test=agent-state]').text()).toContain('last run interrupted: the app closed');
  });
});

// The Monitor's model: what raises a need-you item, what effects and sounds a stopped run makes.
type State = TeamSnapshot['members'][number]['state'];
const snap = (name: string, state: State) => ({ id: 1, name, role: 'member' as const, state, queued: 0, statusLine: null, cli: 'claude' });
const row = (seq: number, kind: FeedRow['kind'], m: string, extra: Partial<FeedRow> = {}): FeedRow =>
  ({ seq, kind, team: 'payments', member: m, from: m, text: null, workflow: 1, at: new Date().toISOString(), ...extra });

describe('Monitor: a stopped run never needs you', () => {
  it('a stopped member raises no Needs you item; a failed one does', () => {
    const m = new MonitorModel();
    m.reconcile([{ id: 1, name: 'payments', members: [snap('Ana', 'stopped'), snap('Bo', 'failed')] }], 0);
    const items = m.needYouItems();
    expect(items.map((i) => [i.kind, i.member])).toEqual([['failed', 'Bo']]);
    expect(m.counts().needYou).toBe(1);
    const fired = m.syncNeedYou(1);
    expect(fired.map((i) => i.member)).toEqual(['Bo']);
    expect(m.effects.filter((e) => e.kind === 'you').map((e) => (e as Extract<Effect, { kind: 'you' }>).from)).toEqual(['m:payments/bo']);
  });

  it('feed kind "stopped": a neutral burst and "stopped" float; no cross home, no fail burst, no you', () => {
    const m = new MonitorModel();
    m.reconcile([{ id: 1, name: 'payments', members: [snap('Ana', 'running')] }], 0);
    m.apply(row(1, 'instruction', 'Ana', { from: 'Manager', text: 'card 12: do it' }), 1);
    m.effects.length = 0;
    m.apply(row(2, 'stopped', 'Ana'), 2);
    expect(m.effects.map((e) => [e.kind, 'color' in e ? e.color : null])).toEqual([['burst', 'stopped'], ['float', 'stopped']]);
    expect(m.effects.some((e) => e.kind === 'comet' || e.kind === 'you')).toBe(false);
    expect(m.activity[0]).toMatchObject({ verb: 'run stopped by you', tone: 'stopped' });
  });

  it('feed kind "failed" with failureClass "stopped" plays as stopped; a real failure still crosses', () => {
    const m = new MonitorModel();
    m.reconcile([{ id: 1, name: 'payments', members: [snap('Ana', 'running'), snap('Bo', 'running')] }], 0);
    m.apply(row(1, 'failed', 'Ana', { failureClass: 'stopped' }), 1);
    expect(m.activity[0]).toMatchObject({ verb: 'run stopped by you', tone: 'stopped' });
    expect(m.effects.some((e) => 'color' in e && e.color === 'fail')).toBe(false);
    m.effects.length = 0;
    m.apply(row(2, 'failed', 'Bo', { failureClass: 'timeout' }), 2);
    expect(m.activity[0]).toMatchObject({ verb: 'run failed', tone: 'fail' });
    expect(m.effects.some((e) => e.kind === 'burst' && e.color === 'fail')).toBe(true);
    expect(m.effects.some((e) => e.kind === 'comet' && e.mark === 'cross')).toBe(true);
  });

  it('no fail sound for a stopped run; a failed run still sounds', () => {
    const quiet = new MonitorModel();
    quiet.reconcile([{ id: 1, name: 'payments', members: [snap('Ana', 'running')] }], 0);
    quiet.apply(row(1, 'stopped', 'Ana'), 1);
    const s = new MonitorSound(() => {});
    s.on = true;
    expect(s.hear(quiet.effects, 10_000)).toEqual([]);
    const loud = new MonitorModel();
    loud.reconcile([{ id: 1, name: 'payments', members: [snap('Bo', 'running')] }], 0);
    loud.apply(row(1, 'failed', 'Bo'), 1);
    const s2 = new MonitorSound(() => {});
    s2.on = true;
    expect(s2.hear(loud.effects, 10_000)).toContain('failed');
  });

  it('the timeline: a stopped row is the neutral tone; a replay shows the member stopped, not failed', () => {
    expect(feedKindOf({ kind: 'failed', failureClass: 'stopped' })).toBe('stopped');
    expect(toneOfRow({ kind: 'stopped', from: 'Ana' })).toBe('stopped');
    expect(toneOfRow({ kind: 'failed', from: 'Ana', failureClass: 'stopped' })).toBe('stopped');
    expect(toneOfRow({ kind: 'failed', from: 'Ana', failureClass: 'idle' })).toBe('fail');
    const r = new ReplayStates();
    r.row(row(1, 'failed', 'Ana', { failureClass: 'stopped' }));
    r.row(row(2, 'failed', 'Bo'));
    expect(r.of('m:payments/ana', Date.now())).toBe('stopped');
    expect(r.of('m:payments/bo', Date.now())).toBe('failed');
    expect(Palette.stopped).not.toBe(Palette.fail);
  });
});

// The board: a card whose member's run was stopped (the core marks the card "failed").
const boardCard = (over: Partial<BoardCard> = {}): BoardCard => ({
  id: 12, team: 'payments', teamId: 1, member: 'DeveloperMarlo', item: 'B097', title: 'Stop it', body: '', status: 'failed', lane: 'blocked',
  placed: true, awaitingManager: false, workflow: 3, createdAt: '2026-11-18T19:00:00Z', updatedAt: '2026-11-18T19:00:00Z', trailCount: 2, ...over,
});

describe('Board: a stopped run line reads Stopped in a neutral colour', () => {
  it('cardStatusLook: Stopped in --state-stopped for a stopped run; Failed in red otherwise', () => {
    const stopped = boardCard({ memberState: { state: 'idle', attention: 'stopped' } });
    expect(cardStopped(stopped)).toBe(true);
    expect(cardStatusLook(stopped)).toEqual({ label: 'Stopped', color: 'var(--state-stopped)', stopped: true });
    expect(cardFace(stopped)).toBe('stopped');
    const byClass = boardCard({ memberState: { state: 'idle', attention: 'failed', lastRunFailureClass: 'stopped' } });
    expect(cardStatusLook(byClass).label).toBe('Stopped');
    const failed = boardCard({ memberState: { state: 'idle', attention: 'failed', lastRunFailureClass: 'timeout' } });
    expect(cardStatusLook(failed)).toEqual({ label: 'Failed', color: 'var(--status-failed)', stopped: false });
    expect(cardStatusLook(boardCard()).label).toBe('Failed');
  });
  it('the card on the board shows Stopped and its face the neutral ring', () => {
    const w = mount(BoardCardItem, { props: { card: boardCard({ memberState: { state: 'idle', attention: 'stopped' } }) } });
    mounted.push(w);
    expect(w.get('[data-test=card-status]').text()).toBe('Stopped');
    expect(w.get('[data-test=member-avatar]').attributes('data-state')).toBe('stopped');
    expect((w.get('[data-test=board-card-12]').element as HTMLElement).style.getPropertyValue('--status')).toBe('var(--state-stopped)');
  });
  it("a trail's failed line for a stopped run reads stopped; any other failed line stays failed", () => {
    expect(trailKind({ kind: 'failed', text: 'stopped by the person' })).toBe('stopped');
    expect(trailKind({ kind: 'failed', text: 'exit 1' })).toBe('failed');
  });
});

describe('the remote (Cloud Link) view shows the same', () => {
  it('MemberPane over the remote API: "last run stopped by you", Stopped', () => {
    const w = pane(stoppedCard(), true);
    expect(w.get('[data-test=agent-state]').text()).toContain('last run stopped by you');
    expect(w.get('[data-test=agent-face-state]').text()).toBe('Stopped');
  });
  it('the Teams list tile over the remote API: the stopped member wears the neutral ring and says Stopped', async () => {
    const w = mount(TeamMembers, { props: { teamId: 1, api: fakeApi(true, [stoppedCard(), card({ member: { ...member, id: 8, name: 'TesterKofi' }, attention: 'failed', lastRunState: 'failed', lastRunFailureClass: 'credential' })]), fallback: '' } });
    mounted.push(w);
    await flushPromises();
    const faces = w.findAll('[data-test=team-member]');
    expect(faces.map((f) => f.attributes('data-state'))).toEqual(['stopped', 'failed']);
    expect(faces[0]!.attributes('title')).toContain('Stopped');
    expect(faces[1]!.attributes('title')).toContain('Last run failed');
  });
});
