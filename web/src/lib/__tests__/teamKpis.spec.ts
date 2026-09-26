import { describe, expect, it } from 'vitest';
import type { ContainerSnapshot, TeamTokenTotals, TeamWorkflowTiming } from '../../api/types';
import { asMemberId, asTeamId } from '../../api/types';
import type { Message } from '../../api/types';
import {
  containerMark,
  heldMembers,
  waitingForSlotText,
  closeReasonPrefill,
  workflowRowActions,
  teamKpis,
  teamLogFacts,
  teamLogFromTiming,
  teamStatus,
  tokenUsageFromTotals,
  StalledBadgeGrace,
  workflowTiming,
  type MemberTerminal,
  type TeamLog,
  type TerminalEvent,
  type WorkflowRowActions,
  type WorkflowState,
} from '../teamKpis';

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

/**
 * ONE INSTANT FOR THE WHOLE FILE. `teamStatus` takes `now` rather than reading the clock,
 * which is what lets these tests state time exactly instead of sleeping through a grace period.
 */
const NOW = Date.parse('2026-09-03T12:00:00Z');

/** An ISO-8601 instant `msAgo` milliseconds before {@link NOW}, as `Message.occurredAt` spells it. */
const at = (msAgo: number) => new Date(NOW - msAgo).toISOString();

const terminal = (event: TerminalEvent, msAgo: number): MemberTerminal => ({
  event,
  occurredAt: at(msAgo),
});

/**
 * A terminal event OLD ENOUGH TO BE PAST THE GRACE PERIOD, which is what a case in this file
 * means when it writes a bare `'completed'`. Stated relative to the constant rather than
 * as a number, so retuning the grace cannot quietly turn these cases into their opposites.
 */
const settled = (event: TerminalEvent): MemberTerminal => terminal(event, StalledBadgeGrace * 5);

/** `teamStatus` at {@link NOW}, so no case in this file depends on the wall clock. */
const status = (
  containers: ContainerSnapshot[],
  log: TeamLog | null = null,
  now = NOW,
  paused = false,
) => teamStatus(containers, log, now, paused);

const totals = (overrides: Partial<TeamTokenTotals> = {}): TeamTokenTotals => ({
  available: true,
  tokensIn: 0,
  tokensOut: 0,
  tokensCachedIn: 0,
  tokensCacheCreation: 0,
  tokensBillable: 0,
  partial: false,
  runsWithUsage: 0,
  runsWithoutUsage: 0,
  missing: null,
  members: [],
  ...overrides,
});

describe('teamKpis', () => {
  it('counts nothing on an empty roster rather than inventing members', () => {
    const kpis = teamKpis([]);

    expect(kpis.working).toBe(0);
    expect(kpis.members).toBe(0);
    expect(kpis.queued).toBe(0);
    expect(kpis.stopped).toBe(0);
    expect(kpis.atCeiling).toBe(0);
  });

  it('counts members currently Running as working, against the real roster size', () => {
    const kpis = teamKpis([
      container({ id: 'Manager', state: 'Running' }),
      container({ id: 'Writer', name: 'Writer', state: 'Running' }),
      container({ id: 'IdleOne', name: 'IdleOne', state: 'Idle' }),
      container({ id: 'IdleTwo', name: 'IdleTwo', state: 'Idle' }),
    ]);

    expect(kpis.working).toBe(2);
    expect(kpis.members).toBe(4);
  });

  it('sums queueDepth across the team, which is work waiting, not members with a queue', () => {
    const kpis = teamKpis([
      container({ id: 'Manager', queueDepth: 2 }),
      container({ id: 'Writer', name: 'Writer', queueDepth: 3 }),
      container({ id: 'IdleOne', name: 'IdleOne', queueDepth: 0 }),
    ]);

    expect(kpis.queued).toBe(5);
  });

  it('counts a blocked member as stopped even when the card is idle', () => {
    // The whole point of ContainerSnapshot.blocked: idle-and-delivered and idle-and-gave-up
    // look the same without it. The strip has to surface the mark the cards already carry.
    const kpis = teamKpis([
      container({ id: 'Manager', state: 'Idle', blocked: 'same work came back twice' }),
      container({ id: 'Writer', name: 'Writer', state: 'Idle' }),
    ]);

    expect(kpis.stopped).toBe(1);
    expect(kpis.working).toBe(0);
  });

  /**
   * A MEMBER THAT HANDED BACK IS NOT STOPPED. It is the opposite of stopped: it finished.
   * Counting it would reproduce, on the one tile a person glances at, the exact lie the hand-back
   * verb exists to remove - and `handedBack` never clears, so a member that handed back once would
   * be counted as stopped forever.
   */
  it('does not count a member that handed its work back as stopped', () => {
    const kpis = teamKpis([
      container({ id: 'Writer', name: 'Writer', state: 'Idle', handedBack: 'card 4539' }),
    ])

    expect(kpis.stopped).toBe(0)
    expect(kpis.members).toBe(1)
  })

  it('does not treat a missing Agent as stopped', () => {
    const kpis = teamKpis([
      container({ id: 'A', name: 'A', missingAgent: 'gone' }),
    ]);

    expect(kpis.stopped).toBe(0);
    expect(kpis.members).toBe(1);
  });

  it('flags at-ceiling from queueDepth against that member’s own ceiling', () => {
    const kpis = teamKpis([
      container({ id: 'Full', name: 'Full', queueDepth: 4, ceiling: 4 }),
      container({ id: 'Ok', name: 'Ok', queueDepth: 3, ceiling: 16 }),
    ]);

    expect(kpis.atCeiling).toBe(1);
    expect(kpis.queued).toBe(7);
  });
});

describe('teamStatus', () => {
  it('returns idle when there are no containers', () => {
    expect(status([])).toBe('idle');
  });

  it('returns running when one member is running', () => {
    expect(status([container({ state: 'Running' })])).toBe('running');
  });

  it('returns blocked when a blocked member and a running member both exist', () => {
    expect(
      status([
        container({ id: 'Runner', name: 'Runner', state: 'Running' }),
        container({ id: 'Blocked', name: 'Blocked', blocked: 'needs human action' }),
      ]),
    ).toBe('blocked');
  });

  it('returns paused when the team is paused, even if work is already queued', () => {
    expect(
      status([
        container({ id: 'Queued', name: 'Queued', queueDepth: 2 }),
        container({ id: 'Idle', name: 'Idle' }),
      ], null, NOW, true),
    ).toBe('paused');
  });

  it('keeps a louder member fault above paused', () => {
    expect(
      status([
        container({ id: 'Blocked', name: 'Blocked', blocked: 'needs human action' }),
        container({ id: 'Idle', name: 'Idle' }),
      ], null, NOW, true),
    ).toBe('blocked');
  });

  it('returns misconfigured when missingAgent is set, even if another member is blocked', () => {
    expect(
      status([
        container({ id: 'Blocked', name: 'Blocked', blocked: 'blocked reason' }),
        container({ id: 'Misconfigured', name: 'Misconfigured', missingAgent: 'missing-agent' }),
      ]),
    ).toBe('misconfigured');
  });

  it('returns misconfigured when unreachableRoot is set, even if another member is blocked', () => {
    expect(
      status([
        container({ id: 'Blocked', name: 'Blocked', blocked: 'blocked reason' }),
        container({
          id: 'Misconfigured',
          name: 'Misconfigured',
          unreachableRoot: 'Z:\\missing-root',
        }),
      ]),
    ).toBe('misconfigured');
  });

  it('returns queued when queueDepth is above zero and nothing is running', () => {
    expect(
      status([
        container({ id: 'Queued', name: 'Queued', queueDepth: 2 }),
        container({ id: 'Idle', name: 'Idle', queueDepth: 0 }),
      ]),
    ).toBe('queued');
  });

  it('returns running when queueDepth is above zero and at least one member is running', () => {
    expect(
      status([
        container({ id: 'Queued', name: 'Queued', queueDepth: 3 }),
        container({ id: 'Runner', name: 'Runner', state: 'Running' }),
      ]),
    ).toBe('running');
  });

  it('returns idle when every member is idle with no queue, block, or misconfiguration', () => {
    expect(
      status([
        container({ id: 'One', name: 'One' }),
        container({ id: 'Two', name: 'Two' }),
      ]),
    ).toBe('idle');
  });
});

/**
 * UNDECLARED, one test per clause.
 *
 * The baseline below IS an undeclared team, and every test after the first flips EXACTLY ONE clause
 * of it. A single test that flipped them all together would pass whichever clause was dropped
 * from the implementation, which is the opposite of "every clause is load-bearing".
 */
