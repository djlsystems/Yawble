import { computed, ref, type InjectionKey, type Ref } from 'vue';
import type { DocumentsFolder, DocumentsFolderKey } from '../api/types';
import { dragCarriesFiles } from './conciergeAttachment';
import {
  dragMode,
  dropHint,
  dropVerdict,
  type DragSource,
  type DropTarget,
  type DropVerdict,
  type ExplorerItem,
} from './documentsExplorer';
import type { DocumentsTransfer } from './useDocumentsTransfer';

/**
 * DRAG AND DROP IN THE DOCUMENTS EXPLORER, held in one place.
 *
 * The dialog creates ONE of these and `provide`s it under {@link documentsDragKey}; the file pane,
 * the tree and the breadcrumb `inject` it and only bind their events to it. None of them calls the
 * API: a drop calls the same `transfer` / `uploadInto` that Paste, Move to… and Upload call, so
 * the clash question and the banner afterwards are the same code.
 *
 * WHAT IS DRAGGED is kept here in memory, because a browser hides `getData` during `dragover`. The
 * MIME entry on the `DataTransfer` is only there so a drop outside the dialog does nothing.
 */

export const DocumentsDragMime = 'application/x-yawble-documents';

export interface DocumentsDragOptions {
  transfer: DocumentsTransfer;
  folders: Ref<DocumentsFolder[]>;
  isMac: () => boolean;

  /** What a drag from this item carries: the whole selection when it is selected, else only it
   *  (selecting it first). Owned by the dialog, which owns the selection. */
  pick: (item: ExplorerItem) => { folder: DocumentsFolderKey; paths: string[] };

  /** Something worth saying that is not the outcome of a transfer: folders left out of a drop. */
  notice: (sentence: string) => void;
}

export interface DocumentsDragState {
  active: Ref<boolean>;
  source: Ref<{ folder: DocumentsFolderKey; paths: string[] } | null>;
  fromComputer: Ref<boolean>;
  over: Ref<DropTarget | null>;
  mode: Ref<'move' | 'copy'>;
  verdict: Ref<DropVerdict>;
  /** The hint's sentence and where the pointer is, for `DocumentsDropHint`. */
  hint: Ref<string>;
  point: Ref<{ x: number; y: number }>;
  start(event: DragEvent, item: ExplorerItem): void;
  enter(event: DragEvent, target: DropTarget): void;
  leave(target: DropTarget): void;
  drop(event: DragEvent, target: DropTarget): Promise<void>;
  end(): void;
  /** Whether `target` is the folder under the pointer, and how it would answer. */
  stateOf(target: DropTarget): 'ok' | 'refused' | null;
}

export const documentsDragKey: InjectionKey<DocumentsDragState> = Symbol('documentsDrag');

const sameTarget = (a: DropTarget | null, b: DropTarget) => a !== null && a.folder === b.folder && a.path === b.path;

/** The files a drag from the computer carries, as far as `dragover` lets anyone see: how many,
 *  never their names. Folders are only known at the drop. */
function filesOver(data: DataTransfer | null): { name: string; isFolder: boolean }[] | null {
  if (!data?.items) return null;

  return Array.from(data.items)
    .filter((item) => item.kind === 'file')
    .map(() => ({ name: '', isFolder: false }));
}

/** At the drop: every file, and which entries are folders (a folder cannot be uploaded). */
function filesDropped(data: DataTransfer | null): { files: File[]; folders: string[] } {
  const files: File[] = [];
  const folders: string[] = [];
  if (!data) return { files, folders };

  const items = data.items ? Array.from(data.items) : [];
  if (items.length > 0) {
    for (const item of items) {
      if (item.kind !== 'file') continue;

      const file = item.getAsFile();
      const entry = (item as DataTransferItem & { webkitGetAsEntry?: () => { isDirectory: boolean } | null })
        .webkitGetAsEntry?.();

      if (entry?.isDirectory) folders.push(file?.name ?? 'a folder');
      else if (file) files.push(file);
    }

    return { files, folders };
  }

  return { files: Array.from(data.files ?? []), folders };
}

