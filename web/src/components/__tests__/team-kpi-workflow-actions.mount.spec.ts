// @vitest-environment happy-dom
//
// WHICH BUTTONS A WORKFLOW ROW ACTUALLY RENDERS, and what the table around them says.
//
// `workflowRowActions` has a pure spec of its own in `lib/__tests__/teamKpis.spec.ts`, and it is
// the one that pins the action table cell by cell. THIS FILE EXISTS BECAUSE THAT SPEC CANNOT SEE THE
// CALL SITE. A rule such as "no Nudge on a COMPLETED row, whatever the team's findings" can live
// half in the function and half in the template: a `v-if` fed `props.findings` would let the
// function be perfectly correct while the button rendered anyway. A `v-if` is still a rule, and a
// rule nobody tests is a rule that drifts.
//
// SO THE ASSERTIONS HERE ARE ON RENDERED BUTTONS, never on the function's return. If the decision
// ever moves into a `v-if` condition, the pure cases go on passing and these catch it, which is
// exactly why both exist.
import { afterEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { TeamWorkflows, TeamWorkflowTiming } from '../../api/types';
// Importing this installs the Quasar plugin at module level, which is what makes the component
// mountable at all. The Workflows dialog TELEPORTS, so every assertion below reads `document.body`
// rather than the wrapper.
import { bodyText, resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

const team = {
  id: teamId,
  name: 'Alpha',
  memberAgents: ['claude-headless'],
  repos: [],
};

const STARTED = '2026-09-20T09:00:00Z';
const ENDED = '2026-09-20T09:40:00Z';

/**
 * The shape the server really sends, not the minimum one assertion touches — a fixture trimmed to
 * what a single case reads is how a component that needs more comes up empty for a reason that has
 * nothing to do with the defect under test.
 */
function timing(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return {
    available: true,
    correlation: 1707,
    state: 'Completed',
    startedAt: STARTED,
    endedAt: ENDED,
    serverNow: '2026-09-20T10:00:00Z',
    executionSeconds: 0,
    partial: false,
    runsCounted: 0,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [],
    lastActivityAt: ENDED,
    awaitingFrom: null,
    subject: 'Execute B001F',
    ...over,
  } as TeamWorkflowTiming;
}

function payload(over: Partial<TeamWorkflows> = {}): TeamWorkflows {
  return {
    available: true,
    openCount: 0,

    // `openCount` means OPEN; this is the uncapped total since the team's
    // floor, and it is what the truncation line compares the rendered list against.
    totalCount: 1,
    earliestStartedAt: null,
    serverNow: '2026-09-20T10:00:00Z',
    workflows: [timing()],
    missing: null,
    ...over,
  } as unknown as TeamWorkflows;
}

const runningContainer = {
  team: teamId,
  id: 'Manager',
  name: 'Manager',
  agent: 'claude-headless',
  state: 'Running',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: 1707,
  sinceSeq: 0,
};

afterEach(resetBody);

/** Mounted CLOSED, then the Workflows dialog is OPENED by clicking the member line — which is what
 *  a person does, and the only control on that tile. */
async function openDialog(
  workflows: TeamWorkflows,
  findings: string[] | null,
  containers: unknown[] = [],
) {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({
    teams: [team],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: null,
  });

  const wrapper = mount(TeamKpiStrip, {
    props: {
      teamId,
      containers: containers as never,
      usage: null,
      timing: null,
      workflows,
      findings,
      clockOffset: 0,
    },
  });

  await flushPromises();
  await wrapper.find('.team-kpi-members').trigger('click');
  await flushPromises();

  return wrapper;
}

/** The labels on the action buttons of the first workflow row, in DOM order. */
function rowActions(index = 0): string[] {
  const row = document.body.querySelectorAll('.workflow-row')[index];

  if (!row) return [];

  return [...row.querySelectorAll('button')].map((button) => (button.textContent ?? '').trim());
}

describe('the Workflows dialog, row by row', () => {
  /**
   * A COMPLETED row with a live finding must not render Nudge, whatever `props.findings`
   * holds.
   *
   * Nudge publishes an instruction whose causation is this row's correlation, and `WorkflowOpenSql`'s
   * `woke` subquery treats that as RE-OPENING the workflow — so the button would wake a Manager on
   * already-delivered work. Re-engaging finished work is the Concierge's job.
   */
  it('offers Show thread and NOT Nudge on a COMPLETED row WITH findings', async () => {
    const wrapper = await openDialog(payload(), ['StaleBranch']);

    expect(rowActions()).toEqual(['Show thread']);

    wrapper.unmount();
  });

  /** The same row without findings — kept so the pair reads as one rule
   *  rather than as a special case that happens to agree. */
  it('offers Show thread and NOT Nudge on a COMPLETED row WITHOUT findings', async () => {
    const wrapper = await openDialog(payload(), []);

    expect(rowActions()).toEqual(['Show thread']);

    wrapper.unmount();
  });

  /** A null `findings` prop is the third spelling of "none" and must not behave as a fourth thing. */
  it('offers Show thread and NOT Nudge on a COMPLETED row with a null findings prop', async () => {
    const wrapper = await openDialog(payload(), null);

    expect(rowActions()).toEqual(['Show thread']);

    wrapper.unmount();
  });

  /** A CLOSED row is terminal for the same reason and gets the same answer. */
  it('offers Show thread and NOT Nudge on a CLOSED row with findings', async () => {
    const wrapper = await openDialog(
      payload({ workflows: [timing({ state: 'Closed' })] }),
      ['StaleBranch'],
    );

    expect(rowActions()).toEqual(['Show thread']);

    wrapper.unmount();
  });

  /**
   * No Close on a RUNNING row: `POST .../workflows/{correlation}/close` has no busy check,
   * so a person could close a workflow whose members are still spending. Stop first.
   *
   * Show thread is on every row and writes nothing, so "Stop and nothing else" means nothing else
   * that acts on the workflow.
   */
  it('offers Stop and nothing else on a RUNNING row', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 1,
        totalCount: 1,
        earliestStartedAt: STARTED,
        workflows: [timing({ state: 'Running', endedAt: null, lastActivityAt: STARTED })],
      } as Partial<TeamWorkflows>),
      ['StaleBranch'],
      [runningContainer],
    );

    expect(rowActions()).toEqual(['Show thread', 'Stop']);

    wrapper.unmount();
  });

  /** An UNDECLARED row is the one Close exists for, and it is open however long it has been quiet. */
  it('offers Close, Nudge and Show thread on an UNDECLARED row', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 1,
        totalCount: 1,
        earliestStartedAt: STARTED,
        workflows: [
          timing({
            state: 'Undeclared',
            endedAt: null,
            // Comfortably outside `StalledBadgeGrace`, so the word is reached rather than the null
            // state a fresh row carries.
            lastActivityAt: '2026-09-19T09:00:00Z',
          }),
        ],
      } as Partial<TeamWorkflows>),
      [],
    );

    expect(rowActions()).toEqual(['Close', 'Nudge', 'Show thread']);

    wrapper.unmount();
  });

  /**
   * An AWAITING row must SHOW what it is waiting for and who is waiting. The row's `detail`
   * already carries that, so the requirement is that the template keeps it visible rather than
   * truncating it away — and there is deliberately NO new button: Nudge on an AWAITING row wakes the
   * Manager with no answer and can only produce the same question again.
   */
  it('renders what an AWAITING row is waiting for, and who is waiting', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 1,
        totalCount: 1,
        earliestStartedAt: STARTED,
        workflows: [
          timing({
            state: 'Awaiting',
            endedAt: null,
            awaitingFrom: 'Developer',
            lastActivityAt: STARTED,
          }),
        ],
      } as Partial<TeamWorkflows>),
      [],
    );

    const detail = document.body.querySelector('.workflow-row-detail');

    expect(detail).not.toBeNull();
    expect(detail!.textContent).toContain('Developer is waiting on you');

    // NOT TRUNCATED. `ellipsis` is Quasar's one-line clip, and it is on the subject line above by
    // design; on this line it would hide the half of the row that says what to go and do.
    expect(detail!.className).not.toContain('ellipsis');

    wrapper.unmount();
  });

  /**
   * UNDECLARED READS AS A REAL STATE, NOT AS A FAILURE. The word is rendered beside the subject like
   * any other, and the row's rule is `--quiet` (a dashed edge), never `--error`: an undeclared
   * workflow's runs SUCCEEDED and the work stopped anyway, which is a silence rather than a fault.
   */
  it('renders UNDECLARED as a state word with the quiet rule, never the error one', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 1,
        totalCount: 1,
        earliestStartedAt: STARTED,
        workflows: [
          timing({
            state: 'Undeclared',
            endedAt: null,
            lastActivityAt: '2026-09-19T09:00:00Z',
          }),
        ],
      } as Partial<TeamWorkflows>),
      [],
    );

    expect(bodyText()).toContain('UNDECLARED');
    expect(document.body.querySelector('.workflow-row--quiet')).not.toBeNull();
    expect(document.body.querySelector('.workflow-row--error')).toBeNull();

    wrapper.unmount();
  });

  /**
   * THE TABLE LISTS OPEN AND CLOSED ALIKE, NEWEST FIRST — so the heading must not claim it is
   * "every workflow this team currently holds open": `workflows` falls back to the newest CLOSED
   * workflow when none are open.
   */
  it('does not claim the list is only what the team holds open', async () => {
    const wrapper = await openDialog(payload(), []);

    expect(bodyText()).not.toContain('currently holds open');
    expect(bodyText()).toContain('newest first');

    wrapper.unmount();
  });

  /** EACH ROW CARRIES ITS START AND ITS END AS INSTANTS. Elapsed is a client-side subtraction;
   *  nothing here asks the server for a number. */
  it('renders the start and the end of each row as instants', async () => {
    const wrapper = await openDialog(payload(), []);

    const when = document.body.querySelector('.workflow-row-when');

    expect(when).not.toBeNull();
    expect(when!.textContent).toContain(new Date(STARTED).toLocaleString());
    expect(when!.textContent).toContain(new Date(ENDED).toLocaleString());

    wrapper.unmount();
  });

  /** An open row has no end, and an em dash is what this codebase says for an absent measurement —
   *  never an invented one and never `now`. */
  it('renders an em dash for the end of a row that has not ended', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 1,
        totalCount: 1,
        earliestStartedAt: STARTED,
        workflows: [timing({ state: 'Running', endedAt: null, lastActivityAt: STARTED })],
      } as Partial<TeamWorkflows>),
      [],
      [runningContainer],
    );

    const when = document.body.querySelector('.workflow-row-when');

    expect(when!.textContent).toContain('ended —');

    wrapper.unmount();
  });

  /**
   * The truncation line compares the rendered list against the total, not against `openCount`.
   * `workflows.length < openCount` can be inverted — 0 open against 1 row — so it would both miss
   * real truncation and fire on a list that is complete.
   */
  it('says how many of the total it is showing, counting against the total and not the open count', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 2,
        totalCount: 312,
        earliestStartedAt: STARTED,
        workflows: Array.from({ length: 50 }, (_, index) =>
          timing({ correlation: 2000 + index, subject: `Workflow ${index}` })),
      } as Partial<TeamWorkflows>),
      [],
    );

    expect(bodyText()).toContain('showing 50 of 312');

    wrapper.unmount();
  });

  /** AND STAYS SILENT WHEN NOTHING IS TRUNCATED. A line that always shows is a line nobody reads —
   *  and this is the half today's inverted comparison gets wrong in the other direction. */
  it('says nothing about truncation when the list is complete', async () => {
    const wrapper = await openDialog(payload({ openCount: 0, totalCount: 1 } as Partial<TeamWorkflows>), []);

    expect(bodyText()).not.toContain('showing 1 of 1');
    expect(bodyText()).not.toContain('showing');

    wrapper.unmount();
  });

  /**
   * `openCount` REPORTS OPEN WORKFLOWS. `totalCount` is a field beside it rather than a
   * redefinition of it, so the tile's own count line is unaffected by any of this.
   */
  it('keeps the tile count reporting OPEN workflows, not the total', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 2,
        totalCount: 312,
        earliestStartedAt: STARTED,
        workflows: [
          timing({ correlation: 1, state: 'Running', endedAt: null, lastActivityAt: STARTED }),
          timing({ correlation: 2, state: 'Running', endedAt: null, lastActivityAt: STARTED }),
        ],
      } as Partial<TeamWorkflows>),
      [],
      [runningContainer],
    );

    expect(bodyText()).not.toContain('312 open');

    wrapper.unmount();
  });
});

