import { defineStore, acceptHMRUpdate } from 'pinia'
import { listAgentUpdates } from '../api/client'
import type { AgentUpdateState } from '../api/types'

/** How often the gate's updates are re-read while anything on screen is showing them. */
export const AgentUpdatesPollMs = 5000

/**
 * THE ONE COPY OF `GET /api/agents/updates` IN THE BROWSER, for the board: which launches are held
 * behind an Agent CLI update, by team and member. Polling is reference-counted as `stores/wip.ts`'s is.
 */
export const useAgentUpdatesStore = defineStore('agentUpdates', {
  state: () => ({
    states: [] as AgentUpdateState[],
    watchers: 0,
    timer: 0 as ReturnType<typeof setInterval> | 0,
  }),

  actions: {
    async refresh() {
      try {
        this.states = await listAgentUpdates()
      } catch {
        // The last answer stays; a person without the route's permit simply sees no mark.
      }
    },

    watch() {
      this.watchers++
      if (this.watchers > 1) return

      void this.refresh()
      this.timer = setInterval(() => void this.refresh(), AgentUpdatesPollMs)
    },

    unwatch() {
      this.watchers = Math.max(0, this.watchers - 1)
      if (this.watchers > 0 || !this.timer) return

      clearInterval(this.timer)
      this.timer = 0
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
