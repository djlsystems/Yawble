<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, provide, ref, watch } from 'vue';
import { copyToClipboard, useQuasar } from 'quasar';
import * as api from '../api/documents';
import type { DocumentsClash, DocumentsFolder, DocumentsFolderKey } from '../api/types';
import { isViewable } from '../lib/documentView';
import {
  RootLocation,
  agentPath,
  availability,
  bytesText,
  clearSelection,
  compareEntries,
  crumbsOf,
  extend,
  folderState,
  itemKey,
  itemOfEntry,
  itemOfFolder,
  joinPath,
  leafOf,
  menuFor,
  moveFocus,
  pruneSelection,
  readOnlyReason,
  sameLocation,
  selectAll,
  selectOnly,
  targetName,
  toggle,
  upOf,
  EmptySelection,
  type ClashAnswer,
  type DocumentsAction,
  type DocumentsLocation,
  type DocumentsSort,
  type DocumentsSortColumn,
  type DropTarget,
  type ExplorerItem,
  type MenuContext,
  type MenuTarget,
  type Selection,
} from '../lib/documentsExplorer';
import {
  readDocumentsSort,
  readDocumentsTree,
  readDocumentsView,
  readDocumentsWindow,
  writeDocumentsSort,
  writeDocumentsTree,
  writeDocumentsView,
  writeDocumentsWindow,
  DocumentsTreeWidth,
  DocumentsWindowBounds,
  type DocumentsTreePrefs,
  type DocumentsView,
} from '../lib/documentsPrefs';
import { nextSort } from '../lib/tableSort';
import { useWindowFrame } from '../lib/useWindowFrame';
import { clampGeometry, clampToViewport, type StoredWindow, type WindowGeometry } from '../lib/windowGeometry';
import { useDocumentsTransfer, type TransferOutcome } from '../lib/useDocumentsTransfer';
import { documentsDragKey, useDocumentsDrag } from '../lib/useDocumentsDrag';
import { useDocumentsClipboardStore } from '../stores/documentsClipboard';
import DocumentsBreadcrumb, { type BreadcrumbCrumb } from './documents/DocumentsBreadcrumb.vue';
import DocumentsClashDialog from './documents/DocumentsClashDialog.vue';
import DocumentsContextMenu from './documents/DocumentsContextMenu.vue';
import DocumentsDetails from './documents/DocumentsDetails.vue';
import DocumentsDropHint from './documents/DocumentsDropHint.vue';
import DocumentsListView from './documents/DocumentsListView.vue';
import DocumentsMoveDialog from './documents/DocumentsMoveDialog.vue';
import DocumentsTiles from './documents/DocumentsTiles.vue';
import DocumentsTree from './documents/DocumentsTree.vue';

/**
 * THE DOCUMENTS EXPLORER: a file manager over the TENANT documents root, one folder per team, a
 * folder outliving the team that wrote it.
 *
 * A movable, resizable window (remembered), a tree of every team's folder and the folders inside
 * them, a breadcrumb, Details / List / Tiles, multi-select, and copy, cut, paste, move, delete,
 * rename and copy as path - from the toolbar, the keyboard, a context menu and drag and drop.
 *
 * WHAT THIS FILE OWNS: where the person is, what is selected, the dialogs it asks with, and the
 * one transfer and one drag state every surface shares. The rules are pure functions in
 * `lib/documentsExplorer.ts`; the window's pointer half is `lib/useWindowFrame.ts`, the Concierge's
 * own; every move, copy and upload goes through `lib/useDocumentsTransfer.ts`.
 *
 * `exists` AND `retired` ARE TWO FACTS WITH DIFFERENT FIXES - a team was deleted, versus a later
 * team took its identifier and this folder was moved aside - and each keeps its own sentence here.
 * Nothing can be added to either; both can be read, copied or moved out of, and cleared out.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{
  /** The team folder to open at, from Active Team › Documents. Null opens at the root. */
  start?: DocumentsFolderKey | null;
}>();

const $q = useQuasar();
const clipboard = useDocumentsClipboardStore();

const narrow = computed(() => !!$q.screen?.lt?.sm);
const isMac = () => !!$q.platform?.is?.mac;

// ---- where the person is ------------------------------------------------------------------

/** Every documents folder that EXISTS, which is not the same list as the teams. */
const folders = ref<DocumentsFolder[]>([]);
const root = ref('');
const location = ref<DocumentsLocation>(RootLocation);
const back = ref<DocumentsLocation[]>([]);
const forward = ref<DocumentsLocation[]>([]);
const entries = ref<ExplorerItem[]>([]);

const busy = ref(false);
const error = ref('');
const done = ref<string[]>([]);

/** The folder on screen, or null at the root. */
const current = computed(() =>
  location.value.folder === null ? null : (folders.value.find((entry) => entry.folder === location.value.folder) ?? null),
);
const currentState = computed(() => (current.value ? folderState(current.value) : null));
const atRoot = computed(() => location.value.folder === null);

// ---- remembered preferences --------------------------------------------------------------

const view = ref<DocumentsView>(readDocumentsView());
const sort = ref<DocumentsSort>(readDocumentsSort());
const tree = ref<DocumentsTreePrefs>(readDocumentsTree());
const treeSlide = ref(false);

function setView(next: DocumentsView) {
  view.value = next;
  writeDocumentsView(next);
}

function sortBy(column: DocumentsSortColumn) {
  sort.value = nextSort(sort.value, column);
  writeDocumentsSort(sort.value);
}

function toggleTree() {
  if (narrow.value) {
    treeSlide.value = !treeSlide.value;
    return;
  }

  tree.value = { ...tree.value, open: !tree.value.open };
  writeDocumentsTree(tree.value);
}

const items = computed(() => [...entries.value].sort((a, b) => compareEntries(a, b, sort.value)));
const order = computed(() => items.value.map((item) => item.key));

// ---- selection ----------------------------------------------------------------------------

