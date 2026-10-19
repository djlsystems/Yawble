// @vitest-environment happy-dom
//
// THE BOARD'S CARDS SIZED BY DRAGGING THEIR EDGE. The arithmetic is `draggedSize` in
// `lib/boardSize`, tested there without a mount; this file asserts the board wires it: a corner drag
// reflows `--os-card-w`/`--os-card-h` on the board container while the pointer moves, writes
// `harness.boardSize` once on release and not before, every card shares the size, the keyboard and a
// double-click work on a grip, a press on a grip reaches nothing beneath it, the Board display dialog
// reads the dragged size, and a card the screen already clamps offers no width grip.
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
import { useDisplayStore } from '../../stores/display';
import {
  DefaultBoardSize,
  draggedSize,
  GripShiftStep,
  GripStep,
  WidthBounds,
} from '../../lib/boardSize';
import { asMemberId, asTeamId, type ContainerSnapshot } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

const StorageKey = 'harness.boardSize';

const member = (id: string): ContainerSnapshot => ({
  team: asTeamId('alpha'),
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

/** A storage of our own, so every write is counted (a spy on happy-dom's Storage misses some). */
let stored: Record<string, string> = {};
let written: string[] = [];

function useStorage() {
  stored = {};
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
    clear: () => {
      stored = {};
    },
  });
}

/** How many times the board size reached storage. */
const writes = () => written.filter((key) => key === StorageKey).length;

/** The variable as the store renders it, so the expectation goes through the same formatter. */
const rem = (pixels: number) => `${pixels / 16}rem`;

function gridStyle(): string {
  return wrapper!.get('[data-board-grid]').attributes('style') ?? '';
}

function pointer(target: Element, type: string, x: number, y: number, over: PointerEventInit = {}): PointerEvent {
  const event = new PointerEvent(type, {
    bubbles: true,
    cancelable: true,
    pointerId: 1,
    pointerType: 'mouse',
    button: 0,
    clientX: x,
    clientY: y,
    ...over,
  });
  target.dispatchEvent(event);
  return event;
}

function key(target: Element, name: string, shiftKey = false) {
  target.dispatchEvent(new KeyboardEvent('keydown', { key: name, shiftKey, bubbles: true, cancelable: true }));
}

async function mountBoard(names = ['Manager', 'Builder']) {
  wrapper = mount(BoardGrid, {
    props: { containers: names.map(member) },
    global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true, LiveViewDialog: true } },
    attachTo: document.body,
  });
  await flushPromises();
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

describe('dragging a card corner', () => {
  it('reflows the board container during the drag and writes storage once, on release', async () => {
    await mountBoard();
    const corner = wrapper!.get('[data-test="card-grip-corner"]').element;
    const start = { width: DefaultBoardSize.width, height: DefaultBoardSize.height };

    pointer(corner, 'pointerdown', 100, 100);
    pointer(corner, 'pointermove', 140, 130);
    await flushPromises();

    const mid = draggedSize(start, 40, 30);
    expect(gridStyle()).toContain(`--os-card-w: ${rem(mid.width)}`);
    expect(gridStyle()).toContain(`--os-card-h: ${rem(mid.height)}`);
    expect(writes()).toBe(0);

    pointer(corner, 'pointermove', 180, 220);
    await flushPromises();

    const last = draggedSize(start, 80, 120);
    expect(gridStyle()).toContain(`--os-card-w: ${rem(last.width)}`);
    expect(gridStyle()).toContain(`--os-card-h: ${rem(last.height)}`);
    expect(writes()).toBe(0);

    pointer(corner, 'pointerup', 180, 220);
    await flushPromises();

    expect(writes()).toBe(1);
    expect(JSON.parse(localStorage.getItem(StorageKey)!)).toMatchObject(last);

    // Released: a later move is not a drag.
    pointer(corner, 'pointermove', 400, 400);
    await flushPromises();
    expect(gridStyle()).toContain(`--os-card-w: ${rem(last.width)}`);
    expect(writes()).toBe(1);
  });

  it('clamps the live size to the bounds as the pointer runs past them', async () => {
    await mountBoard();
    const corner = wrapper!.get('[data-test="card-grip-corner"]').element;

    pointer(corner, 'pointerdown', 0, 0);
    pointer(corner, 'pointermove', 5000, 0);
    await flushPromises();

    expect(gridStyle()).toContain(`--os-card-w: ${rem(WidthBounds.max)}`);
    pointer(corner, 'pointerup', 5000, 0);
  });

  it('ends a cancelled drag at the size it last applied, written once', async () => {
    await mountBoard();
    const corner = wrapper!.get('[data-test="card-grip-corner"]').element;

    pointer(corner, 'pointerdown', 0, 0);
    pointer(corner, 'pointermove', 24, 48);
    pointer(corner, 'pointercancel', 24, 48);
    await flushPromises();

    const last = draggedSize(DefaultBoardSize, 24, 48);
    expect(writes()).toBe(1);
    expect(JSON.parse(localStorage.getItem(StorageKey)!)).toMatchObject(last);
    expect(gridStyle()).toContain(`--os-card-w: ${rem(last.width)}`);
  });

  it('moves only the width from the right edge and only the height from the bottom edge', async () => {
    await mountBoard();
    const display = useDisplayStore();

    const right = wrapper!.get('[data-test="card-grip-width"]').element;
    pointer(right, 'pointerdown', 0, 0);
    pointer(right, 'pointermove', 50, 70);
    pointer(right, 'pointerup', 50, 70);
    expect(display.width).toBe(DefaultBoardSize.width + 50);
    expect(display.height).toBe(DefaultBoardSize.height);

    const bottom = wrapper!.get('[data-test="card-grip-height"]').element;
    pointer(bottom, 'pointerdown', 0, 0);
    pointer(bottom, 'pointermove', 50, 70);
    pointer(bottom, 'pointerup', 50, 70);
    expect(display.width).toBe(DefaultBoardSize.width + 50);
    expect(display.height).toBe(DefaultBoardSize.height + 70);
  });
});

