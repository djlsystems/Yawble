import { describe, expect, it } from 'vitest';
import { WaitingForSlot, workflowsExecution, workflowsTile, teamChip } from '../teamKpis';
import type { ContainerSnapshot, TeamWorkflows, TeamWorkflowTiming } from '../../api/types';
import { asMemberId, asTeamId } from '../../api/types';

const now = Date.parse('2026-09-08T12:00:00Z');

/** Built the way `teamKpis.spec.ts` builds a `ContainerSnapshot` — this file's own copy of that
 *  fixture, since the original is not exported and Task 8 does not touch that spec file. */
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

  // BRANDED HERE, AT THE FIXTURE'S EDGE, which is what lets every spec go on writing `id: 'Writer'`.
  // A literal in a test genuinely IS an identifier arriving from outside the app, so this is the
  // edge `asMemberId` is for. Wrapping all seventy-three call sites instead would be noise over the
  // one thing the brand exists to catch: a LABEL reaching a route in production code.
  id: asMemberId(overrides.id ?? 'Manager'),
});

function timing(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return {
    available: true,
    correlation: 1,
    state: 'Running',
    startedAt: '2026-09-08T11:30:00Z',
    endedAt: null,
    serverNow: '2026-09-08T12:00:00Z',
    executionSeconds: 0,
    partial: false,
    runsCounted: 0,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [],
    lastActivityAt: '2026-09-08T11:59:00Z',
    awaitingFrom: null,
    subject: null,
    ...over,
  } as TeamWorkflowTiming;
}

function payload(over: Partial<TeamWorkflows> = {}): TeamWorkflows {
  return {
    available: true,
    openCount: 1,

    // OPEN AND CLOSED ALIKE, uncapped - what the dialog's truncation line counts against.
    // `openCount` still means OPEN, and every case below that cares overrides one or the other.
    totalCount: 1,
    earliestStartedAt: '2026-09-08T11:30:00Z',
    serverNow: '2026-09-08T12:00:00Z',
    workflows: [timing()],
    missing: null,
    ...over,
  } as TeamWorkflows;
}