export function useDocumentsDrag(options: DocumentsDragOptions): DocumentsDragState {
  const active = ref(false);
  const source = ref<{ folder: DocumentsFolderKey; paths: string[] } | null>(null);
  const fromComputer = ref(false);
  const computerFiles = ref<{ name: string; isFolder: boolean }[] | null>(null);
  const over = ref<DropTarget | null>(null);
  const mode = ref<'move' | 'copy'>('move');
  const point = ref({ x: 0, y: 0 });

  const dragSource = computed<DragSource | null>(() => {
    if (source.value) return { kind: 'items', folder: source.value.folder, paths: source.value.paths };
    if (fromComputer.value) return { kind: 'files', files: computerFiles.value };

    return null;
  });

  const verdictFor = (target: DropTarget): DropVerdict => {
    const from = dragSource.value;
    if (!from) return { ok: false, reason: '' };

    return dropVerdict(from, target, options.folders.value, mode.value);
  };

  const verdict = computed<DropVerdict>(() => (over.value ? verdictFor(over.value) : { ok: false, reason: '' }));

  const hint = computed(() => {
    const from = dragSource.value;
    if (!from || !over.value) return '';

    return dropHint(from, over.value, options.folders.value, mode.value, verdict.value);
  });

  function start(event: DragEvent, item: ExplorerItem) {
    const picked = options.pick(item);

    source.value = picked;
    fromComputer.value = false;
    active.value = true;

    const data = event.dataTransfer;
    if (!data) return;

    data.effectAllowed = 'copyMove';
    data.setData(DocumentsDragMime, JSON.stringify(picked));
    data.setData('text/plain', picked.paths.join('\n'));

    // A small chip under the pointer: the name, or how many.
    if (typeof document !== 'undefined' && typeof data.setDragImage === 'function') {
      const chip = document.createElement('div');
      chip.className = 'documents-drag-chip';
      chip.textContent = picked.paths.length === 1 ? item.name : `${picked.paths.length} items`;
      document.body.appendChild(chip);
      data.setDragImage(chip, 12, 12);
      setTimeout(() => chip.remove(), 0);
    }
  }

  /** `dragenter` and `dragover` on a folder: highlight it, and accept only if it would accept. */
  function enter(event: DragEvent, target: DropTarget) {
    // Something being dragged that this dialog did not start, carrying files: the computer.
    if (!source.value) {
      if (!dragCarriesFiles(event.dataTransfer)) return;

      fromComputer.value = true;
      computerFiles.value = filesOver(event.dataTransfer);
      active.value = true;
    }

    event.stopPropagation();
    mode.value = fromComputer.value ? 'copy' : dragMode(event, options.isMac());
    over.value = target;
    point.value = { x: event.clientX, y: event.clientY };

    const answer = verdictFor(target);

    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = answer.ok ? (mode.value === 'copy' ? 'copy' : 'move') : 'none';
    }

    // A REFUSED TARGET DOES NOT CALL preventDefault, so the browser accepts nothing there.
    if (answer.ok) event.preventDefault();
  }

  function leave(target: DropTarget) {
    if (sameTarget(over.value, target)) over.value = null;
  }

  async function drop(event: DragEvent, target: DropTarget) {
    event.preventDefault();
    event.stopPropagation();

    const items = source.value;
    const computer = fromComputer.value;
    const dropped = computer ? filesDropped(event.dataTransfer) : null;

    if (dropped) {
      computerFiles.value = [
        ...dropped.files.map((file) => ({ name: file.name, isFolder: false })),
        ...dropped.folders.map((name) => ({ name, isFolder: true })),
      ];
    }
    mode.value = computer ? 'copy' : dragMode(event, options.isMac());

    const dropMode = mode.value;
    const answer = verdictFor(target);
    end();

    // Said AFTER the upload, whose own outcome would otherwise replace it.
    const leftOut = () => {
      if (dropped && dropped.folders.length > 0) {
        options.notice(`Folders cannot be uploaded by dropping; drop the files inside them. Left out: ${dropped.folders.join(', ')}.`);
      }
    };

    if (!answer.ok || target.folder === null) {
      leftOut();
      return;
    }

    if (dropped) {
      if (dropped.files.length > 0) await options.transfer.uploadInto(target.folder, target.path, dropped.files);
      leftOut();

      return;
    }

    if (items) await options.transfer.transfer(dropMode, items.folder, items.paths, target);
  }

  function end() {
    active.value = false;
    source.value = null;
    fromComputer.value = false;
    computerFiles.value = null;
    over.value = null;
    mode.value = 'move';
  }

  function stateOf(target: DropTarget): 'ok' | 'refused' | null {
    if (!sameTarget(over.value, target)) return null;

    return verdict.value.ok ? 'ok' : 'refused';
  }

  return { active, source, fromComputer, over, mode, verdict, hint, point, start, enter, leave, drop, end, stateOf };
}