const selection = ref<Selection>(EmptySelection);
const selectedItems = computed(() => items.value.filter((item) => selection.value.keys.includes(item.key)));

function press(item: ExplorerItem, event: MouseEvent) {
  if (event.shiftKey) selection.value = extend(selection.value, order.value, item.key);
  else if (event.ctrlKey || event.metaKey) selection.value = toggle(selection.value, item.key);
  else selection.value = selectOnly(item.key);
}

// ---- the window ---------------------------------------------------------------------------

const viewport = () => ({
  width: window.visualViewport?.width ?? window.innerWidth,
  height: window.visualViewport?.height ?? window.innerHeight,
});

/** Where the person put the window, or null while it sits at its default (centred, os-dialog-lg). */
const placed = ref<WindowGeometry | null>(null);
const maximised = ref(false);

function remember() {
  if (placed.value) writeDocumentsWindow({ ...placed.value, maximised: maximised.value });
}

const windowed = computed(() => !narrow.value && !maximised.value);

const frame = useWindowFrame({
  geometry: () => placed.value,
  apply: (next) => {
    const { width, height } = viewport();
    const fallback = placed.value ?? { left: 0, top: 0, width: DocumentsWindowBounds.width.min, height: DocumentsWindowBounds.height.min };
    placed.value = clampToViewport(clampGeometry(next, DocumentsWindowBounds, fallback), DocumentsWindowBounds, width, height);
    remember();
  },
  isWindowed: () => windowed.value,
  shellSelector: '.documents-window',
  minimum: { width: DocumentsWindowBounds.width.min, height: DocumentsWindowBounds.height.min },
});

/** Maximise keeps the placed geometry, so restoring goes back to it. An unplaced window has no
 *  place to remember, so its maximised state lasts only while the dialog is open. */
function toggleMaximised() {
  maximised.value = !maximised.value;
  remember();
}

function restoreWindow() {
  const { width, height } = viewport();
  const stored: StoredWindow | null = readDocumentsWindow(width, height);

  placed.value = stored ? { left: stored.left, top: stored.top, width: stored.width, height: stored.height } : null;
  maximised.value = stored?.maximised ?? false;
}

// ---- the tree pane's width ------------------------------------------------------------------

let treeDrag: { x: number; width: number } | null = null;

function onTreeGripDown(event: PointerEvent) {
  event.preventDefault();
  treeDrag = { x: event.clientX, width: tree.value.width };
  (event.currentTarget as Element).setPointerCapture?.(event.pointerId);
}

function onTreeGripMove(event: PointerEvent) {
  if (!treeDrag) return;

  const width = Math.min(DocumentsTreeWidth.max, Math.max(DocumentsTreeWidth.min, treeDrag.width + event.clientX - treeDrag.x));
  tree.value = { ...tree.value, width };
}

function onTreeGripUp() {
  if (!treeDrag) return;

  treeDrag = null;
  writeDocumentsTree(tree.value);
}

// ---- loading ------------------------------------------------------------------------------

async function loadFolders() {
  try {
    const answer = await api.listDocumentsRoot();
    folders.value = answer.folders;
    root.value = answer.root;
  } catch (failure) {
    error.value = (failure as Error).message;
    folders.value = [];
  }
}

async function loadEntries() {
  busy.value = true;

  try {
    const here = location.value;

    if (here.folder === null) {
      entries.value = folders.value.map(itemOfFolder);
    } else {
      const folder = here.folder;
      entries.value = (await api.listDocuments(folder, here.path)).map((entry) => itemOfEntry(folder, entry));
    }

    selection.value = pruneSelection(selection.value, order.value);
  } catch (failure) {
    error.value = (failure as Error).message;
    entries.value = [];
  } finally {
    busy.value = false;
  }
}

/** Everything re-read: the folder list, then what is on screen. */
async function refresh() {
  await loadFolders();

  // A folder that has genuinely gone takes the explorer back to the root.
  if (location.value.folder !== null && !folders.value.some((entry) => entry.folder === location.value.folder)) {
    location.value = RootLocation;
  }

  await loadEntries();
}

async function navigate(to: DocumentsLocation, history = true) {
  if (sameLocation(to, location.value)) return;

  if (history) {
    back.value = [...back.value, location.value];
    forward.value = [];
  }

  location.value = to;
  selection.value = EmptySelection;
  renaming.value = null;
  treeSlide.value = false;
  error.value = '';
  done.value = [];
  await loadEntries();
}

function goBack() {
  const previous = back.value.at(-1);
  if (!previous) return;

  back.value = back.value.slice(0, -1);
  forward.value = [location.value, ...forward.value];
  void navigate(previous, false);
}

function goForward() {
  const next = forward.value[0];
  if (!next) return;

  forward.value = forward.value.slice(1);
  back.value = [...back.value, location.value];
  void navigate(next, false);
}

function goUp() {
  if (atRoot.value) return;

  void navigate(upOf(location.value));
}

watch(open, async (showing) => {
  if (!showing) return;

  view.value = readDocumentsView();
  sort.value = readDocumentsSort();
  tree.value = readDocumentsTree();
  restoreWindow();

  back.value = [];
  forward.value = [];
  error.value = '';
  done.value = [];
  selection.value = EmptySelection;
  renaming.value = null;

  await loadFolders();

  // Opened from a team: its folder, matched on `folder` - never on `team`, which a RETIRED folder
  // shares with the live team that took its name. A team that has written nothing yet has no
  // folder, and the explorer opens at the root.
  const start = props.start ? folders.value.find((entry) => entry.folder === props.start) : undefined;
  location.value = start ? { folder: start.folder, path: '' } : RootLocation;

  await loadEntries();
});

// ---- the one question ------------------------------------------------------------------------

/** ONE QUESTION AT A TIME, asked here and answered by the person. */
const question = ref<{ text: string; yes: string; answer: (yes: boolean) => void } | null>(null);

