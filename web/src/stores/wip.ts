import { defineStore, acceptHMRUpdate } from 'pinia'
import { getWip } from '../api/client'
import type { WipHold, WipView } from '../api/types'

/** How often the ledger is re-read while anything on screen is showing it. */
export const WipPollMs = 5000

/**
 * THE ONE COPY OF `GET /api/wip` IN THE BROWSER.
 *
 * The header strip, the kanban's In Progress header, the Teams table and the Admission tab all read
 * this; none of them fetches. Polling is reference-counted: each reader calls `watch()` when it
 * mounts and `unwatch()` when it goes, and the timer runs only while somebody is watching.
 */
export const useWipStore = defineStore('wip', {
  state: () => ({
    view: null as WipView | null,
    error: '' as string,
    watchers: 0,
    timer: 0 as ReturnType<typeof setInterval> | 0,
  }),

  getters: {
    /** The limit, or null before the first read. 0 is no limit. */
    max: (state): number | null => state.view?.max ?? null,

    runningCount: (state): number => state.view?.running.length ?? 0,
    waitingCount: (state): number => state.view?.waiting.length ?? 0,
  },

  actions: {
    async refresh() {
      try {
        this.view = await getWip()
        this.error = ''
      } catch (cause) {
        // The last answer stays. The ledger is advisory on every surface that reads it.
        this.error = cause instanceof Error ? cause.message : String(cause)
      }
    },

    watch() {
      this.watchers++
      if (this.watchers > 1) return

      void this.refresh()
      this.timer = setInterval(() => void this.refresh(), WipPollMs)
    },

    unwatch() {
      this.watchers = Math.max(0, this.watchers - 1)
      if (this.watchers > 0 || !this.timer) return

      clearInterval(this.timer)
      this.timer = 0
    },

    runningFor(team: string): WipHold[] {
      return this.view?.running.filter((hold) => hold.team === team) ?? []
    },

    waitingFor(team: string): WipHold[] {
      return this.view?.waiting.filter((hold) => hold.team === team) ?? []
    },

    isWaiting(team: string, member: string | null | undefined): boolean {
      if (!member) return false

      return this.view?.waiting.some((hold) => hold.team === team && hold.member === member) ?? false
    },
  },
})

/** `2 running, 1 waiting` for one team, or '' when it has nothing on the ledger. */
export function wipLine(running: number, waiting: number): string {
  if (running === 0 && waiting === 0) return ''

  return `${running} running, ${waiting} waiting`
}

/**
 * The In Progress header: `3 / 4 running`, or `3 running` with no limit.
 */
export function inProgressHeader(running: number, max: number | null): string {
  if (max === null || max === 0) return `${running} running`

  return `${running} / ${max} running`
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useWipStore, import.meta.hot))
}
