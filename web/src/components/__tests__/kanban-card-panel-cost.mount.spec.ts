// @vitest-environment happy-dom
//
// AN OPENED CARD'S ONE LINE: its workflow's Agent Hours and Estimated Execution Cost over every run of it, from the
// workflow read for that one workflow - the line its row in the Workflows dialog carries. The card on the board is
// unchanged: it reads nothing.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { getTeamCostWorkflows } = vi.hoisted(() => ({ getTeamCostWorkflows: vi.fn() }));

vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamCostWorkflows,
}));

import KanbanCardPanel from '../KanbanCardPanel.vue';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { useKanbanStore } from '../../stores/kanban';
import type { KanbanCardDetail } from '../../api/kanban';
import { teamCostWorkflows } from '../../test/teamPeriodFixtures';
import { misnamedOnTeamsList, readWords as readOff } from '../../test/estimateLabels';
import { NoRunsLine, workflowLine } from '../../lib/teamPeriod';

let wrapper: VueWrapper | null = null;

beforeEach(() => {
  getTeamCostWorkflows.mockReset();
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  resetBody();
});

async function open(workflowSeq: number) {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  kanban.load = vi.fn(async () => {});
  kanban.select = vi.fn(async () => {});
  wrapper = mount(KanbanCardPanel);
  const detail: KanbanCardDetail = {
    id: `c-${workflowSeq}`, workflowSeq, team: 'alpha', member: 'Dev', title: 'Ship billing', body: '',
    status: 'running', laneId: 'in-progress', progress: [], createdAt: '2026-10-16T01:00:00Z',
    updatedAt: '2026-10-16T01:10:00Z', awaitingManager: false, paused: false, trail: [],
  };
  kanban.selectedId = detail.id;
  kanban.detail = detail;
  await flushPromises();
}

const line = () => bodyFind('[data-card-cost]');
const squash = (text: string | null | undefined) => (text ?? '').replace(/\s+/g, ' ').trim();

describe('an opened card\'s workflow line', () => {
  it('reads its one workflow and says its Agent Hours and Estimated Execution Cost, as the Workflows dialog row does', async () => {
    const answer = teamCostWorkflows();
    answer.workflows = answer.workflows.filter((w) => w.workflow === 41);
    getTeamCostWorkflows.mockResolvedValue(answer);
    await open(41);

    expect(getTeamCostWorkflows).toHaveBeenCalledWith('alpha', 41);
    expect(squash(line()?.textContent)).toBe('Agent Hours 1.5 · Estimated Execution Cost $90*');
    expect(squash(line()?.textContent)).toBe(workflowLine(answer.workflows[0]!.figures, 'USD'));
    expect(document.body.querySelectorAll('[data-card-cost]')).toHaveLength(1);
    expect(misnamedOnTeamsList(readOff(line()))).toEqual([]);
  });

  it('says no hours and no estimate for a workflow with no runs', async () => {
    getTeamCostWorkflows.mockResolvedValue({ ...teamCostWorkflows(), workflows: [] });
    await open(99);

    expect(squash(line()?.textContent)).toBe(NoRunsLine);
  });

  it('says nothing when the workflow cannot be read', async () => {
    getTeamCostWorkflows.mockRejectedValue(new Error('No such team.'));
    await open(41);

    expect(line()).toBeNull();
  });
});
