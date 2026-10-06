// @vitest-environment happy-dom
//
// THE BOARD, ITS CARDS, THE CARD PANEL AND THE FILTER BAR, MOUNTED TOGETHER through the real
// store. The colour rule, the lane placement, the search matcher and the FLIP arithmetic are pure
// functions with their own tests; this file asserts what the components render from them and what
// each control calls.
//
// What is left out is CSS: the colour transition and the awaiting-manager ring are stylesheet
// facts, and `styles-match-templates.spec.ts` is what guards a selector against matching nothing.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { notify } = vi.hoisted(() => ({ notify: vi.fn() }));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify }),
}));

import KanbanBoard from '../KanbanBoard.vue';
import { useKanbanStore } from '../../stores/kanban';
import { colourFor } from '../../lib/kanban';
import { flipTransform } from '../../lib/flip';
import type { KanbanCard, KanbanCardDetail } from '../../api/kanban';
import { resetBody } from '../../test/mountQuasar';

function card(over: Partial<KanbanCard> & { id: string }): KanbanCard {
  return {
    team: 'alpha',
    member: 'DeveloperInes',
    item: null,
    workflowSeq: 2226,
    title: `Card ${over.id}`,
    body: '',
    status: 'queued',
    laneId: 'todo',
    progress: [],
    createdAt: '2026-09-18T00:00:00Z',
    updatedAt: '2026-09-18T00:00:00Z',
    awaitingManager: false,
    paused: false,
    ...over,
  } as KanbanCard;
}

const lanes = [
  { id: 'todo', title: 'To do' },
  { id: 'doing', title: 'Doing' },
  { id: 'done', title: 'Done' },
];

let wrapper: VueWrapper | undefined;
let kanban: ReturnType<typeof useKanbanStore>;

beforeEach(() => {
  notify.mockReset();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function mountBoard(cards: KanbanCard[] | null, laneList = lanes) {
  setActivePinia(createPinia());
  kanban = useKanbanStore();
  kanban.board = cards === null ? null : { lanes: laneList, cards };

  vi.spyOn(kanban, 'select').mockImplementation(async (id: string) => {
    kanban.selectedId = id;
    kanban.detail = null;
  });
  for (const name of ['edit', 'move', 'comment'] as const) vi.spyOn(kanban, name).mockResolvedValue();
  vi.spyOn(kanban, 'setFilters').mockImplementation(() => {});
  vi.spyOn(kanban, 'clearFilters').mockImplementation(() => {});

  wrapper = mount(KanbanBoard, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

const laneTitles = (board: VueWrapper) => board.findAll('.k-lane-title').map((t) => t.text());
const cardsIn = (board: VueWrapper, lane: number) =>
  board.findAll('.k-lane')[lane]!.findAll('.k-card').map((c) => c.attributes('data-card-id'));

async function openCard(board: VueWrapper, id: string, detail: Partial<KanbanCardDetail> = {}) {
  await board.find(`[data-card-id="${id}"]`).trigger('click');
  kanban.detail = { ...kanban.selectedCard!, ...detail } as KanbanCardDetail;
  await flushPromises();
}

const panel = () => document.body.querySelector('.k-panel') as HTMLElement | null;
const panelText = () => panel()?.textContent?.replace(/\s+/g, ' ') ?? '';

function panelButton(label: string): HTMLButtonElement {
  const found = [...panel()!.querySelectorAll('button')]
    .find((b) => b.querySelector('.block')?.textContent?.trim() === label);
  if (!found) throw new Error(`no "${label}" button in the card panel`);
  return found as HTMLButtonElement;
}

async function typeInto(label: string, value: string) {
  const input = wrapper!.findAllComponents({ name: 'QInput' }).find((i) => i.props('label') === label)!;
  input.vm.$emit('update:modelValue', value);
  await flushPromises();
}

describe('the board', () => {
  /** THE LANES COME FROM THE PAYLOAD, never from a list in the component. */
  it('draws the lanes the board payload names, in its order, with each card in its lane', async () => {
    const board = await mountBoard(
      [card({ id: '1' }), card({ id: '2', laneId: 'doing' })],
      [{ id: 'doing', title: 'Being worked' }, { id: 'todo', title: 'Waiting' }],
    );

    expect(laneTitles(board)).toEqual(['Being worked', 'Waiting']);
    expect(cardsIn(board, 0)).toEqual(['2']);
    expect(cardsIn(board, 1)).toEqual(['1']);
    expect(board.findAll('.k-lane-count').map((c) => c.text())).toEqual(['1', '1']);
  });

  it('says there is no board yet, and where it comes from', async () => {
    const board = await mountBoard(null);

    expect(board.text()).toContain('No board yet');
    expect(board.text()).toContain('/api/kanban/board');
  });

  /** The search box narrows the view and not the fetch, so an empty result names it too. */
  it('says nothing matches, naming the search box, when every card is filtered out', async () => {
    const board = await mountBoard([card({ id: '1', title: 'Read spec' })]);
    kanban.setText('no such words');
    await flushPromises();

    expect(board.text()).toContain('Nothing matches this filter');
    expect(board.text()).toContain('search box');
    expect(board.findAll('.k-card')).toHaveLength(0);
  });

  it('shows no sample-data banner', async () => {
    const board = await mountBoard([card({ id: '1' })]);

    expect(board.text()).not.toContain('Sample data');
  });

  /**
   * FLIP: a card that changes lane is drawn at its OLD place, then released to travel. Measured by
   * the card's stable id, so the animation follows the same card across a replay of the board.
   */
  describe('moving a card between lanes', () => {
    beforeEach(() => {
      // A card's box is its lane's column: 300px per lane, which happy-dom cannot lay out itself.
      vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
        const lane = this.closest('.k-lane');
        // Among the LANES only: a divider sits between every two lanes.
        const index = lane ? [...lane.parentElement!.querySelectorAll(':scope > .k-lane')].indexOf(lane) : 0;
        return { left: index * 300, top: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0, toJSON() {} } as DOMRect;
      });
    });

    it('starts the moved card at its old position and lets it travel on the next frame', async () => {
      const frames: FrameRequestCallback[] = [];
      vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => frames.push(callback));
      const board = await mountBoard([card({ id: '1' }), card({ id: '2' })]);

      kanban.board!.cards = [card({ id: '1', laneId: 'done' }), card({ id: '2' })];
      await flushPromises();

      const moved = board.find('[data-card-id="1"]').element as HTMLElement;
      expect(moved.style.transform).toBe(flipTransform({ dx: -600, dy: 0 }));
      expect((board.find('[data-card-id="2"]').element as HTMLElement).style.transform).toBe('');

      frames.forEach((frame) => frame(0));
      expect(moved.style.transform).toBe('');
      expect(moved.style.transition).toContain('transform');
    });

    it('does not animate under prefers-reduced-motion', async () => {
      vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('reduce'), media: query }));
      const board = await mountBoard([card({ id: '1' })]);

      kanban.board!.cards = [card({ id: '1', laneId: 'done' })];
      await flushPromises();

      expect((board.find('[data-card-id="1"]').element as HTMLElement).style.transform).toBe('');
    });
  });
});

