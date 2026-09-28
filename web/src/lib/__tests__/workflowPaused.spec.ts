import { describe, expect, it } from 'vitest';
import { RowLoudness, workflowRowActions, workflowsTile, workflowTiming } from '../teamKpis';
import type { WorkflowState } from '../teamKpis';
import type { ContainerSnapshot, TeamWorkflows, TeamWorkflowTiming } from '../../api/types';
import { asMemberId, asTeamId } from '../../api/types';

/**
 * PAUSED.
 *
 * Without its own word, a workflow the pump has stopped waking would read `RUNNING` and then
 * `UNDECLARED` - the word for *nobody is working this*, which is the exact opposite of the truth:
 * the platform is actively refusing to let anybody work it.
 *
 * TWO RULES ARE PINNED HERE AND THEY ARE EASY TO GET BACKWARDS.
 *
 * 1. **The word is READ, never re-derived.** `TeamWorkflowTiming.state` is resolved on the server,
 *    in one place, from `pausedAt`. This side maps `'Paused'` to `PAUSED` exactly as it maps
 *    `'Failed'` to `FAILED`, and never asks `pausedAt !== null` to decide the word - the same
 *    pairing `blockedBy`/`Blocked` already has.
 * 2. **A live member still outranks it.** A workflow can be paused while one member is finishing
 *    the run that took it over budget, and at that instant somebody IS working it.
 */

const now = Date.parse('2026-09-20T12:00:00Z');

const container = (
  overrides: Partial<Omit<ContainerSnapshot, 'id'>> & { id?: string } = {},
): ContainerSnapshot => ({
  team: asTeamId('TestTeam'),
  name: 'Manager',
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  ...overrides,
  id: asMemberId(overrides.id ?? 'Manager'),
});

function timing(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return {
    available: true,
    correlation: 1707,
    state: 'Running',
    startedAt: '2026-09-20T11:30:00Z',
    endedAt: null,
    serverNow: '2026-09-20T12:00:00Z',
    executionSeconds: 0,
    partial: false,
    runsCounted: 0,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [],
    lastActivityAt: '2026-09-20T11:59:00Z',
    awaitingFrom: null,
    subject: 'Execute B001F',
    pausedAt: null,
    pausedReason: null,
    pausedLimit: null,
    ...over,
  } as TeamWorkflowTiming;
}

/** A workflow the pump stopped: the server's `Paused` state, with the three fields beside it. */
function paused(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return timing({
    state: 'Paused',
    pausedAt: '2026-09-20T11:58:00Z',
    pausedReason:
      'this workflow has spent 104,221,900 tokens, which is over its budget of 100,000,000.',
    pausedLimit: 100_000_000,
    ...over,
  });
}

function payload(entries: TeamWorkflowTiming[]): TeamWorkflows {
  return {
    available: true,
    openCount: entries.length,
    totalCount: entries.length,
    earliestStartedAt: '2026-09-20T11:30:00Z',
    serverNow: '2026-09-20T12:00:00Z',
    workflows: entries,
    missing: null,
  } as TeamWorkflows;
}

describe('workflowTiming says PAUSED', () => {
  it('maps the server’s own Paused state to the word a reader sees', () => {
    const tile = workflowTiming(paused(), [], now);

    expect(tile.state).toBe('PAUSED');
  });

  /**
   * WARN, NOT ERROR. Nothing failed - the platform stopped the work on purpose and there is a
   * known way back - but somebody has to go and press it, which is what `warn` says. The same
   * tone `BLOCKED` and `NO RESULT` carry for the same reason.
   */
  it('is toned warn', () => {
    expect(workflowTiming(paused(), [], now).tone).toBe('warn');
  });

  /**
   * THE FIGURE THAT WAS IN FORCE, off the payload. The browser does not re-resolve which of the
   * team's number and the instance's applied - the server answered that when it wrote the row, and
   * `pausedLimit` is that answer.
   */
  it('names the budget it was stopped by', () => {
    const tile = workflowTiming(paused(), [], now);

    expect(tile.detail).toBe('paused - over its 100,000,000 budget');
  });

  /** No limit on the row - an older Host, or a pause for some other reason - falls back to the
   *  sentence the server wrote rather than to a number nobody sent. */
  it('falls back to the reason when no limit came with the row', () => {
    const tile = workflowTiming(
      paused({ pausedLimit: null, pausedReason: 'stopped by hand' }),
      [],
      now,
    );

    expect(tile.detail).toBe('stopped by hand');
  });

  /**
   * RUNNING STILL WINS. A member finishing the very run that took the workflow over its budget is
   * a member working right now; telling a reader the workflow is idle while a process is burning
   * tokens is the reassuring-but-wrong reading.
   */
  it('is outranked by a member still running under this workflow', () => {
    const tile = workflowTiming(
      paused(),
      [container({ id: 'Writer', state: 'Running', currentCorrelation: 1707 })],
      now,
    );

    expect(tile.state).toBe('RUNNING');
  });

  /**
   * AND IT OUTRANKS FAILED. A paused workflow whose last member run also failed will not run again
   * whatever that failure says - FAILED would send a reader to read a run when the reason nothing
   * moves is the budget.
   */
  it('outranks a failed run on the same workflow', () => {
    const tile = workflowTiming(
      paused({ runsFailed: 2, failedMembers: ['Writer'] }),
      [],
      now,
    );

    expect(tile.state).toBe('PAUSED');
  });
});

