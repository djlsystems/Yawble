import { readSort } from './tableSort';
import {
  DefaultDocumentsSort,
  DocumentsSortColumns,
  type DocumentsSort,
} from './documentsExplorer';
import {
  clampNumber,
  readStored,
  readWindowGeometry,
  writeStored,
  writeWindowGeometry,
  type StoredWindow,
  type WindowBounds,
} from './windowGeometry';

/**
 * WHAT THE DOCUMENTS EXPLORER REMEMBERS, per browser: the window, the view, the sort and the tree
 * pane. Each is read in try/catch and checked on the way in, and each falls back to its default
 * when storage is empty, corrupt or refuses to be read - a remembered preference is never worth a
 * dialog that will not open. Writing never throws either.
 */

export const DocumentsWindowKey = 'harness.documents.window';
export const DocumentsViewKey = 'harness.documents.view';
export const DocumentsSortKey = 'harness.documents.sort';
export const DocumentsTreeKey = 'harness.documents.tree';

export const DocumentsWindowBounds: WindowBounds = {
  width: { min: 480, max: 2400 },
  height: { min: 320, max: 1600 },
};

export type DocumentsView = 'details' | 'list' | 'tiles';
export const DocumentsViews: readonly DocumentsView[] = ['details', 'list', 'tiles'];
export const DefaultDocumentsView: DocumentsView = 'details';

export interface DocumentsTreePrefs {
  open: boolean;
  width: number;
}

export const DocumentsTreeWidth = { min: 160, max: 480 } as const;
export const DefaultDocumentsTree: DocumentsTreePrefs = { open: true, width: 240 };

/** The remembered window, or null for unplaced (the dialog's default size, centred). */
export function readDocumentsWindow(viewportWidth: number, viewportHeight: number): StoredWindow | null {
  return readWindowGeometry(DocumentsWindowKey, DocumentsWindowBounds, viewportWidth, viewportHeight);
}

export function writeDocumentsWindow(window: StoredWindow): void {
  writeWindowGeometry(DocumentsWindowKey, DocumentsWindowBounds, window);
}

export function readDocumentsView(): DocumentsView {
  const stored = readStored(DocumentsViewKey);

  return DocumentsViews.includes(stored as DocumentsView) ? (stored as DocumentsView) : DefaultDocumentsView;
}

export function writeDocumentsView(view: DocumentsView): void {
  writeStored(DocumentsViewKey, view);
}

export function readDocumentsSort(): DocumentsSort {
  let raw: string | null = null;
  try {
    raw = localStorage.getItem(DocumentsSortKey);
  } catch {
    return DefaultDocumentsSort;
  }

  return readSort(raw, DocumentsSortColumns, DefaultDocumentsSort);
}

export function writeDocumentsSort(sort: DocumentsSort): void {
  writeStored(DocumentsSortKey, sort);
}

/** The tree pane: open or closed, and how wide. A width outside 160-480 is not believed. */
export function readDocumentsTree(): DocumentsTreePrefs {
  const stored = readStored(DocumentsTreeKey);
  if (typeof stored !== 'object' || stored === null || Array.isArray(stored)) return { ...DefaultDocumentsTree };

  const { open, width } = stored as Partial<DocumentsTreePrefs>;
  if (typeof width !== 'number' || width < DocumentsTreeWidth.min || width > DocumentsTreeWidth.max) {
    return { open: typeof open === 'boolean' ? open : DefaultDocumentsTree.open, width: DefaultDocumentsTree.width };
  }

  return {
    open: typeof open === 'boolean' ? open : DefaultDocumentsTree.open,
    width: clampNumber(width, DocumentsTreeWidth, DefaultDocumentsTree.width),
  };
}

export function writeDocumentsTree(tree: DocumentsTreePrefs): void {
  writeStored(DocumentsTreeKey, { open: tree.open, width: clampNumber(tree.width, DocumentsTreeWidth, DefaultDocumentsTree.width) });
}