describe('a card', () => {
  it('shows the member, its id, the workflow seq and the progress count', async () => {
    const board = await mountBoard([
      card({ id: '2316', progress: [{ at: '2026-09-18T00:00:00Z', text: 'read spec' }, { at: '2026-09-18T00:01:00Z', text: 'wrote test' }] }),
    ]);
    const shown = board.find('.k-card');

    expect(shown.find('.k-card-member').text()).toBe('DeveloperInes');
    expect(shown.find('.k-card-id').text()).toBe('card 2316');
    expect(shown.find('.k-card-workflow').text()).toBe('#2226');
    expect(shown.find('.k-card-progress').text()).toContain('2');
  });

  it('says Unassigned for a card nobody has been given', async () => {
    const board = await mountBoard([card({ id: '1', member: null as never })]);

    expect(board.find('.k-card-member').text()).toBe('Unassigned');
  });

  /** On an attribute the template spells, from the shared rule - never a class built in a binding. */
  it('takes its colour from the shared rule, on an attribute', async () => {
    const shown = card({ id: '1', status: 'blocked' as never });
    const board = await mountBoard([shown]);

    expect(board.find('.k-card').attributes('data-colour')).toBe(colourFor(shown));
    expect(board.find('.k-card').classes().every((cls) => !cls.startsWith('k-card--'))).toBe(true);
  });

  /** An overlay beside the status, never a status of its own. */
  it('marks awaiting-manager as an attribute and a badge, keeping its status', async () => {
    const board = await mountBoard([card({ id: '1', status: 'running' as never, awaitingManager: true })]);
    const shown = board.find('.k-card');

    expect(shown.attributes('data-awaiting')).toBe('yes');
    expect(shown.find('.k-card-awaiting').exists()).toBe(true);
    expect(shown.find('.k-card-status').text()).not.toMatch(/awaiting/i);
  });
});

