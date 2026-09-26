import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { Message } from '../../api/types';

// The store's own module graph reaches the hub and the client; neither is exercised here.
vi.mock('../../lib/hub', () => ({ joinTeam: vi.fn(), leaveTeam: vi.fn() }));

import { useConsoleStore } from '../console';
import { useDisplayStore } from '../display';

/**
 * A card's "load older" reads history into the store, and the viewer's feed depth bounds the
 * LIVE TAIL only. What somebody scrolled down to read is not trimmed by the next progress line.
 */
const message = (seq: number, source = 'TestTeam/Manager'): Message => ({
  seq,
  type: 'agentContainer.completed',
  payload: JSON.stringify({ container: source, output: `answer ${seq}` }),
  source,
  correlationId: seq,
  causationSeq: null,
  depth: 0,
  occurredAt: '2026-08-14T13:57:52Z',
});

const seqs = (messages: Message[]) => messages.map((m) => m.seq);

beforeEach(() => {
  setActivePinia(createPinia());
  useDisplayStore().feedDepth = 3;
});

describe('a card feed with history', () => {
  it('appends older rows of its own owner below the live tail', () => {
    const board = useConsoleStore();
    board.record([message(50), message(51)]);

    board.recordHistory('TestTeam', 'Manager', [
      message(49, 'Other/Manager'),
      message(40),
      message(45),
      message(51),
    ]);

    // Its own rows only, newest first, and nothing the live tail already holds.
    expect(seqs(board.activityFor('TestTeam', 'Manager'))).toEqual([51, 50, 45, 40]);
  });

  it('is not trimmed by the live window', () => {
    const board = useConsoleStore();
    board.record([message(10)]);
    board.recordHistory('TestTeam', 'Manager', [9, 8, 7, 6, 5].map((s) => message(s)));

    board.record([message(11)]);

    expect(seqs(board.activityFor('TestTeam', 'Manager'))).toEqual([11, 10, 9, 8, 7, 6, 5]);
  });

  it('keeps what the live window drops once somebody has read back', () => {
    const board = useConsoleStore();
    board.record([message(1), message(2), message(3)]);
    board.recordHistory('TestTeam', 'Manager', []);

    board.record([message(4), message(5)]);

    // The live tail is bounded at three; the two it dropped moved into history, with no gap.
    expect(seqs(board.activity['TestTeam/Manager'] ?? [])).toEqual([5, 4, 3]);
    expect(seqs(board.activityFor('TestTeam', 'Manager'))).toEqual([5, 4, 3, 2, 1]);
  });

  it('still bounds a card nobody has read back through', () => {
    const board = useConsoleStore();
    board.record([1, 2, 3, 4, 5].map((s) => message(s)));

    expect(seqs(board.activityFor('TestTeam', 'Manager'))).toEqual([5, 4, 3]);
  });
});
