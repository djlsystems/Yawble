// @vitest-environment happy-dom
//
// MOUNTED. The board's two layouts, the In Progress header read off the WIP
// ledger (`3 / 4 running`), an advisory lane turning amber over its limit, and the waiting mark on
// a card whose member is held. The ledger is the WIP store's one copy; nothing here fetches it.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

// The board fetch, observed. Never answers unless a test says so.
const { getKanbanBoard } = vi.hoisted(() => ({
  getKanbanBoard: vi.fn(
    (_filters?: Record<string, string>): Promise<unknown> => new Promise<never>(() => {}),
  ),
}));

vi.mock('../../api/kanban', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getKanbanBoard: (filters?: Record<string, string>) => getKanbanBoard(filters),
}));

import KanbanBoard from '../KanbanBoard.vue';
import { KanbanViewKey, useKanbanStore } from '../../stores/kanban';
import { useConsoleStore } from '../../stores/console';
import { useWipStore } from '../../stores/wip';
import type { KanbanCard } from '../../api/kanban';
import type { Team } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

function card(over: Partial<KanbanCard> & { id: string }): KanbanCard {
  return {
    team: 'alpha',
    member: 'Dev1',
    item: null,
    workflowSeq: 10,
    title: `Card ${over.id}`,
    body: '',
    status: 'running',
    laneId: 'in-progress',
    progress: [],
    createdAt: '2026-09-23T00:00:00Z',
    updatedAt: '2026-09-23T00:00:00Z',
    awaitingManager: false,
    paused: false,
    ...over,
  } as KanbanCard;
}

const lanes = [
  { id: 'todo', title: 'Todo', wipLimit: 1 },
  { id: 'in-progress', title: 'In Progress', wipLimit: 4 },
  { id: 'done', title: 'Done' },
];

const team = (id: string, containers: unknown[] = []) =>
  ({ id, name: id.toUpperCase(), scope: 'team', containers }) as unknown as Team;

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  getKanbanBoard.mockReset();
  getKanbanBoard.mockImplementation(() => new Promise<never>(() => {}));
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function mountBoard(cards: KanbanCard[], teams: Team[], view: 'board' | 'swimlanes' = 'board') {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  useConsoleStore().$patch({ teams });
  const wip = useWipStore();
  vi.spyOn(wip, 'refresh').mockResolvedValue();
  wip.view = {
    max: 4,
    running: [
      { team: 'alpha', member: 'Dev1', since: '' },
      { team: 'alpha', member: 'Dev2', since: '' },
      { team: 'beta', member: 'Dev1', since: '' },
    ],
    waiting: [{ team: 'beta', member: 'Manager', since: '' }],
  };
  kanban.board = { lanes, cards };
  kanban.view = view;
  vi.spyOn(kanban, 'select').mockResolvedValue();

  wrapper = mount(KanbanBoard, { attachTo: document.body });
  await flushPromises();
  return { wrapper, kanban };
}

describe('lane headers', () => {
  it('reads In Progress as running over the ledger limit', async () => {
    const { wrapper } = await mountBoard([card({ id: 'a' })], [team('alpha')]);

    const head = wrapper.find('[data-lane-id="in-progress"] .k-lane-count');
    expect(head.text()).toBe('3 / 4 running');
  });

  it('turns an advisory lane amber over its limit, and only then', async () => {
    const { wrapper } = await mountBoard(
      [card({ id: 'a', laneId: 'todo', status: 'queued' }), card({ id: 'b', laneId: 'todo', status: 'queued' })],
      [team('alpha')],
    );

    const todo = wrapper.find('[data-lane-id="todo"] .k-lane-head');
    expect(todo.classes()).toContain('k-lane-head--over');
    expect(todo.find('.k-lane-count').text()).toBe('2 / 1');
    expect(wrapper.find('[data-lane-id="done"] .k-lane-head').classes()).not.toContain('k-lane-head--over');
  });
});

/**
 * An In Progress lane reading `4 / 2 running` is not exempt from the amber rule: an
 * over-limit In Progress lane turns amber the same as any other lane - over the figure its header shows, the ledger's running count and limit.
 */