describe('the card panel', () => {
  it('opens as a full-height side panel, coloured like its card', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    await openCard(board, '1');

    const dialog = board.findAllComponents({ name: 'QDialog' }).find((d) => d.props('modelValue') === true)!;
    expect(dialog.props('position')).toBe('right');
    expect(dialog.props('fullHeight')).toBe(true);
    expect(panel()!.dataset.colour).toBe(colourFor(kanban.selectedCard!));
  });

  /** The body a person opens the panel to read; a field nobody renders looks exactly like an empty one. */
  it('renders the card body beneath the title, whitespace kept, and no section for an empty one', async () => {
    const board = await mountBoard([card({ id: '1', body: '1. read\n   2. write' }), card({ id: '2' })]);
    await openCard(board, '1');

    expect(panel()!.querySelector('.k-panel-description')?.textContent).toBe('1. read\n   2. write');

    await openCard(board, '2');
    expect(panel()!.querySelector('.k-panel-description')).toBeNull();
  });

  it('shows the trail and the attributes', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    await openCard(board, '1', {
      trail: [{ seq: 9, type: 'kanban.card.commented', occurredAt: '2026-09-18T01:00:00Z', text: 'Looks right' }],
      attributes: { hiredFor: 'backend' } as never,
    });

    expect(panel()!.querySelectorAll('.k-trail-row')).toHaveLength(1);
    expect(panelText()).toContain('Looks right');
    expect(panelText()).toContain('Hired For');
    expect(panelText()).toContain('backend');
  });

  it('saves a title and a note as one edit', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    await openCard(board, '1');

    await typeInto('Title', 'Read the whole spec');
    await typeInto('Note (goes with the edit or the move)', 'see section 7');
    panelButton('Save').click();
    await flushPromises();

    expect(kanban.edit).toHaveBeenCalledWith('1', { title: 'Read the whole spec', note: 'see section 7' });
  });

  it('moves a card to the chosen lane, and comments', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    await openCard(board, '1');

    const lane = board.findAllComponents({ name: 'QSelect' }).find((s) =>
      (s.props('options') as { value: string }[]).some((o) => o.value === 'doing'))!;
    lane.vm.$emit('update:modelValue', 'doing');
    await flushPromises();
    panelButton('Move').click();
    await flushPromises();

    await typeInto('Comment', 'Picked up');
    panelButton('Add comment').click();
    await flushPromises();

    expect(kanban.move).toHaveBeenCalledWith('1', 'doing', undefined);
    expect(kanban.comment).toHaveBeenCalledWith('1', 'Picked up');
  });

  /** It posted a field the server had no property for; its replacement is that every edit wakes the Manager. */
  it('has no instruction box, and says an edit wakes the Manager - unless there is none', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    await openCard(board, '1');

    expect(panelText()).not.toContain('Send instruction');
    expect(panelText()).toContain("wakes this team's Manager");
    expect(panelText()).toContain('A team with no Manager records it and wakes nobody.');
  });

  /** A half-typed note must not be posted against the next card opened. */
  it('resets the form when the open card changes', async () => {
    const board = await mountBoard([card({ id: '1' }), card({ id: '2' })]);
    await openCard(board, '1');
    await typeInto('Note (goes with the edit or the move)', 'for card 1');

    await openCard(board, '2');

    const note = board.findAllComponents({ name: 'QInput' })
      .find((i) => i.props('label') === 'Note (goes with the edit or the move)')!;
    expect(note.props('modelValue')).toBe('');
  });

  /** These are appends: a silent retry appends twice. */
  it('reports a refused append once, and does not retry it', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout'] });
    const board = await mountBoard([card({ id: '1' })]);
    vi.mocked(kanban.comment).mockRejectedValueOnce(new Error('refused'));
    await openCard(board, '1');

    await typeInto('Comment', 'Picked up');
    panelButton('Add comment').click();
    await flushPromises();
    vi.advanceTimersByTime(60_000);
    await flushPromises();
    vi.useRealTimers();

    expect(kanban.comment).toHaveBeenCalledTimes(1);
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'negative', message: 'refused' }));
  });
});

