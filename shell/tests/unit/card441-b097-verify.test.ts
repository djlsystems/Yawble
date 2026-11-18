// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import BoardCardItem from '../../src/renderer/src/board/BoardCardItem.vue';
import { cardFace, cardStatusLook, trailKind } from '../../src/renderer/src/board/cards';
import { memberStateOf } from '../../src/renderer/src/faces/faces';
import { feedKindOf, MonitorModel, type FeedRow } from '../../src/renderer/src/monitor/model';
import type { BoardCard } from '../../src/shared/api';

/**
 * Card 441 (B097 verify, TesterKofi): the window fed exactly what the core (card 437, 742c73e) sends, not the shapes the
 * window's own tests assumed. The core's side is pinned in core Ui/Card441B097VerifyTests.cs:
 * - board.list for a stopped run: the card KEEPS its status ("running" after a progress line, "queued" before one) and
 *   lane; memberState is { state: 'idle', attention: 'stopped' } (no lastRunFailureClass); the trail line kind is "stopped".
 * - the feed row: kind "stopped", failureClass "stopped"; a timeout: kind "failed", failureClass "timeout".
 */

const mounted: { unmount(): void }[] = [];
afterEach(() => { while (mounted.length) mounted.pop()!.unmount(); });

// The board node as the core sends it after a stop (BoardService.ListAsync + BoardProjector, B097).
const coreStoppedCard = (status: BoardCard['status'], lane: string): BoardCard => ({
  id: 41, team: 'Alpha', teamId: 1, member: 'stop', item: 'B097', title: 'job stop', body: '', status, lane,
  placed: false, awaitingManager: false, workflow: 5, createdAt: '2026-11-18T12:00:00Z', updatedAt: '2026-11-18T12:02:00Z', trailCount: 3,
  memberState: { state: 'idle', attention: 'stopped' },
});

describe('the board card for a run the person stopped, as the core sends it', () => {
  it('the face wears the neutral Stopped ring (passes)', () => {
    expect(cardFace(coreStoppedCard('running', 'in-progress'))).toBe('stopped');
  });

  it('the core\'s trail line kind "stopped" shows as stopped (passes)', () => {
    expect(trailKind({ kind: 'stopped', text: 'stopped by the person' })).toBe('stopped');
  });

  // FAILS on 742c73e + 96ddd99: cards.ts cardStopped() only looks for status "failed", but the core keeps the card's
  // status for a stopped run, so the status line reads "Running" (or "Queued") for a run that has ended.
  it.each([['running', 'in-progress'], ['queued', 'todo']] as const)('status %s (lane %s) from the core: the run line reads Stopped', (status, lane) => {
    const card = coreStoppedCard(status, lane);
    expect(cardStatusLook(card).label).toBe('Stopped');
    const w = mount(BoardCardItem, { props: { card } });
    mounted.push(w);
    expect(w.get('[data-test=card-status]').text()).toBe('Stopped');
  });
});

describe('the Monitor fed the core\'s rows', () => {
  const row = (seq: number, extra: Partial<FeedRow>): FeedRow =>
    ({ seq, kind: 'failed', team: 'Alpha', member: 'stop', from: 'stop', text: null, workflow: 5, at: new Date().toISOString(), ...extra });

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
