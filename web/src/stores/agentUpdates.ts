import { defineStore, acceptHMRUpdate } from 'pinia'
import { listAgentUpdates } from '../api/client'
import type { AgentUpdateState } from '../api/types'

/** How often the gate's updates are re-read while one is in the gate (waiting or updating). */
export const AgentUpdatesPollMs = 5000

/** How often they are re-read while none is: an update begins rarely, and a held launch says so at once. */
export const AgentUpdatesIdlePollMs = 60_000

/**
 * THE ONE COPY OF `GET /api/agents/updates` IN THE BROWSER, for the board: which launches are held
 * behind an Agent CLI update, by team and member. Reading is reference-counted as `stores/wip.ts`'s
 * is, and paced by what the last answer said: every few seconds only while an update is in the
 * gate, once a minute otherwise, and at once when a held member's snapshot is pushed (`onHeld`),
 * since a launch held behind an update is the one thing this list explains.
 */
export const useAgentUpdatesStore = defineStore('agentUpdates', {
  state: () => ({
    states: [] as AgentUpdateState[],
    watchers: 0,
    timer: 0 as ReturnType<typeof setTimeout> | 0,
    reading: false,
  }),

  getters: {
    /** Whether any update is waiting or running now. */
    inGate: (state): boolean => state.states.some((s) => s.phase === 'waiting' || s.phase === 'updating'),
  },

  actions: {
    /** One read in flight at a time; a read asked during one is dropped, the next timer or hold asks again. */
    async refresh() {
      if (this.reading) return

      this.reading = true
      try {
        this.states = await listAgentUpdates()
      } catch {
        // The last answer stays; a person without the route's permit simply sees no mark.
      } finally {
        this.reading = false
      }
    },

    watch() {
      this.watchers++
      if (this.watchers > 1) return

      void this.refresh().then(() => this.schedule())
    },

    unwatch() {
      this.watchers = Math.max(0, this.watchers - 1)
      if (this.watchers > 0 || !this.timer) return

      clearTimeout(this.timer)
      this.timer = 0
    },

    /** A member's snapshot said it is held: the list that explains a hold is read now. */
    onHeld() {
      if (this.watchers === 0) return

      void this.refresh()
    },

    schedule() {
      if (this.timer) clearTimeout(this.timer)
      this.timer = 0
      if (this.watchers === 0) return

      this.timer = setTimeout(async () => {
        this.timer = 0
        await this.refresh()
        this.schedule()
      }, this.inGate ? AgentUpdatesPollMs : AgentUpdatesIdlePollMs)
    },

    /** The command whose update holds this member's launch, or null. Held, never failed. */
    heldBy(team: string, member: string | null | undefined): string | null {
      if (!member) return null

      const holding = this.states.find(
        (state) =>
          (state.phase === 'waiting' || state.phase === 'updating')
          && state.held.some((hold) => hold.team === team && hold.member === member),
      )

      return holding?.command ?? null
    },
  },
})

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useAgentUpdatesStore, import.meta.hot))
}
