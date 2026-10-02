// @vitest-environment happy-dom
//
// AT PHONE WIDTH: full screen; the tree is a slide-over from the toolbar; Details shows Name and
// Size only; a row's ⋮ and a long-press open the same context menu; a selection swaps the toolbar
// for the selection bar (Copy, Cut, Move, Delete, More › Rename, Copy as path). There is no drag:
// the touch path is Cut then Paste, and Move to…, and each sends one move.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const q = vi.hoisted(() => ({
  screen: { lt: { sm: true } },
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
import {
  click,
  menuActions,
  openExplorer,
  rows,
  row,
  settle,
  toolbarButton,
  treeNode,
  until,
} from '../../test/documentsExplorer';
import { LongPressMs } from '../documents/itemEvents';
import { resetBody } from '../../test/mountQuasar';

let server: ReturnType<typeof documentsServer>;

beforeEach(() => {
  localStorage.clear();
  q.screen.lt.sm = true;
  server = documentsServer();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

const barButton = (action: string) => document.body.querySelector<HTMLElement>(`.documents-selection-bar [data-action="${action}"]`);

describe('the explorer at phone width', () => {
  it('is full screen, with the tree closed until the toolbar opens it as a slide-over', async () => {
    await openExplorer('alpha');

    expect(document.body.querySelector('.q-dialog__inner--maximized')).not.toBeNull();
    expect(document.body.querySelector('.documents-tree-pane')).toBeNull();
    expect(document.body.querySelector('[data-tree-slide]')).toBeNull();

    await click(toolbarButton('tree')!);
    expect(document.body.querySelector('[data-tree-slide]')).not.toBeNull();

    await click(treeNode('beta')!);
    expect(document.body.querySelector('[data-tree-slide]')).toBeNull();
  });

  it('shows Name and Size only in Details', async () => {
    await openExplorer('alpha');

    expect([...document.body.querySelectorAll('.documents-details th[data-col]')].map((th) => th.getAttribute('data-col')))
      .toEqual(['name', 'size']);
  });

  it("a row's ⋮ opens the same context menu as a right-click", async () => {
    await openExplorer('alpha');
    await click(row('notes.md').querySelector('[aria-label="More"]')!);
    await until(() => document.body.querySelector('.documents-menu [data-action]'));

    expect(menuActions()).toEqual(['open', 'view', 'download', 'cut', 'copy', 'rename', 'moveTo', 'copyPath', 'delete']);
  });

  it('a long-press opens it too', async () => {
    await openExplorer('alpha');
    vi.useFakeTimers();
    row('notes.md').dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'touch', pointerId: 1 }));
    vi.advanceTimersByTime(LongPressMs + 10);
    vi.useRealTimers();
    await until(() => document.body.querySelector('.documents-menu [data-action]'));

    expect(menuActions()).toContain('rename');
  });

  it('a selection swaps the toolbar for the selection bar', async () => {
    await openExplorer('alpha');
    await click(row('notes.md'));

    expect(['copy', 'cut', 'moveTo', 'delete', 'more'].map((action) => !!barButton(action))).toEqual([true, true, true, true, true]);
    expect(toolbarButton('newFolder')).toBeNull();

    await click(barButton('more')!);
    await until(() => document.body.querySelector('.q-menu [data-action="rename"]'));
    expect([...document.body.querySelectorAll('.q-menu [data-action]')].map((item) => item.getAttribute('data-action')))
      .toEqual(['rename', 'copyPath']);
  });

  it('rows are not draggable', async () => {
    await openExplorer('alpha');

    expect(rows().length).toBeGreaterThan(0);
    expect(rows().some((element) => element.hasAttribute('draggable'))).toBe(false);
  });
});

describe('the touch path: Cut and Paste, and Move to…', () => {
  it('Cut, open Beta, Paste sends one move', async () => {
    await openExplorer('alpha');
    await click(row('notes.md'));
    await click(barButton('cut')!);

    // With the item still selected the selection bar is up; leaving the folder clears it.
    await click(document.body.querySelector('.documents-titlebar [aria-label="Up"]')!);
    await click(row('Beta'));
    await click(row('Beta').querySelector('[aria-label="More"]')!);
    await until(() => document.body.querySelector('.documents-menu [data-action="open"]'));
    await click(document.body.querySelector('.documents-menu [data-action="open"]')!);
    await settle();

    await click(toolbarButton('paste')!);

    expect(server.callsTo('move').map((call) => [call.url, call.body])).toEqual([
      ['/api/teams/alpha/documents/move', { to: { folder: 'beta', path: '' }, items: [{ path: 'notes.md' }] }],
    ]);
  });

  it('Move to… from the selection bar opens the picker and sends one move', async () => {
    await openExplorer('alpha');
    await click(row('notes.md'));
    await click(barButton('moveTo')!);

    const picker = await until(() => document.body.querySelector<HTMLElement>('.documents-move'));
    await click(picker.querySelector('[data-node="beta::"]')!);
    await click(picker.querySelector('[data-move="confirm"]')!);

    expect(server.callsTo('move').map((call) => call.body)).toEqual([{ to: { folder: 'beta', path: '' }, items: [{ path: 'notes.md' }] }]);
  });
});