describe('teamStatus — UNDECLARED', () => {
  const roster = (): ContainerSnapshot[] => [
    container({ id: 'Manager', name: 'Manager' }),
    container({ id: 'Writer', name: 'Writer' }),
  ];

  const openAndDelivered = (): TeamLog => ({
    workflowOpen: true,
    lastTerminal: { Manager: settled('completed'), Writer: settled('completed') },
  });

  it('is undeclared when the workflow is open, nothing runs, no queue, and every member last completed', () => {
    // Every number correct, and together they describe a healthy team with nothing to do.
    expect(status(roster(), openAndDelivered())).toBe('undeclared');
  });

  /**
   * THE CHIP MUST NOT ASK A STRICTER QUESTION THAN THE TILE. Requiring EVERY member on the roster
   * to carry a 'completed' terminal would let a member that had never been given anything to do --
   * no terminal at all -- disqualify the whole team. The server projection asks it only of the
   * members that RAN.
   *
   * A team of six with two working is not exotic, it is the normal shape, and the tile and the chip
   * must agree on it. A routine disagreement is what makes a REAL one hard to see: a chip saying
   * BLOCKED beside a tile saying UNDECLARED would look no different from the ordinary case.
   */
  it('counts only the members that RAN, so an untasked member does not silence the mark', () => {
    const withNewcomer = [...roster(), container({ id: 'Newcomer', name: 'Newcomer' })];

    // Newcomer has never run, so it has no terminal at all.
    expect(status(withNewcomer, openAndDelivered())).toBe('undeclared');
  });

  /**
   * AND A ROSTER THAT HAS RUN NOTHING IS STILL NOT UNDECLARED. Skipping members without a terminal
   * must not collapse into "no evidence means undeclared" -- a team that has never done anything is
   * idle, which is the same reason an empty roster is excluded.
   */
  it('is not undeclared when NO member has run at all', () => {
    expect(status(roster(), { workflowOpen: true, lastTerminal: {} })).toBe('idle');
  });

  it('CLAUSE 1 — a CLOSED workflow is idle, because a team between jobs is not undeclared', () => {
    // The clause that stops the mark firing on every team that has finished its work. UNDECLARED requires
    // an OPEN workflow rather than merely an idle roster, and this is the whole reason.
    expect(status(roster(), { ...openAndDelivered(), workflowOpen: false })).toBe('idle');
  });

  it('CLAUSE 2 — one member Running is running, not undeclared', () => {
    const containers = roster();
    containers[1] = container({ id: 'Writer', name: 'Writer', state: 'Running' });

    expect(status(containers, openAndDelivered())).toBe('running');
  });

  it('CLAUSE 3 — work still queued is queued, not undeclared', () => {
    const containers = roster();
    containers[1] = container({ id: 'Writer', name: 'Writer', queueDepth: 1 });

    expect(status(containers, openAndDelivered())).toBe('queued');
  });

  it('CLAUSE 4 — a member whose last terminal event is not `completed` is not an undeclared team', () => {
    // `blocked` here is the LOG's last terminal event, with no mark left on the snapshot - the
    // narrow case that isolates this clause from the `blocked` branch above it.
    expect(
      status(roster(), {
        workflowOpen: true,
        lastTerminal: { Manager: settled('completed'), Writer: settled('blocked') },
      }),
    ).toBe('idle');
  });

  it('a member running again after a failed run does not make the team failed', () => {
    const log: TeamLog = { workflowOpen: true, lastTerminal: { Marek: settled('failed') } };

    expect(status([container({ id: 'Marek', name: 'Developer Marek', state: 'Running' })], log))
      .toBe('running');
    expect(status([container({ id: 'Marek', name: 'Developer Marek', queueDepth: 1 })], log))
      .toBe('queued');
    expect(status([container({ id: 'Marek', name: 'Developer Marek' })], log)).toBe('failed');
  });

  it('CLAUSE 5 — a member awaiting a decision is EXCLUDED, so the team is not undeclared', () => {
    // Mutually exclusive BY CONSTRUCTION rather than by ranking. Without this clause a Manager
    // that stopped on purpose to ask its owner a question renders as a team that stopped for no
    // reason, and the two want opposite things from the person reading the board.
    expect(
      status(roster(), {
        workflowOpen: true,
        lastTerminal: { Manager: settled('needs-decision'), Writer: settled('completed') },
      }),
    ).toBe('idle');
  });

  /**
   * "Silence is not a delivery" - nothing heard from is not the same as finished cleanly.
   *
   * That reasoning holds, and skipping a member with no terminal does NOT count it as delivered -- it
   * is excluded from the question entirely, which is what the server projection does.
   * The distinction is kept by the `ran === 0` guard directly below: skipping everybody cannot
   * collapse into "undeclared", so silence still never stands in for a delivery.
   *
   * What matters is whose silence disqualifies whom. Requiring EVERY member to have run would make
   * a team of six with two working -- the normal shape -- read UNDECLARED on the tile and IDLE on
   * the chip indefinitely, and a routine disagreement between two surfaces is what hides a real one.
   */
  it('skips a member that never ran rather than letting it silence the mark', () => {
    expect(status(roster(), { workflowOpen: true, lastTerminal: { Manager: settled('completed') } })).toBe(
      'undeclared',
    );
  });

  it('is not undeclared with an empty roster, however open the workflow', () => {
    expect(status([], { workflowOpen: true, lastTerminal: {} })).toBe('idle');
  });

  it('cannot be undeclared without the log, because four of the five clauses are not on a snapshot', () => {
    // The one-argument call, which other callers make. It degrades to `idle` rather than guessing.
    expect(status(roster())).toBe('idle');
  });
});

/**
 * THE GRACE PERIOD.
 *
 * The case these exist for: a team is created, sent one setup instruction, does the job
 * and stops — and without a grace period the board would read UNDECLARED within seconds. Every
 * clause holds; the verdict is correct and it is the wrong thing to tell a person. A completed
 * errand and an abandoned job are structurally identical in the log, so until the Manager declares
 * the only thing separating them is how long the silence has lasted.
 */
describe('teamStatus — the badge grace period', () => {
  const roster = (): ContainerSnapshot[] => [
    container({ id: 'Manager', name: 'Manager' }),
    container({ id: 'Writer', name: 'Writer' }),
  ];

  const finishedAt = (msAgo: number): TeamLog => ({
    workflowOpen: true,
    lastTerminal: {
      Manager: terminal('completed', msAgo),
      Writer: terminal('completed', msAgo + 60_000),
    },
  });

  it('is NOT undeclared inside the grace period, and IS undeclared after it', () => {
    // THE HEADLINE. Same team, same open workflow, same idle roster, same empty queues, same
    // `completed` from every member. The only thing that differs is how long ago it stopped.
    expect(status(roster(), finishedAt(30_000))).toBe('idle');
    expect(status(roster(), finishedAt(3 * 60_000))).toBe('undeclared');
  });

  it('measures the NEWEST row on the team, not the oldest — a member that just spoke is not silence', () => {
    // A Manager that finished ten seconds ago has not been silent, however long ago its colleagues
    // last said anything. Reading the oldest row would call this team undeclared while it was still
    // publishing.
    expect(
      status(roster(), {
        workflowOpen: true,
        lastTerminal: {
          Manager: terminal('completed', 10_000),
          Writer: terminal('completed', 2 * 60 * 60_000),
        },
      }),
    ).toBe('idle');
  });

  it('becomes undeclared exactly ON the boundary, so the grace is a minimum and not a gap', () => {
    expect(status(roster(), finishedAt(StalledBadgeGrace))).toBe('undeclared');
    expect(status(roster(), finishedAt(StalledBadgeGrace - 1))).toBe('idle');
  });

  it('says nothing rather than shouting when an instant cannot be read', () => {
    // The gate cannot be applied without a clock reading, and of the two ways to be wrong the
    // over-eager one is the one the grace period exists to prevent.
    expect(
      status(roster(), {
        workflowOpen: true,
        lastTerminal: {
          Manager: { event: 'completed', occurredAt: 'not a date' },
          Writer: settled('completed'),
        },
      }),
    ).toBe('idle');
  });

  it('states the grace ONCE, as two minutes, and it is NOT the sweep window', () => {
    // The sweep's 30 minutes is how long a condition must persist before it is worth writing
    // down FOREVER; this is how long before it is worth SHOWING. Same shape, different reasons, and
    // a shared constant would hide that. If this ever equals 30 minutes, somebody has merged them.
    expect(StalledBadgeGrace).toBe(2 * 60 * 1000);
    expect(StalledBadgeGrace).not.toBe(30 * 60 * 1000);
  });
});


describe('teamStatus — ranking', () => {
  it('FAILED outranks UNDECLARED when both apply', () => {
    // Both present: an open workflow, an idle roster with empty queues, and one member whose last
    // run the PLATFORM failed. A failure is a louder fact than a silence.
    expect(
      status(
        [container({ id: 'Manager', name: 'Manager' }), container({ id: 'Writer', name: 'Writer' })],
        { workflowOpen: true, lastTerminal: { Manager: settled('completed'), Writer: settled('failed') } },
      ),
    ).toBe('failed');
  });

  it('UNDECLARED outranks idle, which is the whole point of the state', () => {
    expect(
      status([container({ id: 'Manager', name: 'Manager' })], {
        workflowOpen: true,
        lastTerminal: { Manager: settled('completed') },
      }),
    ).toBe('undeclared');
  });

  it('FAILED outranks blocked, because one is recoverable by the team and the other is not', () => {
    expect(
      status(
        [
          container({ id: 'Manager', name: 'Manager', blocked: 'gave up' }),
          container({ id: 'Writer', name: 'Writer' }),
        ],
        { workflowOpen: true, lastTerminal: { Writer: settled('failed') } },
      ),
    ).toBe('failed');
  });

  it('misconfigured still outranks failed', () => {
    expect(
      status([container({ id: 'Manager', name: 'Manager', missingAgent: 'gone' })], {
        workflowOpen: true,
        lastTerminal: { Manager: settled('failed') },
      }),
    ).toBe('misconfigured');
  });
});

