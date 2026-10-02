import { defineStore, acceptHMRUpdate } from 'pinia';
import type { DocumentsFolderKey } from '../api/types';

/**
 * THE DOCUMENTS EXPLORER'S OWN CLIPBOARD: what was copied or cut, from which folder.
 *
 * In-app only. It survives closing and reopening the dialog, and is never written to storage or
 * the system clipboard - those hold text, and a path copied there is what Copy as path is for.
 * After a cut is pasted, the items that were moved leave it; the ones that were not stay, so a
 * second Paste can try them again.
 */
export interface DocumentsClipboard {
  mode: 'copy' | 'cut';
  folder: DocumentsFolderKey;
  paths: string[];
}

export const useDocumentsClipboardStore = defineStore('documentsClipboard', {
  state: () => ({ held: null as DocumentsClipboard | null }),

  getters: {
    full: (state) => state.held !== null && state.held.paths.length > 0,
  },

  actions: {
    put(mode: 'copy' | 'cut', folder: DocumentsFolderKey, paths: string[]) {
      this.held = paths.length > 0 ? { mode, folder, paths: [...paths] } : null;
    },

    /** A cut whose items were moved: those leave the clipboard, and it empties when none are left. */
    moved(paths: string[]) {
      if (!this.held || this.held.mode !== 'cut') return;

      const left = this.held.paths.filter((path) => !paths.includes(path));
      this.held = left.length > 0 ? { ...this.held, paths: left } : null;
    },

    clear() {
      this.held = null;
    },
  },
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useDocumentsClipboardStore, import.meta.hot));
}