/**
 * "WHERE THE RUN TIME WENT" AGGREGATES ACROSS EVERY WORKFLOW IN THE TABLE, not across one
 * workflow the dialog picks (`props.timing`, the team's newest correlation — which need not even
 * be one of the rows above it).
 *
 * Three honesties hold: the per-member sum comes from the message log, the caption
 * still says a total is often LARGER than the tile's elapsed because members run at the same time,
 * and a run that started and never reported an end counts as UNKNOWN rather than zero and stays out
 * of the total.
 */
describe('the Workflows dialog, where the run time went', () => {
  it('sums a member across every workflow in the table', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 0,
        totalCount: 2,
        workflows: [
          timing({
            correlation: 1,
            runsCounted: 1,
            executionSeconds: 600,
            members: [{ member: 'Writer', runs: 1, executionSeconds: 600, unfinished: 0 }],
          }),
          timing({
            correlation: 2,
            runsCounted: 1,
            executionSeconds: 300,
            members: [{ member: 'Writer', runs: 2, executionSeconds: 300, unfinished: 0 }],
          }),
        ],
      } as Partial<TeamWorkflows>),
      [],
    );

    // 600 + 300 across the two workflows, and 1 + 2 runs — one line for Writer, not two.
    expect(bodyText()).toContain('15m 00s');
    expect(bodyText()).toContain('3 run(s)');

    wrapper.unmount();
  });

  /** THE CAPTION NAMES ACROSS WHAT, which is the reword: the claim is MORE true across several
   *  workflows than across one, not less. */
  it('captions the total as spanning the workflows listed above', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 0,
        totalCount: 2,
        workflows: [timing({ correlation: 1 }), timing({ correlation: 2 })],
      } as Partial<TeamWorkflows>),
      [],
    );

    const text = bodyText();

    expect(text).toContain('2 workflows');
    expect(text).toContain('Members run at the same time');
    expect(text).not.toContain('this workflow');

    wrapper.unmount();
  });

  /**
   * UNKNOWN IS NOT ZERO. A run that started and never reported an end is UNKNOWN, not
   * zero, and is not in the total — a host restart mid-flight spent real time and nothing knows how
   * much.
   */
  it('counts an unfinished run as unknown and keeps it out of the total', async () => {
    const wrapper = await openDialog(
      payload({
        openCount: 0,
        totalCount: 2,
        workflows: [
          timing({
            correlation: 1,
            runsCounted: 1,
            executionSeconds: 600,
            runsUnfinished: 0,
            members: [{ member: 'Writer', runs: 1, executionSeconds: 600, unfinished: 0 }],
          }),
          timing({
            correlation: 2,
            runsCounted: 0,
            executionSeconds: 0,
            runsUnfinished: 1,
            members: [{ member: 'Scout', runs: 1, executionSeconds: null, unfinished: 1 }],
          }),
        ],
      } as Partial<TeamWorkflows>),
      [],
    );

    const text = bodyText();

    // Scout's only run was cut short: unknown, never `0s`.
    expect(text).toContain('(unknown)');

    // And the total is Writer's ten minutes alone.
    expect(text).toContain('10m 00s');
    expect(text).toContain('1 run(s) here started and never reported an end');

    wrapper.unmount();
  });
});

describe('the Workflows dialog, its Outcome column', () => {
  /** Each row names the outcome it serves as text, says proposed, and reads No outcome for none. */
  it('names each row\'s outcome, a proposed one as proposed, and No outcome for none', async () => {
    const wrapper = await openDialog(
      payload({
        totalCount: 3,
        workflows: [
          timing({ correlation: 1707, outcome: { id: 'o1', name: 'Ship <i>it</i>', status: 'active' } }),
          timing({ correlation: 1708, outcome: { id: 'o2', name: 'Faster onboarding', status: 'proposed' } }),
          timing({ correlation: 1709, outcome: null }),
        ],
      }),
      null,
    );

    const cells = [...document.body.querySelectorAll('[data-workflow-outcome]')] as HTMLElement[];

    expect(cells.map((cell) => cell.textContent?.trim())).toEqual([
      'Outcome: Ship <i>it</i>',
      'Outcome: Faster onboarding (proposed)',
      'Outcome: No outcome',
    ]);
    expect(cells[0]!.querySelector('i')).toBeNull();
    expect(cells.map((cell) => cell.classList.contains('workflow-row-outcome--proposed'))).toEqual([false, true, false]);

    wrapper.unmount();
  });
});