describe('teamLogFacts', () => {
  const roster = (): ContainerSnapshot[] => [
    container({ id: 'Manager', name: 'Manager' }),
    container({ id: 'Writer', name: 'Writer' }),
  ];

  let seq = 0;
  const message = (
    type: string,
    source: string,
    correlationId = 7,
    occurredAt = '2026-09-03T09:00:00Z',
  ): Message => ({
    seq: (seq += 1),
    type,
    payload: '{}',
    source,
    correlationId,
    causationSeq: null,
    depth: 0,
    occurredAt,
  });

  it('reads no open workflow from an empty feed, so nothing downstream can invent one', () => {
    expect(teamLogFacts([])).toEqual({ workflowOpen: false, lastTerminal: {} });
  });

  it('calls the newest workflow open until a `workflow.completed` carries its correlation', () => {
    const rows = [
      message('agentContainer.completed', 'Alpha/Manager', 7),
      message('workflow.completed', 'Alpha/Manager', 7),
    ];

    expect(teamLogFacts(rows).workflowOpen).toBe(false);
  });

  /**
   * `workflow.closed` IS TERMINAL HERE TOO, since `/api/messages` carries the row. This reader is
   * the tab
   * chip's producer while the server's projection is the table's, and the two are documented as
   * ONE RULE WITH TWO PRODUCERS: with only `workflow.completed` recognised here, a person who
   * closed a workflow would watch the table say `ended, Closed` while the tab chip beside it went
   * on calling the same workflow open - a routine disagreement, which is what makes a real one
   * invisible.
   */
  it('calls a workflow closed by a person closed too, not merely a declared one', () => {
    const rows = [
      message('agentContainer.completed', 'Alpha/Manager', 7),
      message('workflow.closed', 'someone', 7),
    ];

    expect(teamLogFacts(rows).workflowOpen).toBe(false);
  });

  /**
   * CROSS-READER. Partner: `tests/Harness.Host.Tests/QuietSweepTests.cs`,
   * `A_team_that_declares_and_then_finishes_is_not_quiet`, which carries the matching comment
   * pointing back here. ONE scenario driven through both implementations of the same condition,
   * extended rather than duplicated — one half for declaration, one for age.
   *
   * **The two readers agree on the CONDITION and not on the window VALUE.** The sweep's thirty
   * minutes and this badge's {@link StalledBadgeGrace} are different
   * numbers for different reasons, so the ages below — ten seconds and two hours — are chosen to
   * sit clear of both: no window between two minutes and thirty could make the readers disagree.
   */
  it('agrees with the server sweep: a declared workflow is closed, and an open one is quiet only once it is OLD', () => {
    // The declaration half. Declared and then finished on the same correlation — the workflow is closed, and
    // a closed workflow is never quiet however long the silence runs.
    const declared = [
      message('workflow.completed', 'Alpha/Manager', 8),
      message('agentContainer.completed', 'Alpha/Manager', 8),
    ];

    expect(teamLogFacts(declared).workflowOpen).toBe(false);
    expect(status(roster(), teamLogFacts(declared), NOW)).toBe('idle');

    // The age half, and the scenario is fixed so both readers answer without either guessing: an
    // OPEN workflow, an idle roster, every queue empty, and every member's last terminal event a
    // `container.completed`. Only the age of the newest row differs.
    const open = (occurredAt: string) => [
      message('agentContainer.completed', 'Alpha/Manager', 9, occurredAt),
      message('agentContainer.completed', 'Alpha/Writer', 9, occurredAt),
    ];

    // Ten seconds before now — not undeclared here, not reported there. A
    // team that has just finished an errand is not a team that has stopped.
    expect(status(roster(), teamLogFacts(open(at(10_000))), NOW)).toBe('idle');

    // Two hours before now — undeclared here, reported there. The finding the gate must not silence.
    expect(status(roster(), teamLogFacts(open(at(2 * 60 * 60_000))), NOW)).toBe('undeclared');
  });

  it('calls it open when the completion belongs to an EARLIER workflow', () => {
    // The one that matters: a team that finished job 7 and started job 8 is not covered by job
    // 7's completion, and reading "is there any workflow.completed at all" would say it was.
    const rows = [
      message('agentContainer.completed', 'Alpha/Manager', 7),
      message('workflow.completed', 'Alpha/Manager', 7),
      message('agentContainer.completed', 'Alpha/Manager', 8),
    ];

    expect(teamLogFacts(rows).workflowOpen).toBe(true);
  });

  it('keeps each member’s LAST terminal event, by seq and not by array order', () => {
    // `activity` is stored newest-first, so an implementation that trusted the order it was
    // handed would read the oldest row as the latest fact.
    const older = message('agentContainer.completed', 'Alpha/Writer', 9);
    const newer = message('agentContainer.needsDecision', 'Alpha/Writer', 9);

    expect(teamLogFacts([newer, older]).lastTerminal).toEqual({
      Writer: { event: 'needs-decision', occurredAt: '2026-09-03T09:00:00Z' },
    });
  });

  it('carries the instant of that LAST row, which is the fact the grace period is measured against', () => {
    // A fold that kept `{ seq, event }` and emitted only the event would throw away the timestamp
    // the board already holds, and no age gate would be possible client-side. An
    // implementation that kept the FIRST row's instant would grace a team by however long its
    // oldest row was old, which is the opposite of the question.
    const older = message('agentContainer.completed', 'Alpha/Writer', 9, '2026-09-03T09:00:00Z');
    const newer = message('agentContainer.completed', 'Alpha/Writer', 9, '2026-09-03T11:59:30Z');

    expect(teamLogFacts([newer, older]).lastTerminal).toEqual({
      Writer: { event: 'completed', occurredAt: '2026-09-03T11:59:30Z' },
    });
  });

  it('ignores rows that are not terminal facts, `progress` above all', () => {
    const rows = [
      message('agentContainer.completed', 'Alpha/Writer', 9),
      message('agentContainer.progress', 'Alpha/Writer', 9),
      message('agentContainer.started', 'Alpha/Writer', 9),
    ];

    expect(teamLogFacts(rows).lastTerminal).toEqual({
      Writer: { event: 'completed', occurredAt: '2026-09-03T09:00:00Z' },
    });
  });

  it('keys members by the bare id, which is what a snapshot carries', () => {
    expect(teamLogFacts([message('agentContainer.failed', 'Alpha/Manager', 3)]).lastTerminal).toEqual({
      Manager: { event: 'failed', occurredAt: '2026-09-03T09:00:00Z' },
    });
  });

  /**
   * SCOPED TO THE NEWEST WORKFLOW, which is the SHARED rule rather than this reader's preference.
   *
   * A member that failed in workflow 7 and has not run in workflow 8 is not a member this workflow
   * failed on. Keeping it regardless of correlation would make the tab chip read FAILED off the
   * messages while the table row beneath it read IDLE off the server's projection - which answers
   * `MAX(correlation_id)` and nothing else.
   */
  it('drops a terminal fact from a PREVIOUS workflow, which is what the server does', () => {
    const rows = [
      message('agentContainer.failed', 'Alpha/Writer', 7),
      message('agentContainer.completed', 'Alpha/Manager', 8),
    ];

    expect(teamLogFacts(rows).lastTerminal).toEqual({
      Manager: { event: 'completed', occurredAt: '2026-09-03T09:00:00Z' },
    });
  });

  /**
   * THE OTHER HALF OF THE SAME RULE, and it is what stops the scoping being written as "drop
   * everything but the newest ROW". A member that failed in this workflow stays failed however
   * many siblings have since finished in it.
   */
  it('keeps a terminal fact from the newest workflow beside a later sibling', () => {
    const rows = [
      message('agentContainer.failed', 'Alpha/Writer', 8),
      message('agentContainer.completed', 'Alpha/Manager', 8),
    ];

    expect(teamLogFacts(rows).lastTerminal['Writer']?.event).toBe('failed');
  });

  /**
   * AN ABSENCE OF CORRELATION IS NOT EVIDENCE. Nothing in the running system publishes a row with
   * no correlation - a root settles its own seq into the column - but a filter that emptied
   * `lastTerminal` when there is no workflow to scope TO would be this file's own defect, and it
   * would present as a permanently idle team rather than as anything failing.
   */
  it('scopes nothing when no row in the window carries a correlation', () => {
    expect(teamLogFacts([message('agentContainer.failed', 'Alpha/Writer', 0)]).lastTerminal['Writer']
      ?.event).toBe('failed');
  });
});

