import { defineStore, acceptHMRUpdate } from 'pinia'

/** How long a landed card is flashed: one pulse of `.os-flash` in `app.scss`. */
export const FlashMs = 1600

/** The key a member card is flashed under: its team and id. */
export function memberKey(team: string, member: string): string {
  return `member:${team}/${member}`
}

/** The key a Kanban card is flashed under. */
export function cardKey(card: string): string {
  return `card:${card}`
}

/** Whether the person asked for less motion: then a landing scrolls at once and nothing flashes. */
export function reducedMotion(): boolean {
  return typeof globalThis.matchMedia === 'function'
    && globalThis.matchMedia('(prefers-reduced-motion: reduce)').matches
}

let flashTimer: ReturnType<typeof setTimeout> | null = null

/**
 * THE HIGHLIGHT AN ADDRESS ASKED FOR: `member=` beside `team=`, or `card=` on the Kanban
 * (`lib/consoleLinks`). `useConsoleHistory` reads the address into `member` / `card`; the view that
 * shows the card lands it once it is drawn - scrolls it into view and flashes it once - or says in
 * one line that it is not there. Either way the ask is consumed, and `consumed` moving is what tells
 * the history to drop the key from the address.
 */
export const useHighlightStore = defineStore('linkHighlight', {
  state: () => ({
    /** The member card asked for and not yet landed. `member` is the id (or the label) the address gave. */
    member: null as { team: string; member: string } | null,
    /** The Kanban card asked for and not yet landed, or ''. */
    card: '' as string,
    /** The key of the card flashing now (`memberKey`, `cardKey`), or ''. */
    flashing: '' as string,
    /** The one-line notice for an ask that named something not there, or ''. */
    notice: '' as string,
    /** Counts consumed asks, so the history drops the address's key once per landing. */
    consumed: 0,
  }),

  actions: {
    askMember(team: string, member: string) {
      if (this.member?.team === team && this.member.member === member) return
      this.member = { team, member }
      this.notice = ''
    },

    askCard(card: string) {
      if (this.card === card) return
      this.card = card
      this.notice = ''
    },

    /** The address no longer asks: forget the ask without consuming it. */
    forget() {
      this.member = null
      this.card = ''
    },

    /**
     * THE ASKED CARD IS ON SCREEN: scrolled into view and flashed once - no flash, and no smooth
     * scroll, for a person who asked for less motion.
     */
    land(key: string, element: Element | null) {
      const still = reducedMotion()

      element?.scrollIntoView?.({ block: 'center', inline: 'nearest', behavior: still ? 'auto' : 'smooth' })

      if (!still) {
        this.flashing = key
        if (flashTimer !== null) clearTimeout(flashTimer)
        flashTimer = setTimeout(() => {
          if (this.flashing === key) this.flashing = ''
          flashTimer = null
        }, FlashMs)
      }

      this.consume()
    },

    /** The asked card is not there: the notice, and no flash. */
    miss(notice: string) {
      this.notice = notice
      this.consume()
    },

    dismissNotice() {
      this.notice = ''
    },

    consume() {
      this.forget()
      this.consumed += 1
    },
  },
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useHighlightStore, import.meta.hot))
