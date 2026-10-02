// @vitest-environment happy-dom
//
// THE THREE VIEWS AND SORTING, AND MULTI-SELECT: Details, List and Tiles render the same entries;
// the view and the sort are remembered; Details' headers sort with `aria-sort` and its columns are
// resizable; Tiles use the shared tile grid; each item has its kind's icon; and selection by click,
// Ctrl/Cmd+click, Shift+click, Ctrl/Cmd+A and a rubber band, with the status bar saying so.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
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

import { aDir, aFile, documentsServer } from '../../test/documentsServer';
import {
  click,
  openExplorer,
  pane,
  press,
  row,
  rowNames,
  rows,
  selectedNames,
  settle,
  statusText,
} from '../../test/documentsExplorer';
import { resetBody } from '../../test/mountQuasar';

const listing = {
  'alpha:': [
    aFile('b.md', 300, '2026-09-03T00:00:00Z'),
    aDir('zeta', 2),
    aFile('a.pdf', 100, '2026-09-01T00:00:00Z'),
    aFile('c.png', 200, '2026-09-02T00:00:00Z'),
    aDir('alpha', 1),
    aFile('d.zzz', 50, '2026-09-04T00:00:00Z'),
  ],
};

beforeEach(() => {
  localStorage.clear();
  documentsServer({ listings: listing });
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

const viewButton = (view: string) => document.body.querySelector<HTMLElement>(`.documents-titlebar [data-view="${view}"]`)!;
const header = (column: string) => document.body.querySelector<HTMLElement>(`.documents-details th[data-col="${column}"]`)!;

describe('the three views', () => {
  it('Details, List and Tiles render the same entries, folders first', async () => {
    await openExplorer('alpha');
    const details = rowNames();

    await click(viewButton('list'));
    expect(document.body.querySelector('.documents-list')).not.toBeNull();
    expect(rowNames()).toEqual(details);

    await click(viewButton('tiles'));
    expect(document.body.querySelector('.documents-tiles.os-tiles')).not.toBeNull();
    expect(rows().every((tile) => tile.classList.contains('os-tile'))).toBe(true);
    expect(rowNames()).toEqual(details);

    expect(details).toEqual(['alpha', 'zeta', 'a.pdf', 'b.md', 'c.png', 'd.zzz']);
  });

  it('remembers the view across a reopen', async () => {
    const wrapper = await openExplorer('alpha');
    await click(viewButton('tiles'));
    expect(localStorage.getItem('harness.documents.view')).toBe('"tiles"');

    await wrapper.setProps({ modelValue: false });
    await settle();
    await wrapper.setProps({ modelValue: true });
    await settle();

    expect(document.body.querySelector('.documents-tiles')).not.toBeNull();
  });

  it('gives each item its kind\'s icon and folders the folder icon', async () => {
    await openExplorer('alpha');
    const icon = (name: string) => row(name).querySelector('.q-icon')!.textContent?.trim();

    expect(icon('zeta')).toBe('folder');
    expect(icon('a.pdf')).toBe('picture_as_pdf');
    expect(icon('b.md')).toBe('article');
    expect(icon('c.png')).toBe('image');
    expect(icon('d.zzz')).toBe('draft');
  });

  it('Details columns are resizable, and Kind is shown', async () => {
    await openExplorer('alpha');

    expect([...document.body.querySelectorAll('.documents-details th[data-col]')].map((th) => th.getAttribute('data-col')))
      .toEqual(['name', 'kind', 'size', 'modified']);
    expect(row('a.pdf').textContent).toContain('PDF');
    // `v-resizable-columns` gives each header a grip.
    expect(document.body.querySelectorAll('.documents-details th .os-col-resizer').length).toBeGreaterThan(0);
  });

  /**
   * A NARROW WINDOW SCROLLS, it does not clip. happy-dom lays nothing out, so this pins the rule
   * that does it: Quasar's table wrapper scrolls on its own, with its scrollbar under the last row,
   * and Details turns that off so the pane - which scrolls both ways - is the one scroller.
   */
  it('scrolls Details sideways in the pane rather than cutting off its last column', async () => {
    await openExplorer('alpha');

    const table = pane().querySelector<HTMLElement>(':scope > .documents-details')!;
    expect(table).not.toBeNull();
    expect(table.querySelectorAll('th[data-col]').length).toBe(4);

    const read = (file: string) => readFileSync(join(import.meta.dirname, file), 'utf8');
    const rule = (source: string, selector: string) =>
      source.match(new RegExp(`\\n${selector.replace('.', '\\.')} \\{([^}]*)\\}`))?.[1] ?? '';

    expect(rule(read('../documents/DocumentsDetails.vue'), '.documents-details')).toMatch(/overflow:\s*visible;/);
    expect(rule(read('../DocumentsDialog.vue'), '.documents-pane')).toMatch(/overflow:\s*auto;/);
  });

  it('shows the Team column at the root', async () => {
    await openExplorer();

    expect(header('team')).not.toBeNull();
  });
});

describe('sorting', () => {
  it('sorts by each column, flipping on a second click, with aria-sort on the header', async () => {
    await openExplorer('alpha');
    expect(header('name').getAttribute('aria-sort')).toBe('ascending');
    expect(header('size').getAttribute('aria-sort')).toBe('none');

    await click(header('size').querySelector('button')!);
    expect(rowNames()).toEqual(['alpha', 'zeta', 'd.zzz', 'a.pdf', 'c.png', 'b.md']);
    expect(header('size').getAttribute('aria-sort')).toBe('ascending');

    await click(header('size').querySelector('button')!);
    expect(rowNames()).toEqual(['zeta', 'alpha', 'b.md', 'c.png', 'a.pdf', 'd.zzz']);
    expect(header('size').getAttribute('aria-sort')).toBe('descending');

    await click(header('modified').querySelector('button')!);
    expect(rowNames().slice(2)).toEqual(['a.pdf', 'c.png', 'b.md', 'd.zzz']);

    await click(header('kind').querySelector('button')!);
    expect(rowNames().slice(0, 2)).toEqual(['alpha', 'zeta']);
  });

  it('remembers the sort', async () => {
    const wrapper = await openExplorer('alpha');
    await click(header('size').querySelector('button')!);
    await click(header('size').querySelector('button')!);
    expect(JSON.parse(localStorage.getItem('harness.documents.sort')!)).toEqual({ column: 'size', descending: true });

    await wrapper.setProps({ modelValue: false });
    await settle();
    await wrapper.setProps({ modelValue: true });
    await settle();

    expect(header('size').getAttribute('aria-sort')).toBe('descending');
  });
});

describe('multi-select', () => {
  it('a click selects one, Ctrl/Cmd+click toggles, Shift+click selects the range', async () => {
    await openExplorer('alpha');

    await click(row('a.pdf'));
    expect(selectedNames()).toEqual(['a.pdf']);

    await click(row('c.png'), { ctrlKey: true });
    expect(selectedNames()).toEqual(['a.pdf', 'c.png']);
    await click(row('a.pdf'), { metaKey: true });
    expect(selectedNames()).toEqual(['c.png']);

    await click(row('zeta'));
    await click(row('b.md'), { shiftKey: true });
    expect(selectedNames()).toEqual(['zeta', 'a.pdf', 'b.md']);
  });

  it('Ctrl/Cmd+A selects everything, and the status bar counts and sizes it', async () => {
    await openExplorer('alpha');
    expect(statusText()).toBe('6 items');

    await press('a', { ctrlKey: true });
    expect(selectedNames()).toHaveLength(6);
    expect(statusText()).toBe('6 of 6 selected · 650 B + 2 folders');

    await click(row('a.pdf'));
    await click(row('b.md'), { ctrlKey: true });
    expect(statusText()).toBe('2 of 6 selected · 400 B');
  });

  it('a click on empty space clears the selection', async () => {
    await openExplorer('alpha');
    await click(row('a.pdf'));

    pane().dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 5, clientY: 5, pointerId: 1 }));
    pane().dispatchEvent(new PointerEvent('pointerup', { bubbles: true, button: 0, clientX: 5, clientY: 5, pointerId: 1 }));
    await settle();

    expect(selectedNames()).toEqual([]);
  });

  it('a rubber band in Tiles selects what it crosses', async () => {
    await openExplorer('alpha');
    await click(viewButton('tiles'));

    // happy-dom lays nothing out, so each tile is given a box: a row of six, 100px apart.
    rows().forEach((tile, index) => {
      tile.getBoundingClientRect = () => ({ left: index * 100, right: index * 100 + 90, top: 0, bottom: 90, width: 90, height: 90, x: index * 100, y: 0, toJSON: () => ({}) }) as DOMRect;
    });

    const grid = document.body.querySelector<HTMLElement>('.documents-tiles')!;
    grid.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 150, clientY: 95, pointerId: 1 }));
    pane().dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: 350, clientY: 10, pointerId: 1 }));
    await settle();
    expect(document.body.querySelector('.documents-band')).not.toBeNull();
    pane().dispatchEvent(new PointerEvent('pointerup', { bubbles: true, clientX: 350, clientY: 10, pointerId: 1 }));
    await settle();

    expect(selectedNames()).toEqual(['zeta', 'a.pdf', 'b.md']);
    expect(document.body.querySelector('.documents-band')).toBeNull();
  });
});
