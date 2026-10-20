// @vitest-environment happy-dom
//
// THE BOARD'S CARDS ARRANGED BY DRAGGING THEIR HANDLE. The rules are `lib/boardOrder`'s, tested there
// without a mount; this file asserts the board wires them: the cards are laid in the stored order, a
// handle drag carries the card and writes `harness.boardOrder.<team>` once on release (nothing
// before), Escape cancels, the arrow keys on a handle move the card a place, a card mounted with no
// board shows no handle, and the Board display dialog's Reset arrangement forgets every team's order.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { listContainerTriggers } = vi.hoisted(() => ({ listContainerTriggers: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listContainerTriggers,
}));

import BoardGrid from '../BoardGrid.vue';
import BoardDisplayDialog from '../BoardDisplayDialog.vue';
import ContainerCard from '../ContainerCard.vue';
import { boardOrderKey } from '../../lib/boardOrder';
import { asMemberId, asTeamId, type ContainerSnapshot } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';

const Team = 'alpha';
const Key = boardOrderKey(Team);

const member = (id: string): ContainerSnapshot => ({
  team: asTeamId(Team),
  id: asMemberId(id),
  name: id,
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
});

let wrapper: VueWrapper | undefined;
let stored: Record<string, string> = {};
let written: string[] = [];

function useStorage(initial: Record<string, string> = {}) {
  stored = { ...initial };
  written = [];
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => (k in stored ? stored[k] : null),
    setItem: (k: string, v: string) => {
      written.push(k);
      stored[k] = v;
    },
    removeItem: (k: string) => {
      delete stored[k];
    },
    key: (i: number) => Object.keys(stored)[i] ?? null,
    get length() {
      return Object.keys(stored).length;
    },
    clear: () => {
      stored = {};
    },
  });
}

const writes = () => written.filter((key) => key === Key).length;

/** The cards as laid, by member id. */
function laid(): string[] {
  return wrapper!.findAll('[data-member-card]').map((card) => card.attributes('data-member-card')!.split('/').pop()!);
}

function handleOf(id: string) {
  return wrapper!.get(`[data-member-card$="/${id}"] [data-test="card-arrange-handle"]`).element as HTMLElement;
}

function pointer(target: Element, type: string, x: number, y: number): void {
  target.dispatchEvent(new PointerEvent(type, {
    bubbles: true, cancelable: true, pointerId: 1, pointerType: 'mouse', button: 0, clientX: x, clientY: y,
  }));
}

function key(target: Element, name: string) {
  target.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }));
}

/**
 * happy-dom lays nothing out, so each card's box is given: three 380x520 cards in one row with the
 * board's 16px gutter, in the order they are laid.
 */
function layOut() {
  const cards = [...document.querySelectorAll<HTMLElement>('[data-member-card]')];
  cards.forEach((card, i) => {
    const left = i * 396;
    card.getBoundingClientRect = () => ({
      left, right: left + 380, top: 0, bottom: 520, width: 380, height: 520, x: left, y: 0, toJSON: () => ({}),
    }) as DOMRect;
  });
}

async function mountBoard(names = ['Manager', 'Builder', 'Tester']) {
  wrapper = mount(BoardGrid, {
    props: { containers: names.map(member) },
    global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true, LiveViewDialog: true } },
    attachTo: document.body,
  });
  await flushPromises();
  layOut();
  return wrapper;
}

beforeEach(() => {
  useStorage();
  setActivePinia(createPinia());
  listContainerTriggers.mockResolvedValue([]);
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  resetBody();
});

describe('the arrangement the board lays', () => {
  it('is the roster when nothing is stored, and the stored order first then the rest when something is', async () => {
    await mountBoard();
    expect(laid()).toEqual(['Manager', 'Builder', 'Tester']);
    wrapper!.unmount();

    setActivePinia(createPinia());
    useStorage({ [Key]: JSON.stringify(['Tester', 'Gone']) });
    await mountBoard();
    expect(laid()).toEqual(['Tester', 'Manager', 'Builder']);
  });
});

