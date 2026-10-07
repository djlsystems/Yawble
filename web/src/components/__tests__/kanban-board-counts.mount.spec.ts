// @vitest-environment happy-dom
//
// MOUNTED. What the board's counts claim against what the board draws: the header counts every
// entry on screen, cards and proposed outcomes alike; In Progress counts the cards in it and says
// in words that the ledger figure beside it is every team's; and Needs You's proposed outcomes are
// narrowed by the same filters the cards are.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

// Neither read answers: the board and the proposed outcomes are put in the store by each test.
vi.mock('../../api/kanban', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getKanbanBoard: () => new Promise<never>(() => {}),
}));

vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listOutcomes: () => new Promise<never>(() => {}),
  listLiveOutcomes: () => new Promise<never>(() => {}),
}));

import KanbanBoard from '../KanbanBoard.vue';
import { useKanbanStore } from '../../stores/kanban';
import { useWipStore } from '../../stores/wip';
import type { KanbanCard, KanbanFilters } from '../../api/kanban';
import type { Outcome } from '../../api/outcomes';
import { resetBody } from '../../test/mountQuasar';

function card(over: Partial<KanbanCard> & { id: string }): KanbanCard {
  return {
    team: 'quick-notes',
    member: 'Dev1',
    item: null,
    workflowSeq: 10,
    title: `Card ${over.id}`,
    body: '',
    status: 'queued',
    laneId: 'todo',
    progress: [],
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
    awaitingManager: false,
    paused: false,
    ...over,
  } as KanbanCard;
}

/** A Manager's proposal, linked to workflows of `teams`. */
function proposal(id: string, proposer: string, teams: string[] = []): Outcome {
  return {
    id,
    name: `Outcome ${id}`,
    description: '',
    status: 'proposed',
    mergedInto: null,
    source: 'agent',
    targetMetric: null,
    targetUnit: null,
    targetValue: null,
    value: null,
    createdBy: proposer,
    createdByKind: 'member',
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
    confirmedBy: null,
    confirmedAt: null,
    figures: {
      workflows: { open: teams.length, completed: 0, closed: 0, total: teams.length },
      agentSeconds: 0,
      waitingSeconds: 0,
      elapsed: { medianSeconds: null, longestSeconds: null, workflows: 0 },
      tokens: { billable: 0, measuredRuns: 0, unmeasuredRuns: 0 },
      runs: 0,
      teams: teams.map((team) => ({ id: team, name: team, deleted: false })),
      lastWorkedAt: null,
      backlog: { notStarted: 0, inProgress: 0, achieved: 0 },
      blockedSeconds: 0,
      efficiency: null,
      weekly: [],
      cost: { amount: null, currency: 'USD' },
    },
  } as Outcome;
}

const lanes = [
  { id: 'todo', title: 'Todo' },
  { id: 'in-progress', title: 'In Progress' },
  { id: 'blocked', title: 'Needs You' },
  { id: 'done', title: 'Done' },
];

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function mountBoard(options: {
  cards: KanbanCard[];
  proposed?: Outcome[];
  filters?: KanbanFilters;
  view?: 'board' | 'swimlanes';
  running?: { team: string; member: string }[];
  max?: number;
}) {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  const wip = useWipStore();
  vi.spyOn(wip, 'refresh').mockResolvedValue();
  wip.view = {
    max: options.max ?? 2,
    running: (options.running ?? []).map((hold) => ({ ...hold, since: '' })),
    waiting: [],
  };
  kanban.board = { lanes, cards: options.cards };
  kanban.filters = options.filters ?? {};
  kanban.view = options.view ?? 'board';
  kanban.proposed = options.proposed ?? [];

  wrapper = mount(KanbanBoard, { attachTo: document.body });
  await flushPromises();
  return { wrapper, kanban };
}

const headerCount = (w: VueWrapper) => w.find('.k-board > .row .text-caption').text();
const laneHead = (w: VueWrapper, lane: string) => w.find(`[data-lane-id="${lane}"] .k-lane-head`);