function ask(text: string, yes = 'Delete'): Promise<boolean> {
  return new Promise((answer) => {
    question.value = { text, yes, answer };
  });
}

function reply(yes: boolean) {
  const asked = question.value;
  question.value = null;
  asked?.answer(yes);
}

const asking = computed({
  get: () => question.value !== null,
  set: (showing: boolean) => {
    if (!showing) reply(false);
  },
});

/** A name, asked for New folder. */
const naming = ref<{ title: string; value: string; answer: (name: string | null) => void } | null>(null);

function askName(title: string, value: string): Promise<string | null> {
  return new Promise((answer) => {
    naming.value = { title, value, answer };
  });
}

function named(value: string | null) {
  const asked = naming.value;
  naming.value = null;
  asked?.answer(value === null ? null : value.trim() || null);
}

const namingOpen = computed({
  get: () => naming.value !== null,
  set: (showing: boolean) => {
    if (!showing) named(null);
  },
});

// ---- clashes ------------------------------------------------------------------------------

const clashAsked = ref<{ clash: DocumentsClash; remaining: number; answer: (answer: ClashAnswer | null) => void } | null>(null);
const clashFolder = ref('');

function askClash(clash: DocumentsClash, remaining: number): Promise<ClashAnswer | null> {
  return new Promise((answer) => {
    clashAsked.value = { clash, remaining, answer };
  });
}

function clashAnswered(answer: ClashAnswer | null) {
  const asked = clashAsked.value;
  clashAsked.value = null;
  asked?.answer(answer);
}

// ---- the one transfer, and the one drag state -------------------------------------------------

function settled(outcome: TransferOutcome) {
  if (outcome.kind === 'cancelled') return;

  if (outcome.kind === 'refused') error.value = outcome.error;
  else if (outcome.kind === 'partial') {
    error.value = outcome.error;
    done.value = outcome.results.filter((result) => result.outcome === 'done').map((result) => result.to);
  } else {
    error.value = '';
    done.value = [];
    const changed = outcome.results.filter((result) => result.outcome === 'done').length;
    const skipped = outcome.results.filter((result) => result.outcome === 'skipped').length;
    const verb = outcome.verb === 'move' ? 'Moved' : outcome.verb === 'copy' ? 'Copied' : 'Uploaded';
    const notCopied = outcome.results.flatMap((result) => result.notCopied ?? []);

    $q.notify?.({
      message: `${verb} ${changed} item${changed === 1 ? '' : 's'}${skipped ? `, skipped ${skipped}` : ''}`,
      ...(notCopied.length ? { caption: `Not copied: ${notCopied.map((left) => `${left.path} (${left.reason})`).join('; ')}` } : {}),
      timeout: 2500,
    });
  }

  void refresh();
}

const transfer = useDocumentsTransfer({
  askClash: (clash, remaining) => askClash(clash, remaining),
  settled,
});

/** Which folder a clash is in, for the clash dialog's sentence. */
function aimClash(to: DropTarget) {
  clashFolder.value = targetName(to, folders.value);
}

const drag = useDocumentsDrag({
  transfer: {
    transfer: (mode, source, paths, to) => {
      aimClash(to);
      return transfer.transfer(mode, source, paths, to);
    },
    uploadInto: (folder, path, files) => {
      aimClash({ folder, path });
      return transfer.uploadInto(folder, path, files);
    },
  },
  folders,
  isMac,
  pick: (item) => {
    if (!selection.value.keys.includes(item.key)) selection.value = selectOnly(item.key);

    return { folder: item.folder, paths: selectedItems.value.map((selected) => selected.path) };
  },
  notice: (sentence) => {
    error.value = sentence;
  },
});

provide(documentsDragKey, drag);

/** The pane's empty space: the folder on screen. Only files from the computer are dropped there -
 *  items dragged out of this folder are already in it. */
const here = computed<DropTarget>(() => ({ folder: location.value.folder, path: location.value.path }));

function onPaneDragOver(event: DragEvent) {
  if (drag.source.value) return;

  drag.enter(event, here.value);
}

function onPaneDragLeave(event: DragEvent) {
  const pane = event.currentTarget as Element;
  if (event.relatedTarget instanceof Node && pane.contains(event.relatedTarget)) return;

  drag.leave(here.value);
}

function onPaneDrop(event: DragEvent) {
  if (drag.source.value) return;

  void drag.drop(event, here.value);
}

/** A drag from the computer that leaves the window without dropping gets no `dragend` here. */
function onWindowDragLeave(event: DragEvent) {
  const card = event.currentTarget as Element;
  if (event.relatedTarget instanceof Node && card.contains(event.relatedTarget)) return;

  if (drag.fromComputer.value) drag.end();
}

const paneState = computed(() => (drag.fromComputer.value ? drag.stateOf(here.value) : null));

// ---- actions ------------------------------------------------------------------------------

const menuContext = computed<MenuContext>(() => ({
  location: location.value,
  folder: current.value,
  selected: selectedItems.value,
  clipboardFull: clipboard.full,
  viewable: (item) => isViewable(item.name),
}));

const can = (action: DocumentsAction) => availability(action, menuContext.value);

function openItem(item: ExplorerItem) {
  if (item.isFolder) {
    void navigate({ folder: item.folder, path: item.path });
    return;
  }

  if (isViewable(item.name)) window.open(api.documentViewUrl(item.folder, item.path), '_blank', 'noopener');
  else download(item);
}

function download(item: ExplorerItem) {
  const link = document.createElement('a');
  link.href = api.documentUrl(item.folder, item.path);
  link.download = item.name;
  document.body.appendChild(link);
  link.click();
  link.remove();
}

function putOnClipboard(mode: 'copy' | 'cut') {
  const chosen = selectedItems.value;
  const first = chosen[0];
  if (!first) return;

  clipboard.put(mode, first.folder, chosen.map((item) => item.path));
  $q.notify?.({ message: `${mode === 'cut' ? 'Cut' : 'Copied'} ${chosen.length} item${chosen.length === 1 ? '' : 's'}`, timeout: 1500 });
}

