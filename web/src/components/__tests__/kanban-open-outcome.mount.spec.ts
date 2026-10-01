// @vitest-environment happy-dom
//
// THE KANBAN OPENS MANAGE OUTCOMES (B0023): a card's outcome tag emits `open-outcome` with the
// outcome's id, and the board opens the dialog AT THAT OUTCOME; the filter bar's Manage outcomes
// button opens it at the list. The tag itself is the card's (KanbanCard); this pins the board's
// handler, so the event is emitted from the mounted card component.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const api = vi.hoisted(() => ({
  listOutcomes: vi.fn(),
  getOutcome: vi.fn(),
}));

vi.mock('../../api/outcomes', () => api);

import KanbanBoard from '../KanbanBoard.vue';
import KanbanCardComponent from '../KanbanCard.vue';
import { useKanbanStore } from '../../stores/kanban';
import type { KanbanCard } from '../../api/kanban';
import type { OutcomeFigures } from '../../api/outcomes';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';

const figures: OutcomeFigures = {
  workflows: { open: 1, completed: 0, closed: 0, total: 1 },
  agentSeconds: 60,
  waitingSeconds: 0,
  elapsed: { medianSeconds: null, longestSeconds: null, workflows: 0 },
  tokens: { billable: 100, measuredRuns: 1, unmeasuredRuns: 0 },
  runs: 1,
  teams: [],
  lastWorkedAt: null,
};

const pipeline = {
  id: 'o-1',
  name: 'Current job pipeline',
  description: '',
  status: 'active',
  mergedInto: null,
  source: 'person',
  targetMetric: null,
  targetUnit: null,
  targetValue: null,
  createdBy: 'ada@example.com',
  createdByKind: 'person',
  createdAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-09-01T00:00:00Z',
  confirmedBy: null,
  confirmedAt: null,
  figures,
};

const card = {
  id: 'c1',
  team: 'alpha',
  member: 'DeveloperInes',
  item: null,
  workflowSeq: 2226,
  title: 'Card c1',
  body: '',
  status: 'queued',
  laneId: 'todo',
  progress: [],
  createdAt: '2026-09-18T00:00:00Z',
  updatedAt: '2026-09-18T00:00:00Z',
  awaitingManager: false,
  paused: false,
} as unknown as KanbanCard;

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
  api.listOutcomes.mockReset().mockResolvedValue({
    ledgerStartedAt: null,
    outcomes: [pipeline],
    noOutcome: { name: 'No outcome', figures },
  });
  api.getOutcome.mockReset().mockResolvedValue({
    outcome: pipeline,
    resolvedTo: null,
    mergedFrom: [],
    workflows: [],
    history: [],
    ledgerStartedAt: null,
  });
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function mountBoard() {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  kanban.board = { lanes: [{ id: 'todo', title: 'To do' }], cards: [card] };
  vi.spyOn(kanban, 'setFilters').mockImplementation(() => {});

  wrapper = mount(KanbanBoard, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

describe('the Kanban opens Manage Outcomes', () => {
  it('opens the dialog at the outcome a card emits with open-outcome', async () => {
    const board = await mountBoard();
    expect(bodyFind('[data-outcomes-dialog]')).toBeNull();

    board.findComponent(KanbanCardComponent).vm.$emit('open-outcome', 'o-1');
    await settle();

    expect(bodyFind('[data-outcomes-dialog]')).not.toBeNull();
    expect(api.getOutcome).toHaveBeenCalledWith('o-1');
    expect(bodyFind('[data-outcome-detail]')?.getAttribute('data-outcome-id')).toBe('o-1');
    expect(bodyFind('[data-outcome-title]')?.textContent).toBe('Current job pipeline');
  });

  it('opens the dialog at the list from the filter bar', async () => {
    const board = await mountBoard();

    await board.find('[data-manage-outcomes]').trigger('click');
    await settle();

    expect(bodyFind('[data-outcomes-dialog]')).not.toBeNull();
    expect(api.getOutcome).not.toHaveBeenCalled();
    expect(bodyFind('[data-outcome-row="o-1"]')?.textContent).toContain('Current job pipeline');
  });
});
