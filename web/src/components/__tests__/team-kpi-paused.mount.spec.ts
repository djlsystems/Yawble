// @vitest-environment happy-dom
//
// WHAT A READER SEES WHEN A WORKFLOW HAS SPENT ITS BUDGET.
//
// Without a PAUSED state the tile would read `RUNNING` and then `UNDECLARED` - *nobody is working
// this* - while the platform is actively refusing to let anybody work it, with the only trace one
// `agentContainer.rejected` row inside one member's activity feed and the two-character string
// `> limit` on the tile.
//
// THREE THINGS ARE PINNED HERE AND ALL THREE ARE RENDERING QUESTIONS.
//
// 1. The word PAUSED reaches the tile and the row, with the figure that stopped it.
// 2. Resume is on the row, and ONLY on a paused row, and it does not take Nudge's place.
// 3. Pressing it resumes THAT workflow, and says so when the answer is not a plain success.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { resumeWorkflow, notify } = vi.hoisted(() => ({
  resumeWorkflow: vi.fn(),
  notify: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  resumeWorkflow,
  // The Statistics tile's read; nothing here is about it, so it never answers.
  getTeamActivity: () => new Promise(() => {}),
}));

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { TeamWorkflows, TeamWorkflowTiming } from '../../api/types';
import { bodyText, resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

const team = {
  id: teamId,
  name: 'Alpha',
  memberAgents: ['claude-headless'],
  repos: [],
};

const STARTED = '2026-09-20T09:00:00Z';
const NOW = '2026-09-20T10:00:00Z';

function timing(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return {
    available: true,
    correlation: 1707,
    state: 'Running',
    startedAt: STARTED,
    endedAt: null,
    serverNow: NOW,
    executionSeconds: 0,
    partial: false,
    runsCounted: 0,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [],
    lastActivityAt: STARTED,
    awaitingFrom: null,
    subject: 'Execute B001F',
    pausedAt: null,
    pausedReason: null,
    pausedLimit: null,
    ...over,
  } as TeamWorkflowTiming;
}

function pausedTiming(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return timing({
    state: 'Paused',
    pausedAt: '2026-09-20T09:58:00Z',
    pausedReason: 'this workflow has spent 104,221,900 tokens, over its budget of 100,000,000.',
    pausedLimit: 100_000_000,
    ...over,
  });
}

function payload(entries: TeamWorkflowTiming[]): TeamWorkflows {
  return {
    available: true,
    openCount: entries.length,
    totalCount: entries.length,
    earliestStartedAt: STARTED,
    serverNow: NOW,
    workflows: entries,
    missing: null,
  } as unknown as TeamWorkflows;
}

beforeEach(() => {
  resumeWorkflow.mockReset();
  resumeWorkflow.mockResolvedValue(null);
  notify.mockReset();
});

afterEach(resetBody);

function mountStrip(workflows: TeamWorkflows, timingProp: TeamWorkflowTiming | null = null) {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({
    teams: [team],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: 100_000_000,
  });

  vi.spyOn(board, 'pullWorkflows').mockResolvedValue();
  vi.spyOn(board, 'pullWorkflow').mockResolvedValue();

  return mount(TeamKpiStrip, {
    props: {
      teamId,
      containers: [],
      usage: null,
      timing: timingProp,
      workflows,
      findings: [],
      clockOffset: 0,
    },
  });
}

async function openDialog(workflows: TeamWorkflows, timingProp: TeamWorkflowTiming | null = null) {
  const wrapper = mountStrip(workflows, timingProp);

  await flushPromises();
  await wrapper.find('.team-kpi-members').trigger('click');
  await flushPromises();

  return wrapper;
}

function rowActions(index = 0): string[] {
  const row = document.body.querySelectorAll('.workflow-row')[index];

  if (!row) return [];

  return [...row.querySelectorAll('button')].map((button) => (button.textContent ?? '').trim());
}

function rowButton(label: string, index = 0): HTMLButtonElement {
  const row = document.body.querySelectorAll('.workflow-row')[index];

  const found = row
    ? [...row.querySelectorAll('button')].find((b) => (b.textContent ?? '').trim() === label)
    : undefined;

  if (!found) throw new Error(`no ${label} button on workflow row ${index}`);

  return found as HTMLButtonElement;
}

describe('the tile says PAUSED', () => {
  /**
   * THE TILE'S OWN STATE WORD. The word beside `Workflow` must know about spend, or a stopped
   * workflow wears `RUNNING` and then `UNDECLARED`.
   */
  it('renders PAUSED beside the workflow label for a single paused workflow', async () => {
    const wrapper = mountStrip(payload([pausedTiming()]), pausedTiming());
    await flushPromises();

    expect(wrapper.find('.team-kpi-state').text()).toContain('PAUSED');

    wrapper.unmount();
  });

  /**
   * A PAUSED WORKFLOW BESIDE A QUIETER ONE IS WHAT THE TILE REPORTS. `RowLoudness` ranks PAUSED
   * (3) above COMPLETED (7), so the chip names the stopped one - the loudest-wins rule the tile
   * and the tab chip share, applied to this word rather than given an exception.
   */
  it('is the word the tile picks when one of two workflows is paused', async () => {
    const wrapper = mountStrip(
      payload([
        timing({ correlation: 1707, state: 'Completed', endedAt: '2026-09-20T09:40:00Z' }),
        pausedTiming({ correlation: 1801 }),
      ]),
    );
    await flushPromises();

    expect(wrapper.find('.team-kpi-state').text()).toContain('PAUSED');

    wrapper.unmount();
  });
});

describe('the workflow row says PAUSED and offers Resume', () => {
  it('renders the word on the row', async () => {
    const wrapper = await openDialog(payload([pausedTiming()]));

    expect(bodyText()).toContain('PAUSED');

    wrapper.unmount();
  });

  /** And says WHY, naming the figure the server sent - never one this browser resolved. */
  it('names the budget the workflow was stopped by', async () => {
    const wrapper = await openDialog(payload([pausedTiming()]));

    expect(bodyText()).toContain('paused - over its 100,000,000 budget');

    wrapper.unmount();
  });

  /**
   * BESIDE NUDGE, NOT INSTEAD OF IT. Nudge stays whatever a row's state is, and
   * Resume is the control a reader who has just read PAUSED can actually find.
   */
  it('offers Resume beside Close, Nudge and Show thread', async () => {
    const wrapper = await openDialog(payload([pausedTiming()]));

    expect(rowActions()).toEqual(['Close', 'Nudge', 'Resume', 'Show thread']);

    wrapper.unmount();
  });

  /** An unpaused row has no Resume at all - a control that is present and refuses invites the
   *  second click a control that is absent never gets. */
  it('withholds Resume from a row nobody paused', async () => {
    const wrapper = await openDialog(
      payload([timing({ state: 'Undeclared', lastActivityAt: '2026-09-19T09:00:00Z' })]),
    );

    expect(rowActions()).not.toContain('Resume');

    wrapper.unmount();
  });

  /** THE POINT OF THE CONTROL: pressing it resumes THAT workflow, by its own correlation. */
  it('resumes this workflow and no other', async () => {
    const wrapper = await openDialog(
      payload([timing({ correlation: 1707 }), pausedTiming({ correlation: 1801 })]),
    );

    rowButton('Resume', 1).click();
    await flushPromises();

    expect(resumeWorkflow).toHaveBeenCalledOnce();
    expect(resumeWorkflow).toHaveBeenCalledWith(teamId, 1801);

    wrapper.unmount();
  });

  /**
   * A PAUSED TEAM LOGS THE INSTRUCTION AND DOES NOT RUN IT. The workflow resumes; nothing moves
   * until the TEAM resumes too, and a person who is not told reads a working button as broken -
   * the identical pair `nudgeWorkflow` already carries and `confirmNudge` already renders.
   */
  it('says so when the team itself is paused', async () => {
    resumeWorkflow.mockResolvedValue('Alpha is paused; this will run after it resumes.');

    const wrapper = await openDialog(payload([pausedTiming()]));

    rowButton('Resume').click();
    await flushPromises();

    expect(notify).toHaveBeenCalledWith(
      expect.objectContaining({ message: 'Alpha is paused; this will run after it resumes.' }),
    );

    wrapper.unmount();
  });

  /** A refusal is SHOWN. A resume that failed silently leaves a reader pressing a button that has
   *  already answered. */
  it('shows the server’s refusal rather than swallowing it', async () => {
    resumeWorkflow.mockRejectedValue(new Error('No workflow with correlation 1707.'));

    const wrapper = await openDialog(payload([pausedTiming()]));

    rowButton('Resume').click();
    await flushPromises();

    expect(notify).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'negative',
        message: 'No workflow with correlation 1707.',
      }),
    );

    wrapper.unmount();
  });
});