describe('teamLogFromTiming', () => {
  /** A timing with sensible defaults - a team that has run and is idle. */
  const timing = (overrides: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming => ({
    available: true,
    correlation: 12,
    state: 'Running',
    startedAt: '2026-09-07T09:00:00Z',
    endedAt: null,
    serverNow: '2026-09-07T09:30:00Z',
    executionSeconds: 60,
    partial: false,
    runsCounted: 1,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [{ member: 'Manager', runs: 1, executionSeconds: 60, unfinished: 0 }],
    lastActivityAt: '2026-09-07T09:10:00Z',
    awaitingFrom: null,
    subject: null,
    pausedAt: null,
    pausedReason: null,
    pausedLimit: null,
    ...overrides,
  });

  it('answers null for a team that has no timing yet', () => {
    expect(teamLogFromTiming(null)).toBeNull();
  });

  it('reads a declared workflow as closed', () => {
    const log = teamLogFromTiming(timing({ endedAt: '2026-09-07T09:20:00Z' }))!;

    expect(log.workflowOpen).toBe(false);
  });

  it('reads an undeclared workflow as open, which is the ordinary case', () => {
    expect(teamLogFromTiming(timing())!.workflowOpen).toBe(true);
  });

  it('carries each failed member as a failed terminal', () => {
    const log = teamLogFromTiming(timing({ failedMembers: ['Worker'] }))!;

    expect(log.lastTerminal['Worker']?.event).toBe('failed');
  });

  it('carries the blocked member and the one awaiting a decision', () => {
    const log = teamLogFromTiming(timing({ blockedBy: 'Worker', awaitingFrom: 'Manager' }))!;

    expect(log.lastTerminal['Worker']?.event).toBe('blocked');
    expect(log.lastTerminal['Manager']?.event).toBe('needs-decision');
  });

  it('reads an unmarked member that ran as completed', () => {
    const log = teamLogFromTiming(timing())!;

    expect(log.lastTerminal['Manager']?.event).toBe('completed');
  });

  /**
   * THE FALLBACK CHAIN. It is
   * `lastActivityAt ?? startedAt ?? ''`, and each arm is a payload the server really sends: a
   * workflow whose only row so far is the instruction that began it carries a start and no
   * activity stamp at all.
   */
  it('stamps its terminals with startedAt when there is no activity stamp', () => {
    const log = teamLogFromTiming(timing({ lastActivityAt: null }))!;

    expect(log.lastTerminal['Manager']?.occurredAt).toBe('2026-09-07T09:00:00Z');
  });

  /**
   * AND THE LAST ARM SILENCES THE BADGE RATHER THAN SHOUTING IT. `''` is not an instant
   * `Date.parse` can read, and `isUndeclared` answers false on one it cannot - so a payload carrying
   * neither stamp leaves the team IDLE rather than UNDECLARED. Of the two ways to be wrong here the
   * over-eager one is the one the grace period exists to prevent, so a fact that is simply MISSING
   * must never be the thing that fires the mark.
   *
   * Asserted through `teamStatus` as well as on the stamp, because "the adapter wrote an empty
   * string" and "the badge stayed quiet" are two claims and only the second one is the behaviour.
   */
  it('silences the badge when the payload carries no readable instant at all', () => {
    const log = teamLogFromTiming(timing({ lastActivityAt: null, startedAt: null }))!;

    expect(log.lastTerminal['Manager']?.occurredAt).toBe('');
    expect(teamStatus([container({ id: 'Manager', name: 'Manager' })], log, NOW)).toBe('idle');
  });

  it('never claims a member ran when it has no runs', () => {
    const log = teamLogFromTiming(
      timing({ members: [{ member: 'Idle', runs: 0, executionSeconds: null, unfinished: 0 }] }),
    )!;

    expect(log.lastTerminal['Idle']).toBeUndefined();
  });

  /**
   * A run with no terminal partner means nobody can say which member finished, so the adapter
   * claims NO completions and `isUndeclared` finds nothing that ran. Of the two ways to be wrong, the
   * over-eager one is the one the grace period exists to prevent.
   */
  it('claims no completions at all when a run is unfinished', () => {
    const log = teamLogFromTiming(timing({ runsUnfinished: 1 }))!;

    expect(log.lastTerminal['Manager']).toBeUndefined();
  });

  /**
   * A single completed row, built by hand rather than reusing `teamLogFacts`' own `message`
   * fixture — that one is scoped to the describe above and auto-increments `seq`, which this one
   * test does not need.
   */
  const completedRow = (occurredAt: string): Message => ({
    seq: 1,
    type: 'agentContainer.completed',
    payload: '{}',
    source: 'Alpha/Manager',
    correlationId: 7,
    causationSeq: null,
    depth: 0,
    occurredAt,
  });

  /**
   * THE TWO-CORRELATION CROSS-READER TEST, and it is the one that would have caught the two
   * producers scoping terminals differently.
   *
   * A team FAILED in workflow 7 and has since started and finished workflow 8. The server's
   * projection is scoped to `MAX(correlation_id)` and reports no failure; `teamLogFacts` read
   * every terminal row in the client's window regardless of correlation, so the tab chip said
   * FAILED while the table row beneath it said IDLE - both on screen at once, from what is
   * documented as ONE rule with two producers. The single-correlation test below cannot see it.
   *
   * TWO MEMBERS, deliberately: with one member the newest row is the workflow-8 completion either
   * way, so the scoping makes no difference and the test would pass against the defect.
   */
  it('agrees with teamLogFacts when a PREVIOUS workflow failed and this one did not', () => {
    const activityAt = at(10_000);
    const row = (type: string, source: string, seq: number, correlationId: number): Message => ({
      seq,
      type,
      payload: '{}',
      source,
      correlationId,
      causationSeq: null,
      depth: 0,
      occurredAt: activityAt,
    });

    const containers = [
      container({ id: 'Manager', state: 'Idle', queueDepth: 0 }),
      container({ id: 'Writer', state: 'Idle', queueDepth: 0 }),
    ];

    const fromMessages = teamStatus(
      containers,
      teamLogFacts([
        row('agentContainer.failed', 'Alpha/Writer', 1, 7),
        row('agentContainer.completed', 'Alpha/Manager', 2, 8),
      ]),
      NOW,
    );

    // What the server answers for the same team: correlation 8, nothing failed in it, and only
    // the Manager ran.
    const fromTiming = teamStatus(
      containers,
      teamLogFromTiming(timing({
        correlation: 8,
        failedMembers: [],
        runsCounted: 1,
        runsUnfinished: 0,
        members: [{ member: 'Manager', runs: 1, executionSeconds: 60, unfinished: 0 }],
        lastActivityAt: activityAt,
      })),
      NOW,
    );

    expect(fromMessages).toBe(fromTiming);

    // NAMED, not merely equal: two readers that both said FAILED would also be equal, and the
    // whole point is that a failure in a previous workflow is not this workflow's failure.
    expect(fromMessages).toBe('idle');
  });

  /**
   * `endedAt` IS READ LOOSELY, THE SAME WAY `lastWorkflowText` READS IT. An absent field on the
   * wire arrives as `undefined`, which is not `null` - and a strict test here beside the loose one
   * there makes one column of a row say the workflow is open while the column next to it says a
   * manager declared it finished.
   */
  it('treats an absent endedAt as open, exactly like a null one', () => {
    const { endedAt: _removed, ...withoutEndedAt } = timing();

    expect(teamLogFromTiming(withoutEndedAt as TeamWorkflowTiming)!.workflowOpen).toBe(true);
  });

  /** THE CROSS-READER TEST. Both producers exist for a team whose tab is open; they must agree. */
  it('agrees with teamLogFacts about a quiet finished team', () => {
    // Well past `StalledBadgeGrace` (five times its own width, the file's usual margin), so the
    // scenario cannot come out differently on account of exactly where the grace boundary sits.
    const activityAt = at(StalledBadgeGrace * 5);
    const containers = [container({ id: 'Manager', state: 'Idle', queueDepth: 0 })];

    const fromMessages = teamStatus(containers, teamLogFacts([completedRow(activityAt)]), NOW);
    const fromTiming = teamStatus(
      containers,
      teamLogFromTiming(timing({ lastActivityAt: activityAt })),
      NOW,
    );

    expect(fromTiming).toBe(fromMessages);
    expect(fromTiming).toBe('undeclared');
  });
});

describe('tokenUsageFromTotals', () => {
  it('is unavailable, never zero, while the log total has not loaded', () => {
    const tokens = tokenUsageFromTotals(null);

    expect(tokens.available).toBe(false);
    if (tokens.available) throw new Error('expected unavailable');
    expect(tokens.missing).toMatch(/message log/i);
  });

  it('is unavailable when every run on the team predates capture, and names the missing keys', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        available: false,
        runsWithoutUsage: 3,
        missing: 'Completed and failed payloads on the message log do not carry tokensIn/tokensOut',
      }),
    );

    expect(tokens.available).toBe(false);
    if (tokens.available) throw new Error('expected unavailable');
    expect(tokens.missing).toMatch(/tokensIn/);
  });

  it('shows a true zero when the team has no completed or failed runs', () => {
    const tokens = tokenUsageFromTotals(totals());

    expect(tokens.available).toBe(true);
    if (!tokens.available) throw new Error('expected available');
    expect(tokens.input).toBe(0);
    expect(tokens.output).toBe(0);
    expect(tokens.partial).toBe(false);
    expect(tokens.claim).toBe('team total');
  });

  it('breaks totals down by agent brand then per member', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        tokensIn: 30,
        tokensOut: 12,
        partial: false,
        runsWithUsage: 3,
        members: [
          { member: 'Manager', brand: 'echo', tokensIn: 10, tokensOut: 4, tokensCachedIn: 100, tokensCacheCreation: 8, tokensBillable: 34 },
          { member: 'Writer', brand: 'grok-headless', tokensIn: 15, tokensOut: 6, tokensCachedIn: 0, tokensCacheCreation: 0, tokensBillable: 21 },
          { member: 'Reviewer', brand: 'echo', tokensIn: 5, tokensOut: 2, tokensCachedIn: 20, tokensCacheCreation: 0, tokensBillable: 9 },
        ],
      }),
    );

    expect(tokens.available).toBe(true);
    if (!tokens.available) throw new Error('expected available');
    expect(tokens.input).toBe(30);
    expect(tokens.output).toBe(12);
    expect(tokens.partial).toBe(false);
    expect(tokens.claim).toBe('team total');
    expect(tokens.byBrand).toEqual([
      { brand: 'echo', input: 15, cachedIn: 120, cacheCreation: 8, output: 6, billable: 43, note: null },
      { brand: 'grok-headless', input: 15, cachedIn: 0, cacheCreation: 0, output: 6, billable: 21, note: null },
    ]);
    expect(tokens.byMember).toHaveLength(3);
  });

  it('says partial when some runs have usage and earlier ones do not', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        tokensIn: 8,
        tokensOut: 2,
        partial: true,
        runsWithUsage: 1,
        runsWithoutUsage: 4,
        members: [{ member: 'Manager', brand: 'echo', tokensIn: 8, tokensOut: 2 }],
      }),
    );

    expect(tokens.available).toBe(true);
    if (!tokens.available) throw new Error('expected available');
    expect(tokens.partial).toBe(true);
    expect(tokens.claim).toBe('team total');
  });

  it('keeps unknown member totals as null, and unknown by-brand totals as null too', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        tokensIn: 15,
        tokensOut: 6,
        partial: true,
        members: [
          { member: 'Measured', brand: 'grok-headless', tokensIn: 15, tokensOut: 6 },
          { member: 'UnknownOne', brand: 'grok-headless', tokensIn: null, tokensOut: null },
        ],
      }),
    );

    expect(tokens.available).toBe(true);
    if (!tokens.available) throw new Error('expected available');
    // NO NOTE ON EITHER ROW, and this fixture is the reason the rule reads `=== 0` and
    // `=== false` rather than defaulting. It states neither `runs` nor `brandReportsUsage` - a
    // shape a server may send - so nothing here is known about WHY `UnknownOne` has no figure,
    // and `(unknown)` stays the honest answer. A rule that defaulted would caption both rows,
    // including the one carrying real numbers.
    expect(tokens.byMember).toEqual([
      {
        member: 'Measured',
        brand: 'grok-headless',
        input: 15,
        cachedIn: null,
        cacheCreation: null,
        output: 6,
        billable: null,
        runs: undefined,
        tokensTotal: null,
        note: null,
      },
      {
        member: 'UnknownOne',
        brand: 'grok-headless',
        input: null,
        cachedIn: null,
        cacheCreation: null,
        output: null,
        billable: null,
        runs: undefined,
        tokensTotal: null,
        note: null,
      },
    ]);
    expect(tokens.byBrand).toEqual([
      {
        brand: 'grok-headless',
        input: null,
        cachedIn: null,
        cacheCreation: null,
        output: null,
        billable: null,
        note: null,
      },
    ]);
    expect(tokens.claim).toBe('team total');
  });

  /**
   * AN AGENT THAT REPORTS NO USAGE AT ALL IS NOT THE SAME AS A MISSING NUMBER, and the dialog
   * rendered both as `(unknown) in - (unknown) out`. `codex-headless` carries no usage format, so
   * its runs will never carry a figure however many of them there are; `(unknown)` there is the
   * permanent answer rather than a gap, and a reader deserves to be told which one they are
   * looking at.
   */
  it('says why a member has no numbers when its Agent reports none', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        tokensIn: 15,
        tokensOut: 6,
        partial: true,
        members: [
          {
            member: 'Measured',
            brand: 'grok-headless',
            tokensIn: 15,
            tokensOut: 6,
            brandReportsUsage: true,
            runs: 1,
          },
          {
            member: 'TesterTessa',
            brand: 'codex-headless',
            tokensIn: null,
            tokensOut: null,
            brandReportsUsage: false,
            runs: 2,
          },
        ],
      }),
    );

    if (!tokens.available) throw new Error('expected available');

    expect(tokens.byMember[0]!.note).toBeNull();
    expect(tokens.byMember[1]!.note).toBe('this Agent reports no usage');
  });

  /**
   * A COMBINED TOTAL BEATS `(unknown)`, and it must not pretend to be a split. codex reports one
   * figure for a run - `tokens used 238,783` - and says nothing about which way they went, so the
   * row shows what was actually reported and labels it as a total.
   *
   * It OUTRANKS the "reports no usage" note, which is only true of a brand that reports
   * nothing at all: once the figure is there, saying nothing was reported is simply false.
   */
  it('shows a combined total where that is all the Agent reports', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        members: [
          {
            member: 'TesterTessa',
            brand: 'codex-headless',
            tokensIn: null,
            tokensOut: null,
            tokensTotal: 238783,
            brandReportsUsage: true,
            runs: 1,
          },
        ],
      }),
    );

    if (!tokens.available) throw new Error('expected available');

    expect(tokens.byMember[0]!.note).toBe('238,783 total');
    expect(tokens.byMember[0]!.input).toBeNull();
    expect(tokens.byMember[0]!.output).toBeNull();
  });

  /** The gap that may still close keeps today's `(unknown)`, and must not be labelled as a brand
   *  that cannot answer: the next run on it may well carry a figure. */
  it('leaves a missing number unexplained when the Agent does report usage', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        members: [
          {
            member: 'Claudia',
            brand: 'claude-headless',
            tokensIn: null,
            tokensOut: null,
            brandReportsUsage: true,
            runs: 1,
          },
        ],
      }),
    );

    if (!tokens.available) throw new Error('expected available');
    expect(tokens.byMember[0]!.note).toBeNull();
  });

  /**
   * THE SAME CONFUSION ONE LINE HIGHER. `By agent brand` must not read
   * `codex-headless  (unknown) in - (unknown) out` directly above `TesterTessa - codex-headless
   * this Agent reports no usage` - two renderings of one fact, a line apart, and the vaguer one
   * first.
   *
   * The flag is a property of the AGENT, and every member on a brand row runs that same Agent, so
   * the brand answer is the member answer. No separate derivation.
   */
  it('says why a brand has no numbers when the Agent reports none', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        members: [
          {
            member: 'Tessa',
            brand: 'codex-headless',
            tokensIn: null,
            tokensOut: null,
            brandReportsUsage: false,
            runs: 2,
          },
          {
            member: 'Dex',
            brand: 'copilot-headless',
            tokensIn: 10,
            tokensOut: 4,
            brandReportsUsage: true,
            runs: 1,
          },
        ],
      }),
    );

    if (!tokens.available) throw new Error('expected available');

    const codex = tokens.byBrand.find((row) => row.brand === 'codex-headless')!;
    const copilot = tokens.byBrand.find((row) => row.brand === 'copilot-headless')!;

    expect(codex.note).toBe('this Agent reports no usage');
    expect(copilot.note).toBeNull();
  });

  /** A member mid-first-run has finished nothing. Absent from the list it reads as "nothing to
   *  report about this member"; the truth is "nothing yet". */
  it('separates a member that has not finished a run from one with nothing to report', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        members: [
          {
            member: 'Fresh',
            brand: 'codex-headless',
            tokensIn: null,
            tokensOut: null,
            brandReportsUsage: false,
            runs: 0,
          },
        ],
      }),
    );

    if (!tokens.available) throw new Error('expected available');
    expect(tokens.byMember[0]!.note).toBe('no completed run yet');
  });

  /**
   * THE REGRESSION LISTING EVERY MEMBER WOULD OTHERWISE CAUSE. A brand total goes null the moment
   * one of its members has a null figure - that is how an unmeasured run refuses to be summed as a
   * zero. A member that has run NOTHING has no figure either, so hiring one onto a measured brand
   * would blank that brand's total: real spend replaced by `(unknown)` because somebody was hired.
   *
   * A row with no runs contributes nothing, which is also just the right arithmetic.
   */
  it('does not let a member with no runs blank its brand total', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        tokensIn: 15,
        tokensOut: 6,
        members: [
          {
            member: 'Measured',
            brand: 'claude-headless',
            tokensIn: 15,
            tokensOut: 6,
            tokensCachedIn: 40,
            tokensCacheCreation: 4,
            tokensBillable: 30,
            brandReportsUsage: true,
            runs: 1,
          },
          {
            member: 'Fresh',
            brand: 'claude-headless',
            tokensIn: null,
            tokensOut: null,
            tokensCachedIn: null,
            tokensCacheCreation: null,
            tokensBillable: null,
            brandReportsUsage: true,
            runs: 0,
          },
        ],
      }),
    );

    if (!tokens.available) throw new Error('expected available');
    expect(tokens.byBrand).toEqual([
      {
        brand: 'claude-headless',
        input: 15,
        cachedIn: 40,
        cacheCreation: 4,
        output: 6,
        billable: 30,
        note: null,
      },
    ]);
  });

  /**
   * REAL-SCALE FIGURES. tokensIn is uncached input only, so on its own the tile would read
   * a few thousand tokens for a team that read ~76M from the cache. Billable is the server's
   * figure and is passed through, never recomputed here.
   */
  it('carries cache reads, cache writes and the server billable figure beside tokensIn', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        tokensIn: 2_142,
        tokensOut: 577_066,
        tokensCachedIn: 76_466_631,
        tokensCacheCreation: 2_393_199,
        tokensBillable: 11_217_369,
        runsWithUsage: 12,
        members: [
          {
            member: 'Manager',
            brand: 'claude-headless',
            tokensIn: 2_000,
            tokensOut: 500_000,
            tokensCachedIn: 70_000_000,
            tokensCacheCreation: 2_000_000,
            tokensBillable: 10_002_000,
            tokensTotal: null,
            brandReportsUsage: true,
            runs: 10,
          },
          {
            member: 'Writer',
            brand: 'claude-headless',
            tokensIn: 142,
            tokensOut: 77_066,
            tokensCachedIn: 6_466_631,
            tokensCacheCreation: 393_199,
            tokensBillable: 1_215_369,
            tokensTotal: null,
            brandReportsUsage: true,
            runs: 2,
          },
          {
            member: 'Tessa',
            brand: 'codex-headless',
            tokensIn: null,
            tokensOut: null,
            tokensCachedIn: null,
            tokensCacheCreation: null,
            tokensBillable: 9_000,
            tokensTotal: 9_000,
            brandReportsUsage: true,
            runs: 1,
          },
        ],
      }),
    );

    if (!tokens.available) throw new Error('expected available');

    expect(tokens.input).toBe(2_142);
    expect(tokens.cachedIn).toBe(76_466_631);
    expect(tokens.cacheCreation).toBe(2_393_199);
    expect(tokens.output).toBe(577_066);
    expect(tokens.billable).toBe(11_217_369);

    expect(tokens.byMember[0]).toMatchObject({
      input: 2_000,
      cachedIn: 70_000_000,
      cacheCreation: 2_000_000,
      output: 500_000,
      billable: 10_002_000,
    });
    expect(tokens.byMember[2]).toMatchObject({ cachedIn: null, cacheCreation: null, billable: 9_000 });

    expect(tokens.byBrand).toEqual([
      {
        brand: 'claude-headless',
        input: 2_142,
        cachedIn: 76_466_631,
        cacheCreation: 2_393_199,
        output: 577_066,
        billable: 11_217_369,
        note: null,
      },
      {
        brand: 'codex-headless',
        input: null,
        cachedIn: null,
        cacheCreation: null,
        output: null,
        billable: 9_000,
        note: '9,000 total',
      },
    ]);
  });

  /** A member row with no cache or billable keys: unknown, never zero. */
  it('reads absent cache and billable figures as unknown', () => {
    const tokens = tokenUsageFromTotals(
      totals({
        tokensIn: 15,
        tokensOut: 6,
        members: [{ member: 'Old', brand: 'claude-headless', tokensIn: 15, tokensOut: 6, runs: 1 }],
      }),
    );

    if (!tokens.available) throw new Error('expected available');
    expect(tokens.byMember[0]).toMatchObject({ cachedIn: null, cacheCreation: null, billable: null });
    expect(tokens.byBrand[0]).toMatchObject({ input: 15, cachedIn: null, billable: null });
  });
});