describe('an over-limit In Progress lane', () => {
  const overLedger = () => {
    const wip = useWipStore();
    wip.view = {
      max: 2,
      running: [
        { team: 'alpha', member: 'Dev1', since: '' },
        { team: 'alpha', member: 'Dev2', since: '' },
        { team: 'beta', member: 'Dev1', since: '' },
        { team: 'beta', member: 'Dev2', since: '' },
      ],
      waiting: [],
    };
  };

  it('turns amber on the board when the ledger reads more running than its limit', async () => {
    const { wrapper } = await mountBoard([card({ id: 'a' })], [team('alpha')]);
    overLedger();
    await flushPromises();

    const head = wrapper.find('[data-lane-id="in-progress"] .k-lane-head');
    expect(head.find('.k-lane-count').text()).toBe('4 / 2 running');
    expect(head.classes()).toContain('k-lane-head--over');
  });

  it('turns amber in the swimlane header too', async () => {
    const { wrapper } = await mountBoard([card({ id: 'a' })], [team('alpha')], 'swimlanes');
    overLedger();
    await flushPromises();

    const head = wrapper.find('.k-swimlanes [data-lane-id="in-progress"]');
    expect(head.find('.k-lane-count').text()).toBe('4 / 2 running');
    expect(head.classes()).toContain('k-lane-head--over');
  });

  it('stays neutral at or under the limit', async () => {
    const { wrapper } = await mountBoard([card({ id: 'a' })], [team('alpha')]);

    const head = wrapper.find('[data-lane-id="in-progress"] .k-lane-head');
    expect(head.find('.k-lane-count').text()).toBe('3 / 4 running');
    expect(head.classes()).not.toContain('k-lane-head--over');
  });

  it('stays neutral under a ledger with no limit', async () => {
    const { wrapper } = await mountBoard([card({ id: 'a' })], [team('alpha')]);
    const wip = useWipStore();
    wip.view = { ...wip.view!, max: 0 };
    await flushPromises();

    const head = wrapper.find('[data-lane-id="in-progress"] .k-lane-head');
    expect(head.find('.k-lane-count').text()).toBe('3 running');
    expect(head.classes()).not.toContain('k-lane-head--over');
  });

  it('before the ledger answers, reads its running cards against the lane limit, not its card count', async () => {
    const running = ['a', 'b', 'c', 'd', 'e'].map((id) => card({ id }));
    const { wrapper, kanban } = await mountBoard(running, [team('alpha')]);
    const wip = useWipStore();
    wip.view = null;
    await flushPromises();

    const head = wrapper.find('[data-lane-id="in-progress"] .k-lane-head');
    expect(head.find('.k-lane-count').text()).toBe('5 / 4 running');
    expect(head.classes()).toContain('k-lane-head--over');

    // Five cards in the lane, but only four running: under the limit the header shows.
    kanban.board!.cards[4]!.status = 'queued';
    await flushPromises();
    expect(head.find('.k-lane-count').text()).toBe('4 / 4 running');
    expect(head.classes()).not.toContain('k-lane-head--over');
  });
});

describe('the waiting mark', () => {
  it('marks a live card whose member the ledger holds', async () => {
    const { wrapper } = await mountBoard(
      [
        card({ id: 'held', team: 'beta', member: 'Manager', status: 'queued', laneId: 'todo' }),
        card({ id: 'free', team: 'alpha', member: 'Dev1' }),
      ],
      [team('alpha'), team('beta')],
    );

    expect(wrapper.find('[data-card-id="held"]').attributes('data-waiting')).toBe('yes');
    expect(wrapper.find('[data-card-id="held"]').text()).toContain('waiting for a slot');
    expect(wrapper.find('[data-card-id="free"]').attributes('data-waiting')).toBeUndefined();
  });

  it('marks a card whose member snapshot says held', async () => {
    const { wrapper } = await mountBoard(
      [card({ id: 'c', team: 'gamma', member: 'Dev9', status: 'queued', laneId: 'todo' })],
      [team('gamma', [{ team: 'gamma', id: 'Dev9', name: 'Dev9', state: 'Idle', queueDepth: 0, held: true }])],
    );

    expect(wrapper.find('[data-card-id="c"]').attributes('data-waiting')).toBe('yes');
  });
});

