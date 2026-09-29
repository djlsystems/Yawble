/**
 * Resizable table columns, as a per-browser preference.
 *
 * A DISPLAY preference rather than data: how wide this browser shows a column is nothing the
 * server or another viewer needs, so it lives in `localStorage` beside `harness.boardSize` and
 * follows that key's wrapper discipline - storage throws in a private window with cookies blocked,
 * and a column width is not worth a crash.
 *
 * ONE DIRECTIVE FOR EVERY TABLE, not a table component. The app's tables are `q-markup-table`
 * (plain HTML under Quasar's styling) and one plain `<table>`; neither Quasar table resizes columns.
 * `v-resizable-columns="'backlog'"` on the table (or on anything holding it) adds a handle to the
 * right edge of each header cell. Dragging it, or Left/Right on a focused handle, sets that
 * column's width; double-clicking a handle resets the whole table to its natural widths.
 *
 * UNTOUCHED UNTIL RESIZED. A table nobody has resized keeps the browser's automatic layout, so it
 * looks exactly as it did before this existed. The first resize measures every column as shown,
 * pins them all, and switches the table to `table-layout: fixed`; what is stored is every column's
 * width, so a reopened table is the table that was left.
 *
 * A COLUMN IS KNOWN BY ITS HEADER: `data-col` when the cell carries one, else its text, else its
 * position. The Backlog's Archived column comes and goes with the tab, so a position alone would
 * hand Actions the width Archived was given. A column with no stored width (one that appeared
 * since) takes its natural width.
 */
import type { Directive } from 'vue';

/** Inclusive, and clamped to on both read and write. */
export const ColumnWidthBounds = { min: 40, max: 2000 } as const;

/** What one Left/Right keystroke on a focused handle moves the edge by. */
export const KeyboardStep = 16;

const StoragePrefix = 'harness.columns.';

export type ColumnWidths = Record<string, number>;

export function clampColumnWidth(value: unknown): number | null {
  // NaN survives every comparison silently (see boardSize.ts), so it is refused here, not clamped.
  if (typeof value !== 'number' || !Number.isFinite(value)) return null;

  return Math.round(Math.min(ColumnWidthBounds.max, Math.max(ColumnWidthBounds.min, value)));
}

export function readColumnWidths(table: string): ColumnWidths {
  try {
    const stored = localStorage.getItem(StoragePrefix + table);

    if (!stored) return {};

    const parsed: unknown = JSON.parse(stored);

    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return {};

    // CLAMPED ON READ, not only on write: storage is editable by hand and shared with older builds.
    const widths: ColumnWidths = {};

    for (const [column, width] of Object.entries(parsed as Record<string, unknown>)) {
      const clamped = clampColumnWidth(width);
      if (clamped !== null) widths[column] = clamped;
    }

    return widths;
  } catch {
    return {};
  }
}

export function writeColumnWidths(table: string, widths: ColumnWidths): void {
  try {
    if (Object.keys(widths).length === 0) {
      localStorage.removeItem(StoragePrefix + table);
    } else {
      localStorage.setItem(StoragePrefix + table, JSON.stringify(widths));
    }
  } catch {
    // Nothing to do and nothing worth saying: the widths still apply until the table is closed.
  }
}

/** The name a header cell's width is stored under. */
export function columnKey(cell: HTMLTableCellElement, index: number): string {
  const declared = cell.dataset.col;
  if (declared) return declared;

  // The handle this directive adds carries no text, so the cell's own label is what remains.
  const text = (cell.textContent ?? '').replace(/\s+/g, ' ').trim();

  return text || `#${index}`;
}

interface TableState {
  table: string;
  widths: ColumnWidths;
  signature: string | null;
}

const states = new WeakMap<HTMLElement, TableState>();

function tableOf(el: HTMLElement): HTMLTableElement | null {
  return el instanceof HTMLTableElement ? el : el.querySelector('table');
}

