// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import BoardCardItem from '../../src/renderer/src/board/BoardCardItem.vue';
import CardPanel from '../../src/renderer/src/board/CardPanel.vue';
import { cardFace, cardStatusLook, trailKind } from '../../src/renderer/src/board/cards';
import { memberStateOf } from '../../src/renderer/src/faces/faces';
import { feedKindOf, MonitorModel, type FeedRow } from '../../src/renderer/src/monitor/model';
import type { BoardCard, YawbleApi } from '../../src/shared/api';

/**
 * The window fed exactly what the core sends for a stopped run (the core's side is in
 * core/tests/Yawble.Core.Tests/Ui/StoppedRunTests.cs):
 * - board.list for a stopped run: the card's status is "stopped" in the lane it had; memberState is { state: 'idle', attention: 'stopped' } (no lastRunFailureClass); the trail line kind is "stopped".
 * - the feed row: kind "stopped", failureClass "stopped"; a timeout: kind "failed", failureClass "timeout".
 */

const mounted: { unmount(): void }[] = [];
afterEach(() => { while (mounted.length) mounted.pop()!.unmount(); });

// The board node as the core sends it after a stop (BoardService.ListAsync + BoardProjector).
const coreStoppedCard = (status: BoardCard['status'], lane: BoardCard['lane']): BoardCard => ({
  id: 41, team: 'Alpha', teamId: 1, member: 'stop', item: 'B097', title: 'job stop', body: '', status, lane,
  placed: false, awaitingManager: false, deliverable: 5, createdAt: '2026-11-18T12:00:00Z', updatedAt: '2026-11-18T12:02:00Z', trailCount: 3,
  memberState: { state: 'idle', attention: 'stopped' },
});

describe('the board card for a run the person stopped, as the core sends it', () => {
  it('the face wears the neutral Stopped ring (passes)', () => {
    expect(cardFace(coreStoppedCard('running', 'in-progress'))).toBe('stopped');
  });

  it('the core\'s trail line kind "stopped" shows as stopped (passes)', () => {
    expect(trailKind({ kind: 'stopped', text: 'stopped by the person' })).toBe('stopped');
  });

  // This first fed status "running" / "queued", as the core then sent, and FAILED. Now
  // the core sends the card's own status "stopped", keeping its lane (In Progress after a progress line,
  // To Do before one; the core's stopped-run verify tests pin that). The fed status is 'stopped'.
  it.each([['stopped', 'in-progress'], ['stopped', 'todo']] as const)('status %s (lane %s) from the core: the run line reads Stopped', (status, lane) => {
    const card = coreStoppedCard(status, lane);
    expect(cardStatusLook(card)).toMatchObject({ label: 'Stopped', stopped: true });
    expect(cardStatusLook(card).color).toMatch(/^var\(--(state|status)-stopped\)$/); // the neutral colour, not red/blue
    const w = mount(BoardCardItem, { props: { card } });
    mounted.push(w);
    expect(w.get('[data-test=card-status]').text()).toBe('Stopped');
    expect(w.get('[data-test=card-status]').text()).not.toMatch(/Running|Queued/);
  });

  // The member's attention says "stopped" on every card of that member; only the stopped run's card is marked.
  it.each([['running', 'in-progress', 'Running'], ['queued', 'todo', 'Queued'], ['handback', 'done', 'Handed back'], ['completed', 'done', 'Run finished']] as const)(
    "the member's older card (status %s) is not marked Stopped", (status, lane, label) => {
      const older = { ...coreStoppedCard(status, lane), id: 40, deliverable: 4 };
      expect(cardStatusLook(older).label).toBe(label);
      const w = mount(BoardCardItem, { props: { card: older } });
      mounted.push(w);
      expect(w.get('[data-test=card-status]').text()).toBe(label);
    });

  it('the card panel fed the core\'s stopped card: the chip reads Stopped, the lane chip stays To Do', async () => {
    const detail = { ...coreStoppedCard('stopped', 'todo'), trail: [{ id: 1, at: '2026-11-18T12:02:00Z', source: 'stop', kind: 'stopped', text: 'stopped by the person' }] };
    const api = { call: async (m: string) => (m === 'board.card' ? detail : null), onEvent: () => () => {} } as unknown as YawbleApi;
    const w = mount(CardPanel, { props: { id: 41, lanes: [{ id: 'todo', label: 'To Do' }, { id: 'in-progress', label: 'In Progress' }], api } });
    mounted.push(w);
    await flushPromises();
    const chips = w.findAll('.chips .chip').map((c) => c.text());
    expect(chips.slice(0, 2)).toEqual(['Stopped', 'To Do']);
    expect(w.get('[data-test=card-status-chip]').attributes('data-stopped')).toBe('true');
    expect((w.get('[data-test=card-status-select]').element as HTMLSelectElement).value).toBe('stopped');
    expect(w.get('header').text() + w.get('.chips').text()).not.toMatch(/Running|Queued|Failed/);
  });

  it('after a new instruction the core re-queues / runs the same card: it reads Queued / Running again', () => {
    expect(cardStatusLook({ ...coreStoppedCard('queued', 'todo'), memberState: { state: 'queued', attention: 'stopped' } }).label).toBe('Queued');
    expect(cardStatusLook({ ...coreStoppedCard('running', 'in-progress'), memberState: { state: 'running', attention: 'stopped' } }).label).toBe('Running');
    expect(cardFace({ ...coreStoppedCard('running', 'in-progress'), memberState: { state: 'running', attention: 'stopped' } })).toBe('running');
  });
});

describe('the Monitor fed the core\'s rows', () => {
  const row = (seq: number, extra: Partial<FeedRow>): FeedRow =>
    ({ seq, kind: 'failed', team: 'Alpha', member: 'stop', from: 'stop', text: null, deliverable: 5, at: new Date().toISOString(), ...extra });

  it('kind "stopped" + failureClass "stopped" is stopped; kind "failed" + "timeout" stays failed', () => {
    expect(feedKindOf(row(1, { kind: 'stopped', failureClass: 'stopped' }))).toBe('stopped');
    expect(feedKindOf(row(2, { kind: 'failed', failureClass: 'timeout' }))).toBe('failed');
  });

  it('the teams.agents node (attention "stopped", lastRunFailureClass "stopped") makes no Needs you; a timeout does', () => {
    const stop = memberStateOf({ state: 'idle', attention: 'stopped', lastRunFailureClass: 'stopped' });
    const slow = memberStateOf({ state: 'idle', attention: 'failed', lastRunFailureClass: 'timeout' });
    expect([stop, slow]).toEqual(['stopped', 'failed']);
    const m = new MonitorModel();
    m.reconcile([{ id: 1, name: 'Alpha', members: [
      { id: 1, name: 'stop', role: 'member', state: stop, queued: 0, statusLine: null, cli: 'claude' },
      { id: 2, name: 'slow', role: 'member', state: slow, queued: 0, statusLine: null, cli: 'claude' },
    ] }], 0);
    m.apply(row(3, { kind: 'stopped', failureClass: 'stopped' }), 1);
    expect(m.needYouItems().map((i) => i.member)).toEqual(['slow']);
    expect(m.effects.some((e) => e.kind === 'comet' && e.mark === 'cross')).toBe(false);
  });
});
