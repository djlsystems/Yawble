import { defineStore, acceptHMRUpdate } from 'pinia';
import {
  clampBoardSize,
  DefaultBoardSize,
  readBoardSize,
  writeBoardSize,
  type BoardSize,
} from '../lib/boardSize';

/** The board size: this browser's view, persisted here and sent nowhere. The theme is the person's
 *  and lives in `stores/appearance`. */
export type DisplayState = BoardSize;

/**
 * How this viewer wants the board's cards sized.
 *
 * A preference about a BROWSER rather than a fact about the server, so it never leaves
 * `localStorage` and is never sent anywhere. Two people looking at the same team see whatever each
 * of them chose, which is correct: one is on a phone.
 *
 * The rules live in `lib/boardSize` and this holds none of them - it hydrates, delegates the
 * clamping, and persists. A store that re-implemented the bounds would be a second answer to
 * "how wide may a card be", and the two would drift the first time one moved.
 */
export const useDisplayStore = defineStore('display', {
  state: (): DisplayState => ({ ...readBoardSize() }),

  getters: {
    /**
     * Handed to the board container as an inline `style`, so every card reads its size from one
     * place through the cascade rather than each binding its own width.
     *
     * That indirection is what makes a live preview free: changing the variable on the container
     * re-lays every card out, with no per-card reactivity to wire up and nothing to keep in step.
     */
    cssVars(state): Record<string, string> {
      return {
        // In rem, so a card grows with the person's text size (Themes) and keeps as many words a
        // line: at 100% it is exactly the pixels chosen here.
        '--os-card-w': `${state.width / 16}rem`,
        '--os-card-h': `${state.height / 16}rem`,
      };
    },
  },

  actions: {
    /**
     * Partial on purpose: the dialog edits one field at a time, and sending the other back
     * unchanged is how an untouched box quietly overwrites something.
     */
    set(partial: Partial<BoardSize>) {
      const next = clampBoardSize({
        width: this.width,
        height: this.height,
        feedDepth: this.feedDepth,
        feedWindow: this.feedWindow,
        ...partial,
      });

      this.width = next.width;
      this.height = next.height;
      this.feedDepth = next.feedDepth;
      this.feedWindow = next.feedWindow;

      writeBoardSize(next);
    },

    /**
     * Applies a size to state - and so to `--os-card-w`/`--os-card-h` on the board container, which
     * reflows every card - WITHOUT writing storage. A drag calls this as the pointer moves and `set`
     * once when it is let go, so `harness.boardSize` is written once per drag, not once per pixel.
     */
    preview(partial: Partial<BoardSize>) {
      const next = clampBoardSize({
        width: this.width,
        height: this.height,
        feedDepth: this.feedDepth,
        feedWindow: this.feedWindow,
        ...partial,
      });

      this.width = next.width;
      this.height = next.height;
    },

    /** Puts the cards back to the default width and height, leaving the feed settings as they are. */
    resetCardSize() {
      this.set({ width: DefaultBoardSize.width, height: DefaultBoardSize.height });
    },

    /** Resets the board size. */
    reset() {
      this.set({ ...DefaultBoardSize });
    },
  },
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useDisplayStore, import.meta.hot));
}