/** Paste into the folder on screen, or into the folder right-clicked. */
async function paste(into?: DropTarget) {
  const held = clipboard.held;
  const to = into ?? here.value;
  if (!held || to.folder === null) return;

  aimClash(to);
  const outcome = await transfer.transfer(held.mode === 'cut' ? 'move' : 'copy', held.folder, held.paths, to);

  // After a cut, only what was moved leaves the clipboard.
  if (held.mode === 'cut' && (outcome.kind === 'done' || outcome.kind === 'partial')) {
    clipboard.moved(outcome.results.filter((result) => result.outcome === 'done').map((result) => result.from));
  }
}

const moveOpen = ref(false);
const moving = ref<{ folder: DocumentsFolderKey; paths: string[] } | null>(null);

function moveTo() {
  const chosen = selectedItems.value;
  const first = chosen[0];
  if (!first || atRoot.value) return;

  moving.value = { folder: first.folder, paths: chosen.map((item) => item.path) };
  moveOpen.value = true;
}

async function moveChosen(to: DropTarget) {
  const what = moving.value;
  moving.value = null;
  if (!what || to.folder === null) return;

  aimClash(to);
  await transfer.transfer('move', what.folder, what.paths, to);
}

/** Delete, after asking and naming what goes. A folder with something in it is said so. */
async function removeSelected() {
  const chosen = selectedItems.value;
  if (chosen.length === 0 || atRoot.value) return;

  const names = chosen.slice(0, 2).map((item) => item.name);
  const more = chosen.length - names.length;
  const full = chosen.filter((item) => item.isFolder && item.children > 0);
  const what = more > 0 ? `${names.join(', ')} and ${more} more` : names.join(' and ');
  const folderNote = full
    .map((item) => `${item.name} is a folder with ${item.children} item${item.children === 1 ? '' : 's'}.`)
    .join(' ');

  if (!(await ask(`Delete ${what}?${folderNote ? ` ${folderNote}` : ''} This cannot be undone.`))) return;

  const failures: string[] = [];
  for (const item of chosen) {
    try {
      await api.deleteDocument(item.folder, item.path, item.isFolder && item.children > 0);
    } catch (failure) {
      failures.push(`${item.name}: ${(failure as Error).message}`);
    }
  }

  error.value = failures.length ? `Not deleted: ${failures.join(' ')}` : '';
  selection.value = EmptySelection;
  await refresh();
}

/**
 * A GONE TEAM'S WHOLE FOLDER, deleted after the person is told how many files go. The server
 * removes it only when its marker says the platform made it, and never a live team's.
 */
async function deleteAll(folder: DocumentsFolder | null = current.value) {
  if (!folder || folder.exists) return;

  try {
    const files = (await api.listDocuments(folder.folder, '', true)).length;
    if (!(await ask(`Delete all ${files} documents of ${folder.label}? This cannot be undone.`))) return;

    await api.deleteDocument(folder.folder, '', true);
    await loadFolders();
    location.value = RootLocation;
    selection.value = EmptySelection;
    await loadEntries();
  } catch (failure) {
    error.value = (failure as Error).message;
  }
}

// Rename, inline.
const renaming = ref<string | null>(null);
const renameError = ref('');

function startRename() {
  const one = selectedItems.value.length === 1 ? selectedItems.value[0]! : null;
  if (!one || !can('rename').ok) return;

  renameError.value = '';
  renaming.value = one.key;
}

function cancelRename() {
  if (busy.value) return;

  renaming.value = null;
  renameError.value = '';
}

async function commitRename(item: ExplorerItem, name: string) {
  if (name === item.name) {
    cancelRename();
    return;
  }

  try {
    const answer = await api.renameDocuments(item.folder, [{ path: item.path, name }]);

    if (answer.kind === 'ok') {
      renaming.value = null;
      renameError.value = '';
      await loadEntries();
      const renamed = answer.results[0]?.to;
      if (renamed !== undefined) selection.value = selectOnly(itemKey(item.folder, renamed));
    } else {
      renameError.value = answer.error;
    }
  } catch (failure) {
    renameError.value = (failure as Error).message;
  }
}

/** The path an agent sees, to the system clipboard. */
async function copyPath(folder: DocumentsFolderKey, path: string) {
  if (!root.value) {
    error.value = 'The server did not say where the documents root is, so there is no path to copy.';
    return;
  }

  const copied = agentPath(root.value, folder, path);

  try {
    await copyToClipboard(copied);
    $q.notify?.({ message: 'Copied', caption: copied, timeout: 2000 });
  } catch {
    error.value = `Could not copy ${copied} to the clipboard.`;
  }
}

async function newFolder(parent: DropTarget = here.value) {
  if (parent.folder === null) return;

  const name = await askName('New folder', 'New folder');
  if (!name) return;

  try {
    await api.createFolder(parent.folder, joinPath(parent.path, name));
    await refresh();
  } catch (failure) {
    error.value = (failure as Error).message;
  }
}

const uploadInput = ref<HTMLInputElement | null>(null);

function chooseUpload() {
  uploadInput.value?.click();
}

async function onUploadChosen(event: Event) {
  const input = event.target as HTMLInputElement;
  const files = Array.from(input.files ?? []);
  input.value = '';

  if (files.length === 0 || location.value.folder === null) return;

  aimClash(here.value);
  await transfer.uploadInto(location.value.folder, location.value.path, files);
}