describe('dragging a handle', () => {
  it('carries the card, shows where it lands, and writes the arrangement once on release', async () => {
    await mountBoard();
    const handle = handleOf('Manager');

    pointer(handle, 'pointerdown', 10, 10);
    pointer(handle, 'pointermove', 700, 60); // over Builder's right half: after it, before Tester
    await flushPromises();

    const card = wrapper!.get('[data-member-card$="/Manager"]');
    expect(card.classes()).toContain('board-card--dragging');
    expect(card.attributes('style')).toContain('translate(690px, 50px)');
    const indicator = wrapper!.get('[data-test="board-drop-indicator"]');
    // Centred in the gutter before Tester (left 792): 792 - 8 - 1.
    expect(indicator.attributes('style')).toContain('left: 783px');
    expect(writes()).toBe(0);

    pointer(handle, 'pointerup', 700, 60);
    await flushPromises();

    expect(laid()).toEqual(['Builder', 'Manager', 'Tester']);
    expect(stored[Key]).toBe(JSON.stringify(['Builder', 'Manager', 'Tester']));
    expect(writes()).toBe(1);
    expect(wrapper!.find('[data-test="board-drop-indicator"]').exists()).toBe(false);
    expect(wrapper!.get('[data-member-card$="/Manager"]').classes()).not.toContain('board-card--dragging');
  });

  it('let go past the last card puts it last, and on its own place changes nothing', async () => {
    await mountBoard();
    pointer(handleOf('Manager'), 'pointerdown', 10, 10);
    pointer(handleOf('Manager'), 'pointermove', 1300, 60);
    pointer(handleOf('Manager'), 'pointerup', 1300, 60);
    await flushPromises();
    expect(laid()).toEqual(['Builder', 'Tester', 'Manager']);
    expect(writes()).toBe(1);

    layOut();
    pointer(handleOf('Manager'), 'pointerdown', 10, 10);
    pointer(handleOf('Manager'), 'pointermove', 1100, 60); // on Tester's right half: after it, which is where it is
    pointer(handleOf('Manager'), 'pointerup', 1100, 60);
    await flushPromises();
    expect(laid()).toEqual(['Builder', 'Tester', 'Manager']);
    expect(writes()).toBe(2); // written, unchanged: the drag ended on a place
  });

  it('is cancelled by Escape or a cancelled pointer, with nothing written', async () => {
    await mountBoard();
    const handle = handleOf('Manager');

    pointer(handle, 'pointerdown', 10, 10);
    pointer(handle, 'pointermove', 700, 60);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    await flushPromises();
    expect(wrapper!.get('[data-member-card$="/Manager"]').classes()).not.toContain('board-card--dragging');
    expect(laid()).toEqual(['Manager', 'Builder', 'Tester']);

    pointer(handle, 'pointerdown', 10, 10);
    pointer(handle, 'pointermove', 700, 60);
    pointer(handle, 'pointercancel', 700, 60);
    await flushPromises();
    expect(laid()).toEqual(['Manager', 'Builder', 'Tester']);
    expect(writes()).toBe(0);
  });

  it('a press on a handle reaches nothing beneath it', async () => {
    await mountBoard();
    const handle = handleOf('Manager');
    const event = new PointerEvent('pointerdown', { bubbles: true, cancelable: true, pointerId: 1, pointerType: 'mouse', button: 0 });
    handle.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    pointer(handle, 'pointerup', 0, 0);
  });
});

describe('the keyboard on a handle', () => {
  it('moves the card a place with the arrows, to the ends with Home and End, and writes each move', async () => {
    await mountBoard();

    key(handleOf('Manager'), 'ArrowRight');
    await flushPromises();
    expect(laid()).toEqual(['Builder', 'Manager', 'Tester']);

    key(handleOf('Manager'), 'ArrowDown');
    await flushPromises();
    expect(laid()).toEqual(['Builder', 'Tester', 'Manager']);

    key(handleOf('Manager'), 'Home');
    await flushPromises();
    expect(laid()).toEqual(['Manager', 'Builder', 'Tester']);

    key(handleOf('Manager'), 'End');
    await flushPromises();
    expect(laid()).toEqual(['Builder', 'Tester', 'Manager']);

    key(handleOf('Builder'), 'ArrowLeft');
    await flushPromises();
    expect(laid()).toEqual(['Builder', 'Tester', 'Manager']); // already first
    expect(stored[Key]).toBe(JSON.stringify(['Builder', 'Tester', 'Manager']));
    expect(writes()).toBe(5);
  });

  it('names the card it moves', async () => {
    await mountBoard();
    expect(handleOf('Builder').getAttribute('aria-label')).toBe("Move Builder's card");
  });
});

describe('a card with no board around it', () => {
  it('shows no handle', async () => {
    wrapper = mount(ContainerCard, {
      props: { snapshot: member('Manager'), feed: [] },
      global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true, LiveViewDialog: true } },
      attachTo: document.body,
    });
    await flushPromises();
    expect(wrapper.find('[data-test="card-arrange-handle"]').exists()).toBe(false);
  });
});

describe("the Board display dialog's Reset arrangement", () => {
  it('forgets every stored arrangement and lays the roster again', async () => {
    useStorage({ [Key]: JSON.stringify(['Tester']), [boardOrderKey('beta')]: JSON.stringify(['x']) });
    setActivePinia(createPinia());
    await mountBoard();
    expect(laid()).toEqual(['Tester', 'Manager', 'Builder']);

    const dialog = await mountDialog(BoardDisplayDialog, { modelValue: true }, { pinia: false });
    await flushPromises();
    const reset = [...document.querySelectorAll<HTMLElement>('[data-test="reset-arrangement"]')].pop()!;
    reset.click();
    await flushPromises();

    expect(laid()).toEqual(['Manager', 'Builder', 'Tester']);
    expect(Key in stored).toBe(false);
    expect(boardOrderKey('beta') in stored).toBe(false);
    dialog.unmount();
  });
});
