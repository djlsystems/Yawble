// @vitest-environment happy-dom
//
// RESIZABLE COLUMNS. A table nobody resized keeps its automatic layout; a drag or a keystroke on a
// header's handle pins every column and stores the widths per table in localStorage; a reopened
// table takes them back; a double-click resets the table; the drag never sorts the column.
//
// happy-dom lays nothing out, so a cell's rendered width is stubbed from its `data-natural`
// attribute - the width the browser would have given it in the automatic layout.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h, nextTick, ref, withDirectives } from 'vue';
import { mount } from '@vue/test-utils';
import {
  ColumnWidthBounds,
  KeyboardStep,
  readColumnWidths,
  vResizableColumns,
  writeColumnWidths,
} from '../resizableColumns';

const natural: Record<string, number> = { Id: 80, Title: 400, Team: 150, Archived: 120 };

function makeTable(sorted: string[], archived = ref(false)) {
  return defineComponent({
    setup() {
      return () =>
        withDirectives(
          h('table', { 'data-probe': '' }, [
            h('thead', [
              h('tr', [
                h('th', { 'data-natural': natural.Id, onClick: () => sorted.push('id') }, 'Id'),
                h('th', { 'data-natural': natural.Title, onClick: () => sorted.push('title') }, 'Title'),
                archived.value ? h('th', { 'data-natural': natural.Archived }, 'Archived') : null,
                h('th', { 'data-natural': natural.Team, 'data-col': 'team' }, [h('span', 'Team '), h('i', 'arrow_upward')]),
              ]),
            ]),
            h('tbody', [h('tr', [h('td', 'B001'), h('td', 'A title'), h('td', 'alpha')])]),
          ]),
          [[vResizableColumns, 'probe']],
        );
    },
  });
}

function headers(wrapper: ReturnType<typeof mount>) {
  return Array.from(wrapper.element.querySelectorAll('thead th')) as HTMLTableCellElement[];
}

function cellAt(wrapper: ReturnType<typeof mount>, index: number): HTMLTableCellElement {
  const cell = headers(wrapper)[index];
  if (!cell) throw new Error(`No header cell ${index}.`);
  return cell;
}

function handleOf(cell: HTMLElement) {
  return cell.querySelector(':scope > .os-col-resizer') as HTMLElement;
}

function pointer(type: string, target: EventTarget, clientX: number) {
  target.dispatchEvent(new PointerEvent(type, { bubbles: true, cancelable: true, button: 0, clientX, pointerId: 1 }));
}