/** Every action, from the toolbar, the menu and the keyboard alike. */
function act(action: DocumentsAction, target?: DropTarget) {
  const one = selectedItems.value[0];

  switch (action) {
    case 'open':
      if (target) void navigate({ folder: target.folder, path: target.path });
      else if (one) openItem(one);
      break;
    case 'view':
      if (one) openItem(one);
      break;
    case 'download':
      if (one) download(one);
      break;
    case 'cut':
      putOnClipboard('cut');
      break;
    case 'copy':
      putOnClipboard('copy');
      break;
    case 'paste':
      void paste();
      break;
    case 'pasteInto':
      if (target) void paste(target);
      else if (one?.isFolder) void paste({ folder: one.folder, path: one.path });
      break;
    case 'rename':
      startRename();
      break;
    case 'moveTo':
      moveTo();
      break;
    case 'copyPath':
      if (target?.folder) void copyPath(target.folder, target.path);
      else if (one) void copyPath(one.folder, one.path);
      break;
    case 'delete':
      void removeSelected();
      break;
    case 'deleteAll':
      void deleteAll(one?.team ?? current.value);
      break;
    case 'newFolder':
      void newFolder();
      break;
    case 'newFolderInside':
      if (target) void newFolder(target);
      break;
    case 'upload':
      chooseUpload();
      break;
    case 'refresh':
      void refresh();
      break;
  }
}

// ---- the context menu ---------------------------------------------------------------------

const menu = ref<InstanceType<typeof DocumentsContextMenu> | null>(null);
const menuTarget = ref<MenuTarget>('empty');
const menuTree = ref<DropTarget | null>(null);

const menuEntries = computed(() => {
  if (menuTarget.value !== 'tree' || !menuTree.value) return menuFor(menuTarget.value, menuContext.value);

  // The tree's own menu speaks of the folder right-clicked, as if it were the one selected.
  const target = menuTree.value;
  const folder = folders.value.find((entry) => entry.folder === target.folder) ?? null;
  if (target.folder === null || !folder) return [];

  const node: ExplorerItem = target.path
    ? itemOfEntry(target.folder, { name: leafOf(target.path), path: target.path, isFolder: true, size: 0, modifiedAt: '', children: 0 })
    : itemOfFolder(folder);

  return menuFor('tree', {
    ...menuContext.value,
    location: { folder: target.folder, path: target.path },
    folder,
    selected: [node],
  });
});

async function showMenu(event: MouseEvent) {
  event.preventDefault();
  event.stopPropagation();
  await nextTick();
  menu.value?.show(event);
}

function onItemMenu(item: ExplorerItem | null, event: MouseEvent) {
  if (item && !selection.value.keys.includes(item.key)) selection.value = selectOnly(item.key);

  menuTarget.value = item ? 'item' : 'empty';
  menuTree.value = null;
  void showMenu(event);
}

function onPaneMenu(event: MouseEvent) {
  if ((event.target as Element | null)?.closest?.('[data-key]')) return;

  selection.value = clearSelection(selection.value);
  menuTarget.value = 'empty';
  menuTree.value = null;
  void showMenu(event);
}

function onTreeMenu(target: DropTarget, event: MouseEvent) {
  menuTarget.value = 'tree';
  menuTree.value = target;
  void showMenu(event);
}

function onMenuPick(action: DocumentsAction) {
  act(action, menuTarget.value === 'tree' ? (menuTree.value ?? undefined) : undefined);
}

// ---- rubber band --------------------------------------------------------------------------

const band = ref<{ x: number; y: number; left: number; top: number; width: number; height: number } | null>(null);
let bandStart: { x: number; y: number; additive: boolean; before: string[] } | null = null;

function onPanePointerDown(event: PointerEvent) {
  if (event.button !== 0 || narrow.value) return;
  if ((event.target as Element | null)?.closest?.('[data-key], button, input, th')) return;

  bandStart = { x: event.clientX, y: event.clientY, additive: event.ctrlKey || event.metaKey, before: selection.value.keys };
  (event.currentTarget as Element).setPointerCapture?.(event.pointerId);
}

function onPanePointerMove(event: PointerEvent) {
  if (!bandStart) return;

  const left = Math.min(bandStart.x, event.clientX);
  const top = Math.min(bandStart.y, event.clientY);
  const width = Math.abs(event.clientX - bandStart.x);
  const height = Math.abs(event.clientY - bandStart.y);
  if (width < 4 && height < 4 && !band.value) return;

  band.value = { x: bandStart.x, y: bandStart.y, left, top, width, height };

  const pane = event.currentTarget as Element;
  const hit = [...pane.querySelectorAll<HTMLElement>('[data-key]')]
    .filter((element) => {
      const rect = element.getBoundingClientRect();
      return rect.left < left + width && rect.right > left && rect.top < top + height && rect.bottom > top;
    })
    .map((element) => element.dataset.key!);

  const keys = bandStart.additive ? [...new Set([...bandStart.before, ...hit])] : hit;
  selection.value = { keys, anchor: keys[0] ?? null, focus: keys.at(-1) ?? null };
}

function onPanePointerUp() {
  if (!bandStart) return;

  // A click on empty space, not a band: it clears the selection.
  if (!band.value && !bandStart.additive) selection.value = clearSelection(selection.value);

  bandStart = null;
  band.value = null;
}

// ---- the keyboard -------------------------------------------------------------------------

const pane = ref<HTMLElement | null>(null);

/**
 * THE KEYBOARD MAP, for focus in the view pane. A key typed in the rename box never gets here
 * (`DocumentsName` stops it).
 */