describe('one size for the board', () => {
  it('gives every card the grips, and a drag on one card sizes the container every card reads', async () => {
    await mountBoard(['Manager', 'Builder', 'Tester']);

    expect(wrapper!.findAll('.container-window')).toHaveLength(3);
    expect(wrapper!.findAll('[data-test="card-grips"]')).toHaveLength(3);
    // One container carries the size; no card carries its own.
    expect(wrapper!.findAll('.container-window').every((card) => !(card.attributes('style') ?? '').includes('--os-card'))).toBe(true);

    const third = wrapper!.findAll('[data-test="card-grip-corner"]')[2]!.element;
    pointer(third, 'pointerdown', 0, 0);
    pointer(third, 'pointermove', 64, 32);
    pointer(third, 'pointerup', 64, 32);
    await flushPromises();

    expect(gridStyle()).toContain(`--os-card-w: ${rem(DefaultBoardSize.width + 64)}`);
    expect(wrapper!.findAll('[data-test="card-grip-width"]').map((g) => g.attributes('aria-valuenow')))
      .toEqual(Array(3).fill(String(DefaultBoardSize.width + 64)));
  });
});

describe('the keyboard on a grip', () => {
  it('is a focusable separator that names its axis and its value', async () => {
    await mountBoard(['Manager']);
    const right = wrapper!.get('[data-test="card-grip-width"]');
    const bottom = wrapper!.get('[data-test="card-grip-height"]');

    expect(right.attributes()).toMatchObject({ role: 'separator', 'aria-orientation': 'vertical', tabindex: '0' });
    expect(right.attributes('aria-valuenow')).toBe(String(DefaultBoardSize.width));
    expect(bottom.attributes()).toMatchObject({ role: 'separator', 'aria-orientation': 'horizontal', tabindex: '0' });
    expect(bottom.attributes('aria-valuenow')).toBe(String(DefaultBoardSize.height));
  });

  it('moves the width by a step on Left/Right, a larger one with Shift, and Home resets', async () => {
    await mountBoard(['Manager']);
    const display = useDisplayStore();
    const right = wrapper!.get('[data-test="card-grip-width"]').element as HTMLElement;
    right.focus();

    key(right, 'ArrowRight');
    expect(display.width).toBe(DefaultBoardSize.width + GripStep);
    key(right, 'ArrowRight', true);
    expect(display.width).toBe(DefaultBoardSize.width + GripStep + GripShiftStep);
    key(right, 'ArrowLeft');
    expect(display.width).toBe(DefaultBoardSize.width + GripShiftStep);
    // The other axis's arrows do nothing on the width grip.
    key(right, 'ArrowDown');
    expect(display.height).toBe(DefaultBoardSize.height);
    expect(JSON.parse(localStorage.getItem(StorageKey)!).width).toBe(DefaultBoardSize.width + GripShiftStep);

    key(right, 'Home');
    expect(display.width).toBe(DefaultBoardSize.width);
    expect(display.height).toBe(DefaultBoardSize.height);
  });

  it('moves the height by a step on Up/Down, a larger one with Shift', async () => {
    await mountBoard(['Manager']);
    const display = useDisplayStore();
    const bottom = wrapper!.get('[data-test="card-grip-height"]').element;

    key(bottom, 'ArrowDown');
    expect(display.height).toBe(DefaultBoardSize.height + GripStep);
    key(bottom, 'ArrowUp', true);
    expect(display.height).toBe(DefaultBoardSize.height + GripStep - GripShiftStep);
    key(bottom, 'Home');
    expect(display.height).toBe(DefaultBoardSize.height);
  });
});

