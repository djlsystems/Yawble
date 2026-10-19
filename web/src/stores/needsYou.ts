import { defineStore, acceptHMRUpdate } from 'pinia'
import { getNeedsYou } from '../api/client'
import type { NeedsYouItem, NeedsYouPart } from '../api/types'
import { wireInstant } from '../lib/localTime'

/** How often the stored part is read again while the header shows. */
export const StoredEveryMs = 30_000

/** One part's last answer, and whether a read of it is in flight or owed. */
interface PartState {
  items: NeedsYouItem[]
  loaded: boolean
  error: string
  reading: boolean
  /** A read was asked for while one was in flight: it runs once more when that one lands. */
  owed: boolean
}

const part = (): PartState => ({ items: [], loaded: false, error: '', reading: false, owed: false })

/**
 * THE ONE COPY OF WHAT IS WAITING ON A PERSON, as `GET /api/needs-you` answers it, in its two parts.
 *
 * `live` (decisions, blocked, failed, a solution's missing input) is cheap, and is read again on
 * every `containerChanged` and `kanbanChanged` push and after a window here uploads a solution's
 * input. `stored` (merge-ready, stranded, removals) is read from the stores and the landed cache,
 * and is read again on a 30 s timer and when the panel opens. There is no push for either: the hub
 * gains none. A burst of pushes costs at most one read in flight and one after it.
 *
 * The count is the route's: the rows of both parts, nothing added, nothing folded.
 */
export const useNeedsYouStore = defineStore('needsYou', {
  state: () => ({
    live: part(),
    stored: part(),
  }),

  getters: {
    /** Both parts, newest first. */
    items(state): NeedsYouItem[] {
      return [...state.live.items, ...state.stored.items].sort((a, b) => wireInstant(b.since) - wireInstant(a.since))
    },

    /** The count, or null before either part has been read. */
    count(state): number | null {
      if (!state.live.loaded && !state.stored.loaded) return null
      return state.live.items.length + state.stored.items.length
    },

    /** What the last failed read said, if either part's did. */
    error(state): string {
      return state.live.error || state.stored.error
    },
  },

  actions: {
    async read(which: NeedsYouPart) {
      const held = this[which]
      if (held.reading) {
        held.owed = true
        return
      }

      held.reading = true
      try {
        do {
          held.owed = false
          try {
            const view = await getNeedsYou(which)
            held.items = view.items
            held.loaded = true
            held.error = ''
          } catch (cause) {
            // The last answer stays; the header says the read failed.
            held.error = cause instanceof Error ? cause.message : String(cause)
          }
        } while (held.owed)
      } finally {
        held.reading = false
      }
    },

    loadLive() {
      return this.read('live')
    },

    loadStored() {
      return this.read('stored')
    },

    async load() {
      await Promise.all([this.loadLive(), this.loadStored()])
    },
  },
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useNeedsYouStore, import.meta.hot))