function onPaneKeydown(event: KeyboardEvent) {
  const mod = event.ctrlKey || event.metaKey;
  const key = event.key;
  const handled = () => {
    event.preventDefault();
    event.stopPropagation();
  };

  if (event.altKey && key === 'ArrowUp') return handled(), goUp();
  if (event.altKey && key === 'ArrowLeft') return handled(), goBack();
  if (event.altKey && key === 'ArrowRight') return handled(), goForward();

  if (key === 'ArrowDown' || key === 'ArrowRight' || key === 'ArrowUp' || key === 'ArrowLeft') {
    const delta = key === 'ArrowDown' || key === 'ArrowRight' ? 1 : -1;
    selection.value = moveFocus(selection.value, order.value, delta, event.shiftKey ? 'extend' : mod ? 'focus' : 'select');
    return handled();
  }

  if (key === ' ') {
    if (selection.value.focus) selection.value = toggle(selection.value, selection.value.focus);
    return handled();
  }

  if (key === 'Enter') {
    const focused = items.value.find((item) => item.key === selection.value.focus) ?? selectedItems.value[0];
    if (focused) openItem(focused);
    return handled();
  }

  if (key === 'Backspace' && event.metaKey && isMac()) return handled(), act('delete');
  if (key === 'Backspace') return handled(), goUp();
  if (key === 'Delete') return handled(), act('delete');
  if (key === 'F2') return handled(), act('rename');
  if (key === 'F5') return handled(), act('refresh');

  if (mod && event.shiftKey && (key === 'C' || key === 'c')) return handled(), act('copyPath');
  if (mod && event.shiftKey && (key === 'N' || key === 'n')) return handled(), act('newFolder');

  if (mod && !event.shiftKey) {
    switch (key.toLowerCase()) {
      case 'a':
        selection.value = selectAll(order.value);
        return handled();
      case 'c':
        if (can('copy').ok) act('copy');
        return handled();
      case 'x':
        if (can('cut').ok) act('cut');
        return handled();
      case 'v':
        if (can('paste').ok) act('paste');
        return handled();
    }
  }
}

/**
 * ESCAPE: the first clears the selection, the second closes the dialog. During a drag the browser
 * cancels the drag and this stands down, so the dialog does not close too.
 */
function onWindowKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape') return;
  if (drag.active.value) return;
  if (renaming.value) return;

  event.preventDefault();
  if (selection.value.keys.length > 0) selection.value = clearSelection(selection.value);
  else open.value = false;
}

// ---- what the chrome says -------------------------------------------------------------------

const crumbs = computed<BreadcrumbCrumb[]>(() => crumbsOf(location.value, folders.value));

function goCrumb(crumb: BreadcrumbCrumb) {
  if (crumb.target) void navigate({ folder: crumb.target.folder, path: crumb.target.path });
}

const status = computed(() => {
  const count = items.value.length;
  const chosen = selectedItems.value;
  if (chosen.length === 0) return `${count} item${count === 1 ? '' : 's'}`;

  const files = chosen.filter((item) => !item.isFolder);
  const foldersChosen = chosen.length - files.length;
  const size = bytesText(files.reduce((sum, item) => sum + item.size, 0));

  return `${chosen.length} of ${count} selected · ${size}${foldersChosen ? ` + ${foldersChosen} folder${foldersChosen === 1 ? '' : 's'}` : ''}`;
});

const emptyText = computed(() => {
  if (atRoot.value) return 'Nothing has been written yet. A team’s documents appear here once it writes one.';
  if (readOnlyReason(current.value)) return 'Nothing here.';

  return 'Nothing here yet. Upload a document, or create a folder.';
});

const viewProps = computed(() => ({
  items: items.value,
  selected: selection.value.keys,
  focus: selection.value.focus,
  renaming: renaming.value,
  renameError: renameError.value,
  narrow: narrow.value,
  atRoot: atRoot.value,
  sort: sort.value,
}));

const viewComponent = computed(() =>
  view.value === 'tiles' ? DocumentsTiles : view.value === 'list' ? DocumentsListView : DocumentsDetails,
);

/** Toolbar buttons: the action, its icon and label, and whether it applies now. */
const toolbar: { action: DocumentsAction; icon: string; label: string }[] = [
  { action: 'newFolder', icon: 'create_new_folder', label: 'New folder' },
  { action: 'upload', icon: 'upload', label: 'Upload' },
  { action: 'cut', icon: 'content_cut', label: 'Cut' },
  { action: 'copy', icon: 'content_copy', label: 'Copy' },
  { action: 'paste', icon: 'content_paste', label: 'Paste' },
  { action: 'moveTo', icon: 'drive_file_move', label: 'Move to…' },
  { action: 'rename', icon: 'edit', label: 'Rename' },
  { action: 'delete', icon: 'delete', label: 'Delete' },
  { action: 'copyPath', icon: 'link', label: 'Copy as path' },
  { action: 'refresh', icon: 'refresh', label: 'Refresh' },
];

/** At phone width, while something is selected, this bar replaces the toolbar. */
const selectionBar: { action: DocumentsAction; icon: string; label: string }[] = [
  { action: 'copy', icon: 'content_copy', label: 'Copy' },
  { action: 'cut', icon: 'content_cut', label: 'Cut' },
  { action: 'moveTo', icon: 'drive_file_move', label: 'Move' },
  { action: 'delete', icon: 'delete', label: 'Delete' },
];

const Views: { view: DocumentsView; icon: string; label: string }[] = [
  { view: 'details', icon: 'view_list', label: 'Details' },
  { view: 'list', icon: 'view_headline', label: 'List' },
  { view: 'tiles', icon: 'grid_view', label: 'Tiles' },
];

onBeforeUnmount(() => drag.end());
</script>

