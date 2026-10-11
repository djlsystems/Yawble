import { defineStore, acceptHMRUpdate } from 'pinia'
import { getCapacity } from '../api/client'
import type { CapacitySample } from '../api/types'

/** About how much history the monitor keeps: the Host's own window. */
export const HistorySeconds = 600

/**
 * THE ONE COPY OF THE INSTANCE'S MEASURED CAPACITY IN THE BROWSER.
 *
 * Read ONCE from `GET /api/capacity` (HumansOnly) when the monitor mounts and again after the live
 * connection comes back; every sample in between arrives as the hub's `capacityChanged` and is
 * appended here. Nothing polls: when the pushes stop, the latest sample's age is what says so.
 */
export const useCapacityStore = defineStore('capacity', {
  state: () => ({
    intervalSeconds: 5,
    latest: null as CapacitySample | null,
    history: [] as CapacitySample[],
    loaded: false,
    error: '' as string,
  }),

  actions: {
    async load() {
      try {
        const view = await getCapacity()
        this.intervalSeconds = view.intervalSeconds
        this.history = view.history
        this.latest = view.latest ?? view.history.at(-1) ?? null
        this.loaded = true
        this.error = ''
      } catch (cause) {
        // The last figures stay; their age says how far behind they are.
        this.error = cause instanceof Error ? cause.message : String(cause)
      }
    },

    /** One pushed sample. An older one than the latest (a push racing the read) is dropped. */
    apply(sample: CapacitySample) {
      if (this.latest && Date.parse(sample.at) <= Date.parse(this.latest.at)) return

      this.latest = sample
      const from = Date.parse(sample.at) - HistorySeconds * 1000
      this.history = [...this.history.filter((earlier) => Date.parse(earlier.at) >= from), sample]
      this.loaded = true
    },

    /** Whether this member is queued for the heavy lease in the latest sample. */
    isQueuedForHeavy(team: string, member: string | null | undefined): boolean {
      if (!member) return false

      return this.latest?.heavyLease?.queued.some((party) => party.team === team && party.member === member) ?? false
    },
  },
})

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useCapacityStore, import.meta.hot))
}
