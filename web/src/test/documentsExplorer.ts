import { flushPromises, type VueWrapper } from '@vue/test-utils';
import { nextTick } from 'vue';
import DocumentsDialog from '../components/DocumentsDialog.vue';
import { asDocumentsFolderKey } from '../api/types';
import { mountDialog } from './mountQuasar';

/**
 * DRIVING THE DOCUMENTS EXPLORER IN A MOUNT SPEC, the way a person does: open it, find a row by its
 * name, click, press keys, right-click, pick from the menu, answer a dialog, drag and drop.
 *
 * Everything is found in `document.body`, because a QDialog teleports there. The fetch boundary is
 * `test/documentsServer.ts`; the Quasar platform (screen width, macOS) is mocked in each spec file,
 * because `vi.mock` is hoisted per file.
 */

export async function settle(rounds = 3) {
  for (let round = 0; round < rounds; round++) {
    await flushPromises();
    await nextTick();
  }
}

/** Opens the explorer, at the root or at one team's folder, as Projects or Active Team does. */
export async function openExplorer(start: string | null = null): Promise<VueWrapper> {
  const wrapper = await mountDialog(DocumentsDialog, { start: start === null ? null : asDocumentsFolderKey(start) });
  await settle();

  return wrapper;
}

export const pane = () => document.body.querySelector<HTMLElement>('[data-pane]')!;

/** Every row or tile in the view pane, in display order. */
export const rows = () => [...pane().querySelectorAll<HTMLElement>('[data-key]')];

/** A row's name: its text, or - while it is being renamed - the leaf of its key. */
const nameOf = (element: HTMLElement) =>
  (element.querySelector('.documents-name-text, .documents-tile-name')?.textContent ?? '').trim() ||
  (element.dataset.key ?? '').replace(/^.*::/, '').replace(/^.*\//, '');

export const rowNames = () => rows().map(nameOf);

/** The row or tile showing this name. */
export function row(name: string): HTMLElement {
  const found = rows().find((candidate) => nameOf(candidate) === name);
  if (!found) throw new Error(`No row named ${name}; rows are ${rowNames().join(', ')}`);

  return found;
}

export const selectedNames = () =>
  rows()
    .filter((candidate) => candidate.getAttribute('aria-selected') === 'true')
    .map(nameOf);

export async function click(element: Element, modifiers: MouseEventInit = {}) {
  element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ...modifiers }));
  await settle();
}

export async function doubleClick(element: Element) {
  element.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true }));
  await settle();
}

/** Waits, in real time, until `found` answers - a QMenu or QDialog appears a few ticks later. */
export async function until<T>(found: () => T | null | undefined | false, ms = 1500): Promise<T> {
  const deadline = Date.now() + ms;

  for (;;) {
    await settle(1);
    const value = found();
    if (value) return value;
    if (Date.now() > deadline) throw new Error('Waited, and it did not appear.');
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

/** Waits until no context menu is on screen, so the next one is the one opened next. */
export async function menuGone() {
  await until(() => document.body.querySelectorAll('.documents-menu').length === 0 || 'gone');
  for (let tries = 0; tries < 100 && document.body.querySelector('.documents-menu'); tries++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

export async function rightClick(element: Element) {
  await menuGone();
  element.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 50, clientY: 50 }));
  await until(() => document.body.querySelector('.documents-menu [data-action], .documents-menu [data-submenu]'));
}

/** A key pressed with focus in the view pane (or on `target`). */
export async function press(key: string, modifiers: KeyboardEventInit = {}, target: Element = pane()) {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...modifiers });
  target.dispatchEvent(event);
  await settle();

  return event;
}

/** A toolbar button, or a selection-bar button, by its action. */
export const toolbarButton = (action: string) =>
  document.body.querySelector<HTMLButtonElement>(`.documents-toolbar [data-action="${action}"]`);

/** The open context menu's entry for an action. */
export const menuItem = (action: string) =>
  document.body.querySelector<HTMLElement>(`.documents-menu [data-action="${action}"]`);

