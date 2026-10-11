import { defineStore, acceptHMRUpdate } from 'pinia';
import {
  clampTerminalDisplay,
  clampTerminalDisplayToViewport,
  computeInitialWindowGeometry,
  DefaultTerminalDisplay,
  readTerminalDisplay,
  TerminalDisplayStorageKey,
  writeTerminalDisplay,
  type TerminalDisplay,
} from '../lib/terminalDisplay';
import { isPlaced, readStored } from '../lib/windowGeometry';

/**
 * How this viewer wants the terminal displayed.
 *
 * A preference about a BROWSER rather than a fact about the server, so it never leaves
 * `localStorage` and is never sent anywhere. Two people looking at the same team see whatever each
 * of them chose.
 *
 * The rules live in `lib/terminalDisplay` and this holds none of them - it hydrates, delegates the
 * clamping, and persists. A store that re-implemented the bounds would be a second answer to
 * "what font size is valid", and the two would drift the first time one moved.
 */
export const useTerminalDisplayStore = defineStore('terminalDisplay', {
  state: (): TerminalDisplay => readTerminalDisplay(),

  actions: {
    /**
     * Partial on purpose: the dialog edits one field at a time, and sending the other back
     * unchanged is how an untouched box quietly overwrites something.
     */
    set(partial: Partial<TerminalDisplay>) {
      const next = clampTerminalDisplay({
        fontFamily: this.fontFamily,
        fontSize: this.fontSize,
        maximised: this.maximised,
        width: this.width,
        height: this.height,
        left: this.left,
        top: this.top,
        ...partial,
      });

      this.fontFamily = next.fontFamily;
      this.fontSize = next.fontSize;
      this.maximised = next.maximised;
      this.width = next.width;
      this.height = next.height;
      this.left = next.left;
      this.top = next.top;

      writeTerminalDisplay(next);
    },

    /**
     * Clamp current state to fit within viewport dimensions.
     */
    clampToViewport(viewportWidth: number, viewportHeight: number) {
      const next = clampTerminalDisplayToViewport(
        {
          fontFamily: this.fontFamily,
          fontSize: this.fontSize,
          maximised: this.maximised,
          width: this.width,
          height: this.height,
          left: this.left,
          top: this.top,
        },
        viewportWidth,
        viewportHeight,
      );

      this.fontFamily = next.fontFamily;
      this.fontSize = next.fontSize;
      this.maximised = next.maximised;
      this.width = next.width;
      this.height = next.height;
      this.left = next.left;
      this.top = next.top;

      writeTerminalDisplay(next);
    },

    /**
     * Initialize windowed position/size from viewport if not yet placed.
     * Unplaced only when storage has no key or missing left/top as own properties.
     * Placed includes left:0 / top:0 (user may have dragged window to top-left).
     */
    initializeGeometryIfNeeded(viewportWidth: number, viewportHeight: number) {
      // Read from RAW storage for own `left`/`top` - never from the clamped state, which always has
      // both. Unreadable storage reads as nothing stored, so it is unplaced too.
      if (!isPlaced(readStored(TerminalDisplayStorageKey))) {
        this.set(computeInitialWindowGeometry(viewportWidth, viewportHeight));
        return;
      }

      // Placed: has both left and top keys. Clamp to viewport if it overflowed.
      // This keeps user-placed windows even if viewport changed, instead of jumping to default.
      const current = {
        fontFamily: this.fontFamily,
        fontSize: this.fontSize,
        maximised: this.maximised,
        width: this.width,
        height: this.height,
        left: this.left,
        top: this.top,
      };

      // Only clamp if needed, don't reinitialize
      const clamped = clampTerminalDisplayToViewport(current, viewportWidth, viewportHeight);
      if (
        clamped.left !== current.left ||
        clamped.top !== current.top ||
        clamped.width !== current.width ||
        clamped.height !== current.height
      ) {
        this.set(clamped);
      }
    },

    reset() {
      this.set({ ...DefaultTerminalDisplay });
    },
  },
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useTerminalDisplayStore, import.meta.hot));
}