function headerCells(table: HTMLTableElement): HTMLTableCellElement[] {
  // Selectors rather than `tHead.rows[0].cells`, which not every DOM the suite runs on provides.
  const row = table.querySelector(':scope > thead > tr');
  if (!row) return [];

  return Array.from(row.querySelectorAll<HTMLTableCellElement>(':scope > th'));
}

function horizontalExtras(cell: HTMLElement): number {
  const style = getComputedStyle(cell);

  // The app sets `box-sizing: border-box` on everything, where `width` already includes padding
  // and border; taking them off again narrowed every pinned column by its padding.
  if (style.boxSizing === 'border-box') return 0;

  const px = (value: string) => Number.parseFloat(value) || 0;

  return px(style.paddingLeft) + px(style.paddingRight)
    + px(style.borderLeftWidth) + px(style.borderRightWidth);
}

/** A cell's rendered width, padding and border included. */
function renderedWidth(cell: HTMLElement): number {
  // ROUNDED UP. A natural width is fractional (58.4px); pinning it at 58 left the widest id a fraction
  // short and its ellipsis showed on a column nobody had narrowed.
  return Math.ceil(cell.getBoundingClientRect().width);
}

/**
 * Sets a cell's rendered width. Under `content-box`, `width` is the CONTENT width, so padding and
 * border are taken off: setting the measured width as-is would grow the column by its padding on
 * each reopen.
 */
function setRenderedWidth(cell: HTMLElement, width: number): void {
  cell.style.width = `${Math.max(0, width - horizontalExtras(cell))}px`;
  cell.dataset.colWidth = String(width);
}

function pinnedWidth(cell: HTMLElement): number {
  return Number(cell.dataset.colWidth) || 0;
}

function unpin(table: HTMLTableElement, cells: HTMLTableCellElement[]): void {
  for (const cell of cells) {
    cell.style.width = '';
    delete cell.dataset.colWidth;
  }

  table.style.tableLayout = '';
  table.style.width = '';
  table.style.maxWidth = '';
  table.classList.remove('os-cols-fixed');
}

function fitTable(table: HTMLTableElement, cells: HTMLTableCellElement[]): void {
  table.style.width = `${cells.reduce((sum, cell) => sum + pinnedWidth(cell), 0)}px`;
}

/**
 * Lays the table out from the stored widths: natural layout when nothing is stored, otherwise
 * every column pinned - to its stored width, or to its natural width when it has none.
 */
function layout(table: HTMLTableElement, state: TableState): void {
  const cells = headerCells(table);

  unpin(table, cells);

  if (!cells.some((cell, i) => columnKey(cell, i) in state.widths)) return;

  // Measured in the natural layout, before anything is pinned.
  const natural = cells.map(renderedWidth);

  cells.forEach((cell, i) => {
    const stored = state.widths[columnKey(cell, i)];
    setRenderedWidth(cell, stored ?? clampColumnWidth(natural[i]) ?? ColumnWidthBounds.min);
  });

  pin(table, cells);
}

function pin(table: HTMLTableElement, cells: HTMLTableCellElement[]): void {
  table.style.tableLayout = 'fixed';
  // Quasar's `.q-table` caps the table at 100% of its container; a pinned table may be wider and
  // scroll inside it, which is what the container's `overflow: auto` is for.
  table.style.maxWidth = 'none';
  table.classList.add('os-cols-fixed');
  fitTable(table, cells);
}

/** Pins every column at the width it is shown at now, so moving one edge moves only that edge. */
function freeze(table: HTMLTableElement, cells: HTMLTableCellElement[]): void {
  if (table.classList.contains('os-cols-fixed')) return;

  const shown = cells.map(renderedWidth);
  cells.forEach((cell, i) => setRenderedWidth(cell, clampColumnWidth(shown[i]) ?? ColumnWidthBounds.min));
  pin(table, cells);
}

function remember(table: HTMLTableElement, state: TableState): void {
  const cells = headerCells(table);

  cells.forEach((cell, i) => {
    const width = clampColumnWidth(pinnedWidth(cell));
    if (width !== null) state.widths[columnKey(cell, i)] = width;
  });

  writeColumnWidths(state.table, state.widths);
}