describe('swimlanes', () => {
  it('draws one row per console team, empty teams included', async () => {
    const { wrapper } = await mountBoard([card({ id: 'a' })], [team('alpha'), team('beta'), team('empty')], 'swimlanes');

    const rows = wrapper.findAll('.k-swim-team').map((row) => row.text());
    expect(rows).toEqual(['ALPHA', 'BETA', 'EMPTY']);
  });

  it('puts each card in its own team row and lane column, with sticky headers', async () => {
    const { wrapper } = await mountBoard(
      [card({ id: 'a' }), card({ id: 'b', team: 'beta', laneId: 'done', status: 'done' })],
      [team('alpha'), team('beta')],
      'swimlanes',
    );

    const grid = wrapper.find('.k-swimlanes');
    // Team column, then one cell per lane, per row - after the header row of 1 + 3.
    const cells = grid.findAll('.k-swim-cell');
    expect(cells).toHaveLength(6);
    expect(cells[1]!.find('[data-card-id="a"]').exists()).toBe(true);
    expect(cells[5]!.find('[data-card-id="b"]').exists()).toBe(true);
    expect(grid.attributes('style')).toContain('grid-template-columns: 10rem repeat(3, var(--k-lane-width))');
    expect(grid.findAll('.k-swim-sticky-top')).toHaveLength(4);
    expect(grid.find('[data-lane-id="in-progress"] .k-lane-count').text()).toBe('3 / 4 running');
  });

  it('keeps a row for a team only a card names', async () => {
    const { wrapper } = await mountBoard([card({ id: 'x', team: 'ghost' })], [team('alpha')], 'swimlanes');

    expect(wrapper.findAll('.k-swim-team').map((row) => row.text())).toEqual(['ALPHA', 'ghost']);
  });

  it('switches with the toggle and remembers the choice in this browser', async () => {
    const { wrapper, kanban } = await mountBoard([card({ id: 'a' })], [team('alpha')]);

    expect(wrapper.find('.k-lanes').exists()).toBe(true);

    kanban.setView('swimlanes');
    await flushPromises();

    expect(wrapper.find('.k-swimlanes').exists()).toBe(true);
    expect(wrapper.find('[data-card-id="a"]').exists()).toBe(true);
    expect(localStorage.getItem(KanbanViewKey)).toBe('swimlanes');
  });
});

describe('the running lane before the ledger answers', () => {
  it('reads its running cards against the lane limit', async () => {
    const { wrapper } = await mountBoard([card({ id: 'a' }), card({ id: 'b', status: 'queued' })], [team('alpha')]);
    const wip = useWipStore();
    wip.view = null;
    await flushPromises();

    expect(wrapper.find('[data-lane-id="in-progress"] .k-lane-count').text()).toBe('1 / 4 running');
  });
});

/**
 * SWIMLANES. The waiting mark inside a swimlane
 * row (not only on the plain board), and the sticky header row and team column as declared styles:
 * happy-dom does not lay out, so whether they actually stay put while scrolling is a browser check.
 */
describe('swimlanes', () => {
  it('marks a held card inside its own team row', async () => {
    const { wrapper } = await mountBoard(
      [
        card({ id: 'held', team: 'beta', member: 'Manager', status: 'queued', laneId: 'todo' }),
        card({ id: 'free', team: 'alpha', member: 'Dev1' }),
      ],
      [team('alpha'), team('beta'), team('empty')],
      'swimlanes',
    );

    const cells = wrapper.find('.k-swimlanes').findAll('.k-swim-cell');
    // Rows in console order, three lanes each: beta's Todo cell is the fourth.
    expect(cells).toHaveLength(9);
    const held = cells[3]!.find('[data-card-id="held"]');
    expect(held.exists()).toBe(true);
    expect(held.attributes('data-waiting')).toBe('yes');
    expect(held.text()).toContain('waiting for a slot');
    expect(wrapper.find('[data-card-id="free"]').attributes('data-waiting')).toBeUndefined();
  });

  it('puts every team name in the sticky column and every lane header in the sticky row', async () => {
    const { wrapper } = await mountBoard([card({ id: 'a' })], [team('alpha'), team('beta'), team('empty')], 'swimlanes');

    const teams = wrapper.findAll('.k-swim-team');
    expect(teams).toHaveLength(3);
    for (const row of teams) expect(row.classes()).toContain('k-swim-sticky-left');
    for (const head of wrapper.findAll('.k-swim-head')) expect(head.classes()).toContain('k-swim-sticky-top');
  });

  it('declares both sticky classes as position: sticky', async () => {
    const { readFileSync } = await import('node:fs');
    const { resolve } = await import('node:path');
    const source = readFileSync(resolve(process.cwd(), 'src/components/KanbanBoard.vue'), 'utf8');

    expect(source).toMatch(/\.k-swim-sticky-top\s*\{[^}]*position:\s*sticky;[^}]*top:\s*0/);
    expect(source).toMatch(/\.k-swim-sticky-left\s*\{[^}]*position:\s*sticky;[^}]*left:\s*0/);
  });
});

