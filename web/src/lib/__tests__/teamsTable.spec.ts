import { describe, expect, it } from 'vitest';
import {
  DefaultTeamSort,
  compareTeams,
  lastWorkflowText,
  memberBreakdown,
  nextSort,
  proposeCloneName,
  readTeamSort,
  teamRowFrom,
  writeTeamSort,
  type TeamRow,
} from '../teamsTable';
import { asTeamId, type Team, type TeamWorkflows, type TeamWorkflowTiming } from '../../api/types';

const row = (overrides: Partial<TeamRow> = {}): TeamRow => ({
  id: asTeamId('Alpha'),
  name: 'Alpha',
  members: 1,
  memberBreakdown: '1 idle',
  status: 'idle',
  held: [],
  startedAt: null,
  workflows: null,
  ...overrides,
});

/** A team as `/api/overview` sends one. Defaults match the server's own for an untouched team. */
const team = (overrides: Partial<Team> = {}): Team => ({
  id: asTeamId('Alpha'),
  name: 'Alpha',
  concierge: 'claude',
  memberAgents: null,
  additionalInstructions: null,
  root: null,
  containers: [],
  ...overrides,
});

/**
 * A workflow projection as the rollup sends one, WHOLE.
 *
 * `as never` is enough for `lastWorkflowText`, which reads four fields - but `teamRowFrom` reaches
 * `teamLogFromTiming`, which iterates `failedMembers` and `members`. A partial fixture there fails
 * on its own setup, which is a test failing for the wrong reason and reads like a real defect.
 */
