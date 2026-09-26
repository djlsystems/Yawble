import { defineStore, acceptHMRUpdate } from 'pinia';
import {
  clampBoardSize,
  DefaultBoardSize,
  readBoardSize,
  writeBoardSize,
  type BoardSize,
} from '../lib/boardSize';
import { readTheme, writeTheme, type Theme } from '../lib/theme';

/** The board size, and the colour scheme beside it: both are this browser's view, both persist the
 *  same way, and neither is sent anywhere. */
export interface DisplayState extends BoardSize {
  theme: Theme;
}

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
  state: (): DisplayState => ({ ...readBoardSize(), theme: readTheme() }),

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
        '--os-card-w': `${state.width}px`,
        '--os-card-h': `${state.height}px`,
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

    /** Resets the BOARD SIZE. The theme is not a board setting and the Board display dialog's
     *  reset button must not flip somebody's console from dark to light. */
    reset() {
      this.set({ ...DefaultBoardSize });
    },

    /**
     * Chosen from the account menu, and persisted. APPLYING it is App.vue's: it watches this field
     * and hands it to Quasar's Dark plugin, which toggles `body--dark` - the class every dark token
     * in `app.scss` hangs off. Not done here because importing `quasar` into a store drags the
     * client bundle, and its `window`, into every node-environment spec that touches the store.
     */
    setTheme(theme: Theme) {
      this.theme = theme;
      writeTheme(theme);
    },
  },
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useDisplayStore, import.meta.hot));
}