describe('workflowsTile', () => {
  /**
   * THE SPAN FOLLOWS THE SAME RULE AS THE SINGLE WORKFLOW, and it is the one a person actually sees.
   *
   * A workflow is OPEN until a manager declares it over, which a FAILED one never does - so
   * measuring an open row to `now` would count the night, and a row that failed at 21:33 would read
   * `11h 32m` the next morning. A span of `now - earliest` unconditionally has the same fault.
   *
   * `TeamKpiStrip` renders `tile.span` whenever `openCount > 0` and the frozen `workflow.elapsed`
   * only when nothing is open at all - so if the span kept counting, every team with an open
   * workflow would show a counting clock.
   */
  it('freezes the span at the last activity when every open workflow is idle', () => {
    const failedAt = '2026-09-08T11:45:00Z';

    const tile = workflowsTile(
      payload({
        openCount: 2,
        earliestStartedAt: '2026-09-08T11:30:00Z',
        workflows: [
          timing({ correlation: 1, state: 'Failed', lastActivityAt: failedAt }),
          timing({ correlation: 2, state: 'Failed', lastActivityAt: '2026-09-08T11:40:00Z' }),
        ],
      }),
      // Nobody running: the roster is empty, so no row is live.
      [],
      now,
    );

    // 11:30 to 11:45 - the LATEST activity across the open workflows, not `now` at 12:00.
    expect(tile.spanMs).toBe(15 * 60 * 1000);
    expect(tile.span).toBe('15m 00s');

    // And it must not animate. A figure that ticks says work is happening under it.
    expect(tile.ticking).toBe(false);
  });

  /**
   * The positive half, which is what stops the fix above being "the clock never moves". One member
   * running on one of the two workflows, and the span follows the clock for both - the team IS
   * working, and the span answers how long this burst of work has been going.
   */
  it('still follows the clock while any open workflow is live', () => {
    const tile = workflowsTile(
      payload({
        openCount: 2,
        earliestStartedAt: '2026-09-08T11:30:00Z',
        workflows: [
          timing({ correlation: 1, state: 'Failed', lastActivityAt: '2026-09-08T11:45:00Z' }),
          timing({ correlation: 2, state: 'Running', lastActivityAt: '2026-09-08T11:59:00Z' }),
        ],
      }),
      [container({ id: 'Writer', name: 'Writer', state: 'Running', currentCorrelation: 2 })],
      now,
    );

    expect(tile.spanMs).toBe(30 * 60 * 1000);
    expect(tile.ticking).toBe(true);
  });

  /**
   * THE SPAN, AND IT IS THE ASSERTION THIS FILE EXISTS FOR. Three workflows each thirty minutes
   * long, all covering the SAME thirty minutes, must read 30m and never 1h 30m. A sum is a number
   * larger than the time that has actually passed, rendered confidently - the identical trap the
   * per-member execution figure already carries a rule about.
   *
   * THE ROSTER CARRIES A RUNNING MEMBER, which it did not have to when the span was measured to
   * `now` unconditionally. The figure asserted here is the TICKING one, and the span now follows
   * the clock only while somebody is working - so the fixture states the condition its expectation
   * always assumed. Every assertion below is unchanged; the frozen case is its own spec above.
   */
  it('reports the span from the earliest open workflow, never the sum', () => {
    const tile = workflowsTile(
      payload({
        openCount: 3,
        earliestStartedAt: '2026-09-08T11:30:00Z',
        workflows: [
          timing({ correlation: 3, startedAt: '2026-09-08T11:30:00Z' }),
          timing({ correlation: 2, startedAt: '2026-09-08T11:35:00Z' }),
          timing({ correlation: 1, startedAt: '2026-09-08T11:40:00Z' }),
        ],
      }),
      [container({ id: 'Writer', name: 'Writer', state: 'Running', currentCorrelation: 3 })],
      now,
    );

    expect(tile.openCount).toBe(3);
    expect(tile.count).toBe('3 open');
    expect(tile.spanMs).toBe(30 * 60 * 1000);
    expect(tile.span).toBe('30m 00s');
  });

  it('renders an absence and not a zero for a team that has never run', () => {
    const tile = workflowsTile(
      payload({ available: false, openCount: 0, earliestStartedAt: null, workflows: [] }),
      [],
      now,
    );

    expect(tile.available).toBe(false);
    expect(tile.count).toBe('—');
    expect(tile.span).toBe('—');
    expect(tile.spanMs).toBeNull();
  });

  /**
   * EACH ROW FROM ITS OWN PAIR OF STAMPS. Both workflows are open with nobody live on either, so
   * each figure runs from that row's `startedAt` to that row's `lastActivityAt` - 8m and 45m, and
   * neither is the 10m/60m that measuring to `now` would give. The stamps are spelled out per row
   * rather than left to the fixture default so the two numbers are visibly each row's own, which
   * is the independence this test is named for.
   */
  it('gives one row per workflow, each with its own elapsed', () => {
    const tile = workflowsTile(
      payload({
        openCount: 2,
        workflows: [
          timing({
            correlation: 2,
            startedAt: '2026-09-08T11:50:00Z',
            lastActivityAt: '2026-09-08T11:58:00Z',
          }),
          timing({
            correlation: 1,
            startedAt: '2026-09-08T11:00:00Z',
            lastActivityAt: '2026-09-08T11:45:00Z',
          }),
        ],
      }),
      [],
      now,
    );

    expect(tile.rows).toHaveLength(2);
    expect(tile.rows[0]?.elapsedMs).toBe(8 * 60 * 1000);
    expect(tile.rows[1]?.elapsedMs).toBe(45 * 60 * 1000);
  });

  /**
   * A ROW'S CLOCK STOPS WHEN ITS WORK STOPPED, and `endedAt` is not what says so. A workflow is
   * CLOSED only by a declaration - `workflow.completed` or `workflow.closed` - and a workflow that
   * FAILED gets neither, so `endedAt` stays null and the row reads as open forever. Measured to
   * `now` it therefore climbs for as long as the tab is left open: a team whose last run failed at
   * 21:33 showed `11h 32m` the next morning, which reads as eleven hours of work rather than the
   * fourteen minutes it actually ran.
   *
   * `lastActivityAt` is the newest row in THIS workflow and is present while it is open, which is
   * exactly the instant the figure should freeze at. The tile already measures this way; this is
   * the same rule one workflow at a time.
   */
  it('freezes a failed row at its last activity rather than climbing against now', () => {
    const tile = workflowsTile(
      payload({
        workflows: [
          timing({
            correlation: 265,
            state: 'Failed',
            startedAt: '2026-09-08T11:30:00Z',
            endedAt: null,
            lastActivityAt: '2026-09-08T11:44:00Z',
          }),
        ],
      }),
      [],
      now,
    );

    expect(tile.rows[0]?.elapsedMs).toBe(14 * 60 * 1000);
    expect(tile.rows[0]?.ticking).toBe(false);
  });

  /**
   * THE OTHER HALF, AND IT IS WHAT STOPS THE FIX ABOVE FREEZING A LIVE ROW. A member actually
   * running under THIS correlation means the workflow is still going, so the figure must follow
   * the clock - otherwise a running workflow's elapsed sticks at its newest row and a working team
   * looks stalled.
   */
  it('keeps a row ticking while a member is running under that correlation', () => {
    const tile = workflowsTile(
      payload({
        workflows: [
          timing({
            correlation: 305,
            state: 'Running',
            startedAt: '2026-09-08T11:30:00Z',
            endedAt: null,
            lastActivityAt: '2026-09-08T11:44:00Z',
          }),
        ],
      }),
      [container({ state: 'Running', currentCorrelation: 305 })],
      now,
    );

    expect(tile.rows[0]?.elapsedMs).toBe(30 * 60 * 1000);
    expect(tile.rows[0]?.ticking).toBe(true);
  });

  /** THE SERVER'S SUBJECT IS READ, NEVER RE-DERIVED. */
  it("uses the server's subject when the root carries one", () => {
    const tile = workflowsTile(
      payload({ workflows: [timing({ correlation: 1, subject: 'Ship the widget' })] }),
      [],
      now,
    );

    expect(tile.rows[0]?.subject).toBe('Ship the widget');
  });

  /**
   * THE FALLBACK PATH, AND ONLY THEN. `subject` is null exactly when the root is not an addressed
   * instruction or carries no subject - the label built from `correlation` exists for that case
   * alone, never as a first choice over what the server already read off the root.
   */
  it('falls back to a correlation-based label only when the server has no subject', () => {
    const tile = workflowsTile(
      payload({ workflows: [timing({ correlation: 7, subject: null })] }),
      [],
      now,
    );

    expect(tile.rows[0]?.subject).toBe('Workflow #7');
  });

  it('stops ticking when nothing is open', () => {
    const tile = workflowsTile(
      payload({ openCount: 0, earliestStartedAt: null, workflows: [timing({ state: 'Completed', endedAt: '2026-09-08T11:59:00Z' })] }),
      [],
      now,
    );

    expect(tile.ticking).toBe(false);
  });

  /**
   * WHY PER-WORKFLOW STATUS EXISTS: "A team can legitimately be Running one
   * workflow and Blocked on another." A member Running under workflow B must not leak RUNNING onto
   * workflow A's row - `workflowTiming` checks RUNNING before Failed/Blocked/Awaiting/Undeclared, so
   * an unfiltered roster would force EVERY row to read RUNNING the moment anybody on the team is
   * working anything, masking whatever A's own log says underneath it.
   */
  it("does not let a member Running under one workflow mask another workflow's own state", () => {
    const tile = workflowsTile(
      payload({
        openCount: 2,
        workflows: [
          timing({ correlation: 2, state: 'Running' }),
          timing({ correlation: 1, state: 'Blocked', blockedBy: 'scout' }),
        ],
      }),
      [container({ id: 'Worker', name: 'Worker', state: 'Running', currentCorrelation: 2 })],
      now,
    );

    const rowB = tile.rows.find((row) => row.correlation === 2);
    const rowA = tile.rows.find((row) => row.correlation === 1);

    expect(rowB?.state).toBe('RUNNING');

    // THE ASSERTION THIS TEST EXISTS FOR: workflow A has nobody of its own Running, so it must
    // read what its OWN log says - BLOCKED - never RUNNING borrowed from a teammate on workflow B.
    expect(rowA?.state).toBe('BLOCKED');
  });
});

