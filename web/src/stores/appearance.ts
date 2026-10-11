import { defineStore, acceptHMRUpdate } from 'pinia';
import { getAppearance, saveAppearance } from '../api/client';
import {
  readCached,
  resolve,
  takeOldTheme,
  writeCached,
  type AppearanceRecord,
  type Resolved,
} from '../lib/appearance';

export interface AppearanceState {
  /** The person's saved record; null until they choose (the instance default applies). */
  record: AppearanceRecord | null;
  /** `theme.default`: auto or a preset. */
  instanceDefault: string;
  /** What the Themes dialog shows while it is open: on screen, not yet saved. */
  draft: AppearanceRecord | null;
  /** The computer's light or dark setting, for Match system. Kept by App.vue. */
  systemDark: boolean;
}

/**
 * HOW THE APP LOOKS TO THE SIGNED-IN PERSON. The rules are `lib/appearance.ts`'s; this hydrates from
 * this browser's copy for the first paint, takes the server's answer when it comes, and holds the
 * dialog's draft so a change is live before it is saved. Painting is App.vue's: it watches
 * `resolved`.
 */
export const useAppearanceStore = defineStore('appearance', {
  state: (): AppearanceState => {
    const cached = readCached();
    return { record: cached.record, instanceDefault: cached.instanceDefault, draft: null, systemDark: false };
  },

  getters: {
    /** What is on screen: the draft while the dialog previews one, else the saved record. */
    resolved(state): Resolved {
      return resolve(state.draft ?? state.record, state.instanceDefault, state.systemDark);
    },
  },

  actions: {
    /**
     * The server's answer replaces this browser's copy. A person with no record who chose Light or
     * Dark here before appearances were theirs keeps that choice, saved once as their record.
     */
    async load() {
      const answer = await getAppearance();
      this.instanceDefault = answer.instanceDefault;
      this.record = answer.appearance;

      if (this.record === null) {
        const old = takeOldTheme();
        if (old) {
          this.record = { selected: old };
          await saveAppearance(this.record).catch(() => undefined);
        }
      }

      writeCached({ record: this.record, instanceDefault: this.instanceDefault });
    },

    preview(record: AppearanceRecord) {
      this.draft = record;
    },

    cancel() {
      this.draft = null;
    },

    /** Saves the whole record. On a refusal nothing changes and the error reaches the caller. */
    async save(record: AppearanceRecord) {
      await saveAppearance(record);
      this.record = record;
      this.draft = null;
      writeCached({ record, instanceDefault: this.instanceDefault });
    },
  },
});

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useAppearanceStore, import.meta.hot));
}