describe('the header count', () => {
  it('counts every entry on screen, a proposed outcome in Needs You included', async () => {
    const { wrapper } = await mountBoard({
      cards: [card({ id: 'a' })],
      proposed: [proposal('o-1', 'quick-notes/Manager', ['quick-notes'])],
    });

    const drawn =
      wrapper.findAll('[data-card-id]').length + wrapper.findAll('[data-needs-you-outcome]').length;
    expect(drawn).toBe(2);
    expect(headerCount(wrapper)).toBe('1 card, 1 proposed outcome');
  });

  it('says only cards when no proposed outcome is shown', async () => {
    const { wrapper } = await mountBoard({ cards: [card({ id: 'a' }), card({ id: 'b' })] });

    expect(headerCount(wrapper)).toBe('2 cards');
  });
});

describe('the In Progress count', () => {
  it('counts the cards in the lane, and names the ledger figure as every team\'s', async () => {
    const { wrapper } = await mountBoard({
      cards: [card({ id: 'a' })],
      running: [{ team: 'other-team', member: 'Manager' }],
      max: 2,
    });

    expect(wrapper.findAll('[data-lane-id="in-progress"] [data-card-id]')).toHaveLength(0);
    const head = laneHead(wrapper, 'in-progress');
    expect(head.find('.k-lane-count').text()).toBe('0');
    expect(head.find('[data-lane-wip]').text()).toBe('1 / 2 agents running, all teams');
  });

  it('turns amber when the ledger is over its limit, and says so in the same words', async () => {
    const { wrapper } = await mountBoard({
      cards: [card({ id: 'a', laneId: 'in-progress', status: 'running' })],
      running: [
        { team: 'quick-notes', member: 'Dev1' },
        { team: 'other-team', member: 'Dev1' },
        { team: 'other-team', member: 'Dev2' },
      ],
      max: 2,
    });

    const head = laneHead(wrapper, 'in-progress');
    expect(head.find('.k-lane-count').text()).toBe('1');
    expect(head.find('[data-lane-wip]').text()).toBe('3 / 2 agents running, all teams');
    expect(head.classes()).toContain('k-lane-head--over');
  });
});

describe('Needs You under the board\'s filters', () => {
  const proposals = () => [
    proposal('mine', 'quick-notes/Manager', ['quick-notes']),
    proposal('theirs', 'other-team/Manager', ['other-team']),
  ];

  it('shows only the picked team\'s proposed outcomes, and counts only those', async () => {
    const { wrapper } = await mountBoard({
      cards: [card({ id: 'a', laneId: 'blocked', status: 'blocked' })],
      proposed: proposals(),
      filters: { team: 'quick-notes' },
    });

    const shown = wrapper.findAll('[data-needs-you-outcome]').map((entry) => entry.attributes('data-needs-you-outcome'));
    expect(shown).toEqual(['mine']);
    expect(laneHead(wrapper, 'blocked').find('.k-lane-count').text()).toBe('2');
    expect(headerCount(wrapper)).toBe('1 card, 1 proposed outcome');
  });

  it('keeps an outcome another team proposed when a workflow of the picked team serves it', async () => {
    const { wrapper } = await mountBoard({
      cards: [],
      proposed: [proposal('shared', 'other-team/Manager', ['other-team', 'quick-notes'])],
      filters: { team: 'quick-notes' },
    });

    expect(wrapper.findAll('[data-needs-you-outcome]')).toHaveLength(1);
  });

  it('shows every team\'s in Swimlanes, which does not apply the team filter', async () => {
    const { wrapper } = await mountBoard({
      cards: [],
      proposed: proposals(),
      filters: { team: 'quick-notes' },
      view: 'swimlanes',
    });

    expect(wrapper.findAll('[data-needs-you-outcome]')).toHaveLength(2);
  });

  it('says nothing matches when the filter leaves no card and no outcome', async () => {
    const { wrapper } = await mountBoard({
      cards: [],
      proposed: [proposal('theirs', 'other-team/Manager', ['other-team'])],
      filters: { team: 'quick-notes' },
    });

    expect(wrapper.text()).toContain('Nothing matches this filter');
  });
});
