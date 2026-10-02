import type {
  DocumentEntry,
  DocumentsClash,
  DocumentsFolder,
  DocumentsFolderKey,
  OnClash,
} from '../api/types';
import type { TableSort } from './tableSort';

/**
 * THE DOCUMENTS EXPLORER'S RULES, AS PURE FUNCTIONS: what kind a file is and its icon, how a
 * folder sorts, what a click does to the selection, what a context menu offers, the path an agent
 * sees, and whether a drop would be refused. The dialog and its views only render these answers,
 * so each rule is tested here without mounting anything.
 */

// ---------------------------------------------------------------------------------------------
// Locations and items
// ---------------------------------------------------------------------------------------------

/** Where the explorer is: the root (every team's folder), or a path inside one folder. */
export interface DocumentsLocation {
  folder: DocumentsFolderKey | null;
  path: string;
}

/** The documents root: the list of team folders. */
export const RootLocation: DocumentsLocation = { folder: null, path: '' };

/** A folder something can be dropped on or pasted into: always inside a team's folder. */
export interface DropTarget {
  folder: DocumentsFolderKey | null;
  path: string;
}

/**
 * ONE ROW OF ANY VIEW. At the root a row is a team's folder (`team` set, `path` ''); below it, an
 * entry of the folder on screen.
 */
export interface ExplorerItem {
  /** Unique across folders: the folder key and the path. */
  key: string;
  folder: DocumentsFolderKey;
  path: string;
  name: string;
  isFolder: boolean;
  size: number;
  modifiedAt: string;
  children: number;
  kind: string;
  icon: string;
  /** Set on a root row: the team folder it is. */
  team?: DocumentsFolder;
}

export const itemKey = (folder: DocumentsFolderKey, path: string) => `${folder}::${path}`;

export function itemOfEntry(folder: DocumentsFolderKey, entry: DocumentEntry): ExplorerItem {
  const kind = kindOf(entry.name, entry.isFolder);

  return {
    key: itemKey(folder, entry.path),
    folder,
    path: entry.path,
    name: entry.name,
    isFolder: entry.isFolder,
    size: entry.size,
    modifiedAt: entry.modifiedAt,
    children: entry.children,
    kind: kind.kind,
    icon: kind.icon,
  };
}

export function itemOfFolder(folder: DocumentsFolder): ExplorerItem {
  return {
    key: itemKey(folder.folder, ''),
    folder: folder.folder,
    path: '',
    name: folder.label,
    isFolder: true,
    size: 0,
    modifiedAt: folder.modifiedAt,
    children: folder.entries,
    kind: folderState(folder) === 'live' ? 'Team folder' : folderState(folder) === 'gone' ? 'Gone team' : 'Superseded',
    icon: folderState(folder) === 'live' ? 'folder_shared' : 'folder_off',
    team: folder,
  };
}

/** Live, gone (the team was deleted) or retired (a later team took the name). Two facts, two
 *  sentences: never one "unavailable". */
export function folderState(folder: DocumentsFolder): 'live' | 'gone' | 'retired' {
  if (folder.retired) return 'retired';

  return folder.exists ? 'live' : 'gone';
}

/** The parent of a relative path, `''` at the folder's top. */
export function parentOf(path: string): string {
  const at = path.lastIndexOf('/');

  return at < 0 ? '' : path.slice(0, at);
}