describe('teamChip', () => {
  /** LOUDEST WINS, WITH THE COUNT BESIDE IT. The seven-word ranking already exists so the worst
   * news surfaces; the count preserves "there are several" without inventing a second concept. */
  it('shows the loudest workflow state and how many are open', () => {
    const tile = workflowsTile(
      payload({
        openCount: 3,
        workflows: [
          timing({ correlation: 3, state: 'Running' }),
          timing({ correlation: 2, state: 'Blocked', blockedBy: 'scout' }),
          timing({ correlation: 1, state: 'Running' }),
        ],
      }),
      [],
      now,
    );

    const chip = teamChip(tile, []);

    expect(chip.label).toBe('BLOCKED');
    expect(chip.count).toBe(3);
  });

  /**
   * `misconfigured` IS A MEMBER FACT AND NOT A WORKFLOW FACT - it is a missing Agent, a missing
   * Prompt or an unreachable root, which belong to a MEMBER and which no workflow can carry. The
   * chip is therefore the union of member-level facts and per-workflow facts, and the COUNT counts
   * only the latter. Stated as a test so the next reader does not try to derive it from a workflow.
   */
  it('lets a member-level misconfiguration outrank every workflow state', () => {
    const tile = workflowsTile(payload({ openCount: 2 }), [], now);

    const chip = teamChip(tile, [
      container({ id: 'A', name: 'A', missingAgent: 'gone' }),
    ]);

    expect(chip.label).toBe('MISCONFIGURED');
    expect(chip.count).toBe(2);
  });

  /**
   * `RowLoudness`'s PLACEMENT OF `AWAITING` AND `NO RESULT` IS THIS FUNCTION'S OWN DECISION, not an
   * adaptation of `teamStatus`'s ranking (which predates both words) or of `workflowTiming`'s own
   * numbered list (a fact-resolution chain, not a loudness order — reusing its position would in
   * fact rank AWAITING above FAILED). Pinned here rather than left implicit, per this codebase's
   * rule that a "which of several correct answers" ordering must be asserted, not merely read.
   */
  describe("RowLoudness's own ranking for the words teamStatus does not have", () => {
    it('ranks BLOCKED louder than NO RESULT', () => {
      const tile = workflowsTile(
        payload({
          openCount: 2,
          workflows: [
            // Raw log says 'Running' with nobody live on it -> NO RESULT ("nothing has reported
            // back").
            timing({ correlation: 2, state: 'Running' }),
            timing({ correlation: 1, state: 'Blocked', blockedBy: 'scout' }),
          ],
        }),
        [],
        now,
      );

      expect(teamChip(tile, []).label).toBe('BLOCKED');
    });

    it('ranks NO RESULT louder than AWAITING', () => {
      const tile = workflowsTile(
        payload({
          openCount: 2,
          workflows: [
            timing({ correlation: 2, state: 'Running' }), // -> NO RESULT, nobody live.
            timing({ correlation: 1, state: 'Awaiting', awaitingFrom: 'writer' }),
          ],
        }),
        [],
        now,
      );

      expect(teamChip(tile, []).label).toBe('NO RESULT');
    });

    it('ranks AWAITING louder than RUNNING', () => {
      const tile = workflowsTile(
        payload({
          openCount: 2,
          workflows: [
            timing({ correlation: 2, state: 'Running' }),
            timing({ correlation: 1, state: 'Awaiting', awaitingFrom: 'writer' }),
          ],
        }),
        // A member Running under workflow 2 gives that row RUNNING; workflow 1 has nobody of its
        // own, so its row stays AWAITING from the raw log - and AWAITING must still outrank it.
        [container({ id: 'Worker', name: 'Worker', state: 'Running', currentCorrelation: 2 })],
        now,
      );

      expect(teamChip(tile, []).label).toBe('AWAITING');
    });

    /**
     * `CLOSED` IS A RESOLUTION, NOT AN ALARM, so it ranks with `COMPLETED` at the quiet foot of the
     * table rather than beside `UNDECLARED`. Ranking it with UNDECLARED would push a workflow
     * somebody has already looked at and ended ABOVE one nobody has touched, which is backwards:
     * the whole point of the ranking is that the row still needing a person surfaces.
     */
    it('ranks UNDECLARED louder than CLOSED — a closed workflow has been dealt with', () => {
      const tile = workflowsTile(
        payload({
          openCount: 2,
          workflows: [
            timing({ correlation: 2, state: 'Closed', endedAt: '2026-09-08T11:59:00Z' }),
            // PAST THE GRACE PERIOD, or the client never says UNDECLARED at all and this would rank a
            // null state instead of the word it is about.
            timing({
              correlation: 1,
              state: 'Undeclared',
              lastActivityAt: '2026-09-08T11:00:00Z',
            }),
          ],
        }),
        [],
        now,
      );

      expect(teamChip(tile, []).label).toBe('UNDECLARED');
    });

    /**
     * AND A DECLARED DELIVERY IS THE STRONGER OF THE TWO TERMINAL WORDS, mirroring the server's own
     * ladder in `SqliteMessageStore.TimingForAsync`, where `Completed` wins whenever a workflow
     * carries both rows. A tie would resolve on row order instead, which is nobody's decision.
     */
    it('ranks COMPLETED louder than CLOSED', () => {
      const tile = workflowsTile(
        payload({
          openCount: 2,
          workflows: [
            timing({ correlation: 2, state: 'Closed', endedAt: '2026-09-08T11:59:00Z' }),
            timing({ correlation: 1, state: 'Completed', endedAt: '2026-09-08T11:59:00Z' }),
          ],
        }),
        [],
        now,
      );

      expect(teamChip(tile, []).label).toBe('COMPLETED');
    });
  });
});

