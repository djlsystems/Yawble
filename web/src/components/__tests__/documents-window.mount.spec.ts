// @vitest-environment happy-dom
//
// THE WINDOW: moved by its title bar, resized by its edges, remembered, clamped back on to the
// screen, opened at its default when storage is corrupt or throws, maximised and restored, and full
// screen at phone width with no handles and the stored place untouched. A press on a crumb or a
// draggable row in the title bar never starts a window move.
//
// happy-dom's viewport is 1024 x 768 and lays nothing out, so an unplaced window's box reads as
// zero; the cases start from a stored place unless they are about the unplaced one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const q = vi.hoisted(() => ({
  screen: { lt: { sm: false } },
  platform: { is: { mac: false } },
  notify: vi.fn(),
  copy: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: q.notify, screen: q.screen, platform: q.platform }),
  copyToClipboard: q.copy,
}));

import { documentsServer } from '../../test/documentsServer';
import { buttonLabelled, click, crumb, doubleClick, openExplorer, row, settle } from '../../test/documentsExplorer';
import { resetBody } from '../../test/mountQuasar';

const Key = 'harness.documents.window';

const card = () => document.body.querySelector<HTMLElement>('.documents-window')!;
const titlebar = () => document.body.querySelector<HTMLElement>('.documents-titlebar')!;
const stored = () => JSON.parse(localStorage.getItem(Key) ?? 'null');

function pointer(type: string, target: Element, x: number, y: number) {
  target.dispatchEvent(new PointerEvent(type, { clientX: x, clientY: y, bubbles: true, pointerId: 1 }));
}

function place(geometry: Record<string, unknown>) {
  localStorage.setItem(Key, JSON.stringify(geometry));
}

beforeEach(() => {
  localStorage.clear();
  q.screen.lt.sm = false;
  documentsServer();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

describe('at rest', () => {
  it('has the scale class and no size of its own until the person moves it', async () => {
    await openExplorer();

    expect(card().classList).toContain('os-dialog-lg');
    expect(card().style.width).toBe('');
    expect(card().style.left).toBe('');
  });
});

describe('moving and resizing', () => {
  beforeEach(() => place({ left: 100, top: 100, width: 600, height: 400 }));

  it('drags by the title bar, and stops on pointerup', async () => {
    await openExplorer();
    pointer('pointerdown', titlebar(), 200, 200);
    pointer('pointermove', card(), 230, 210);
    await settle();

    expect([card().style.left, card().style.top]).toEqual(['130px', '110px']);
    expect(stored()).toMatchObject({ left: 130, top: 110 });

    pointer('pointerup', card(), 230, 210);
    pointer('pointermove', card(), 300, 300);
    await settle();
    expect(card().style.left).toBe('130px');
  });

  it('stops on pointercancel too', async () => {
    await openExplorer();
    pointer('pointerdown', titlebar(), 200, 200);
    pointer('pointercancel', card(), 200, 200);
    pointer('pointermove', card(), 260, 260);
    await settle();

    expect(card().style.left).toBe('100px');
  });

  it('resizes from the east, south and west edges', async () => {
    await openExplorer();
    pointer('pointerdown', card().querySelector('.documents-resize-e')!, 700, 300);
    pointer('pointermove', card(), 760, 300);
    pointer('pointerup', card(), 760, 300);
    pointer('pointerdown', card().querySelector('.documents-resize-s')!, 300, 500);
    pointer('pointermove', card(), 300, 540);
    pointer('pointerup', card(), 300, 540);
    pointer('pointerdown', card().querySelector('.documents-resize-w')!, 100, 300);
    pointer('pointermove', card(), 80, 300);
    pointer('pointerup', card(), 80, 300);
    await settle();

    expect(stored()).toEqual({ left: 80, top: 100, width: 680, height: 440, maximised: false });
  });

  it('does not move when a crumb, a button or a draggable row in the title bar is pressed', async () => {
    await openExplorer('alpha');
    await doubleClick(row('reports'));

    pointer('pointerdown', crumb('Alpha'), 200, 20);
    pointer('pointermove', card(), 260, 60);
    pointer('pointerup', card(), 260, 60);
    pointer('pointerdown', buttonLabelled('Up')!, 200, 20);
    pointer('pointermove', card(), 260, 60);
    pointer('pointerup', card(), 260, 60);

    const draggable = document.createElement('span');
    draggable.setAttribute('draggable', 'true');
    titlebar().appendChild(draggable);
    pointer('pointerdown', draggable, 200, 20);
    pointer('pointermove', card(), 260, 60);
    await settle();

    expect(card().style.left).toBe('100px');
  });
});

describe('remembered', () => {
  it('reopens where it was left, the same size', async () => {
    place({ left: 40, top: 30, width: 700, height: 500 });
    const wrapper = await openExplorer();
    pointer('pointerdown', titlebar(), 100, 100);
    pointer('pointermove', card(), 150, 120);
    pointer('pointerup', card(), 150, 120);

    await wrapper.setProps({ modelValue: false });
    await settle();
    await wrapper.setProps({ modelValue: true });
    await settle();

    expect([card().style.left, card().style.top, card().style.width, card().style.height]).toEqual(['90px', '50px', '700px', '500px']);
  });

  it('brings a window stored off-screen back on to this one', async () => {
    place({ left: 3000, top: 2000, width: 700, height: 500 });
    await openExplorer();

    expect([card().style.left, card().style.top]).toEqual([`${1024 - 700 - 10}px`, `${768 - 500 - 10}px`]);
  });

  it('opens at the default when storage throws', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    await openExplorer();

    expect(card().style.width).toBe('');
    expect(document.body.textContent).toContain('Alpha');
  });

  it('opens at the default when what is stored is corrupt', async () => {
    localStorage.setItem(Key, '{"left":');
    await openExplorer();

    expect(card().style.width).toBe('');
  });
});

describe('maximise', () => {
  it('fills the screen and restores to where it was, remembering both', async () => {
    place({ left: 100, top: 100, width: 600, height: 400 });
    await openExplorer();

    await click(buttonLabelled('Maximise')!);
    expect(document.body.querySelector('.q-dialog__inner--maximized')).not.toBeNull();
    expect(card().style.left).toBe('');
    expect(card().querySelector('.documents-resize')).toBeNull();
    expect(stored()).toMatchObject({ left: 100, maximised: true });

    await click(buttonLabelled('Restore')!);
    expect(card().style.left).toBe('100px');
    expect(stored()).toMatchObject({ maximised: false });
  });

  it('tells the shell while it fills the screen, so the Concierge bubble is not drawn over it', async () => {
    const wrapper = await openExplorer();
    const told = () => wrapper.emitted('update:fullScreen')?.at(-1)?.[0];

    expect(told() ?? false).toBe(false);

    await click(buttonLabelled('Maximise')!);
    expect(told()).toBe(true);

    await click(buttonLabelled('Restore')!);
    expect(told()).toBe(false);
  });
});

describe('at phone width', () => {
  it('is full screen with no handles, and leaves the stored place alone', async () => {
    q.screen.lt.sm = true;
    place({ left: 100, top: 100, width: 600, height: 400 });
    await openExplorer();

    expect(document.body.querySelector('.q-dialog__inner--maximized')).not.toBeNull();
    expect(card().querySelector('.documents-resize')).toBeNull();
    expect(card().style.left).toBe('');

    pointer('pointerdown', titlebar(), 200, 200);
    pointer('pointermove', card(), 260, 260);
    await settle();

    expect(stored()).toEqual({ left: 100, top: 100, width: 600, height: 400 });
  });
});
