import { defineStore, acceptHMRUpdate } from 'pinia'
import { useConsoleStore } from './console'
import type { TeamId } from '../api/types'

/**
 * A click on a workflow's name, on its way to the dialog that answers it.
 *
 * `WorkflowName` is on the member card, the feed, the Kanban card panel, the Teams table and the
 * Monitor; the dialogs are elsewhere. The Backlog dialog is the layout's, so a chip leaves the item
 * here and the layout opens the dialog on it. The Workflows dialog is the active team's KPI strip's,
 * so a number opens that team's board and leaves the team here; the strip opens its dialog when it
 * is the team asked for - at once if it is on screen, on mount if the board was not showing.
 */
export const useWorkflowNavStore = defineStore('workflowNav', {
  state: () => ({
    /** The Backlog dialog, the layout's, and the item it opens on (null: the list). */
    backlogOpen: false,
    backlogItem: null as number | null,
    /** The team whose Workflows dialog is asked for and not yet opened. */
    workflowsTeam: null as string | null,
    /** Counts asks, so asking for the same team twice is still a change the strip sees. */
    workflowsAsks: 0,
  }),

  actions: {
    /** Opens the Backlog dialog on one item. */
    openItem(id: number) {
      this.backlogItem = id
      this.backlogOpen = true
    },

    /** Opens the Backlog dialog on its list. */
    openBacklog() {
      this.backlogItem = null
      this.backlogOpen = true
    },

    /**
     * Shows the team's board, from a name that links to it (a team chip, a team's line). The
     * Backlog is modal over the board, so a link clicked in it leaves it.
     */
    openBoard(team: TeamId) {
      this.backlogOpen = false
      useConsoleStore().openTeam(team)
    },

    /** Shows the team's board and asks its strip for the Workflows dialog. */
    openWorkflows(team: TeamId) {
      // The Backlog is modal over the board: a number clicked in it leaves it for the dialog asked.
      this.backlogOpen = false
      useConsoleStore().openTeam(team)
      this.workflowsTeam = team
      this.workflowsAsks += 1
    },

    /** Whether the Workflows dialog was asked for this team, taken once. */
    takeWorkflows(team: string): boolean {
      if (this.workflowsTeam !== team) return false
      this.workflowsTeam = null
      return true
    },
  },
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useWorkflowNavStore, import.meta.hot))