/**
 * An undeclared workflow is, by definition, one nobody is running — so `entry.correlation` matches
 * no container, and `workflowsTile`'s per-row filter always hands `workflowTiming` an EMPTY roster
 * for an Undeclared row. A `containers.length > 0` guard on `undeclared` would therefore be false
 * for every row, no matter how long the team had been silent; `loudestRow` would rank the
 * resulting `null` state at `+Infinity`, and a `?? 'COMPLETED'` fallback in `teamChip` would make
 * three workflows nobody had touched in hours read `COMPLETED · 3 open`.
 */
describe('UNDECLARED over a filtered, necessarily-empty roster', () => {
  it('reads UNDECLARED on every row and on the chip once the grace period has passed', () => {
    const tile = workflowsTile(
      payload({
        openCount: 3,
        workflows: [
          timing({ correlation: 3, state: 'Undeclared', lastActivityAt: '2026-09-08T11:30:00Z' }),
          timing({ correlation: 2, state: 'Undeclared', lastActivityAt: '2026-09-08T11:20:00Z' }),
          timing({ correlation: 1, state: 'Undeclared', lastActivityAt: '2026-09-08T11:10:00Z' }),
        ],
      }),
      // THE ROSTER IS EMPTY, ON PURPOSE — an undeclared workflow has nobody running under it, so this
      // is the realistic input, not an edge case chosen to dodge the bug.
      [],
      now,
    );

    for (const row of tile.rows) {
      expect(row.state).toBe('UNDECLARED');
      expect(row.state).not.toBe('COMPLETED');
    }

    const chip = teamChip(tile, []);
    expect(chip.label).toBe('UNDECLARED');
    expect(chip.label).not.toBe('COMPLETED');
    expect(chip.count).toBe(3);
  });

  it('reads no word at all — never COMPLETED — while still inside the grace period', () => {
    const tile = workflowsTile(
      payload({
        openCount: 3,
        workflows: [
          // Well within `StalledBadgeGrace` (2 minutes) of `now` (2026-09-08T12:00:00Z).
          timing({ correlation: 3, state: 'Undeclared', lastActivityAt: '2026-09-08T11:59:30Z' }),
          timing({ correlation: 2, state: 'Undeclared', lastActivityAt: '2026-09-08T11:59:20Z' }),
          timing({ correlation: 1, state: 'Undeclared', lastActivityAt: '2026-09-08T11:59:10Z' }),
        ],
      }),
      [],
      now,
    );

    for (const row of tile.rows) {
      expect(row.state).toBeNull();
    }

    const chip = teamChip(tile, []);
    expect(chip.label).toBeNull();
    expect(chip.label).not.toBe('COMPLETED');
    expect(chip.count).toBe(3);
  });
});