/**
 * The workflow projection as it arrives over HTTP. `serverNow` is stated at {@link NOW}, so every
 * case below reads as if the browser's clock agreed with the server's — the skew case states its
 * own.
 */
const workflow = (overrides: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming => ({
  available: true,
  correlation: 42,
  state: 'Running',
  startedAt: at(64 * 60 * 1000),
  endedAt: null,
  serverNow: at(0),
  executionSeconds: 0,
  partial: false,
  runsCounted: 0,
  runsUnfinished: 0,
  blockedBy: null,
  runsFailed: 0,
  failedMembers: [],
  missing: null,
  members: [],
  lastActivityAt: at(StalledBadgeGrace * 5),
  awaitingFrom: null,
  subject: null,
  pausedAt: null,
  pausedReason: null,
  pausedLimit: null,
  ...overrides,
});

/** The tile at {@link NOW}, so no case here depends on the wall clock. */
const tile = (
  timing: TeamWorkflowTiming | null,
  containers: ContainerSnapshot[] = [container()],
  now = NOW,
) => workflowTiming(timing, containers, now);

describe('workflowTiming', () => {
  it('renders an em dash for a team that has never run, and never a zero', () => {
    const rendered = tile(workflow({ available: false, startedAt: null, state: null }));

    expect(rendered.available).toBe(false);
    expect(rendered.elapsed).toBe('—');
    expect(rendered.elapsedMs).toBeNull();
    expect(rendered.state).toBeNull();
    expect(rendered.ticking).toBe(false);
  });

  it('renders an em dash before the first fetch lands, which is unavailable rather than zero', () => {
    const rendered = tile(null);

    expect(rendered.available).toBe(false);
    expect(rendered.elapsed).toBe('—');
    expect(rendered.elapsedMs).toBeNull();
  });

  it('measures an open workflow from its root to now, and keeps ticking, WHILE SOMEBODY IS WORKING', () => {
    const rendered = tile(workflow(), [container({ state: 'Running' })]);

    expect(rendered.elapsedMs).toBe(64 * 60 * 1000);
    expect(rendered.elapsed).toBe('1h 04m');
    expect(rendered.ticking).toBe(true);
  });

  /**
   * TICKING MEANS SOMEBODY IS WORKING, AND NOTHING ELSE. A figure that advanced while every member
   * was idle would read as work in progress on a board where none was happening - an undeclared
   * team's clock could reach fifteen hours.
   *
   * IT FREEZES AT THE LAST ACTIVITY RATHER THAN AT `now`. Stopping at whatever instant the last
   * render happened to fall on would put an arbitrary number on screen that then JUMPS on the next
   * reload; the newest row is a fact both the server and every reader can agree on, so the figure is
   * stable across refreshes and means one thing: how long this workflow was ACTIVE for.
   */
  it('freezes the figure at the last activity when nobody is working', () => {
    const rendered = tile(workflow());

    expect(rendered.elapsedMs).toBe(64 * 60 * 1000 - StalledBadgeGrace * 5);
    expect(rendered.elapsed).toBe('54m 00s');
    expect(rendered.ticking).toBe(false);
  });

  /**
   * THE FIGURE DOES NOT MEAN "HOW LONG HAS THIS BEEN OPEN", SO SOMETHING ELSE HAS TO SAY IT.
   * A workflow open for fifteen hours is the fact that gets a person to look. It is said here, in the idiom COMPLETED already uses for `finished
   * 12m ago`, and only on the two states that mean "stuck and nobody is coming" — FAILED, BLOCKED
   * and AWAITING already name a member and an action.
   */
  it('says how long ago the team was last active, once the figure has stopped', () => {
    const rendered = tile(workflow({ state: 'Undeclared' }));

    expect(rendered.detail).toBe('no one is working · last active 10m ago');
  });

  it('freezes a closed workflow at its own end rather than following the clock', () => {
    const rendered = tile(
      workflow({
        state: 'Completed',
        startedAt: at(4 * 60 * 60 * 1000 + 17 * 60 * 1000 + 12 * 60 * 1000),
        endedAt: at(12 * 60 * 1000),
      }),
    );

    expect(rendered.state).toBe('COMPLETED');
    expect(rendered.elapsed).toBe('4h 17m');
    expect(rendered.ticking).toBe(false);
    expect(rendered.detail).toBe('finished 12m ago');
  });

  /**
   * A PERSON'S CLOSE GETS ITS OWN WORD, AND IT IS NOT `COMPLETED`. `workflow.closed` is
   * deliberately not `workflow.completed` because a person clicking Close is not declaring a
   * delivery, and folding the two together here would put back on the screen exactly the fiction
   * the server refuses to write on the log.
   *
   * `closed 12m ago`, NOT `finished 12m ago`, for the same reason at sentence level - and the tile
   * stops ticking, because a closed workflow has ENDED.
   */
  it('says CLOSED for a workflow a person ended, and never COMPLETED', () => {
    const rendered = tile(
      workflow({
        state: 'Closed',
        startedAt: at(4 * 60 * 60 * 1000 + 17 * 60 * 1000 + 12 * 60 * 1000),
        endedAt: at(12 * 60 * 1000),
      }),
    );

    expect(rendered.state).toBe('CLOSED');
    expect(rendered.elapsed).toBe('4h 17m');
    expect(rendered.ticking).toBe(false);
    expect(rendered.detail).toBe('closed 12m ago');

    // COMPLETED'S TONE, DELIBERATELY - terminal news rather than an alarm, and no new colour.
    expect(rendered.tone).toBe('none');
  });

  /**
   * A NEGATIVE DURATION IS THE VISIBLE-NONSENSE BUG `serverNow` EXISTS FOR, and it gets diagnosed
   * as a server fault. The caller applies the offset; this clamps whatever still gets through, so
   * the worst case is a figure that is briefly wrong rather than one that is impossible.
   */
  it('never renders a negative elapsed time', () => {
    const rendered = tile(workflow({ startedAt: at(-10 * 60 * 1000) }));

    expect(rendered.elapsedMs).toBe(0);
    expect(rendered.elapsed).toBe('0s');
  });

  it('says RUNNING while a member is Running, whatever the log last said', () => {
    const rendered = tile(workflow({ state: 'Undeclared' }), [container({ state: 'Running' })]);

    expect(rendered.state).toBe('RUNNING');
    expect(rendered.tone).toBe('live');
  });

  it('says RUNNING on queue depth alone, because accepted work is work', () => {
    const rendered = tile(workflow({ state: 'Undeclared' }), [container({ queueDepth: 3 })]);

    expect(rendered.state).toBe('RUNNING');
  });

  /**
   * FOUR RUNS FAILED ACROSS TWO MEMBERS, and the board must not say `IDLE`; both numbers belong on
   * the tile, because a count of members understates it and a count of runs does
   * not say who to go and look at.
   */
  it('says FAILED, with the run count and the members', () => {
    const rendered = tile(
      workflow({
        state: 'Failed',
        runsFailed: 4,
        failedMembers: ['Manager', 'TesterUlla'],
        startedAt: at(2 * 60 * 60 * 1000 + 25 * 60 * 1000),
        lastActivityAt: at(0),
      }),
    );

    expect(rendered.state).toBe('FAILED');
    expect(rendered.elapsed).toBe('2h 25m');
    expect(rendered.detail).toBe('4 runs failed · Manager, TesterUlla');
    expect(rendered.tone).toBe('error');
  });

  it('says FAILED even when a member also gave up, matching the chip', () => {
    const rendered = tile(
      workflow({ state: 'Failed', runsFailed: 1, failedMembers: ['Manager'], blockedBy: 'Scout' }),
    );

    expect(rendered.state).toBe('FAILED');
  });

  it('says BLOCKED and names who gave up', () => {
    const rendered = tile(
      workflow({
        state: 'Blocked',
        blockedBy: 'Scout',
        lastActivityAt: at(0),
        startedAt: at(38 * 60 * 1000 + 2000),
      }),
    );

    expect(rendered.state).toBe('BLOCKED');
    expect(rendered.elapsed).toBe('38m 02s');
    expect(rendered.detail).toBe('Scout gave up');
  });

  /**
   * THE ONE STATE THAT IS NOT A PROBLEM. It carries a name and an imperative rather than an
   * observation, and it is coloured as information: nothing went wrong.
   */
  it('says AWAITING and names who is waiting on you', () => {
    const rendered = tile(
      workflow({
        state: 'Awaiting',
        awaitingFrom: 'Manager',
        startedAt: at(9 * 60 * 1000 + 12_000),
        lastActivityAt: at(0),
      }),
    );

    expect(rendered.state).toBe('AWAITING');
    expect(rendered.elapsed).toBe('9m 12s');
    expect(rendered.detail).toBe('Manager is waiting on you');
    expect(rendered.tone).toBe('info');
  });

  it('says UNDECLARED once the team has been silent for the grace period', () => {
    const rendered = tile(workflow({ state: 'Undeclared' }));

    expect(rendered.state).toBe('UNDECLARED');
    expect(rendered.detail).toBe('no one is working · last active 10m ago');
  });

  /**
   * NOT COLOURED AS AN ERROR, and this is the assertion that stops somebody "fixing" it: an undeclared
   * team's runs SUCCEEDED and the work stopped anyway. A red tile sends a person to find out what
   * broke, and nothing did.
   */
  it('does not colour UNDECLARED as an error', () => {
    expect(tile(workflow({ state: 'Undeclared' })).tone).toBe('quiet');
  });

  /**
   * A BRAND-NEW TEAM THAT HAS JUST FINISHED ITS OWN SETUP IS NOT UNDECLARED. Every clause of the
   * condition holds and the verdict is still the wrong thing to tell a person - an errand was run
   * and finished. The same grace the tab chip waits out.
   */
  it('waits out the grace period before it will say UNDECLARED', () => {
    const rendered = tile(
      workflow({ state: 'Undeclared', lastActivityAt: at(StalledBadgeGrace / 2) }),
    );

    expect(rendered.state).toBeNull();
    expect(rendered.detail).toBeNull();

    // AND IT DOES NOT TICK EITHER. Nothing is working, so the figure is frozen even though the
    // board has not yet decided what to call the team.
    expect(rendered.ticking).toBe(false);
  });

  /**
   * THE LOG CAN DESCRIBE THIS CASE, precisely - an open workflow here is not one to say nothing
   * about.
   *
   * The projection answers `Running` if and only if NO member has published a terminal fact in
   * this correlation: the row that would clear `everyTerminalCompleted` is the same row that fills
   * `failedMembers`, `blockedBy` or `awaitingFrom`, and all three rank above `Undeclared` on the
   * server. So mixed facts always resolve to the louder word and never reach here — only an EMPTY
   * set does. Work began and nothing came back.
   */
  it('says NO RESULT when work began and nothing ever came back', () => {
    const rendered = tile(
      workflow({ state: 'Running', runsUnfinished: 1 }),
      [container()],
    );

    expect(rendered.state).toBe('NO RESULT');
    expect(rendered.detail).toBe('nothing has reported back · last active 10m ago');
  });

  /**
   * NOT `quiet`, WHICH IS UNDECLARED'S. That tone is earned by UNDECLARED's runs having SUCCEEDED, and
   * nothing here succeeded. Not `error` either — nothing has reported a failure. `warn` is what
   * BLOCKED uses and is the right neighbour: somebody needs to go and look.
   */
  it('colours NO RESULT as a warning, which UNDECLARED deliberately is not', () => {
    expect(tile(workflow({ state: 'Running' }), [container()]).tone).toBe('warn');
  });

  /**
   * A REJECTION REACHES THIS STATE WITH NO UNFINISHED RUN — work refused at the queue ceiling, and
   * nothing since. `0 runs never finished` would be a visible falsehood on the one line the tile
   * has to explain itself with.
   */
  it('says nothing came back rather than counting zero unfinished runs', () => {
    const rendered = tile(workflow({ state: 'Running', runsUnfinished: 0 }), [container()]);

    expect(rendered.detail).toBe('nothing has reported back · last active 10m ago');
  });

  it('never says NO RESULT while a member is working', () => {
    const rendered = tile(
      workflow({ state: 'Running', runsUnfinished: 1 }),
      [container({ state: 'Running' })],
    );

    expect(rendered.state).toBe('RUNNING');
  });

  /**
   * THE CLOSED SET. Every state the projection can answer must map to a word here. A server
   * state that no client arm names costs nothing at compile time, because `TeamWorkflowTiming.state` is a `string` by
   * design (an enum crosses two serialisers as a name and the SPA compares it).
   *
   * Every clause the CLIENT owns is settled here: a roster that is present but idle, no queue
   * depth, and a team silent well past the grace period. THE ONE PERMITTED NULL IS THE GRACE
   * WINDOW, pinned separately by `waits out the grace period before it will say UNDECLARED` — these
   * two tests have to be read together or this one looks stricter than it is.
   */
  it('maps every state the projection can answer to a word', () => {
    const projected = ['Failed', 'Blocked', 'Awaiting', 'Completed', 'Undeclared', 'Running'];

    for (const state of projected) {
      expect(tile(workflow({ state }), [container()]).state, state).not.toBeNull();
    }
  });

  /**
   * EXECUTION ALWAYS CARRIES ITS UNIT OF MEASURE IN WORDS, because it legitimately EXCEEDS the
   * elapsed figure on the tile whenever two members overlap - and side by side as two bare numbers
   * one of them reads as a bug.
   */
  it('states execution in words, and lets it exceed elapsed', () => {
    const rendered = tile(
      workflow({
        startedAt: at(60 * 60 * 1000),
        executionSeconds: 111 * 60,
        runsCounted: 4,
        members: [
          { member: 'Manager', runs: 1, executionSeconds: 52 * 60, unfinished: 0 },
          { member: 'Scout', runs: 3, executionSeconds: 59 * 60, unfinished: 0 },
        ],
      }),
    );

    expect(rendered.execution.total).toBe('1h 51m across 2 members');
    expect(rendered.execution.seconds * 1000).toBeGreaterThan(rendered.elapsedMs!);
  });

  it('renders a member with no finished run as unknown, never as zero', () => {
    const rendered = tile(
      workflow({
        runsUnfinished: 1,
        partial: true,
        members: [{ member: 'Manager', runs: 1, executionSeconds: null, unfinished: 1 }],
      }),
    );

    expect(rendered.execution.members[0]!.execution).toBe('(unknown)');
    expect(rendered.execution.total).toBe('no measured run time');
    expect(rendered.execution.unfinished).toBe(1);
  });

  /**
   * NO PERCENTAGE AND NO PROGRESS ANYWHERE. Nothing knows how much work remains, and a ratio of the
   * two durations is a concurrency figure nobody asked for that reads as progress.
   */
  it('derives no ratio between the two durations', () => {
    const rendered = tile(
      workflow({ executionSeconds: 111 * 60, runsCounted: 4 }),
    );

    expect(rendered.execution.total).not.toContain('%');
    expect(rendered.detail ?? '').not.toContain('%');
    expect(Object.keys(rendered)).not.toContain('percent');
    expect(Object.keys(rendered)).not.toContain('progress');
  });

  it('puts the three member counts on the click-target line', () => {
    const rendered = tile(workflow(), [
      container({ id: 'Manager', state: 'Running' }),
      container({ id: 'Scout', queueDepth: 2 }),
      container({ id: 'Ulla', blocked: 'gave up' }),
    ]);

    expect(rendered.members).toBe('1 working · 2 queued · 1 stopped');
  });
});

describe('containerMark', () => {
  it('shows nothing for a member whose last run left no mark', () => {
    expect(containerMark(container())).toBeNull();
  });

  /**
   * A HAND-BACK IS NOT A MARK. Everything this function returns is a call to action -
   * something a person has to clear - and a member that handed its finished work back needs nothing
   * done about it. Its card says so through its STATUS, which the board colours and labels as a
   * success. A fourth kind here would put a delivery in the row reserved for trouble and, since
   * `handedBack` never clears, would leave it there permanently.
   */
  it('shows nothing for a member that handed its finished work back', () => {
    expect(containerMark(container({ handedBack: 'card 4539' }))).toBeNull()
  })

  /**
   * AND IT DOES NOT SOFTEN A REAL ONE. `blocked` keeps meaning gave-up: a member that delivered an
   * earlier card and has since given up on this one still shows the give-up.
   */
  it('still shows a give-up on a member that handed something back earlier', () => {
    const member = container({ blocked: 'gave up', handedBack: 'an earlier card' })

    expect(containerMark(member)?.kind).toBe('blocked')
  })

  /**
   * THE CARD AND THE CHIP MUST AGREE. A platform failure outranks an agent's own decision to stop,
   * because one is recoverable by the team and the other is not - and if these two disagreed, a
   * chip and the card beneath it would say different things about the same member.
   */
  it('shows FAILED when a member gave up and then died, exactly as the chip does', () => {
    const member = container({ failed: 'the process died', blocked: 'gave up' });

    expect(containerMark(member)?.kind).toBe('failed');
    expect(containerMark(member)?.reason).toBe('the process died');

    expect(
      status([member], {
        workflowOpen: true,
        lastTerminal: { Manager: settled('failed') },
      }),
    ).toBe('failed');
  });

  it('shows BLOCKED when only the agent gave up', () => {
    expect(containerMark(container({ blocked: 'gave up' }))?.kind).toBe('blocked');
  });

  it('ranks a decision below both, so a run that died after asking reads as failed', () => {
    expect(
      containerMark(container({ needsDecision: 'Staging or production?' }))?.kind,
    ).toBe('needs-decision');

    expect(
      containerMark(container({ failed: 'died', needsDecision: 'Staging?' }))?.kind,
    ).toBe('failed');
  });
});

/**
 * `closeWorkflow`'s prefilled reason. The route's own comment names the mitigation this is: an
 * optional reason is acceptable only because a one-click submission still produces a row that
 * accounts for itself, and this is what the box starts with.
 */
describe('closeReasonPrefill', () => {
  /** THE WORKED EXAMPLE FROM THE BRIEF ITSELF: `closed as undeclared · last active 22h ago`. */
  it('replaces the verdict with "closed as <state>" and keeps the qualifying clause verbatim', () => {
    expect(
      closeReasonPrefill({ state: 'UNDECLARED', detail: 'no one is working · last active 22h ago' }),
    ).toBe('closed as undeclared · last active 22h ago');
  });

  it('lower-cases the state word', () => {
    expect(closeReasonPrefill({ state: 'NO RESULT', detail: null })).toBe('closed as no result');
  });

  it('keeps whatever follows the first " · ", not only a "last active" clause', () => {
    expect(
      closeReasonPrefill({ state: 'FAILED', detail: '2 runs failed · scout, writer' }),
    ).toBe('closed as failed · scout, writer');
  });

  it('drops the qualifier entirely when the detail carries none', () => {
    expect(closeReasonPrefill({ state: 'BLOCKED', detail: 'scout gave up' })).toBe('closed as blocked');
  });

  it('falls back to "closed as workflow" for a null state and no detail', () => {
    expect(closeReasonPrefill({ state: null, detail: null })).toBe('closed as workflow');
  });

  it('still names the state when there is no detail at all', () => {
    expect(closeReasonPrefill({ state: 'RUNNING', detail: null })).toBe('closed as running');
  });
});

/**
 * THE ACTIONS TABLE, STATE BY STATE — the whole of what the dialog may offer, in one place.
 *
 * `findings` IS NOT IN THIS SIGNATURE, which is the point rather than a tidy-up. Nudge must not
 * reach a terminal row at all, even when the team holds a live finding: it publishes an
 * instruction whose causation is that correlation, and `WorkflowOpenSql`'s `woke` subquery treats
 * exactly that as RE-OPENING the workflow. A Manager is then woken on already-delivered work.
 * Re-engaging finished work is the Concierge's job, and it has its own surface for it. Without the
 * parameter the exception cannot be introduced by someone passing an argument.
 *
 * CLOSE IS NOT ON THE RUNNING ROW for a different reason, and a server-side one: `POST
 * .../workflows/{correlation}/close` has no busy check, so a person could close a workflow whose
 * members were still spending. Stop first, then Close.
 *
 * ONE `toEqual` PER STATE, OVER THE WHOLE RECORD, so flipping any single cell reddens exactly the
 * row that was flipped and names it. A per-field assertion would let three of the four drift while
 * the fourth held the test green.
 */
describe('workflowRowActions', () => {
  /**
   * TEN ROWS: the nine words {@link WorkflowState} carries, and the null that is not a word.
   *
   * A NULL STATE IS NOT TERMINAL. It is an UNDECLARED row still inside its grace period, or a row
   * the vocabulary has no word for yet — unknown, never finished. Reading it as terminal would take
   * Close and Nudge away from a workflow that is genuinely open, which is the opposite defect.
   *
   * `resume` IS FALSE ON EVERY ROW HERE AND THAT IS THE POINT OF LEAVING IT IN THE TABLE. It is
   * decided by the PAUSE and not by the word — `workflowRowActions` is called with `paused` absent
   * throughout this table, and absent is not paused. A `PAUSED` row with the pause fact on it is
   * pinned in `workflowPaused.spec.ts`, beside the rule that Resume never replaces Nudge.
   */
  const table: ReadonlyArray<readonly [WorkflowState | null, WorkflowRowActions]> = [
    ['RUNNING',    { close: false, nudge: false, thread: true, stop: true,  resume: false }],
    ['COMPLETED',  { close: false, nudge: false, thread: true, stop: false, resume: false }],
    ['CLOSED',     { close: false, nudge: false, thread: true, stop: false, resume: false }],
    ['BLOCKED',    { close: true,  nudge: true,  thread: true, stop: false, resume: false }],
    ['FAILED',     { close: true,  nudge: true,  thread: true, stop: false, resume: false }],
    ['UNDECLARED', { close: true,  nudge: true,  thread: true, stop: false, resume: false }],
    ['AWAITING',   { close: true,  nudge: true,  thread: true, stop: false, resume: false }],
    ['NO RESULT',  { close: true,  nudge: true,  thread: true, stop: false, resume: false }],
    ['PAUSED',     { close: true,  nudge: true,  thread: true, stop: false, resume: false }],
    [null,         { close: true,  nudge: true,  thread: true, stop: false, resume: false }],
  ];

  it.each(table)('offers exactly the table row for state %s', (state, expected) => {
    expect(workflowRowActions({ state })).toEqual(expected);
  });

  /**
   * THE TABLE IS THE WHOLE VOCABULARY, and this is what keeps it that way. A ninth word added to
   * {@link WorkflowState} without a row here would otherwise be untested — the `it.each` above only
   * asserts the rows it was given, and says nothing about the ones it was not.
   */
  it('covers every state the vocabulary carries', () => {
    const covered = table.map(([state]) => state);
    const words: Array<WorkflowState | null> = [
      'RUNNING', 'COMPLETED', 'CLOSED', 'BLOCKED',
      'FAILED', 'UNDECLARED', 'AWAITING', 'NO RESULT', 'PAUSED', null,
    ];

    expect([...covered].sort()).toEqual([...words].sort());
  });

  /**
   * NO NUDGE ON A TERMINAL ROW, SAID ONCE MORE IN ITS OWN WORDS: this is the plainest instance of the rule, not an
   * exception to it.
   */
  it('withholds Nudge from a COMPLETED row, which no finding can re-open', () => {
    expect(workflowRowActions({ state: 'COMPLETED' }).nudge).toBe(false);
    expect(workflowRowActions({ state: 'CLOSED' }).nudge).toBe(false);
  });

  /**
   * NO CLOSE ON A RUNNING ROW, likewise. The reason is the server's missing busy check, so this is the client declining to
   * offer an action the route would accept and should not have been asked to.
   */
  it('withholds Close from a RUNNING row, so Stop comes first', () => {
    const running = workflowRowActions({ state: 'RUNNING' });

    expect(running.close).toBe(false);
    expect(running.stop).toBe(true);
  });

  /** THE ROW CLOSE EXISTS FOR. An undeclared workflow is open, however long it has been quiet. */
  it('keeps Close and Nudge on an UNDECLARED row, which is the row they are for', () => {
    const actions = workflowRowActions({ state: 'UNDECLARED' });

    expect(actions.close).toBe(true);
    expect(actions.nudge).toBe(true);
    expect(actions.stop).toBe(false);
  });

  /** SHOW THREAD IS ON EVERY ROW. Locating a finished workflow in the feed is exactly what it is
   *  for, and it writes nothing. */
  it('offers Show thread on every state', () => {
    for (const [state] of table) expect(workflowRowActions({ state }).thread).toBe(true);
  });

  /** STOP KEEPS ITS OWN, STRICTER RULE: only while this row itself is RUNNING. */
  it('offers Stop only on a RUNNING row', () => {
    for (const [state] of table) {
      expect(workflowRowActions({ state }).stop).toBe(state === 'RUNNING');
    }
  });

  /**
   * THE SIGNATURE TAKES ONE ARGUMENT, and this is the guard that says so at runtime as well as at
   * compile time. A second argument reaching it would be ignored; what must never come back is a
   * second argument that CHANGES the answer.
   */
  it('takes one argument, so nothing passed beside the row can change the answer', () => {
    expect(workflowRowActions.length).toBe(1);

    const withFindings = (workflowRowActions as (
      row: { state: WorkflowState | null }, findings?: string[] | null,
    ) => WorkflowRowActions);

    expect(withFindings({ state: 'COMPLETED' }, ['SomeKind']))
      .toEqual(withFindings({ state: 'COMPLETED' }));
  });
});

/**
 * A team whose members are all idle but whose Manager holds a delivered wake behind
 * the WIP limit is about to work, and must not read UNDECLARED while its Manager waits behind
 * another team's runs.
 */
describe('teamStatus — waiting for a slot', () => {
  const log = (): TeamLog => ({ workflowOpen: true, lastTerminal: { Manager: settled('completed'), Writer: settled('completed') } });

  it('reads waiting, never undeclared, while a member is held', () => {
    const roster = [container({ id: 'Manager', name: 'Manager', held: true }), container({ id: 'Writer', name: 'Writer' })];

    expect(status(roster, log())).toBe('waiting');
  });

  it('reads running when somebody else on the team is running', () => {
    const roster = [container({ id: 'Manager', held: true }), container({ id: 'Writer', name: 'Writer', state: 'Running' })];

    expect(status(roster, log())).toBe('running');
  });

  it('still lets a failure outrank the wait', () => {
    const roster = [container({ id: 'Manager', held: true }), container({ id: 'Writer', name: 'Writer', blocked: 'gave up' })];

    expect(status(roster, log())).toBe('blocked');
  });

  it('names the held members by label', () => {
    expect(heldMembers([container({ id: 'm', name: 'Manager Mia', held: true }), container({ id: 'w', name: 'Writer' })]))
      .toEqual(['Manager Mia']);
    expect(waitingForSlotText(['Manager'])).toBe('waiting for a slot: Manager');
  });
});
