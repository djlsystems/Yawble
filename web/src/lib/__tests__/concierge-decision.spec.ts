// The rules behind "Discuss with Concierge" that are not a component: which decision a card names,
// the one request typed about it, whether the person left text at the prompt, and when a session
// is ready for typing.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  askForCard,
  askForMember,
  decisionRequest,
  PromptLine,
  ReadyQuietMs,
  ReadyWatch,
} from '../conciergeDecision';
import { asMemberId, asTeamId, type ContainerSnapshot } from '../../api/types';
import type { KanbanCard } from '../../api/kanban';

const member = (over: Partial<ContainerSnapshot> = {}): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  id: asMemberId('Manager'),
  name: 'Manager',
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  ...over,
});

const card = (over: Partial<KanbanCard> = {}): KanbanCard => ({
  id: '4100_Manager',
  workflowSeq: 4100,
  team: 'alpha',
  member: 'Manager',
  title: 'Choose',
  body: '',
  status: 'needs-decision',
  laneId: 'blocked',
  progress: [],
  createdAt: '',
  updatedAt: '',
  awaitingManager: false,
  paused: false,
  needsDecisionSeq: 4187,
  needsDecisionCorrelation: 4100,
  ...over,
});

describe('which decision', () => {
  it('a member names the row of its open needs-decision mark', () => {
    expect(askForMember(member({ needsDecision: 'Q?', needsDecisionSeq: 4187, needsDecisionCorrelation: 4100 }), 'Alpha'))
      .toEqual({ team: 'alpha', teamName: 'Alpha', member: 'Manager', workflow: 4100, seq: 4187 });
  });

  it('a member that failed, or whose snapshot names no row, has none', () => {
    expect(askForMember(member({ needsDecision: 'Q?', failed: 'x', needsDecisionSeq: 1, needsDecisionCorrelation: 1 }), 'A')).toBeNull();
    expect(askForMember(member({ needsDecision: 'Q?' }), 'A')).toBeNull();
  });

  it('a card must be in Needs You, in needs-decision, with a member and its row', () => {
    expect(askForCard(card(), undefined)?.teamName).toBe('alpha');
    expect(askForCard(card({ laneId: 'in-progress' }), 'A')).toBeNull();
    expect(askForCard(card({ status: 'blocked' }), 'A')).toBeNull();
    expect(askForCard(card({ member: null }), 'A')).toBeNull();
    expect(askForCard(card({ needsDecisionSeq: undefined }), 'A')).toBeNull();
  });
});

describe('the request', () => {
  const ask = { team: 'alpha', teamName: 'Alpha Team', member: 'Manager', workflow: 4100, seq: 4187 };

  it('names the team by name and id, the member, the workflow and the row, and asks for a recommendation', () => {
    const text = decisionRequest(ask);

    expect(text).toContain('"Alpha Team" (id alpha)');
    expect(text).toContain('Member Manager');
    expect(text).toContain('seq 4187 in workflow 4100');
    expect(text).toMatch(/recommendation with the reason/);
    expect(text).toMatch(/do not act on the decision until I answer/);
  });

  it('is one line, and a name cannot end the paste or submit it', () => {
    const text = decisionRequest({ ...ask, teamName: 'Evil\x1b[201~\rrm -rf\nteam' });

    // eslint-disable-next-line no-control-regex
    expect(text).not.toMatch(/[\u0000-\u001f]/);
    expect(text).toContain('Evil [201~ rm -rf team');
  });
});

describe('unsent text at the prompt', () => {
  it('typing makes it unsent; Enter sends it', () => {
    const line = new PromptLine();
    expect(line.unsent).toBe(false);

    line.input('h');
    line.input('i');
    expect(line.unsent).toBe(true);

    line.input('\r');
    expect(line.unsent).toBe(false);
  });

  it('Backspace back to nothing clears it', () => {
    const line = new PromptLine();
    line.input('ab');
    line.input('\x7f');
    line.input('\x7f');
    line.input('\x7f');
    expect(line.unsent).toBe(false);
  });

  it('a paste or a Shift+Enter newline is unsent; a composed line is sent', () => {
    const line = new PromptLine();
    line.input('\x1b[200~a\rb\x1b[201~');
    expect(line.unsent).toBe(true);
    line.input('\r');

    line.input('\x1b\r');
    expect(line.unsent).toBe(true);
    line.input('\r');

    line.input('a whole line\r');
    expect(line.unsent).toBe(false);
  });

  it('history and Tab make the line unknown until the next submit', () => {
    const line = new PromptLine();
    line.input('\x1b[A');
    expect(line.unsent).toBe(true);
    line.input('\x7f');
    expect(line.unsent).toBe(true);
    line.input('\r');

    line.input('\t');
    expect(line.unsent).toBe(true);
  });

  it('keys that insert nothing and xterm\'s own replies change nothing', () => {
    const line = new PromptLine();
    for (const data of ['\x1b', '\x1b[C', '\x1b[D', '\x03', '\x1b[I', '\x1b[O', '\x1b[<0;10;5M', '\x1b[12;1R', '\x1b[?1;2c', '\x1b[?2026;2$y']) {
      line.input(data);
    }
    expect(line.unsent).toBe(false);
  });
});

describe('ready', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('is first output, then quiet for the interval; output pushes it back', () => {
    const ready = vi.fn();
    const watch = new ReadyWatch(ready, vi.fn());

    vi.advanceTimersByTime(ReadyQuietMs * 3);
    expect(ready).not.toHaveBeenCalled();

    watch.output();
    vi.advanceTimersByTime(ReadyQuietMs - 1);
    watch.output();
    vi.advanceTimersByTime(ReadyQuietMs - 1);
    expect(ready).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(ready).toHaveBeenCalledTimes(1);
    expect(watch.ready).toBe(true);
  });

  it('gives up at the ceiling when output never goes quiet', () => {
    const ready = vi.fn();
    const gaveUp = vi.fn();
    const watch = new ReadyWatch(ready, gaveUp, 1000, 5000);

    for (let elapsed = 0; elapsed < 6000; elapsed += 500) {
      watch.output();
      vi.advanceTimersByTime(500);
    }

    expect(ready).not.toHaveBeenCalled();
    expect(gaveUp).toHaveBeenCalledTimes(1);
  });
});
