import { defineStore, acceptHMRUpdate } from 'pinia'
import type { DecisionAsk } from '../lib/conciergeDecision'

/**
 * A "Discuss with Concierge" click, on its way to the Concierge panel.
 *
 * The button is on a member card, a Kanban card and the opened card; the terminal is the panel's.
 * The click only leaves the decision here, and the panel opens itself, waits until its session is
 * ready, and types the request - or says, in `notice`, why it typed nothing. One request at a time:
 * a second click before the first is typed replaces it, so one request is typed, never two.
 */
export const useConciergeAskStore = defineStore('conciergeAsk', {
  state: () => ({
    asked: null as DecisionAsk | null,
    /** Counts clicks, so asking about the same decision twice is still a change the panel sees. */
    clicks: 0,
    /** One sentence the panel shows when it typed nothing, or null. */
    notice: null as string | null,
  }),

  actions: {
    ask(decision: DecisionAsk) {
      this.asked = decision
      this.notice = null
      this.clicks += 1
    },

    /** The decision to type, taken once: whatever happens next, it is not typed again. */
    take(): DecisionAsk | null {
      const decision = this.asked
      this.asked = null
      return decision
    },

    /** Nothing was typed, and why. */
    refuse(sentence: string) {
      this.asked = null
      this.notice = sentence
    },
  },
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useConciergeAskStore, import.meta.hot))
