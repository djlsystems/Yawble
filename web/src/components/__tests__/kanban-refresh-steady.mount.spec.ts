// @vitest-environment happy-dom
//
// THE HEADER ROW HOLDS STILL WHILE THE BOARD REFRESHES. Every push re-reads the board, and a
// spinner that came and went beside the Board / Swimlanes toggle on each read pushed the toggle
// sideways in and out several times a second. The row now renders the same nodes whether a read
// is in flight or not.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

// The board fetch, held open until a test answers it.
const { getKanbanBoard } = vi.hoisted(() => ({
  getKanbanBoard: vi.fn((_filters?: Record<string, string>): Promise<unknown> => new Promise<never>(() => {})),
}));

vi.mock('../../api/kanban', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getKanbanBoard: (filters?: Record<string, string>) => getKanbanBoard(filters),
}));

import KanbanBoard from '../KanbanBoard.vue';
import { useKanbanStore } from '../../stores/kanban';
import { useWipStore } from '../../stores/wip';
import { resetBody } from '../../test/mountQuasar';

const lanes = [
  { id: 'todo', title: 'Todo' },
  { id: 'done', title: 'Done' },
];

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

/** The row the Board / Swimlanes toggle sits in, as markup. */
function headerRow(w: VueWrapper): string {
  return w.find('.k-view-toggle').element.parentElement!.outerHTML;
}

describe('a board refresh', () => {
  it('shows no progress indicator and leaves the Board / Swimlanes row exactly as it was', async () => {
    setActivePinia(createPinia());
    const kanban = useKanbanStore();
    vi.spyOn(useWipStore(), 'refresh').mockResolvedValue();
    kanban.board = { lanes, cards: [] };

    wrapper = mount(KanbanBoard, { attachTo: document.body });
    await flushPromises();

    const idle = headerRow(wrapper);
    expect(wrapper.find('.k-view-toggle').exists()).toBe(true);

    // A push arrives: the board is re-read, and the read is still in flight.
    let answer!: (board: unknown) => void;
    getKanbanBoard.mockImplementation(() => new Promise((resolve) => (answer = resolve)));
    const refreshing = kanban.refresh();
    await flushPromises();

    expect(kanban.loading).toBe(true);
    expect(wrapper.find('.q-spinner').exists()).toBe(false);
    expect(wrapper.find('[role="progressbar"]').exists()).toBe(false);
    expect(headerRow(wrapper)).toBe(idle);

    answer({ lanes, cards: [] });
    await refreshing;
    await flushPromises();

    expect(kanban.loading).toBe(false);
    expect(headerRow(wrapper)).toBe(idle);
  });
});