function drag(cell: HTMLElement, dx: number) {
  const handle = handleOf(cell);
  pointer('pointerdown', handle, 100);
  pointer('pointermove', window, 100 + dx);
  pointer('pointerup', window, 100 + dx);
  // The browser's click after the drag lands on the handle.
  handle.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

beforeEach(() => {
  localStorage.clear();
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    const width = Number(this.dataset.natural ?? 0);
    return { width, height: 20, top: 0, left: 0, right: width, bottom: 20, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

describe('resizable columns', () => {
  it('adds a labelled handle to every header and leaves an unresized table in its automatic layout', () => {
    const wrapper = mount(makeTable([]), { attachTo: document.body });
    const cells = headers(wrapper);

    expect(cells.map((c) => handleOf(c)?.getAttribute('aria-label'))).toEqual([
      'Resize column Id',
      'Resize column Title',
      'Resize column team',
    ]);

    const table = wrapper.element as HTMLTableElement;
    expect(table.style.tableLayout).toBe('');
    expect(cells.every((c) => c.style.width === '')).toBe(true);
    expect(localStorage.getItem('harness.columns.probe')).toBeNull();
  });

  it('a drag pins every column at its shown width, moves only the dragged one, stores them all and does not sort', () => {
    const sorted: string[] = [];
    const wrapper = mount(makeTable(sorted), { attachTo: document.body });
    const title = cellAt(wrapper, 1);

    drag(title, -150);

    const table = wrapper.element as HTMLTableElement;
    expect(table.style.tableLayout).toBe('fixed');
    expect(table.style.width).toBe(`${80 + 250 + 150}px`);
    expect(title.style.width).toBe('250px');
    expect(readColumnWidths('probe')).toEqual({ Id: 80, Title: 250, team: 150 });
    expect(sorted).toEqual([]);
  });

  it('a reopened table takes its stored widths back', () => {
    writeColumnWidths('probe', { Id: 60, Title: 300, team: 90 });

    const wrapper = mount(makeTable([]), { attachTo: document.body });

    expect(headers(wrapper).map((c) => c.style.width)).toEqual(['60px', '300px', '90px']);
    expect((wrapper.element as HTMLTableElement).style.width).toBe('450px');
  });

  it('a column that appears later takes its natural width and keeps the others where they were', async () => {
    writeColumnWidths('probe', { Id: 60, Title: 300, team: 90 });
    const archived = ref(false);
    const wrapper = mount(makeTable([], archived), { attachTo: document.body });

    archived.value = true;
    await nextTick();

    const cells = headers(wrapper);
    expect(cells.map((c) => c.textContent)).toEqual(['Id', 'Title', 'Archived', 'Team arrow_upward']);
    expect(cells.map((c) => c.style.width)).toEqual(['60px', '300px', '120px', '90px']);
    expect(handleOf(cellAt(wrapper, 2))).not.toBeNull();
  });

  // The width set on a cell is what it renders at, whichever box model the page uses: the app is
  // border-box throughout, and taking the padding off there narrowed every pinned column.
  it.each([
    ['border-box', '400px'],
    ['content-box', '384px'],
  ])('a pinned column renders at its stored width under %s', (boxSizing, expected) => {
    const style = document.createElement('style');
    style.textContent = `th { padding: 0 8px; box-sizing: ${boxSizing}; }`;
    document.head.appendChild(style);
    try {
      writeColumnWidths('probe', { Title: 400 });
      const wrapper = mount(makeTable([]), { attachTo: document.body });

      expect(cellAt(wrapper, 1).style.width).toBe(expected);
    } finally {
      style.remove();
    }
  });

  it('pins a fractional natural width rounded up, so text that fit is not clipped by a fraction', () => {
    natural.Id = 58.4;
    try {
      const wrapper = mount(makeTable([]), { attachTo: document.body });
      drag(cellAt(wrapper, 1), -10);

      expect(cellAt(wrapper, 0).style.width).toBe('59px');
    } finally {
      natural.Id = 80;
    }
  });

  it('never goes below the minimum width', () => {
    const wrapper = mount(makeTable([]), { attachTo: document.body });
    const id = cellAt(wrapper, 0);

    drag(id, -500);

    expect(id.style.width).toBe(`${ColumnWidthBounds.min}px`);
    expect(readColumnWidths('probe').Id).toBe(ColumnWidthBounds.min);
  });

  it('Left and Right on a focused handle resize by a step and store it', () => {
    const wrapper = mount(makeTable([]), { attachTo: document.body });
    const title = cellAt(wrapper, 1);

    handleOf(title).dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));

    expect(title.style.width).toBe(`${400 + KeyboardStep}px`);
    expect(readColumnWidths('probe').Title).toBe(400 + KeyboardStep);
  });

  it('a double-click resets the whole table to its automatic layout and forgets the widths', () => {
    writeColumnWidths('probe', { Id: 60, Title: 300, team: 90 });
    const sorted: string[] = [];
    const wrapper = mount(makeTable(sorted), { attachTo: document.body });
    const title = cellAt(wrapper, 1);

    handleOf(title).dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));

    expect((wrapper.element as HTMLTableElement).style.tableLayout).toBe('');
    expect(headers(wrapper).every((c) => c.style.width === '')).toBe(true);
    expect(localStorage.getItem('harness.columns.probe')).toBeNull();
    expect(sorted).toEqual([]);
  });

  it('ignores unreadable or out-of-range stored widths and survives storage that throws', () => {
    localStorage.setItem('harness.columns.probe', JSON.stringify({ Id: 'wide', Title: 99999, team: -3 }));
    expect(readColumnWidths('probe')).toEqual({ Title: ColumnWidthBounds.max, team: ColumnWidthBounds.min });

    localStorage.setItem('harness.columns.probe', 'not json');
    expect(readColumnWidths('probe')).toEqual({});

    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    const wrapper = mount(makeTable([]), { attachTo: document.body });
    const title = cellAt(wrapper, 1);
    drag(title, 50);

    expect(title.style.width).toBe('450px');
  });
});