<template>
  <q-dialog v-model="open" :maximized="narrow || maximised" no-esc-dismiss>
    <q-card
      class="documents-window os-dialog-lg column no-wrap"
      :class="{ 'documents-window-placed': windowed && placed !== null, 'documents-window-full': !windowed }"
      :style="frame.style.value"
      @keydown="onWindowKeydown"
      @dragleave="onWindowDragLeave"
      @dragend="drag.end()"
    >
      <!-- THE TITLE BAR IS THE HANDLE. Presses on its buttons and crumbs never move the window. -->
      <div class="documents-titlebar row items-center no-wrap q-px-sm" @pointerdown="frame.onHandlePointerDown">
        <q-btn flat dense round size="sm" icon="arrow_back" aria-label="Back" :disable="back.length === 0" @click="goBack" />
        <q-btn flat dense round size="sm" icon="arrow_forward" aria-label="Forward" :disable="forward.length === 0" @click="goForward" />
        <q-btn flat dense round size="sm" icon="arrow_upward" aria-label="Up" :disable="atRoot" @click="goUp" />
        <DocumentsBreadcrumb class="col q-mx-xs" :crumbs="crumbs" @go="goCrumb" />
        <q-btn-toggle
          v-if="!narrow"
          :model-value="view"
          flat
          dense
          size="sm"
          class="documents-views"
          :options="Views.map((choice) => ({ value: choice.view, icon: choice.icon, attrs: { 'aria-label': choice.label, 'data-view': choice.view } }))"
          @update:model-value="setView"
        />
        <q-btn
          v-if="!narrow"
          flat
          dense
          round
          size="sm"
          :icon="maximised ? 'close_fullscreen' : 'open_in_full'"
          :aria-label="maximised ? 'Restore' : 'Maximise'"
          @click="toggleMaximised"
        />
        <q-btn flat dense round size="sm" icon="close" aria-label="Close" @click="open = false" />
      </div>

      <!-- At phone width, with something selected, the selection bar replaces the toolbar. -->
      <div v-if="narrow && selectedItems.length > 0" class="documents-toolbar documents-selection-bar row items-center no-wrap q-px-sm">
        <span class="text-caption q-mr-sm">{{ selectedItems.length }} selected</span>
        <q-btn
          v-for="button in selectionBar"
          :key="button.action"
          flat
          dense
          no-caps
          size="sm"
          :icon="button.icon"
          :label="button.label"
          :data-action="button.action"
          :disable="!can(button.action).ok"
          @click="act(button.action)"
        />
        <q-btn flat dense no-caps size="sm" icon="more_horiz" label="More" data-action="more">
          <q-menu>
            <q-list dense>
              <q-item v-close-popup clickable data-action="rename" :disable="!can('rename').ok" @click="act('rename')">
                <q-item-section>Rename</q-item-section>
              </q-item>
              <q-item v-close-popup clickable data-action="copyPath" :disable="!can('copyPath').ok" @click="act('copyPath')">
                <q-item-section>Copy as path</q-item-section>
              </q-item>
            </q-list>
          </q-menu>
        </q-btn>
      </div>

      <div v-else class="documents-toolbar row items-center no-wrap q-px-sm">
        <q-btn flat dense round size="sm" :icon="narrow ? 'account_tree' : tree.open ? 'left_panel_close' : 'left_panel_open'" aria-label="Folders" data-action="tree" @click="toggleTree" />
        <q-separator vertical class="q-mx-xs" />
        <template v-for="button in toolbar" :key="button.action">
          <q-btn
            flat
            dense
            round
            size="sm"
            :icon="button.icon"
            :aria-label="button.label"
            :data-action="button.action"
            :disable="!can(button.action).ok"
            @click="act(button.action)"
          >
            <q-tooltip>{{ can(button.action).ok ? button.label : `${button.label}: ${can(button.action).reason}` }}</q-tooltip>
          </q-btn>
        </template>
        <q-space />
        <q-btn-toggle
          v-if="narrow"
          :model-value="view"
          flat
          dense
          size="sm"
          :options="Views.map((choice) => ({ value: choice.view, icon: choice.icon, attrs: { 'aria-label': choice.label, 'data-view': choice.view } }))"
          @update:model-value="setView"
        />
        <input ref="uploadInput" type="file" multiple class="hidden" data-upload-input @change="onUploadChosen" />
      </div>

      <!-- SAID WHERE IT BITES: a gone team's folder, or a superseded one - two facts, two sentences. -->
      <q-banner v-if="currentState === 'gone' || currentState === 'retired'" dense class="documents-gone q-mx-sm q-mt-xs">
        <template #avatar>
          <q-icon name="folder_off" />
        </template>
        <span v-if="currentState === 'retired'">
          A later team took this name. These documents belong to the earlier one and were kept.
          You can read, copy, move out or delete them here.
        </span>
        <span v-else>This team no longer exists. Its documents were kept. You can read, copy, move out or delete them here.</span>
        <template #action>
          <q-btn flat dense no-caps color="negative" icon="delete" label="Delete all of these documents" @click="deleteAll()" />
        </template>
      </q-banner>

      <div v-if="error" class="documents-error q-px-md q-py-xs text-negative" role="alert">
        {{ error }}
        <div v-if="done.length" class="os-text-muted text-caption">Done: {{ done.join(', ') }}</div>
      </div>

      <div class="documents-body col row no-wrap">
        <!-- THE TREE: a pane on a desk, a slide-over on a phone. -->
        <template v-if="!narrow && tree.open">
          <DocumentsTree class="documents-tree-pane" :style="{ flexBasis: `${tree.width}px` }" :folders="folders" :location="location" @open="navigate" @menu="onTreeMenu" />
          <div
            class="documents-tree-grip"
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize the folder tree"
            @pointerdown="onTreeGripDown"
            @pointermove="onTreeGripMove"
            @pointerup="onTreeGripUp"
            @pointercancel="onTreeGripUp"
          />
        </template>
        <div v-if="narrow && treeSlide" class="documents-tree-slide" data-tree-slide>
          <DocumentsTree :folders="folders" :location="location" @open="navigate" @menu="onTreeMenu" />
        </div>

        <div
          ref="pane"
          class="documents-pane col"
          :class="{ 'documents-drop-ok': paneState === 'ok', 'documents-drop-refused': paneState === 'refused' }"
          tabindex="0"
          data-pane
          @keydown="onPaneKeydown"
          @contextmenu="onPaneMenu"
          @pointerdown="onPanePointerDown"
          @pointermove="onPanePointerMove"
          @pointerup="onPanePointerUp"
          @pointercancel="onPanePointerUp"
          @dragenter="onPaneDragOver"
          @dragover="onPaneDragOver"
          @dragleave="onPaneDragLeave"
          @drop="onPaneDrop"
        >
          <q-inner-loading :showing="busy" />
          <component
            :is="viewComponent"
            v-if="items.length > 0"
            v-bind="viewProps"
            @press="press"
            @activate="openItem"
            @menu="onItemMenu"
            @rename="commitRename"
            @rename-cancel="cancelRename"
            @sort="sortBy"
          />
          <div v-else-if="!busy" class="documents-empty os-text-muted q-pa-lg text-center">{{ emptyText }}</div>
          <div
            v-if="band"
            class="documents-band"
            :style="{ left: `${band.left}px`, top: `${band.top}px`, width: `${band.width}px`, height: `${band.height}px` }"
          />
        </div>
      </div>

      <div class="documents-status row items-center q-px-md text-caption os-text-muted" aria-live="polite">
        <span data-status>{{ status }}</span>
        <q-space />
        <span>Documents live in one place for the whole tenant.</span>
      </div>

      <!-- Edges and a corner: only while it is a window. -->
      <template v-if="windowed">
        <div class="documents-resize documents-resize-e" @pointerdown="frame.onEdgePointerDown($event, 'resize-e')" />
        <div class="documents-resize documents-resize-s" @pointerdown="frame.onEdgePointerDown($event, 'resize-s')" />
        <div class="documents-resize documents-resize-se" @pointerdown="frame.onEdgePointerDown($event, 'resize-se')" />
        <div class="documents-resize documents-resize-w" @pointerdown="frame.onEdgePointerDown($event, 'resize-w')" />
        <div class="documents-resize documents-resize-n" @pointerdown="frame.onEdgePointerDown($event, 'resize-n')" />
      </template>

      <DocumentsContextMenu
        ref="menu"
        :entries="menuEntries"
        :view="view"
        :sort-column="sort.column"
        :at-root="atRoot"
        @pick="onMenuPick"
        @view="setView"
        @sort="sortBy"
      />
      <DocumentsDropHint />
    </q-card>

    <!-- The one question the delete paths ask. -->
    <q-dialog v-model="asking">
      <q-card class="os-dialog-sm">
        <q-card-section data-question>{{ question?.text }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" @click="reply(false)" />
          <q-btn flat no-caps color="negative" :label="question?.yes ?? 'Delete'" data-confirm @click="reply(true)" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <!-- A name, for New folder. -->
    <q-dialog v-model="namingOpen">
      <q-card class="os-dialog-sm">
        <q-card-section class="os-dialog-title">{{ naming?.title }}</q-card-section>
        <q-card-section class="q-pt-none">
          <q-input
            v-if="naming"
            v-model="naming.value"
            dense
            autofocus
            label="Name"
            data-name-input
            @keydown.enter.prevent="named(naming?.value ?? null)"
          />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps label="Cancel" @click="named(null)" />
          <q-btn unelevated no-caps color="primary" label="Create" data-name-confirm @click="named(naming?.value ?? null)" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <DocumentsClashDialog
      :clash="clashAsked?.clash ?? null"
      :remaining="clashAsked?.remaining ?? 0"
      :folder="clashFolder"
      @answer="clashAnswered"
    />

    <DocumentsMoveDialog
      v-model="moveOpen"
      :folders="folders"
      :count="moving?.paths.length ?? 0"
      :start="location"
      @move="moveChosen"
    />
  </q-dialog>
</template>

<style scoped>
/* AT REST: the scale's width (os-dialog-lg) and this height, centred by the dialog. Once the person
   moves or resizes it, `frame.style` places it - their size, not this component's. */
.documents-window {
  height: 70vh;
  position: relative;
  overflow: hidden;
}

.documents-window-full {
  height: 100%;
}

.documents-titlebar {
  min-height: 40px;
  background: var(--os-chrome);
  border-bottom: 1px solid var(--os-rule);
  cursor: move;
  user-select: none;
  touch-action: none;
}

.documents-window-full .documents-titlebar {
  cursor: default;
}

.documents-toolbar {
  min-height: 36px;
  border-bottom: 1px solid var(--os-rule);
  overflow-x: auto;
}

.documents-gone {
  background: var(--os-chrome);
  color: var(--os-ink-muted);
  border-radius: 3px;
}

.documents-error {
  font-size: 13px;
}

.documents-body {
  min-height: 0;
  position: relative;
}

.documents-tree-pane {
  flex: none;
  border-right: 1px solid var(--os-rule);
}

.documents-tree-grip {
  flex: none;
  width: 5px;
  margin-left: -3px;
  cursor: col-resize;
  touch-action: none;
  z-index: 1;
}

.documents-tree-slide {
  position: absolute;
  inset: 0 25% 0 0;
  z-index: 2;
  background: var(--os-surface);
  border-right: 1px solid var(--os-rule-strong);
  box-shadow: 2px 0 8px rgba(0, 0, 0, 0.2);
  overflow: auto;
}

.documents-pane {
  position: relative;
  overflow: auto;
  min-width: 0;
  outline: none;
}

.documents-pane:focus-visible {
  box-shadow: inset 0 0 0 1px var(--q-primary);
}

.documents-band {
  position: fixed;
  border: 1px solid var(--q-primary);
  background: color-mix(in srgb, var(--q-primary) 10%, transparent);
  pointer-events: none;
}

.documents-status {
  min-height: 28px;
  border-top: 1px solid var(--os-rule);
}

.documents-resize {
  position: absolute;
  z-index: 3;
  touch-action: none;
}

.documents-resize-e {
  top: 0;
  right: 0;
  bottom: 0;
  width: 6px;
  cursor: ew-resize;
}

.documents-resize-w {
  top: 0;
  left: 0;
  bottom: 0;
  width: 6px;
  cursor: ew-resize;
}

.documents-resize-s {
  left: 0;
  right: 0;
  bottom: 0;
  height: 6px;
  cursor: ns-resize;
}

.documents-resize-n {
  left: 0;
  right: 0;
  top: 0;
  height: 4px;
  cursor: ns-resize;
}

.documents-resize-se {
  right: 0;
  bottom: 0;
  width: 14px;
  height: 14px;
  cursor: nwse-resize;
}
</style>