function resize(table: HTMLTableElement, cell: HTMLTableCellElement, width: number): void {
  setRenderedWidth(cell, clampColumnWidth(width) ?? ColumnWidthBounds.min);
  fitTable(table, headerCells(table));
}

function addHandle(el: HTMLElement, cell: HTMLTableCellElement, index: number): void {
  const handle = document.createElement('span');
  handle.className = 'os-col-resizer';
  handle.tabIndex = 0;
  handle.setAttribute('role', 'separator');
  handle.setAttribute('aria-orientation', 'vertical');
  handle.setAttribute('aria-label', `Resize column ${columnKey(cell, index)}`);
  handle.title = 'Drag to resize. Double-click to reset this table\'s columns.';

  const context = () => {
    const state = states.get(el);
    const table = tableOf(el);
    return state && table ? { state, table, cells: headerCells(table) } : null;
  };

  handle.addEventListener('pointerdown', (event) => {
    if (event.button !== 0) return;

    const found = context();
    if (!found) return;

    // Neither the header's own click (the Backlog sorts on it) nor a text selection starts here.
    event.preventDefault();
    event.stopPropagation();

    freeze(found.table, found.cells);

    const startX = event.clientX;
    const startWidth = pinnedWidth(cell);

    try {
      handle.setPointerCapture(event.pointerId);
    } catch {
      // A synthetic or already-released pointer; the window listeners below still follow it.
    }

    document.body.classList.add('os-col-resizing');

    const move = (e: PointerEvent) => resize(found.table, cell, startWidth + (e.clientX - startX));

    const end = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', end);
      window.removeEventListener('pointercancel', end);
      document.body.classList.remove('os-col-resizing');
      remember(found.table, found.state);
    };

    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', end);
    window.addEventListener('pointercancel', end);
  });

  // The click that ends a drag lands on the handle, inside the header cell: it must not sort.
  handle.addEventListener('click', (event) => event.stopPropagation());

  handle.addEventListener('dblclick', (event) => {
    event.preventDefault();
    event.stopPropagation();

    const found = context();
    if (!found) return;

    found.state.widths = {};
    writeColumnWidths(found.state.table, {});
    unpin(found.table, found.cells);
  });

  handle.addEventListener('keydown', (event) => {
    const step = event.key === 'ArrowLeft' ? -KeyboardStep : event.key === 'ArrowRight' ? KeyboardStep : 0;
    if (step === 0) return;

    const found = context();
    if (!found) return;

    event.preventDefault();
    event.stopPropagation();

    freeze(found.table, found.cells);
    resize(found.table, cell, pinnedWidth(cell) + step);
    remember(found.table, found.state);
  });

  cell.classList.add('os-col-resizable');
  cell.appendChild(handle);
}

function attach(el: HTMLElement, state: TableState): void {
  const table = tableOf(el);
  if (!table) return;

  const cells = headerCells(table);

  cells.forEach((cell, i) => {
    if (!cell.querySelector(':scope > .os-col-resizer')) addHandle(el, cell, i);
  });

  // Laid out again only when the set of columns changes (the Backlog's Archived tab), not on
  // every re-render of the component around it: a poll must not undo a drag in progress.
  const signature = cells.map(columnKey).join('\u0000');

  if (signature !== state.signature) {
    state.signature = signature;
    layout(table, state);
  }
}

/**
 * `v-resizable-columns="'<table name>'"`. The name is the storage key, so it is unique per table
 * and stable across builds.
 */
export const vResizableColumns: Directive<HTMLElement, string> = {
  mounted(el, binding) {
    const state: TableState = { table: binding.value, widths: readColumnWidths(binding.value), signature: null };
    states.set(el, state);
    attach(el, state);
  },

  updated(el) {
    const state = states.get(el);
    if (state) attach(el, state);
  },

  unmounted(el) {
    states.delete(el);
  },
};
