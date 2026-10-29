// @vitest-environment happy-dom
//
// EACH ROW OF THE WORKFLOWS DIALOG SHOWS ITS TOKENS AGAINST THE BUDGET IN FORCE, open and closed
// alike: the tile's bar is drawn only while one workflow is open, so the dialog is where a finished
// workflow's spend against its limit is read back.
import { afterEach, describe, expect, it, vi } from 'vitest';

// The Activity tile reads `/activity` when it mounts. Nothing here is about it.
vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamActivity: () => new Promise(() => {}),
}));
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { TeamWorkflows, TeamWorkflowTiming } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');
const team = { id: teamId, name: 'Alpha', memberAgents: ['claude-headless'], repos: [] };

function timing(correlation: number, tokensSpent: number | null): TeamWorkflowTiming {
  return {
    available: true,
    correlation,
    state: 'Completed',
    startedAt: '2026-09-20T09:00:00Z',
    endedAt: '2026-09-20T09:40:00Z',
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
    lastActivityAt: '2026-09-20T09:40:00Z',
    awaitingFrom: null,
    subject: `Workflow ${correlation}`,
    spend: { tokensSpent, partial: false },
  } as unknown as TeamWorkflowTiming;
}

function payload(rows: TeamWorkflowTiming[]): TeamWorkflows {
  return {
    available: true,
    openCount: 0,
    totalCount: rows.length,
    earliestStartedAt: null,
    serverNow: '2026-09-20T10:00:00Z',
    workflows: rows,
    missing: null,
  } as unknown as TeamWorkflows;
}

afterEach(resetBody);

async function openDialog(workflows: TeamWorkflows, limit: number | null) {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [team],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: limit,
  });

  const wrapper = mount(TeamKpiStrip, {
    props: { teamId, containers: [], usage: null, timing: null, workflows, findings: [], clockOffset: 0 },
  });

  await flushPromises();
  await wrapper.find('.team-kpi-members').trigger('click');
  await flushPromises();

  return wrapper;
}

function rowBudgets() {
  return [...document.body.querySelectorAll('.workflow-row')].map((row) => ({
    text: row.querySelector('[data-workflow-budget]')?.textContent?.replace(/\s+/g, ' ').trim() ?? null,
    width: (row.querySelector('.workflow-row-budget-bar__fill') as HTMLElement | null)?.style.width ?? null,
    near: row.querySelector('.workflow-row-budget-bar--near') !== null,
  }));
}

describe('the Workflows dialog, tokens against the budget per row', () => {
  it('gives every row, closed ones included, its own bar and figure against the limit', async () => {
    const wrapper = await openDialog(payload([timing(2, 25_000_000), timing(1, 95_000_000)]), 100_000_000);

    expect(rowBudgets()).toEqual([
      { text: 'Tokens: 25,000,000 of 100,000,000', width: '25%', near: false },
      { text: 'Tokens: 95,000,000 of 100,000,000', width: '95%', near: true },
    ]);

    wrapper.unmount();
  });

  it('says > limit on a row past it, with the bar full and never wider', async () => {
    const wrapper = await openDialog(payload([timing(1, 130_000_000)]), 100_000_000);

    expect(rowBudgets()).toEqual([{ text: 'Tokens: > limit', width: '100%', near: true }]);

    wrapper.unmount();
  });

  it('names the limit and draws no bar for spend that was never measured', async () => {
    const wrapper = await openDialog(payload([timing(1, null)]), 100_000_000);

    expect(rowBudgets()).toEqual([{ text: 'Tokens: limit 100,000,000', width: null, near: false }]);

    wrapper.unmount();
  });

  it('shows nothing when no budget is in force', async () => {
    const wrapper = await openDialog(payload([timing(1, 25_000_000)]), null);

    expect(rowBudgets()).toEqual([{ text: null, width: null, near: false }]);

    wrapper.unmount();
  });
});