describe('resetting from a grip', () => {
  it('resets the card size on a double-click, keeping the feed settings', async () => {
    await mountBoard(['Manager']);
    const display = useDisplayStore();
    display.set({ width: 700, height: 900, feedDepth: 50 });

    wrapper!.get('[data-test="card-grip-corner"]').element.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    await flushPromises();

    expect(display.width).toBe(DefaultBoardSize.width);
    expect(display.height).toBe(DefaultBoardSize.height);
    expect(display.feedDepth).toBe(50);
    expect(gridStyle()).toContain(`--os-card-w: ${rem(DefaultBoardSize.width)}`);
  });

  it('resets the card size on a double-tap', async () => {
    await mountBoard(['Manager']);
    const display = useDisplayStore();
    display.set({ width: 700, height: 900 });
    const corner = wrapper!.get('[data-test="card-grip-corner"]').element;

    pointer(corner, 'pointerdown', 0, 0, { pointerType: 'touch' });
    pointer(corner, 'pointerup', 0, 0, { pointerType: 'touch' });
    pointer(corner, 'pointerdown', 0, 0, { pointerType: 'touch' });

    expect(display.width).toBe(DefaultBoardSize.width);
    expect(display.height).toBe(DefaultBoardSize.height);
  });
});

describe('a press on a grip is only a drag', () => {
  it('opens no card dialog, reaches nothing beneath and does not scroll', async () => {
    await mountBoard(['Manager']);
    const card = wrapper!.get('.container-window').element;
    const reached = vi.fn();
    card.addEventListener('click', reached);
    card.addEventListener('pointerdown', reached);

    for (const name of ['card-grip-corner', 'card-grip-width', 'card-grip-height']) {
      const grip = wrapper!.get(`[data-test="${name}"]`).element;
      const down = pointer(grip, 'pointerdown', 10, 10);
      pointer(grip, 'pointerup', 10, 10);
      grip.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));

      expect(down.defaultPrevented).toBe(true);
    }
    await flushPromises();

    expect(reached).not.toHaveBeenCalled();
    expect(document.body.querySelector('.q-dialog')).toBeNull();
  });
});

describe('the Board display dialog', () => {
  it('shows the size a drag set, and says the cards can be dragged', async () => {
    await mountBoard(['Manager']);
    const corner = wrapper!.get('[data-test="card-grip-corner"]').element;
    pointer(corner, 'pointerdown', 0, 0);
    pointer(corner, 'pointermove', 37, 59);
    pointer(corner, 'pointerup', 37, 59);
    const dragged = draggedSize(DefaultBoardSize, 37, 59);

    const dialog = await mountDialog(BoardDisplayDialog);

    expect(bodyText()).toContain(`${dragged.width}px`);
    expect(bodyText()).toContain(`${dragged.height}px`);
    expect(document.body.querySelector('[data-drag-line]')?.textContent).toContain('can also be dragged');
    dialog.unmount();
  });
});

describe('a card the screen clamps', () => {
  it('has no width grip, and its height grip still works', async () => {
    // The screen holds the card narrower than the width chosen for it.
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      const width = this.classList.contains('container-window') ? 300 : 0;
      return { width, height: 0, top: 0, left: 0, right: width, bottom: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
    });
    await mountBoard(['Manager']);
    const display = useDisplayStore();

    expect(wrapper!.find('[data-test="card-grip-width"]').exists()).toBe(false);
    const bottom = wrapper!.get('[data-test="card-grip-height"]').element;
    pointer(bottom, 'pointerdown', 0, 0);
    pointer(bottom, 'pointermove', 0, 40);
    pointer(bottom, 'pointerup', 0, 40);
    expect(display.height).toBe(DefaultBoardSize.height + 40);

    // The corner moves the height only: the width it would set is not on screen.
    const corner = wrapper!.get('[data-test="card-grip-corner"]').element;
    pointer(corner, 'pointerdown', 0, 0);
    pointer(corner, 'pointermove', 90, 10);
    pointer(corner, 'pointerup', 90, 10);
    expect(display.width).toBe(DefaultBoardSize.width);
    expect(display.height).toBe(DefaultBoardSize.height + 50);
  });

  it('offers the width grip on a card drawn at its chosen width', async () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      const width = this.classList.contains('container-window') ? DefaultBoardSize.width : 0;
      return { width, height: 0, top: 0, left: 0, right: width, bottom: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
    });
    await mountBoard(['Manager']);

    expect(wrapper!.find('[data-test="card-grip-width"]').exists()).toBe(true);
  });
});
