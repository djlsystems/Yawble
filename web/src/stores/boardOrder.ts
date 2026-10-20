import { acceptHMRUpdate, defineStore } from 'pinia';
import {
  arrange,
  movedBy,
  placed,
  readBoardOrder,
  storedBoardOrderKeys,
  writeBoardOrder,
} from '../lib/boardOrder';

/**
 * Where this viewer put each team's cards.
 *
 * A preference about a BROWSER, like the board size (`stores/display`): it never leaves
 * `localStorage` and is never sent anywhere. The rules live in `lib/boardOrder` and this holds none
 * of them - it hydrates a team's order when a board first asks, delegates every move, and persists.
 */
export const useBoardOrderStore = defineStore('boardOrder', {
  state: () => ({
    /** Each team's arrangement, once read. A team not here has not been asked for. */
    orders: {} as Record<string, string[]>,
  }),

  actions: {
    /** Reads a team's stored arrangement once; a board calls this for the team it shows. */
    load(team: string) {
      if (!(team in this.orders)) this.orders[team] = readBoardOrder(team);
    },

    /** The members in this viewer's order for the team (see `arrange`). */
    arranged<T extends { id: string }>(team: string, members: readonly T[]): T[] {
      return arrange(members, this.orders[team] ?? []);
    },

    /** Puts `moved` before `before` (null: last) and writes the arrangement once. */
    place(team: string, roster: readonly string[], moved: string, before: string | null) {
      const next = placed(roster, this.orders[team] ?? [], moved, before);
      this.orders[team] = next;
      writeBoardOrder(team, next);
    },

    /** Moves `moved` by `delta` places and writes the arrangement once. */
    move(team: string, roster: readonly string[], moved: string, delta: number) {
      const next = movedBy(roster, this.orders[team] ?? [], moved, delta);
      this.orders[team] = next;
      writeBoardOrder(team, next);
    },

    /** Forgets every team's arrangement in this browser: each board goes back to its roster's order. */
    resetAll() {
      for (const key of storedBoardOrderKeys()) {
        try {
          localStorage.removeItem(key);
        } catch {
          // Storage refused: the in-memory orders still reset below.
        }
      }
      for (const team of Object.keys(this.orders)) this.orders[team] = [];
    },
  },
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useBoardOrderStore, import.meta.hot));
}
