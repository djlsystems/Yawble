// @vitest-environment happy-dom
//
// THE TEAM PAGE'S OTHER PERIOD FIGURES, MOUNTED: the Activity tile's executions, Agent Hours and active members for the
// period (the period read's own `totals`), the Workflow tile's Needs you count (the header's one list filtered to the
// team), and each Workflows dialog row's one line (the workflow read's figures, all time) - each unchanged.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { getTeamCostPeriod, getTeamCostWorkflows } = vi.hoisted(() => ({
  getTeamCostPeriod: vi.fn(),
  getTeamCostWorkflows: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamActivity: () => new Promise(() => {}),
}));
vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamCost: () => new Promise(() => {}),
  getTeamCostPeriod,
  getTeamCostWorkflows,
}));

import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { useNeedsYouStore } from '../../stores/needsYou';
import { asTeamId, type NeedsYouItem, type TeamWorkflows, type TeamWorkflowTiming } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';
import { teamCostPeriod, teamCostWorkflows } from '../../test/teamPeriodFixtures';
import { misnamedOnTeamsList, readWords as readOff } from '../../test/estimateLabels';
import { NoRunsLine, activityLine, workflowLine } from '../../lib/teamPeriod';

const teamId = asTeamId('alpha');
const squash = (text: string | null | undefined) => (text ?? '').replace(/\s+/g, ' ').trim();

function timing(correlation: number): TeamWorkflowTiming {
  return {
    available: true, correlation, state: 'Completed',
    startedAt: '2026-09-20T09:00:00Z', endedAt: '2026-09-20T09:40:00Z', serverNow: '2026-09-20T10:00:00Z',
    executionSeconds: 0, partial: false, runsCounted: 0, runsUnfinished: 0, blockedBy: null, runsFailed: 0,
    failedMembers: [], missing: null, members: [], lastActivityAt: '2026-09-20T09:40:00Z', awaitingFrom: null,
    subject: `Workflow ${correlation}`, spend: { tokensSpent: null, partial: false },
  } as unknown as TeamWorkflowTiming;
}

const workflows = {
  available: true, openCount: 0, totalCount: 4, earliestStartedAt: null, serverNow: '2026-09-20T10:00:00Z',
  workflows: [timing(42), timing(41), timing(40), timing(7)], missing: null,
} as unknown as TeamWorkflows;

function item(team: string, extra: Partial<NeedsYouItem> = {}): NeedsYouItem {
  return {
    kind: 'blocked', team: { id: team, name: team }, member: 'Dev', card: null, workflow: 1, item: null,
    since: '2026-11-01T00:00:00Z', words: 'Blocked.', where: { view: 'workflow', team }, ...extra,
  } as NeedsYouItem;
}

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  getTeamCostPeriod.mockReset();
  getTeamCostWorkflows.mockReset();
  getTeamCostPeriod.mockResolvedValue(teamCostPeriod());
  getTeamCostWorkflows.mockResolvedValue(teamCostWorkflows());
  localStorage.clear();
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  resetBody();
});

async function mountStrip(needsYou: { live: NeedsYouItem[]; stored: NeedsYouItem[] } | null = null, team = teamId) {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [{ id: team, name: 'Alpha', memberAgents: [], repos: [] }],
    activeTeamId: team,
    overviewLanded: true,
  } as never);
  if (needsYou) {
    const store = useNeedsYouStore();
    store.live.items = needsYou.live;
    store.live.loaded = true;
    store.stored.items = needsYou.stored;
    store.stored.loaded = true;
  }

  wrapper = mount(TeamKpiStrip, {
    attachTo: document.body,
    props: { teamId: team, containers: [], usage: null, timing: null, workflows, findings: [], clockOffset: 0 },
  });
  await flushPromises();
  return wrapper;
}

describe('the Activity tile\'s period line', () => {
  it('says the period read\'s executions, Agent Hours and active members, unchanged; the spans keep their caption', async () => {
    const period = teamCostPeriod();
    await mountStrip();

    const line = wrapper!.find('[data-activity-period]');
    expect(squash(line.text())).toBe('8 executions · 12.0 Agent Hours · 4 active members');
    expect(squash(line.text())).toBe(activityLine(period.totals));
    expect(wrapper!.find('.stats-caption').exists()).toBe(true);
    expect(misnamedOnTeamsList(readOff(line.element))).toEqual([]);
  });

  it('says zeros for a team with no runs in the period', async () => {
    getTeamCostPeriod.mockResolvedValue(teamCostPeriod({ empty: true }));
    await mountStrip();

    expect(squash(wrapper!.find('[data-activity-period]').text())).toBe('0 executions · 0.0 Agent Hours · 0 active members');
  });

  it('says nothing until the period is read, or when it cannot be', async () => {
    getTeamCostPeriod.mockRejectedValue(new Error('down'));
    await mountStrip();

    expect(wrapper!.find('[data-activity-period]').exists()).toBe(false);
  });
});