export const menuActions = () =>
  [...document.body.querySelectorAll<HTMLElement>('.documents-menu [data-action]')].map((item) => item.dataset.action);

export function buttonLabelled(label: string): HTMLButtonElement | undefined {
  return [...document.body.querySelectorAll<HTMLButtonElement>('button')].find(
    (candidate) => candidate.getAttribute('aria-label') === label || !!candidate.textContent?.trim().endsWith(label),
  );
}

export const statusText = () => document.body.querySelector('[data-status]')?.textContent?.trim() ?? '';

export const questionText = () => document.body.querySelector('[data-question]')?.textContent?.trim() ?? '';

/** The breadcrumb's labels, root first. */
export const crumbLabels = () =>
  [...document.body.querySelectorAll<HTMLElement>('.documents-titlebar .breadcrumb-crumb')].map((crumb) =>
    (crumb.querySelector('.ellipsis')?.textContent ?? '').trim(),
  );

export const crumb = (label: string) =>
  [...document.body.querySelectorAll<HTMLElement>('.documents-titlebar .breadcrumb-crumb')].find(
    (candidate) => (candidate.querySelector('.ellipsis')?.textContent ?? '').trim() === label,
  )!;

/** A tree node's header by its folder and path. */
export const treeNode = (folder: string, path = '') =>
  document.body.querySelector<HTMLElement>(`.documents-tree-pane [data-node="${folder}::${path}"], [data-tree-slide] [data-node="${folder}::${path}"]`);

/**
 * A STAND-IN `DataTransfer`, which happy-dom does not have: what drag and drop code reads and
 * writes - types, data, effects, files and items.
 */
export class FakeDataTransfer {
  dropEffect = 'none';
  effectAllowed = 'all';
  private readonly data = new Map<string, string>();
  readonly files: File[];
  readonly items: { kind: string; type: string; getAsFile: () => File | null; webkitGetAsEntry: () => { isDirectory: boolean } }[];

  constructor(files: File[] = [], folders: string[] = []) {
    this.files = files;
    this.items = [
      ...files.map((file) => ({ kind: 'file', type: file.type, getAsFile: () => file, webkitGetAsEntry: () => ({ isDirectory: false }) })),
      ...folders.map((name) => ({
        kind: 'file',
        type: '',
        getAsFile: () => new File([], name),
        webkitGetAsEntry: () => ({ isDirectory: true }),
      })),
    ];
  }

  get types(): string[] {
    return [...this.data.keys(), ...(this.items.length > 0 ? ['Files'] : [])];
  }

  setData(type: string, value: string) {
    this.data.set(type, value);
  }

  getData(type: string) {
    return this.data.get(type) ?? '';
  }

  setDragImage() {}
}

/** One drag event, carrying the given transfer and modifier keys. */
export function dragEvent(type: string, data: FakeDataTransfer, init: { ctrlKey?: boolean; altKey?: boolean } = {}) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX: 100, clientY: 100, ...init }) as MouseEvent & {
    dataTransfer: FakeDataTransfer;
  };
  Object.defineProperty(event, 'dataTransfer', { value: data });

  return event as unknown as DragEvent & { dataTransfer: FakeDataTransfer };
}

/** Dispatches a drag event and settles. */
export async function fire(element: Element, type: string, data: FakeDataTransfer, init: { ctrlKey?: boolean; altKey?: boolean } = {}) {
  const event = dragEvent(type, data, init);
  element.dispatchEvent(event);
  await settle();

  return event;
}

/** A whole drag from one element to another: start, enter, over, drop, end. */
export async function dragAndDrop(from: Element, to: Element, init: { ctrlKey?: boolean; altKey?: boolean } = {}) {
  const data = new FakeDataTransfer();
  await fire(from, 'dragstart', data);
  await fire(to, 'dragenter', data, init);
  const over = await fire(to, 'dragover', data, init);
  await fire(to, 'drop', data, init);
  await fire(from, 'dragend', data);

  return { data, over };
}
