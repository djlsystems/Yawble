// @vitest-environment happy-dom
//
// THE KANBAN WITHOUT ARCHIVED TEAMS, MOUNTED: an archived team's cards never show in Board or
// Swimlanes, it has no swimlane row, and the Team filter offers active teams only. The board the
// server answers still carries the archived team's card; the browser leaves it out.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

const { getKanbanBoard } = vi.hoisted(() => ({
  getKanbanBoard: vi.fn((): Promise<unknown> => new Promise<never>(() => {})),
}));

vi.mock('../../api/kanban', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getKanbanBoard: () => getKanbanBoard(),
}));

import KanbanBoard from '../KanbanBoard.vue';
import KanbanFilterBar from '../KanbanFilterBar.vue';
import { useKanbanStore } from '../../stores/kanban';
import { useConsoleStore } from '../../stores/console';
import { useWipStore } from '../../stores/wip';
import type { KanbanCard } from '../../api/kanban';
import type { Team } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

function card(id: string, team: string, member: string): KanbanCard {
  return {
    id,
    team,
    member,
    item: null,
    workflowSeq: 10,
    title: `Card ${id}`,
    body: '',
    status: 'queued',
    laneId: 'todo',
    progress: [],
    createdAt: '2026-09-23T00:00:00Z',
    updatedAt: '2026-09-23T00:00:00Z',
    awaitingManager: false,
    paused: false,
  } as KanbanCard;
}

const lanes = [
  { id: 'todo', title: 'Todo' },
  { id: 'done', title: 'Done' },
];

const team = (id: string, member: string, archived = false) =>
  ({ id, name: id.toUpperCase(), scope: 'team', containers: [{ id: member, team: id }], archived }) as unknown as Team;

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

async function mountBoard(view: 'board' | 'swimlanes') {
  setActivePinia(createPinia());
  const kanban = useKanbanStore();
  useConsoleStore().$patch({ teams: [team('alpha', 'Dev1'), team('beta', 'Builder', true)] });
  const wip = useWipStore();
  vi.spyOn(wip, 'refresh').mockResolvedValue();
  wip.view = { max: 4, running: [], waiting: [] };
  kanban.board = { lanes, cards: [card('a', 'alpha', 'Dev1'), card('b', 'beta', 'Builder')] };
  kanban.view = view;
  vi.spyOn(kanban, 'select').mockResolvedValue();

  wrapper = mount(KanbanBoard, { attachTo: document.body });
  await flushPromises();
  return { wrapper, kanban };
}

const shownCards = () => [...document.body.querySelectorAll('[data-card-id]')].map((node) => node.getAttribute('data-card-id'));

describe('the Kanban with an archived team', () => {
  it('never shows the archived team\'s cards on the Board, and counts only what it shows', async () => {
    const { kanban } = await mountBoard('board');

    expect(shownCards()).toEqual(['a']);
    expect(kanban.cardCount).toBe(1);
  });

  it('gives the archived team no row and no cards in Swimlanes', async () => {
    await mountBoard('swimlanes');

    const rows = [...document.body.querySelectorAll('[data-team-row]')].map((node) => node.getAttribute('data-team-row'));
    expect(rows).toEqual(['alpha']);
    expect(shownCards()).toEqual(['a']);
  });

  it('offers active teams only in the Team filter, and only their members', async () => {
    const { wrapper, kanban } = await mountBoard('board');

    const teamSelect = wrapper.findComponent(KanbanFilterBar).findAllComponents(QSelect).find((select) => select.props('label') === 'Team')!;
    const offered = (teamSelect.props('options') as { value: string }[]).map((option) => option.value);
    expect(offered).toEqual(['alpha']);
    expect(kanban.teamOptions).toEqual(['alpha']);
    expect(kanban.memberOptions).toEqual(['Dev1']);
  });
});