describe('the Workflow tile\'s Needs you count', () => {
  it('counts the header\'s one list filtered to this team, both parts, and a connection holding this team up', async () => {
    const connection = item('beta', {
      kind: 'connection', member: null,
      connection: { id: 'c1', name: 'Mail', holds: [{ team: { id: 'beta', name: 'Beta' }, members: ['x'] }, { team: { id: 'alpha', name: 'Alpha' }, members: ['Dev'] }] },
    });
    await mountStrip({ live: [item('alpha'), item('beta'), connection], stored: [item('Alpha', { kind: 'merge-ready' })] });

    expect(squash(wrapper!.find('[data-test="workflow-needs-you"]').text())).toBe('Needs you · 3');
    expect(useNeedsYouStore().countFor('alpha')).toBe(3);
    expect(useNeedsYouStore().countFor('beta')).toBe(2);
  });

  it('says zero when nothing on the list is this team\'s', async () => {
    await mountStrip({ live: [item('beta')], stored: [] });

    expect(squash(wrapper!.find('[data-test="workflow-needs-you"]').text())).toBe('Needs you · 0');
  });

  it('says zero for a team the list does not name at all - one the caller cannot reach', async () => {
    await mountStrip({ live: [item('alpha'), item('beta')], stored: [] }, asTeamId('gone'));

    expect(squash(wrapper!.find('[data-test="workflow-needs-you"]').text())).toBe('Needs you · 0');
  });

  it('says nothing before the list has been read', async () => {
    await mountStrip(null);

    expect(wrapper!.find('[data-test="workflow-needs-you"]').exists()).toBe(false);
  });

  it('keeps the tile named Workflow', async () => {
    await mountStrip({ live: [], stored: [] });

    expect(squash(wrapper!.find('.team-kpi--workflow .team-kpi-label').text())).toMatch(/^Workflow/);
  });
});

describe('each Workflows dialog row\'s one line', () => {
  it('is the workflow read\'s Agent Hours and Estimated Execution Cost, all time, read when the dialog opens', async () => {
    await mountStrip();
    expect(getTeamCostWorkflows).not.toHaveBeenCalled();

    await wrapper!.find('.team-kpi-members').trigger('click');
    await flushPromises();

    expect(getTeamCostWorkflows).toHaveBeenCalledWith('alpha');
    const answer = teamCostWorkflows();
    const lines = [...document.body.querySelectorAll('.workflow-row')].map((row) => squash(row.querySelector('[data-workflow-cost]')?.textContent));
    expect(lines).toEqual([
      'Agent Hours 6.0 · Estimated Execution Cost $360',
      'Agent Hours 1.5 · Estimated Execution Cost $90*',
      'Agent Hours 4.0 · Estimated Execution Cost $240',
      NoRunsLine,
    ]);
    expect(lines[0]).toBe(workflowLine(answer.workflows[0]!.figures, 'USD'));
    expect(document.body.querySelectorAll('[data-workflow-cost]')).toHaveLength(4);
    for (const row of document.body.querySelectorAll('.workflow-row')) {
      expect(row.querySelectorAll('[data-workflow-cost]')).toHaveLength(1);
      expect(misnamedOnTeamsList(readOff(row.querySelector('[data-workflow-cost]')))).toEqual([]);
    }
  });

  it('reads Estimate unavailable for a workflow that ran unpriced', async () => {
    getTeamCostWorkflows.mockResolvedValue(teamCostWorkflows({ unpriced: true }));
    await mountStrip();
    await wrapper!.find('.team-kpi-members').trigger('click');
    await flushPromises();

    expect(squash(document.body.querySelector('.workflow-row [data-workflow-cost]')!.textContent))
      .toBe('Agent Hours 6.0 · Estimated Execution Cost Estimate unavailable');
  });
});