/**
 * SWIMLANES SHOW EVERY TEAM. The team filter is kept, greyed out and not applied in
 * Swimlanes - not in the rows and not in the fetch - and applies again on Board.
 */
describe('swimlanes show every team', () => {
  const everyCard = [
    card({ id: 'a', team: 'alpha' }),
    card({ id: 'b', team: 'beta', laneId: 'done', status: 'done' }),
    card({ id: 'c', team: 'gamma', laneId: 'todo', status: 'queued' }),
  ];
  const teams = () => [team('alpha'), team('beta'), team('gamma')];

  /** The server's answer: a team filter narrows the cards, no team is every team. */
  function serveBoard() {
    getKanbanBoard.mockImplementation(async (filters?: Record<string, string>) => ({
      
      lanes,
      cards: everyCard.filter((entry) => !filters?.team || entry.team === filters.team),
    }));
  }

  async function mountWithTeam(view: 'board' | 'swimlanes' = 'board') {
    serveBoard();
    const mounted = await mountBoard(everyCard.filter((entry) => entry.team === 'beta'), teams(), view);
    mounted.kanban.setFilters({ team: 'beta' });
    await flushPromises();
    getKanbanBoard.mockClear();
    return mounted;
  }

  async function chooseLayout(label: 'Board' | 'Swimlanes') {
    const button = wrapper!.findAll('.k-view-toggle button').find((entry) => entry.text().endsWith(label));
    await button!.trigger('click');
    await flushPromises();
  }

  const teamField = () => wrapper!.find('[data-filter="team"] .q-field');
  const cardIds = () => wrapper!.findAll('[data-card-id]').map((entry) => entry.attributes('data-card-id')).sort();
  const clearButton = () => wrapper!.findAll('button').find((entry) => entry.text().includes('Clear ('));

  it('shows every team in Swimlanes with the Team select disabled, and fetches without team', async () => {
    const { kanban } = await mountWithTeam();
    expect(cardIds()).toEqual(['b']);

    await chooseLayout('Swimlanes');

    expect(getKanbanBoard).toHaveBeenCalledTimes(1);
    expect(getKanbanBoard.mock.calls[0]![0]).toEqual({});
    expect(wrapper!.findAll('.k-swim-team').map((row) => row.text())).toEqual(['ALPHA', 'BETA', 'GAMMA']);
    expect(cardIds()).toEqual(['a', 'b', 'c']);
    expect(teamField().classes()).toContain('q-field--disabled');
    expect(wrapper!.find('[data-filter="team"]').text()).toContain('Swimlanes show every team');
    expect(wrapper!.find('[data-filter="team"]').attributes('title')).toBe('Swimlanes show every team');
    expect(kanban.filters).toEqual({ team: 'beta' });
  });

  it('narrows to the kept team again back on Board, with the select enabled and still set', async () => {
    const { kanban } = await mountWithTeam();
    await chooseLayout('Swimlanes');
    getKanbanBoard.mockClear();

    await chooseLayout('Board');

    expect(getKanbanBoard).toHaveBeenCalledWith({ team: 'beta' });
    expect(wrapper!.find('.k-lanes').exists()).toBe(true);
    expect(cardIds()).toEqual(['b']);
    expect(teamField().classes()).not.toContain('q-field--disabled');
    expect(wrapper!.find('[data-filter="team"]').attributes('title')).toBeUndefined();
    expect(wrapper!.find('[data-filter="team"]').text()).not.toContain('Swimlanes show every team');
    expect(teamField().text()).toContain('BETA');
    expect(kanban.filters).toEqual({ team: 'beta' });
  });

  it('refetches nothing on a layout switch that changes no filter in effect', async () => {
    serveBoard();
    await mountBoard(everyCard, teams());

    await chooseLayout('Swimlanes');
    await chooseLayout('Board');

    expect(getKanbanBoard).not.toHaveBeenCalled();
  });

  it('still applies Member and Status in Swimlanes', async () => {
    const { kanban } = await mountWithTeam('swimlanes');

    kanban.setFilters({ member: 'Dev1', status: 'running' });
    await flushPromises();

    expect(getKanbanBoard).toHaveBeenLastCalledWith({ member: 'Dev1', status: 'running' });
  });

  it('counts the kept team on Board and not in Swimlanes', async () => {
    const { kanban } = await mountWithTeam();
    kanban.setFilters({ status: 'running' });
    await flushPromises();
    expect(clearButton()!.text()).toContain('Clear (2)');

    await chooseLayout('Swimlanes');
    expect(clearButton()!.text()).toContain('Clear (1)');

    kanban.setFilters({ status: '' });
    await flushPromises();
    expect(clearButton()).toBeUndefined();
    expect(kanban.filters).toEqual({ team: 'beta' });
  });

  it('Clear empties everything on Board, the team included', async () => {
    const { kanban } = await mountWithTeam();

    await clearButton()!.trigger('click');
    await flushPromises();

    expect(kanban.filters).toEqual({});
    expect(clearButton()).toBeUndefined();
    expect(getKanbanBoard).toHaveBeenLastCalledWith({});
  });

  it('Clear empties everything in Swimlanes, the kept team included', async () => {
    const { kanban } = await mountWithTeam('swimlanes');
    kanban.setText('card');
    await flushPromises();
    expect(clearButton()!.text()).toContain('Clear (1)');

    await clearButton()!.trigger('click');
    await flushPromises();

    expect(kanban.filters).toEqual({});
    expect(kanban.text).toBe('');
    expect(clearButton()).toBeUndefined();

    // Nothing is left for Board to apply either.
    await chooseLayout('Board');
    expect(clearButton()).toBeUndefined();
    expect(cardIds()).toEqual(['a', 'b', 'c']);
  });

  it("a team tab's Kanban button keeps Swimlanes and every row, and scrolls to that team's row", async () => {
    serveBoard();
    const scrolled: string[] = [];
    vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(function (this: Element) {
      scrolled.push(this.getAttribute('data-team-row') ?? '');
    });
    const { kanban } = await mountBoard(everyCard, teams(), 'swimlanes');

    kanban.openForTeam('gamma');
    await flushPromises();

    expect(kanban.view).toBe('swimlanes');
    expect(kanban.filters).toEqual({ team: 'gamma' });
    expect(getKanbanBoard).toHaveBeenLastCalledWith({});
    expect(wrapper!.findAll('.k-swim-team').map((row) => row.text())).toEqual(['ALPHA', 'BETA', 'GAMMA']);
    expect(cardIds()).toEqual(['a', 'b', 'c']);
    expect(scrolled).toEqual(['gamma']);

    // Once shown it is done: a later refetch does not pull the page back to it.
    kanban.refreshIfActive();
    await flushPromises();
    expect(scrolled).toEqual(['gamma']);

    // And on Board the team it set applies.
    await chooseLayout('Board');
    expect(cardIds()).toEqual(['c']);
  });

  it("a team tab's Kanban button on Board narrows to that team and scrolls nothing", async () => {
    serveBoard();
    const scroll = vi.spyOn(Element.prototype, 'scrollIntoView').mockImplementation(() => {});
    const { kanban } = await mountBoard(everyCard, teams());

    kanban.openForTeam('gamma');
    await flushPromises();

    expect(kanban.view).toBe('board');
    expect(getKanbanBoard).toHaveBeenLastCalledWith({ team: 'gamma' });
    expect(cardIds()).toEqual(['c']);
    expect(scroll).not.toHaveBeenCalled();
  });
});