describe('the filter bar', () => {
  const labels = (board: VueWrapper) => [
    ...board.findAllComponents({ name: 'QSelect' }).map((s) => s.props('label')),
    ...board.findAllComponents({ name: 'QInput' }).map((i) => i.props('label')),
  ];

  it('offers Team, Member, Status and Search, and no workflow box or date picker', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    const bar = board.find('.k-filters');

    expect(labels(board)).toEqual(expect.arrayContaining(['Team', 'Member', 'Status', 'Search']));
    for (const gone of ['From', 'To', 'Workflow']) expect(labels(board)).not.toContain(gone);
    expect(bar.find('input[type="date"]').exists()).toBe(false);
  });

  /** The selects refetch through the store; the search box is view state and leaves the cards alone. */
  it('refetches on a select, and narrows only the view on search', async () => {
    const board = await mountBoard([card({ id: '1', title: 'Read spec' }), card({ id: '2', title: 'Write test' })]);

    // A MULTIPLE SELECT: its list goes to the store as one comma-separated value.
    const team = board.findAllComponents({ name: 'QSelect' }).find((s) => s.props('label') === 'Team')!;
    for (const label of ['Team', 'Member', 'Status', 'Outcome']) {
      expect(board.findAllComponents({ name: 'QSelect' }).find((s) => s.props('label') === label)!.props('multiple')).toBe(true);
    }
    team.vm.$emit('update:modelValue', ['alpha']);
    expect(kanban.setFilters).toHaveBeenCalledWith({ team: 'alpha' });
    team.vm.$emit('update:modelValue', ['alpha', 'beta']);
    expect(kanban.setFilters).toHaveBeenLastCalledWith({ team: 'alpha,beta' });
    team.vm.$emit('update:modelValue', null);
    expect(kanban.setFilters).toHaveBeenLastCalledWith({ team: '' });

    await typeInto('Search', 'Read');
    expect(kanban.text).toBe('Read');
    expect(kanban.board!.cards).toHaveLength(2);
    expect(board.findAll('.k-card').map((c) => c.attributes('data-card-id'))).toEqual(['1']);
  });

  it('draws no hint under the search box', async () => {
    const board = await mountBoard([card({ id: '1' })]);

    expect(board.findAllComponents({ name: 'QInput' }).find((i) => i.props('label') === 'Search')!.props('hint'))
      .toBeUndefined();
    expect(board.text()).not.toContain('Matches title');
  });

  /** A box left typed in must not leave Clear hidden. */
  it('counts the search box in Clear, and clears through the store', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    expect(board.find('.k-filters').text()).not.toContain('Clear');

    await typeInto('Search', 'Read');
    const clear = board.findAll('.k-filters button').find((b) => b.text().includes('Clear'))!;
    expect(clear.text()).toContain('Clear (1)');

    await clear.trigger('click');
    expect(kanban.clearFilters).toHaveBeenCalled();
  });
});

// ONE WIDTH FOR EVERY LANE: a divider between two lanes sets it, the board carries it as a CSS
// variable every lane reads, and the browser remembers it. A long outcome once stretched every lane.
describe('the lane width', () => {
  beforeEach(() => {
    try { window.localStorage.removeItem('kanban.laneWidth'); } catch { /* none */ }
  });

  const width = (board: VueWrapper) => (board.find('.k-board').element as HTMLElement).style.getPropertyValue('--k-lane-width');

  it('starts at 300px, with one divider between every two lanes', async () => {
    const board = await mountBoard([card({ id: '1' })]);

    expect(width(board)).toBe('300px');
    expect(board.findAll('[data-test="lane-divider"]')).toHaveLength(board.findAll('.k-lane').length - 1);
  });

  it('widens every lane by dragging a divider, and remembers the width', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    const divider = board.find('[data-test="lane-divider"]');

    await divider.trigger('pointerdown', { clientX: 300, pointerId: 1 });
    divider.element.dispatchEvent(new PointerEvent('pointermove', { clientX: 400, pointerId: 1 }));
    divider.element.dispatchEvent(new PointerEvent('pointerup', { clientX: 400, pointerId: 1 }));
    await flushPromises();

    expect(width(board)).toBe('400px');
    expect(window.localStorage.getItem('kanban.laneWidth')).toBe('400');
  });

  it('opens at the remembered width', async () => {
    window.localStorage.setItem('kanban.laneWidth', '360');
    const board = await mountBoard([card({ id: '1' })]);

    expect(width(board)).toBe('360px');
  });

  it('moves with the arrow keys, and Home or a double-click resets it', async () => {
    const board = await mountBoard([card({ id: '1' })]);
    const divider = board.find('[data-test="lane-divider"]');

    await divider.trigger('keydown', { key: 'ArrowRight' });
    expect(width(board)).toBe('316px');
    await divider.trigger('keydown', { key: 'ArrowLeft' });
    await divider.trigger('keydown', { key: 'ArrowLeft' });
    expect(width(board)).toBe('284px');
    await divider.trigger('keydown', { key: 'Home' });
    expect(width(board)).toBe('300px');

    await divider.trigger('keydown', { key: 'ArrowRight' });
    await divider.trigger('dblclick');
    expect(width(board)).toBe('300px');
    expect(window.localStorage.getItem('kanban.laneWidth')).toBe('300');
  });
});