const timing = (overrides: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming => ({
  available: true,
  correlation: 1,
  state: 'Running',
  startedAt: '2026-09-07T09:00:00Z',
  endedAt: null,
  serverNow: '2026-09-07T09:10:00Z',
  executionSeconds: 0,
  partial: false,
  runsCounted: 0,
  runsUnfinished: 0,
  blockedBy: null,
  runsFailed: 0,
  failedMembers: [],
  missing: null,
  members: [],
  lastActivityAt: '2026-09-07T09:05:00Z',
  awaitingFrom: null,
  subject: null,
  pausedAt: null,
  pausedReason: null,
  pausedLimit: null,
  ...overrides,
});

/**
 * THE PLURAL PROJECTION, as `/api/teams/{team}/workflows` sends one - `workflowsTile` iterates
 * `workflows.workflows` and reads `earliestStartedAt`, so a fixture missing either fails on its own
 * setup rather than on the behaviour under test, for `timing`'s own reason above.
 */
const workflows = (overrides: Partial<TeamWorkflows> = {}): TeamWorkflows => ({
  available: true,
  openCount: 1,

  // OPEN AND CLOSED ALIKE, uncapped - what the dialog's truncation line counts against. `openCount`
  // still means OPEN and is what this file's own assertions read.
  totalCount: 1,
  earliestStartedAt: '2026-09-07T09:00:00Z',
  serverNow: '2026-09-07T09:10:00Z',
  workflows: [timing()],
  missing: null,
  ...overrides,
});

describe('lastWorkflowText', () => {
  /**
   * THE TWO EM-DASH SHAPES ARE DIFFERENT CLAIMS, and telling them apart is the whole of this pair.
   *
   * `null` is the store answering "the rollup has not landed" - the first paint, and every row for
   * the rest of a session after one failed fetch. `available: false` is the SERVER saying it read
   * the log and found nothing. Collapsing them rendered "never run" over teams that run
   * constantly: a confident wrong answer in place of an honest one that costs nothing.
   */
  it('says NOT LOADED YET for a team whose rollup has not arrived, never `never run`', () => {
    expect(lastWorkflowText(null).text).toBe('—');
    expect(lastWorkflowText(null).hint).toBe('not loaded yet');
  });

  it('says NEVER RUN only when the server measured no workflow, and never says 0m', () => {
    const cell = lastWorkflowText(timing({ available: false }));

    expect(cell.text).toBe('—');
    expect(cell.hint).toBe('never run');
  });

  it('says STARTED while the workflow is open, which is the ordinary case', () => {
    const cell = lastWorkflowText({
      available: true, startedAt: '2026-09-07T09:00:00Z', endedAt: null, state: 'Running',
    } as never);

    expect(cell.text).toContain('started');
    expect(cell.text).toContain('Running');
  });

  it('says ENDED only when a manager declared it finished', () => {
    const cell = lastWorkflowText({
      available: true, startedAt: '2026-09-07T09:00:00Z', endedAt: '2026-09-07T09:20:00Z',
      state: 'Completed',
    } as never);

    expect(cell.text).toContain('ended');
    expect(cell.hint).toBe('A manager declared this workflow finished.');
  });

  /**
   * THE HINT IS A CLAIM ABOUT WHO ENDED IT, so a workflow a PERSON closed cannot wear the
   * manager's one. `endedAt` is non-null for a close too - a closed workflow has ended, and its
   * clock must stop - so both states take the `ended` arm and the hint is the only thing left that
   * can tell them apart. Saying "a manager declared this workflow finished" over a close is the
   * same fiction the log refuses to record, moved to the tooltip.
   */
  it('does not credit a manager for a workflow a person closed', () => {
    const cell = lastWorkflowText({
      available: true, startedAt: '2026-09-07T09:00:00Z', endedAt: '2026-09-07T09:20:00Z',
      state: 'Closed',
    } as never);

    expect(cell.text).toContain('ended');
    expect(cell.text).toContain('Closed');
    expect(cell.hint).not.toContain('manager');
  });

  /**
   * AN ABSENT FIELD IS `undefined`, NOT `null`. A strict test takes the `ended` arm and renders
   * `ended —, Running` - a fourth shape, claiming a declaration nobody made, from a server change
   * nothing else would flag.
   */
  it('treats an absent endedAt as open, exactly like a null one', () => {
    const cell = lastWorkflowText({
      available: true, startedAt: '2026-09-07T09:00:00Z', state: 'Running',
    } as never);

    expect(cell.text).toContain('started');
    expect(cell.text).not.toContain('ended');
  });
});

describe('compareTeams', () => {
  /**
   * A DISCRIMINATING PAIR, which `alpha` against `Beta` was not: those two answer -1 with or
   * without `sensitivity: 'base'`, so the assertion held against a bare `localeCompare` and could
   * not fail for the reason it names. Two names differing ONLY in case can: base sensitivity
   * ignores case and answers 0, where the root collation orders lowercase before uppercase at the
   * tertiary level and answers -1.
   */
  it('sorts by name case-insensitively by default', () => {
    expect(DefaultTeamSort).toEqual({ column: 'name', descending: false });
    expect(compareTeams(row({ name: 'alpha' }), row({ name: 'ALPHA' }), DefaultTeamSort)).toBe(0);

    // AND IT STILL ORDERS. Case-blind EQUALITY alone would be satisfied by a comparator that
    // answered 0 for everything, which is the other way to have no sort at all.
    expect(compareTeams(row({ name: 'alpha' }), row({ name: 'Beta' }), DefaultTeamSort))
      .toBeLessThan(0);
  });

  /**
   * ALSO A DISCRIMINATING PAIR. `failed` before `idle` and `running` after `blocked` are both true
   * of the alphabet as well, so the test passed against the comparator it exists to refuse.
   * `misconfigured` against `blocked` is the pair that separates them: the ranking puts
   * `misconfigured` FIRST - it leads the chip's own order - and the alphabet puts `blocked` first.
   */
  it('sorts status by the ranking, not alphabetically', () => {
    const sort = { column: 'status', descending: false } as const;

    expect(compareTeams(row({ status: 'misconfigured' }), row({ status: 'blocked' }), sort))
      .toBeLessThan(0);

    // The other direction of the same pair, so a comparator that answered a constant cannot pass.
    expect(compareTeams(row({ status: 'blocked' }), row({ status: 'misconfigured' }), sort))
      .toBeGreaterThan(0);
  });

  /** A team that has never run has no instant, and must not sort as if it ran at the epoch. */
  it('puts teams with no workflow last, in both directions', () => {
    const asc = { column: 'workflow', descending: false } as const;
    const desc = { column: 'workflow', descending: true } as const;
    const ran = row({ startedAt: 1_000 });
    const never = row({ startedAt: null });

    expect(compareTeams(ran, never, asc)).toBeLessThan(0);
    expect(compareTeams(ran, never, desc)).toBeLessThan(0);
  });

  /**
   * ASCENDING IS OLDEST FIRST, which is what makes the header's `arrow_upward` honest. It read
   * `b - a` and sorted newest-first under an up arrow - a control saying the opposite of what it
   * did, and the nulls test above could not see it because neither row had an instant.
   */
  it('sorts two real instants oldest-first ascending, and reverses descending', () => {
    const older = row({ startedAt: 1_000 });
    const newer = row({ startedAt: 2_000 });

    expect(compareTeams(older, newer, { column: 'workflow', descending: false })).toBeLessThan(0);
    expect(compareTeams(older, newer, { column: 'workflow', descending: true })).toBeGreaterThan(0);
  });

  /**
   * THE COARSE COLUMNS NEED A SECOND KEY. Status has seven values and Members is a small
   * integer, so most of the table ties on them - and a comparator answering 0 leaves those rows in whatever order the
   * server happened to send, which moves between refreshes for no reason a person can see.
   *
   * Written as a full sort rather than one comparison, because `Array.sort` is STABLE: a
   * comparator that answers 0 passes any assertion made about a pair it was already given in the
   * right order. Only a list that arrives in the WRONG order can tell the two apart.
   */
  it('falls back to name when the chosen column ties', () => {
    const sort = { column: 'status', descending: false } as const;
    const unordered = [
      row({ name: 'Charlie', status: 'idle' }),
      row({ name: 'alpha', status: 'idle' }),
      row({ name: 'Bravo', status: 'idle' }),
    ];

    expect([...unordered].sort((a, b) => compareTeams(a, b, sort)).map((team) => team.name))
      .toEqual(['alpha', 'Bravo', 'Charlie']);
  });

  it('ties on members fall back to name too', () => {
    const members = { column: 'members', descending: false } as const;

    expect(compareTeams(row({ name: 'Bravo' }), row({ name: 'alpha' }), members)).toBeGreaterThan(0);
  });

  /**
   * THE ARROW NAMES ONE COLUMN. Reversing Status must not also reverse the names inside each
   * status, or a click changes two orderings and only one of them is on screen.
   */
  it('keeps the name tiebreak ascending when the primary column is reversed', () => {
    const sort = { column: 'status', descending: true } as const;

    expect(compareTeams(row({ name: 'alpha' }), row({ name: 'Bravo' }), sort)).toBeLessThan(0);
  });
});

describe('nextSort', () => {
  it('flips direction when the same column is clicked again', () => {
    expect(nextSort({ column: 'name', descending: false }, 'name'))
      .toEqual({ column: 'name', descending: true });
    expect(nextSort({ column: 'name', descending: true }, 'name'))
      .toEqual({ column: 'name', descending: false });
  });

  /** A different column starts ascending. Inheriting the previous column's direction means a
   *  click that changes two things at once. */
  it('resets to ascending when a different column is clicked', () => {
    expect(nextSort({ column: 'name', descending: true }, 'members'))
      .toEqual({ column: 'members', descending: false });
  });
});

describe('teamRowFrom', () => {
  /** A roster with members, none of them Running - the roster half of the pair below. */
  const teamWithNoRunningMembers = team({ containers: [{}, {}] as never });

  it('has no workflow instant for a team with no timing', () => {
    expect(teamRowFrom(team(), null, null, 0).startedAt).toBeNull();
  });

  /**
   * NaN IS NOT NULL, and that is the whole point of this test.
   *
   * `Date.parse` answers `NaN` for an instant it cannot read. `NaN` walks past `compareTeams`'
   * nulls-last branch, makes every comparison `NaN`, and leaves `sort` with a comparator that is
   * not a total order - so the rule the nulls test protects is defeated one layer up, and the row
   * order becomes implementation-defined rather than wrong in a way anyone could see.
   */
  it('has no workflow instant for an unreadable one either, never NaN', () => {
    const built = teamRowFrom(team(), timing({ startedAt: 'not an instant' }), null, 0);

    expect(built.startedAt).toBeNull();
    expect(Number.isNaN(built.startedAt as number)).toBe(false);
  });

  it('reads the instant a team did start work', () => {
    const built = teamRowFrom(team(), timing({ startedAt: '2026-09-07T09:00:00Z' }), null, 0);

    expect(built.startedAt).toBe(Date.parse('2026-09-07T09:00:00Z'));
  });

  it('counts the members off the roster and carries the name through', () => {
    const built = teamRowFrom(
      team({ name: 'Research', containers: [{}, {}] as never }),
      null,
      null,
      0,
    );

    expect(built.name).toBe('Research');
    expect(built.members).toBe(2);
  });

  /**
   * TWO ANSWERS, DELIBERATELY, BECAUSE THEY ARE TWO QUESTIONS.
   *
   * `status` is the ROSTER: is anybody working right now. `workflows` is the PROJECTION: what is
   * open and what is the loudest of it. One cell trying to be both is what produced a Teams table
   * reading IDLE beside that team's own tile reading STALLED, both true, about different workflows,
   * neither saying which.
   *
   * Null until the plural payload lands - never a stand-in word. An absent answer is not `idle`.
   */
  it('carries the workflow chip beside the roster status, as two separate answers', () => {
    // A team with nothing running and one undeclared workflow: IDLE and UNDECLARED are both true,
    // and this is the pair that reads as a contradiction when one cell has to say both.
    const undeclaredWorkflowsPayload = workflows({
      workflows: [timing({ state: 'Undeclared', lastActivityAt: '2026-09-06T00:00:00Z' })],
    });
    const now = Date.parse('2026-09-09T00:10:00Z');

    const row = teamRowFrom(teamWithNoRunningMembers, null, undeclaredWorkflowsPayload, now);

    expect(row.status).toBe('idle');
    expect(row.workflows?.label).toBe('UNDECLARED');
    expect(row.workflows?.count).toBe(1);
  });

  it('leaves the workflow chip null until the plural payload lands, rather than inventing one', () => {
    const row = teamRowFrom(teamWithNoRunningMembers, null, null, Date.parse('2026-09-09T00:10:00Z'));

    expect(row.status).toBe('idle');
    expect(row.workflows).toBeNull();
  });
});

describe('the stored sort', () => {
  it('round-trips', () => {
    const sort = { column: 'members', descending: true } as const;

    expect(readTeamSort(writeTeamSort(sort))).toEqual(sort);
  });

  it('falls back to the default for absent, unparseable and unknown columns', () => {
    expect(readTeamSort(null)).toEqual(DefaultTeamSort);
    expect(readTeamSort('{')).toEqual(DefaultTeamSort);
    expect(readTeamSort('{"column":"nonsense","descending":false}')).toEqual(DefaultTeamSort);
    expect(readTeamSort('{"column":"name","descending":"yes"}')).toEqual(DefaultTeamSort);
  });
});

describe('proposeCloneName', () => {
  it('proposes the second of a set', () => {
    expect(proposeCloneName('research kanban', ['research kanban'])).toBe('research kanban 2');
  });

  it('walks past names already taken', () => {
    expect(proposeCloneName('Alpha', ['Alpha', 'Alpha 2', 'Alpha 3'])).toBe('Alpha 4');
  });

  it('compares case-insensitively, because a person reads names and not casing', () => {
    expect(proposeCloneName('Alpha', ['alpha 2'])).toBe('Alpha 3');
  });

  /**
   * THE BOUNDED WALK HAS TO END SOMEWHERE, and where it ends must not be a name this function
   * already knows is taken. Answering `source` - the one name in the list for certain - would make
   * the dialog prefill a value guaranteed to come back 409.
   */
  it('never proposes the source name itself when every suffix it tried was taken', () => {
    const taken = ['Alpha', ...Array.from({ length: 998 }, (_, i) => `Alpha ${i + 2}`)];

    expect(proposeCloneName('Alpha', taken)).not.toBe('Alpha');
    expect(taken.map((name) => name.toLowerCase()))
      .not.toContain(proposeCloneName('Alpha', taken).toLowerCase());
  });
});

describe('memberBreakdown', () => {
  it('counts members by what they are doing and leaves out empty states', () => {
    expect(
      memberBreakdown([
        { state: 'Running', queueDepth: 0 },
        { state: 'Idle', queueDepth: 2 },
        { state: 'Idle', queueDepth: 1 },
        { state: 'Idle', queueDepth: 0, blocked: 'no credentials' },
        { state: 'Idle', queueDepth: 0 },
      ]),
    ).toBe('1 running · 2 queued · 1 blocked · 1 idle')
  })

  it('is empty for a team with no members', () => {
    expect(memberBreakdown([])).toBe('')
  })
})
