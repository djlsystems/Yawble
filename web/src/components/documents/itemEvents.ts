import { inject } from 'vue';
import type { DocumentsSort, ExplorerItem } from '../../lib/documentsExplorer';
import { documentsDragKey } from '../../lib/useDocumentsDrag';

/**
 * WHAT EVERY VIEW OF THE FILE PANE SHARES: its props, its events, and how a row or tile is wired
 * for the pointer, the context menu, a long-press and drag and drop. Details, List and Tiles differ
 * only in layout, so the wiring is written once here and bound with `v-bind`/`v-on`.
 */
export interface DocumentsViewProps {
  items: ExplorerItem[];
  selected: string[];
  focus: string | null;
  renaming: string | null;
  renameError: string;
  /** Phone width: no drag, a ⋮ button on each row. */
  phone: boolean;
  atRoot: boolean;
  sort: DocumentsSort;
}

export interface DocumentsViewEmits {
  (event: 'press', item: ExplorerItem, pointer: MouseEvent): void;
  (event: 'activate', item: ExplorerItem): void;
  (event: 'menu', item: ExplorerItem | null, pointer: MouseEvent): void;
  (event: 'rename', item: ExplorerItem, name: string): void;
  (event: 'renameCancel'): void;
  (event: 'sort', column: DocumentsSort['column']): void;
}

/** How long a finger rests on a row before its menu opens. */
export const LongPressMs = 500;

export function useItemEvents(props: DocumentsViewProps, emit: DocumentsViewEmits) {
  const drag = inject(documentsDragKey, null);
  let pressTimer: ReturnType<typeof setTimeout> | null = null;

  const cancelPress = () => {
    if (pressTimer) clearTimeout(pressTimer);
    pressTimer = null;
  };

  const targetOf = (item: ExplorerItem) => ({ folder: item.folder, path: item.path });

  /** Attributes of a row or tile: what it is, whether it is selected, and whether it drags. */
  function attrs(item: ExplorerItem) {
    const state = item.isFolder && drag ? drag.stateOf(targetOf(item)) : null;

    return {
      'data-key': item.key,
      'data-folder': item.isFolder ? ('true' as const) : undefined,
      'aria-selected': props.selected.includes(item.key),
      // NO DRAG AT PHONE WIDTH: a long-press opens the menu there, and HTML drag on touch is
      // unreliable. A team's folder at the root is never dragged either: it is not moved.
      draggable: !props.phone && !item.team && props.renaming !== item.key ? ('true' as const) : undefined,
      class: {
        'documents-item-selected': props.selected.includes(item.key),
        'documents-item-focus': props.focus === item.key,
        'documents-drop-ok': state === 'ok',
        'documents-drop-refused': state === 'refused',
      },
    };
  }

  /** Listeners of a row or tile. Folders are drop targets; files are not. */
  function on(item: ExplorerItem) {
    const listeners: Record<string, (event: Event) => void> = {
      click: (event) => emit('press', item, event as MouseEvent),
      dblclick: () => emit('activate', item),
      contextmenu: (event) => {
        // The row becomes the selection first, then the pane's menu opens on it.
        emit('menu', item, event as MouseEvent);
      },
      pointerdown: (event) => {
        const pointer = event as PointerEvent;
        if (pointer.pointerType !== 'touch' && !props.phone) return;

        cancelPress();
        pressTimer = setTimeout(() => {
          pressTimer = null;
          emit('menu', item, pointer);
        }, LongPressMs);
      },
      pointerup: cancelPress,
      pointercancel: cancelPress,
      pointermove: cancelPress,
    };

    if (drag && !props.phone && !item.team) {
      listeners.dragstart = (event) => drag.start(event as DragEvent, item);
      listeners.dragend = () => drag.end();
    }

    if (drag && item.isFolder) {
      listeners.dragenter = (event) => drag.enter(event as DragEvent, targetOf(item));
      listeners.dragover = (event) => drag.enter(event as DragEvent, targetOf(item));
      listeners.dragleave = () => drag.leave(targetOf(item));
      listeners.drop = (event) => void drag.drop(event as DragEvent, targetOf(item));
    }

    return listeners;
  }

  return { attrs, on };
}