/**
 * THE TWO FIELDS THE DIALOG'S TABLE GAINED, and the reason each is not the one beside it.
 *
 * `openCount` still reports OPEN workflows and still feeds the tile's count line. `totalCount` is
 * the uncapped total since the team's floor, open and closed alike, and it is what truncation is
 * measured against — `rows.length < openCount` was inverted in the wild, reporting 0 open against
 * 1 row, and cannot answer the question at all now that closed workflows are in the list.
 */
describe('workflowsTile, the counts the table reads', () => {
  it('carries the total beside the open count, without either becoming the other', () => {
    const tile = workflowsTile(
      payload({
        openCount: 2,
        totalCount: 312,
        workflows: [timing({ correlation: 1 }), timing({ correlation: 2 })],
      } as Partial<TeamWorkflows>),
      [],
      now,
    );

    expect(tile.openCount).toBe(2);
    expect(tile.totalCount).toBe(312);

    // The count LINE is still the open one. A count that silently grew to mean "ever" would read as
    // a team with three hundred live workflows.
    expect(tile.count).toBe('2 open');
  });

  /** A TEAM WITH NOTHING OPEN STILL HAS A TOTAL, which is the case an `openCount` comparison gets
   *  backwards: `openCount: 0` against a list that carries rows. */
  it('reports a total over a list whose workflows are all closed', () => {
    const tile = workflowsTile(
      payload({
        openCount: 0,
        totalCount: 7,
        earliestStartedAt: null,
        workflows: [timing({ correlation: 1, state: 'Completed', endedAt: '2026-09-08T11:50:00Z' })],
      } as Partial<TeamWorkflows>),
      [],
      now,
    );

    expect(tile.openCount).toBe(0);
    expect(tile.totalCount).toBe(7);
    expect(tile.rows.length).toBe(1);
  });

  /**
   * AN OLDER HOST CARRIES NO TOTAL, and the fallback is the rendered length — which says "nothing is
   * truncated", the only claim that cannot be wrong. Never `openCount`, which would resurrect the
   * inverted comparison this field exists to replace.
   */
  it('falls back to the rendered length for a payload that predates the field', () => {
    const over = { openCount: 0, workflows: [timing({ correlation: 1 })] } as Partial<TeamWorkflows>;
    const without = payload(over);

    delete (without as { totalCount?: number }).totalCount;

    const tile = workflowsTile(without, [], now);

    expect(tile.totalCount).toBe(1);
    expect(tile.totalCount).not.toBe(tile.openCount);
  });

  /** An unavailable payload has no total any more than it has a count. */
  it('reports zero for both when the payload is unavailable', () => {
    const tile = workflowsTile(payload({ available: false } as Partial<TeamWorkflows>), [], now);

    expect(tile.openCount).toBe(0);
    expect(tile.totalCount).toBe(0);
  });
});