describe('RowLoudness ranks PAUSED', () => {
  /**
   * THE EXACT ORDER, AS A TABLE. `RowLoudness` is a `Record` keyed on the union so a new state is
   * a compile error rather than a silent `-1`; this pins WHERE the new one sits, which the type
   * cannot.
   */
  it('is the fixed ordering', () => {
    expect(RowLoudness).toEqual({
      FAILED: 0,
      BLOCKED: 1,
      'NO RESULT': 2,
      PAUSED: 3,
      AWAITING: 4,
      RUNNING: 5,
      UNDECLARED: 6,
      COMPLETED: 7,
      CLOSED: 8,
    } satisfies Record<WorkflowState, number>);
  });

  /**
   * ABOVE AWAITING, AND THE DISTINCTION IS THE POINT. AWAITING is an agent asking a person a
   * question; PAUSED is the platform having stopped the work. The second is louder.
   */
  it('puts a paused workflow above one merely waiting on a person', () => {
    expect(RowLoudness.PAUSED).toBeLessThan(RowLoudness.AWAITING);
    expect(RowLoudness['NO RESULT']).toBeLessThan(RowLoudness.PAUSED);
  });
});

describe('the workflow row carries the pause', () => {
  it('is paused exactly when the payload carries a pausedAt', () => {
    const tile = workflowsTile(payload([paused()]), [], now);

    expect(tile.rows[0]!.paused).toBe(true);
    expect(tile.rows[0]!.state).toBe('PAUSED');
  });

  it('is not paused for an ordinary open workflow', () => {
    const tile = workflowsTile(payload([timing()]), [], now);

    expect(tile.rows[0]!.paused).toBe(false);
  });

  /**
   * THE LOUDEST ROW STILL DECIDES THE TILE. One paused workflow among several open ones is what
   * the tile's word and the tab chip report, because nothing else on that team is stopped.
   */
  it('is the loudest of a paused row and a running one', () => {
    const tile = workflowsTile(
      payload([timing({ correlation: 1707 }), paused({ correlation: 1801 })]),
      [container({ id: 'Writer', state: 'Running', currentCorrelation: 1707 })],
      now,
    );

    expect(tile.tone).toBe('warn');
  });
});

describe('workflowRowActions offers Resume', () => {
  /**
   * THE WHOLE POINT OF THE CARD. A reader who has just been told a workflow is PAUSED must be able
   * to resume it from there, without knowing that `nudge` is the verb.
   */
  it('offers Resume on a paused row', () => {
    expect(workflowRowActions({ state: 'PAUSED', paused: true }).resume).toBe(true);
  });

  /** And on no other row: a Resume button on a workflow nobody paused is a control that can only
   *  confuse, and `pausedAt` is the single fact that decides it. */
  it('withholds Resume from a row that is not paused', () => {
    expect(workflowRowActions({ state: 'RUNNING', paused: false }).resume).toBe(false);
    expect(workflowRowActions({ state: 'UNDECLARED', paused: false }).resume).toBe(false);
    expect(workflowRowActions({ state: 'COMPLETED', paused: false }).resume).toBe(false);
  });

  /** An older payload carries no `paused` at all. Absent is NOT paused - never a Resume button
   *  over a workflow this browser cannot know the pause state of. */
  it('withholds Resume when the payload never said', () => {
    expect(workflowRowActions({ state: 'UNDECLARED' }).resume).toBe(false);
  });

  /**
   * RESUME DOES NOT REPLACE NUDGE. It sits BESIDE Nudge / Close / Stop. A paused row is neither
   * terminal nor live, so the three controls it carries are untouched.
   */
  it('leaves Close, Nudge and Show thread exactly as they were', () => {
    const actions = workflowRowActions({ state: 'PAUSED', paused: true });

    expect(actions.close).toBe(true);
    expect(actions.nudge).toBe(true);
    expect(actions.thread).toBe(true);
    expect(actions.stop).toBe(false);
  });
});