export function leafOf(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

export function joinPath(parent: string, name: string): string {
  return parent ? `${parent}/${name}` : name;
}

/** One level up: a path's parent, a folder's top to the root, the root stays. */
export function upOf(location: DocumentsLocation): DocumentsLocation {
  if (location.folder === null) return RootLocation;
  if (location.path === '') return RootLocation;

  return { folder: location.folder, path: parentOf(location.path) };
}

export const sameLocation = (a: DocumentsLocation, b: DocumentsLocation) =>
  a.folder === b.folder && a.path === b.path;

/** A crumb of the breadcrumb, root first. The folder crumb carries its gone or retired mark. */
export interface DocumentsCrumb {
  label: string;
  path: string;
  icon?: string;
  note?: string;
  target: DropTarget;
}

export function crumbsOf(location: DocumentsLocation, folders: DocumentsFolder[]): DocumentsCrumb[] {
  const crumbs: DocumentsCrumb[] = [{ label: 'Documents', path: '', target: RootLocation }];
  if (location.folder === null) return crumbs;

  const folder = folders.find((entry) => entry.folder === location.folder);
  const state = folder ? folderState(folder) : 'live';
  crumbs.push({
    label: folder?.label ?? location.folder,
    path: '',
    ...(state === 'live' ? {} : { icon: 'folder_off', note: state === 'gone' ? 'gone' : 'superseded' }),
    target: { folder: location.folder, path: '' },
  });

  const parts = location.path.split('/').filter(Boolean);
  parts.forEach((name, index) => {
    const path = parts.slice(0, index + 1).join('/');
    crumbs.push({ label: name, path, target: { folder: location.folder, path } });
  });

  return crumbs;
}

// ---------------------------------------------------------------------------------------------
// Kinds and icons
// ---------------------------------------------------------------------------------------------

const Kinds: { kind: string; icon: string; extensions: string[] }[] = [
  { kind: 'Document', icon: 'description', extensions: ['doc', 'docx', 'odt', 'rtf', 'pages'] },
  { kind: 'Spreadsheet', icon: 'table_chart', extensions: ['xls', 'xlsx', 'ods', 'csv', 'tsv', 'numbers'] },
  { kind: 'Presentation', icon: 'slideshow', extensions: ['ppt', 'pptx', 'odp', 'key'] },
  { kind: 'PDF', icon: 'picture_as_pdf', extensions: ['pdf'] },
  { kind: 'Image', icon: 'image', extensions: ['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'bmp', 'tiff', 'heic'] },
  { kind: 'Video', icon: 'movie', extensions: ['mp4', 'mov', 'webm', 'mkv', 'avi'] },
  { kind: 'Audio', icon: 'audio_file', extensions: ['mp3', 'wav', 'ogg', 'flac', 'm4a'] },
  { kind: 'Archive', icon: 'folder_zip', extensions: ['zip', 'tar', 'gz', 'tgz', '7z', 'rar', 'bz2', 'xz'] },
  {
    kind: 'Code',
    icon: 'code',
    extensions: ['ts', 'js', 'vue', 'cs', 'py', 'go', 'rs', 'java', 'c', 'cpp', 'h', 'sh', 'ps1', 'rb', 'php', 'kt', 'swift', 'sql', 'html', 'css', 'scss'],
  },
  { kind: 'Markdown', icon: 'article', extensions: ['md', 'markdown'] },
  { kind: 'JSON', icon: 'data_object', extensions: ['json', 'jsonl', 'yaml', 'yml', 'toml', 'xml'] },
  { kind: 'Text', icon: 'text_snippet', extensions: ['txt', 'log', 'ini', 'cfg', 'conf'] },
];

/** The best guess at a file's kind, by its extension, case-insensitive. A folder is a folder. */
export function kindOf(name: string, isFolder: boolean): { kind: string; icon: string } {
  if (isFolder) return { kind: 'Folder', icon: 'folder' };

  const dot = name.lastIndexOf('.');
  const extension = dot > 0 ? name.slice(dot + 1).toLowerCase() : '';
  const known = Kinds.find((entry) => entry.extensions.includes(extension));

  return known ? { kind: known.kind, icon: known.icon } : { kind: 'File', icon: 'draft' };
}

/** A size as a person reads it; a folder says how many things are in it. */
export function sizeText(entry: { isFolder: boolean; size: number; children: number }): string {
  if (entry.isFolder) return `${entry.children} item${entry.children === 1 ? '' : 's'}`;
  if (entry.size < 1024) return `${entry.size} B`;
  if (entry.size < 1024 * 1024) return `${Math.round(entry.size / 1024)} KB`;

  return `${(entry.size / (1024 * 1024)).toFixed(1)} MB`;
}

/** Bytes alone, for the status bar's total. */
export function bytesText(size: number): string {
  return sizeText({ isFolder: false, size, children: 0 });
}

export function modifiedText(iso: string): string {
  const when = new Date(iso);
  if (Number.isNaN(when.getTime())) return '';

  return when.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

// ---------------------------------------------------------------------------------------------
// Sorting
// ---------------------------------------------------------------------------------------------

export const DocumentsSortColumns = ['name', 'kind', 'size', 'modified', 'team'] as const;
export type DocumentsSortColumn = (typeof DocumentsSortColumns)[number];
export type DocumentsSort = TableSort<DocumentsSortColumn>;
export const DefaultDocumentsSort: DocumentsSort = { column: 'name', descending: false };

const byName = (a: ExplorerItem, b: ExplorerItem) =>
  a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });

/**
 * FOLDERS FIRST, WHATEVER THE SORT, then the column in its direction, then the name ascending so
 * equal keys never shuffle between two refreshes.
 */
export function compareEntries(a: ExplorerItem, b: ExplorerItem, sort: DocumentsSort): number {
  if (a.isFolder !== b.isFolder) return a.isFolder ? -1 : 1;

  const direction = sort.descending ? -1 : 1;
  let primary = 0;

  switch (sort.column) {
    case 'name':
      primary = byName(a, b);
      break;
    case 'kind':
      primary = a.kind.localeCompare(b.kind);
      break;
    case 'size':
      primary = a.isFolder ? a.children - b.children : a.size - b.size;
      break;
    case 'modified':
      primary = (Date.parse(a.modifiedAt) || 0) - (Date.parse(b.modifiedAt) || 0);
      break;
    case 'team':
      primary = (a.team?.label ?? a.name).localeCompare(b.team?.label ?? b.name);
      break;
  }

  return primary !== 0 ? primary * direction : byName(a, b);
}

// ---------------------------------------------------------------------------------------------
// Selection
// ---------------------------------------------------------------------------------------------

/**
 * WHAT IS SELECTED, as a person builds it: `keys` in the order they were added, the `anchor` a
 * Shift range is measured from, and the `focus` the keyboard moves.
 */
export interface Selection {
  keys: string[];
  anchor: string | null;
  focus: string | null;
}

export const EmptySelection: Selection = { keys: [], anchor: null, focus: null };

/** A plain click: this one alone. */
export function selectOnly(key: string): Selection {
  return { keys: [key], anchor: key, focus: key };
}

/** Ctrl/Cmd+click and Space: in or out, the anchor moves here. */
export function toggle(selection: Selection, key: string): Selection {
  const keys = selection.keys.includes(key)
    ? selection.keys.filter((candidate) => candidate !== key)
    : [...selection.keys, key];

  return { keys, anchor: key, focus: key };
}

/** Shift+click and Shift+arrow: the range from the anchor to here, in display order. */
export function extend(selection: Selection, order: string[], key: string): Selection {
  const anchor = selection.anchor !== null && order.includes(selection.anchor) ? selection.anchor : key;
  const from = order.indexOf(anchor);
  const to = order.indexOf(key);
  if (to < 0) return selection;

  const [low, high] = from <= to ? [from, to] : [to, from];

  return { keys: order.slice(low, high + 1), anchor, focus: key };
}

export function selectAll(order: string[]): Selection {
  return { keys: [...order], anchor: order[0] ?? null, focus: order[0] ?? null };
}

/** Clears what is selected; the focus stays where the keyboard left it. */
export function clearSelection(selection: Selection): Selection {
  return { keys: [], anchor: null, focus: selection.focus };
}

/**
 * An arrow key: focus moves by `delta`. `select` selects only the new one, `extend` grows the
 * range from the anchor (Shift), `focus` moves without selecting (Ctrl).
 */
export function moveFocus(
  selection: Selection,
  order: string[],
  delta: number,
  how: 'select' | 'extend' | 'focus',
): Selection {
  if (order.length === 0) return selection;

  const at = selection.focus === null ? -1 : order.indexOf(selection.focus);
  const next = at < 0 ? (delta > 0 ? 0 : order.length - 1) : Math.max(0, Math.min(order.length - 1, at + delta));
  const key = order[next]!;

  if (how === 'extend') return extend(selection, order, key);
  if (how === 'focus') return { ...selection, focus: key };

  return selectOnly(key);
}

/** Keeps only what is still on screen, so a refresh never leaves a ghost selected. */
export function pruneSelection(selection: Selection, order: string[]): Selection {
  const keys = selection.keys.filter((key) => order.includes(key));

  return {
    keys,
    anchor: selection.anchor !== null && order.includes(selection.anchor) ? selection.anchor : null,
    focus: selection.focus !== null && order.includes(selection.focus) ? selection.focus : null,
  };
}

// ---------------------------------------------------------------------------------------------
// Menus and the toolbar
// ---------------------------------------------------------------------------------------------

export type DocumentsAction =
  | 'open'
  | 'view'
  | 'download'
  | 'cut'
  | 'copy'
  | 'paste'
  | 'pasteInto'
  | 'rename'
  | 'moveTo'
  | 'copyPath'
  | 'delete'
  | 'deleteAll'
  | 'newFolder'
  | 'newFolderInside'
  | 'upload'
  | 'refresh';

/** What the menu and toolbar know about where the person is and what they picked. */
export interface MenuContext {
  location: DocumentsLocation;
  /** The folder on screen, or null at the root. */
  folder: DocumentsFolder | null;
  selected: ExplorerItem[];
  clipboardFull: boolean;
  /** Whether a file is shown by the server's view route (`isViewable`). */
  viewable: (item: ExplorerItem) => boolean;
}

/** Whether an action applies now, and if not, the sentence a disabled control says. */
export interface Availability {
  ok: boolean;
  reason?: string;
}

/** The sentence for a folder nothing can be added to, or null for a live one. */
export function readOnlyReason(folder: DocumentsFolder | null): string | null {
  if (folder === null) return "Open a team's folder first";

  switch (folderState(folder)) {
    case 'gone':
      return "Nothing can be added to a gone team's documents";
    case 'retired':
      return "Nothing can be added to an earlier team's documents";
    default:
      return null;
  }
}

/**
 * ONE ANSWER FOR THE TOOLBAR AND EVERY MENU: whether each action applies, and why not. A disabled
 * button says the same sentence a missing menu item would have meant.
 */
export function availability(action: DocumentsAction, context: MenuContext): Availability {
  const count = context.selected.length;
  const one = count === 1 ? context.selected[0]! : null;
  const atRoot = context.location.folder === null;
  const writeRefusal = readOnlyReason(context.folder);
  const refuse = (reason: string): Availability => ({ ok: false, reason });

  switch (action) {
    case 'open':
      return one ? { ok: true } : refuse('Select one item to open');
    case 'view':
      return one && !one.isFolder && context.viewable(one) ? { ok: true } : refuse('Select one viewable file');
    case 'download':
      return one && !one.isFolder ? { ok: true } : refuse('Select one file to download');
    case 'copy':
      return count > 0 ? { ok: true } : refuse('Select something first');
    case 'cut':
    case 'moveTo':
      if (count === 0) return refuse('Select something first');
      if (atRoot) return refuse("A team's folder itself cannot be moved");
      return { ok: true };
    case 'delete':
      if (count === 0) return refuse('Select something first');
      if (atRoot) return refuse("A live team's folder cannot be deleted");
      return { ok: true };
    case 'deleteAll':
      return one?.team && folderState(one.team) !== 'live' ? { ok: true } : refuse('Only a gone or superseded folder');
    case 'rename':
      if (count !== 1) return refuse('Select one item to rename');
      if (atRoot) return refuse("A team's folder cannot be renamed");
      if (writeRefusal) return refuse(writeRefusal);
      return { ok: true };
    case 'copyPath':
      return count === 1 ? { ok: true } : refuse('Select one item to copy its path');
    case 'paste':
      if (!context.clipboardFull) return refuse('Nothing to paste');
      if (writeRefusal) return refuse(writeRefusal);
      return { ok: true };
    case 'pasteInto': {
      if (!context.clipboardFull) return refuse('Nothing to paste');
      if (!one?.isFolder) return refuse('Select one folder to paste into');
      if (one.team) return folderState(one.team) === 'live' ? { ok: true } : refuse(readOnlyReason(one.team)!);
      if (writeRefusal) return refuse(writeRefusal);
      return { ok: true };
    }
    case 'newFolder':
    case 'upload':
      return writeRefusal ? refuse(writeRefusal) : { ok: true };
    case 'newFolderInside':
      return writeRefusal ? refuse(writeRefusal) : { ok: true };
    case 'refresh':
      return { ok: true };
  }
}

export type MenuEntry =
  | { kind: 'action'; action: DocumentsAction; label: string; icon: string; shortcut?: string; disabled?: string }
  | { kind: 'separator' }
  | { kind: 'submenu'; id: 'view' | 'sort'; label: string; icon: string };

/** Where a context menu was opened. */
export type MenuTarget = 'item' | 'empty' | 'tree';

const Labels: Record<DocumentsAction, { label: string; icon: string; shortcut?: string }> = {
  open: { label: 'Open', icon: 'folder_open' },
  view: { label: 'View', icon: 'visibility' },
  download: { label: 'Download', icon: 'download' },
  cut: { label: 'Cut', icon: 'content_cut', shortcut: 'Ctrl+X' },
  copy: { label: 'Copy', icon: 'content_copy', shortcut: 'Ctrl+C' },
  paste: { label: 'Paste', icon: 'content_paste', shortcut: 'Ctrl+V' },
  pasteInto: { label: 'Paste into this folder', icon: 'content_paste' },
  rename: { label: 'Rename', icon: 'edit', shortcut: 'F2' },
  moveTo: { label: 'Move to…', icon: 'drive_file_move' },
  copyPath: { label: 'Copy as path', icon: 'link', shortcut: 'Ctrl+Shift+C' },
  delete: { label: 'Delete', icon: 'delete', shortcut: 'Delete' },
  deleteAll: { label: 'Delete all of these documents', icon: 'delete' },
  newFolder: { label: 'New folder', icon: 'create_new_folder', shortcut: 'Ctrl+Shift+N' },
  newFolderInside: { label: 'New folder inside', icon: 'create_new_folder' },
  upload: { label: 'Upload', icon: 'upload' },
  refresh: { label: 'Refresh', icon: 'refresh', shortcut: 'F5' },
};

/**
 * THE CONTEXT MENU, as a list. On one item: Open, View and Download where they apply, then the
 * clipboard, then Rename, Move to… and Copy as path, then Delete. On many: only what acts on many.
 * On empty space: what makes something here. In the tree: what a folder can be asked.
 *
 * An entry that does not apply to THIS target is left out; one that applies but is refused here
 * (a gone team's folder, nothing to paste) is shown disabled with the reason.
 */
export function menuFor(target: MenuTarget, context: MenuContext): MenuEntry[] {
  const entry = (action: DocumentsAction, label?: string): MenuEntry => {
    const known = Labels[action];
    const state = availability(action, context);

    return {
      kind: 'action',
      action,
      label: label ?? known.label,
      icon: known.icon,
      ...(known.shortcut ? { shortcut: known.shortcut } : {}),
      ...(state.ok ? {} : { disabled: state.reason ?? '' }),
    };
  };
  const separator: MenuEntry = { kind: 'separator' };
  const atRoot = context.location.folder === null;

  if (target === 'tree') {
    return [entry('open'), entry('pasteInto', 'Paste into'), entry('copyPath'), entry('newFolderInside')];
  }

  if (target === 'empty') {
    if (atRoot) {
      return [entry('refresh'), separator, { kind: 'submenu', id: 'view', label: 'View', icon: 'view_module' }, { kind: 'submenu', id: 'sort', label: 'Sort by', icon: 'sort' }];
    }

    return [
      entry('newFolder'),
      entry('upload'),
      entry('paste'),
      separator,
      entry('refresh'),
      { kind: 'submenu', id: 'view', label: 'View', icon: 'view_module' },
      { kind: 'submenu', id: 'sort', label: 'Sort by', icon: 'sort' },
    ];
  }

  const count = context.selected.length;

  if (count > 1) {
    if (atRoot) return [entry('copy')];

    return [entry('cut'), entry('copy'), entry('moveTo'), separator, entry('delete', `Delete (${count} items)`)];
  }

  const one = context.selected[0];
  if (!one) return [];

  // A TEAM'S FOLDER AT THE ROOT: open it, copy it whole, copy its path. Never cut, moved, renamed
  // or deleted while its team is live; a gone or superseded one can be cleared out.
  if (one.team) {
    const entries: MenuEntry[] = [entry('open'), separator, entry('copy')];
    if (context.clipboardFull) entries.push(entry('pasteInto'));
    entries.push(separator, entry('copyPath'));
    if (folderState(one.team) !== 'live') entries.push(separator, entry('deleteAll'));

    return entries;
  }

  const entries: MenuEntry[] = [entry('open')];
  if (!one.isFolder) {
    if (context.viewable(one)) entries.push(entry('view'));
    entries.push(entry('download'));
  }
  entries.push(separator, entry('cut'), entry('copy'));
  if (one.isFolder && context.clipboardFull) entries.push(entry('pasteInto'));
  entries.push(separator, entry('rename'), entry('moveTo'), entry('copyPath'), separator, entry('delete'));

  return entries;
}

// ---------------------------------------------------------------------------------------------
// Paths and clashes
// ---------------------------------------------------------------------------------------------

/**
 * THE PATH AN AGENT SEES: `<root>/<folder>/<path>`, which is what `HARNESS_SHARED` is
 * (`<root>/<team>`) plus the path inside it.
 */
export function agentPath(root: string, folder: DocumentsFolderKey, path: string): string {
  const base = `${root.replace(/\/+$/, '')}/${folder}`;

  return path ? `${base}/${path}` : base;
}

/** The person's answer to one clash. `rest` applies it to every clash still to ask about. */
export interface ClashAnswer {
  choice: OnClash;
  rest: boolean;
}

/**
 * THE CLASH QUEUE: asks about each clash in turn, until an answer says "do this for the rest",
 * which answers every one left. Null when the person cancelled - nothing is sent then.
 */
export async function clashQueue(
  clashes: DocumentsClash[],
  ask: (clash: DocumentsClash, remaining: number) => Promise<ClashAnswer | null>,
): Promise<Map<string, OnClash> | null> {
  const choices = new Map<string, OnClash>();
  let forRest: OnClash | null = null;

  for (const [index, clash] of clashes.entries()) {
    if (forRest) {
      choices.set(clash.from, forRest);
      continue;
    }

    const answer = await ask(clash, clashes.length - index - 1);
    if (!answer) return null;

    choices.set(clash.from, answer.choice);
    if (answer.rest) forRest = answer.choice;
  }

  return choices;
}

// ---------------------------------------------------------------------------------------------
// Drag and drop
// ---------------------------------------------------------------------------------------------

export type DropVerdict = { ok: true } | { ok: false; reason: string };

/** What is being dragged: items from one folder of this dialog, or files from the computer. */
export type DragSource =
  | { kind: 'items'; folder: DocumentsFolderKey; paths: string[] }
  | { kind: 'files'; files: { name: string; isFolder: boolean }[] | null };

/** Copy with Ctrl on Windows and Linux, with Option on macOS; move otherwise. */
export function dragMode(event: { ctrlKey: boolean; altKey: boolean }, isMac: boolean): 'move' | 'copy' {
  return (isMac ? event.altKey : event.ctrlKey) ? 'copy' : 'move';
}

/** What a target folder is called in a sentence: its last path segment, or the team's label. */
export function targetName(target: DropTarget, folders: DocumentsFolder[]): string {
  if (target.folder === null) return 'Documents';
  if (target.path) return leafOf(target.path);

  return folders.find((entry) => entry.folder === target.folder)?.label ?? target.folder;
}

/**
 * THE COURTESY CHECK: would the server refuse this drop for a reason the dialog can already see?
 * Gone, retired, the items' own folder, a folder into itself or below itself, the documents root,
 * and a folder from the computer. The server refuses all of these regardless; this only lets the
 * folder under the pointer say so before anything is sent.
 */
export function dropVerdict(
  source: DragSource,
  target: DropTarget,
  folders: DocumentsFolder[],
  mode: 'move' | 'copy',
): DropVerdict {
  if (target.folder === null) return { ok: false, reason: "Drop into a team's folder." };

  const folder = folders.find((entry) => entry.folder === target.folder);
  if (folder?.retired) {
    return { ok: false, reason: `This folder belongs to an earlier team called ${folder.label}. Nothing can be added to it.` };
  }
  if (folder && !folder.exists) {
    return { ok: false, reason: `${folder.label} no longer exists. Nothing can be added to its documents.` };
  }

  if (source.kind === 'files') {
    if (source.files !== null && source.files.length > 0 && source.files.every((file) => file.isFolder)) {
      return { ok: false, reason: 'Folders cannot be uploaded by dropping; drop the files inside them.' };
    }

    return { ok: true };
  }

  if (source.folder === target.folder) {
    for (const path of source.paths) {
      if (path === '' || target.path === path || target.path.startsWith(`${path}/`)) {
        const name = path === '' ? (folder?.label ?? source.folder) : leafOf(path);
        return { ok: false, reason: `${name} cannot be ${mode === 'copy' ? 'copied' : 'moved'} into itself.` };
      }
    }

    if (source.paths.every((path) => parentOf(path) === target.path)) {
      return { ok: false, reason: `Already in ${targetName(target, folders)}.` };
    }
  }

  return { ok: true };
}

/** The hint beside the pointer: what a drop here would do, or why it would not. */
export function dropHint(
  source: DragSource,
  target: DropTarget,
  folders: DocumentsFolder[],
  mode: 'move' | 'copy',
  verdict: DropVerdict,
): string {
  if (!verdict.ok) return verdict.reason;

  const where = targetName(target, folders);

  if (source.kind === 'files') {
    const count = source.files?.filter((file) => !file.isFolder).length ?? 0;
    return count > 0 ? `Upload ${count} file${count === 1 ? '' : 's'} to ${where}` : `Upload to ${where}`;
  }

  const what = source.paths.length === 1 ? leafOf(source.paths[0]!) || 'folder' : `${source.paths.length} items`;

  return `${mode === 'copy' ? 'Copy' : 'Move'} ${what} to ${where}`;
}