/**
 * EACH ROW CARRIES THE TWO INSTANTS ITS ELAPSED IS THE SUBTRACTION OF. Elapsed itself stays a
 * client-side subtraction that ticks with no further traffic — nothing here asks the server for a
 * number, and these are its endpoints rather than a replacement for it.
 */
describe('workflowsTile, the start and end on each row', () => {
  it('formats both instants for a workflow that has ended', () => {
    const tile = workflowsTile(
      payload({
        openCount: 0,
        totalCount: 1,
        earliestStartedAt: null,
        workflows: [
          timing({
            state: 'Completed',
            startedAt: '2026-09-08T11:30:00Z',
            endedAt: '2026-09-08T11:50:00Z',
          }),
        ],
      } as Partial<TeamWorkflows>),
      [],
      now,
    );

    expect(tile.rows[0]!.started).toBe(new Date('2026-09-08T11:30:00Z').toLocaleString());
    expect(tile.rows[0]!.ended).toBe(new Date('2026-09-08T11:50:00Z').toLocaleString());
  });

  /** AN EM DASH FOR A WORKFLOW THAT HAS NOT ENDED, never `now` and never an invented stamp. Most
   *  workflows never get an end: `workflow.completed` is a manager's declaration. */
  it('says em dash for an end the payload does not carry', () => {
    const tile = workflowsTile(payload(), [], now);

    expect(tile.rows[0]!.ended).toBe('—');
    expect(tile.rows[0]!.started).not.toBe('—');
  });

  /** An unreadable stamp is absent rather than `Invalid Date`. */
  it('says em dash for an instant it cannot parse', () => {
    const tile = workflowsTile(
      payload({ workflows: [timing({ startedAt: 'not an instant' })] } as Partial<TeamWorkflows>),
      [],
      now,
    );

    expect(tile.rows[0]!.started).toBe('—');
  });
});

/**
 * WHERE THE RUN TIME WENT, ACROSS EVERY WORKFLOW IN THE TABLE.
 *
 * The dialog's breakdown covers every workflow the table above it lists, not the SINGULAR `timing`
 * payload (the team's newest correlation). All three of the section's honesties survive the
 * aggregation, and each has a case here.
 */
describe('workflowsExecution', () => {
  it('sums one member across several workflows into one line', () => {
    const execution = workflowsExecution(payload({
      workflows: [
        timing({
          correlation: 1, runsCounted: 1, executionSeconds: 600,
          members: [{ member: 'Writer', runs: 1, executionSeconds: 600, unfinished: 0 }],
        }),
        timing({
          correlation: 2, runsCounted: 1, executionSeconds: 300,
          members: [{ member: 'Writer', runs: 2, executionSeconds: 300, unfinished: 0 }],
        }),
      ],
    } as Partial<TeamWorkflows>));

    expect(execution.members).toHaveLength(1);
    expect(execution.members[0]!.member).toBe('Writer');
    expect(execution.members[0]!.runs).toBe(3);
    expect(execution.members[0]!.execution).toBe('15m 00s');
    expect(execution.seconds).toBe(900);
  });

  /** THE UNIT OF MEASURE, IN WORDS — which is what says why this figure may exceed the tile's
   *  elapsed. Never a bare number. */
  it('carries its unit of measure in the total', () => {
    const execution = workflowsExecution(payload({
      workflows: [
        timing({
          correlation: 1, runsCounted: 2, executionSeconds: 600,
          members: [
            { member: 'Writer', runs: 1, executionSeconds: 400, unfinished: 0 },
            { member: 'Scout', runs: 1, executionSeconds: 200, unfinished: 0 },
          ],
        }),
      ],
    } as Partial<TeamWorkflows>));

    expect(execution.total).toBe('10m 00s across 2 members');
  });

  /**
   * THE RULE THAT SURVIVES VERBATIM: a run that started and never reported an end is UNKNOWN, never
   * zero, and stays out of the total. A host restart mid-flight spent real time and nothing knows
   * how much.
   */
  it('renders a member whose every run was cut short as unknown, and keeps it out of the total', () => {
    const execution = workflowsExecution(payload({
      workflows: [
        timing({
          correlation: 1, runsCounted: 1, executionSeconds: 600, runsUnfinished: 0,
          members: [{ member: 'Writer', runs: 1, executionSeconds: 600, unfinished: 0 }],
        }),
        timing({
          correlation: 2, runsCounted: 0, executionSeconds: 0, runsUnfinished: 1,
          members: [{ member: 'Scout', runs: 1, executionSeconds: null, unfinished: 1 }],
        }),
      ],
    } as Partial<TeamWorkflows>));

    const scout = execution.members.find((row) => row.member === 'Scout');

    expect(scout!.execution).toBe('(unknown)');
    expect(scout!.unfinished).toBe(1);

    // Writer's ten minutes alone, and the member count in the words excludes Scout.
    expect(execution.seconds).toBe(600);
    expect(execution.total).toBe('10m 00s across 1 member');
    expect(execution.unfinished).toBe(1);
  });

  /**
   * A NULL CONTRIBUTION MUST NOT ERASE A MEASURED ONE. A member with ten measured minutes in one
   * workflow and one unmeasured run in another has spent AT LEAST ten minutes, and `(unknown)` is a
   * worse answer than the floor — only a member whose EVERY contribution is null stays unknown.
   */
  it('keeps a measured contribution when the same member has an unmeasured one elsewhere', () => {
    const execution = workflowsExecution(payload({
      workflows: [
        timing({
          correlation: 1, runsCounted: 1, executionSeconds: 600,
          members: [{ member: 'Writer', runs: 1, executionSeconds: 600, unfinished: 0 }],
        }),
        timing({
          correlation: 2, runsCounted: 0, executionSeconds: 0, runsUnfinished: 1,
          members: [{ member: 'Writer', runs: 1, executionSeconds: null, unfinished: 1 }],
        }),
      ],
    } as Partial<TeamWorkflows>));

    expect(execution.members).toHaveLength(1);
    expect(execution.members[0]!.execution).toBe('10m 00s');
    expect(execution.members[0]!.unfinished).toBe(1);
  });

  /** `partial` IS TRUE IF ANY WORKFLOW IS PARTIAL — the flag says "some runs here could not be
   *  measured", and that is no less true of a set than of one. */
  it('is partial when any workflow in the set is', () => {
    const execution = workflowsExecution(payload({
      workflows: [
        timing({ correlation: 1, partial: false }),
        timing({ correlation: 2, partial: true }),
      ],
    } as Partial<TeamWorkflows>));

    expect(execution.partial).toBe(true);
  });

  /** NOTHING MEASURED IS NOT ZERO. `no measured run time` is the absence of a figure, and a team
   *  that has published nothing has no breakdown at all. */
  it('says no measured run time rather than 0s when nothing was counted', () => {
    expect(workflowsExecution(payload()).total).toBe('no measured run time');
    expect(workflowsExecution(null).total).toBe('no measured run time');
    expect(workflowsExecution(payload({ available: false } as Partial<TeamWorkflows>)).members)
      .toEqual([]);
    expect(workflowsExecution(payload({ workflows: [] } as Partial<TeamWorkflows>)).members)
      .toEqual([]);
  });

  /** NEWEST WORKFLOW FIRST, so the member who ran most recently leads — the same order the
   *  single-workflow breakdown has always had. */
  it('orders members by the workflow they were first seen in, newest first', () => {
    const execution = workflowsExecution(payload({
      workflows: [
        timing({
          correlation: 2, runsCounted: 1, executionSeconds: 60,
          members: [{ member: 'Scout', runs: 1, executionSeconds: 60, unfinished: 0 }],
        }),
        timing({
          correlation: 1, runsCounted: 1, executionSeconds: 60,
          members: [{ member: 'Writer', runs: 1, executionSeconds: 60, unfinished: 0 }],
        }),
      ],
    } as Partial<TeamWorkflows>));

    expect(execution.members.map((row) => row.member)).toEqual(['Scout', 'Writer']);
  });
});

describe('teamChip — a wake held behind the WIP limit', () => {
  it('reads WAITING FOR A SLOT and names the held member instead of UNDECLARED', () => {
    const tile = workflowsTile(
      payload({ openCount: 1, workflows: [timing({ correlation: 1, state: 'Undeclared' })] }),
      [],
      now,
    );

    const chip = teamChip(tile, [container({ id: 'Manager', name: 'Manager', held: true })]);

    expect(chip.label).toBe(WaitingForSlot);
    expect(chip.held).toEqual(['Manager']);
  });

  it('leaves a louder workflow state in place', () => {
    const tile = workflowsTile(
      payload({ openCount: 1, workflows: [timing({ correlation: 1, state: 'Blocked', blockedBy: 'scout' })] }),
      [],
      now,
    );

    expect(teamChip(tile, [container({ id: 'Manager', name: 'Manager', held: true })]).label).toBe('BLOCKED');
  });
});
