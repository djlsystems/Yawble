import { defineStore, acceptHMRUpdate } from 'pinia'
import {
  commentKanbanCard,
  editKanbanCard,
  getKanbanBoard,
  getKanbanCard,
  moveKanbanCard,
  type KanbanBoard,
  type KanbanCard,
  type KanbanCardDetail,
  type KanbanEditRequest,
  type KanbanFilters,
  type KanbanLane,
} from '../api/kanban'
import { Unauthorized } from '../api/client'
import {
  cardsInLane,
  cardsMatchingText,
  defaultKanbanFilters,
  effectiveFilters,
  lanesToRender,
  membersOf,
  swimlaneTeams,
  sameFilters,
  teamsOf,
  type KanbanView,
  type SwimlaneTeam,
  type KanbanFilterPatch,
  withFilters,
} from '../lib/kanban'
import { useConsoleStore } from './console'
import { useWipStore } from './wip'
import type { TeamId } from '../api/types'

/** A write names a card the board no longer holds - so there is no team to address it under. */
const UnknownCard = 'That card is not on the board any more. Refresh and try again.'

export type { KanbanView } from '../lib/kanban'

/** Where the chosen layout is kept. Per browser: it is how this person likes to read the board. */
export const KanbanViewKey = 'harness.kanban.view'

function storedView(): KanbanView {
  try {
    return globalThis.localStorage?.getItem(KanbanViewKey) === 'swimlanes' ? 'swimlanes' : 'board'
  } catch {
    return 'board'
  }
}

/**
 * The Kanban tab's state: which view the console is showing, what the board is filtered to, and
 * the last board the server answered with.
 *
 * A SEPARATE STORE FROM `console`, deliberately. The board is a module that plugs into the same
 * event bus the console already listens to - remove this file and the four components beside it
 * and the console is untouched. The console store keeps teams, tabs and the message feed; nothing
 * here writes to it.
 *
 * THE BOARD IS A DERIVED READ MODEL AND THIS HOLDS NO COPY OF IT. Every write goes to the API,
 * which appends a `kanban.card.*` message to the log, and the board is then refetched. There is no
 * local mutation of `board.cards` anywhere in this file: a second source of truth for team work is
 * the one thing the design brief forbids outright.
 */

export const useKanbanStore = defineStore('kanban', {
  state: () => ({
    /** What the board is narrowed to. Empty means the whole tenant. */
    filters: {} as KanbanFilters,

    /**
     * The free-text box on the bar. VIEW STATE, AND DELIBERATELY NOT A `KanbanFilters` FIELD.
     *
     * It never reaches the server: it narrows what is RENDERED, client-side and instantly, off the
     * board the server already answered with. Kept in `filters` it would be one refactor away from
     * being serialised onto the query string by `kanbanQuery`, to a route with no parameter to
     * receive it - so the value that must not be sent is held somewhere that cannot send it.
     *
     * Counted by `activeFilterCount` and cleared by `clearFilters` and `showKanban` all the same:
     * a narrowing with no affordance to undo it is a board with missing cards and nothing on screen
     * to say why.
     */
    text: '' as string,

    /**
     * Which layout draws the board. Changing it refetches only when it changes the filters in
     * effect - Swimlanes ignores the team filter, so Board-with-a-team to Swimlanes does.
     */
    view: storedView() as KanbanView,

    /**
     * The team row a team's Kanban button asked to see while Swimlanes is on, or ''. The board
     * scrolls it into view once it is drawn and clears this: every row stays, none is hidden.
     */
    revealTeam: '' as string,

    board: null as KanbanBoard | null,

    loading: false,
    error: '' as string,

    /** A write is in flight. One at a time - these are appends, and a person clicked once. */
    busy: false,

    /** The card whose side panel is open, or ''. */
    selectedId: '' as string,
    detail: null as KanbanCardDetail | null,
    detailLoading: false,
    detailError: '' as string,

    /**
     * The fetch this store is waiting for. A board fetch is fired by clicks, by the filter bar and
     * by every hub push, so several are in flight routinely - and the slow one landing last would
     * otherwise overwrite the fast one that answered the newer filter.
     */
    fetchSeq: 0,
  }),

  getters: {
    /** The console is showing the board rather than the container cards or the teams table. */
    active: (): boolean => useConsoleStore().view === 'kanban',

    hasBoard: (state): boolean => state.board !== null,

    /**
     * THE FILTERS THAT ARE ACTUALLY APPLIED: what `load` sends and what Clear counts. In Swimlanes
     * the team is kept in `filters` but not applied - see `effectiveFilters` in `lib/kanban`.
     */
    effectiveFilters: (state): KanbanFilters => effectiveFilters(state.filters, state.view),

    /**
     * THE BOARD AS IT IS DRAWN: what was fetched, narrowed by the search box.
     *
     * A DERIVED VIEW AND NEVER A WRITE. `board` keeps every card the server answered with, and
     * this is the one place the typed text is applied - so `lanes`, `cardCount` and `laneCards`
     * all narrow together and no component can disagree with the header count beside it. Clearing
     * the box needs no refetch because nothing was thrown away.
     *
     * The LANES are recomputed from the narrowed cards rather than kept whole: an `Unplaced` lane
     * exists only because some card named a lane the template has not got, and once that card is
     * narrowed away the lane is an empty column explaining nothing.
     */
    rendered(state): Pick<KanbanBoard, 'lanes' | 'cards'> | null {
      if (!state.board) return null

      return { lanes: state.board.lanes, cards: cardsMatchingText(state.board.cards, state.text) }
    },

    /** The template's lanes, plus a home for any card naming a lane the template does not have. */
    lanes(): KanbanLane[] {
      const rendered = this.rendered

      return rendered ? lanesToRender(rendered) : []
    },

    /** How many cards are ON SCREEN, which is what the header beside them claims to count. */
    cardCount(): number {
      return this.rendered?.cards.length ?? 0
    },

    /**
     * The swimlane rows: every team on the console's list, empty ones included. NEVER NARROWED BY
     * THE TEAM FILTER, which Swimlanes does not apply. Read off the RENDERED cards so a team
     * only a card names still gets its row.
     */
    swimlanes(): SwimlaneTeam[] {
      const teams = useConsoleStore().teams.map((team) => ({ id: team.id as string, name: team.name }))

      return swimlaneTeams(teams, this.rendered?.cards ?? [])
    },

    /** The member filter's options, taken from the cards actually on the board. */
    memberOptions(state): string[] {
      return state.board ? membersOf(state.board) : []
    },

    teamOptions(state): string[] {
      return state.board ? teamsOf(state.board) : []
    },

    /**
     * READ OFF THE FETCHED BOARD, NOT THE NARROWED ONE. A panel opened on a card and then typed
     * out of the search results must not blank itself: the person is reading the card they chose,
     * and its trail is already on screen. The same rule the writes follow through `teamOfCard`.
     */
    selectedCard(state): KanbanCard | null {
      if (!state.selectedId) return null

      return state.board?.cards.find((card) => card.id === state.selectedId) ?? null
    },
  },

  actions: {
    /** The cards of one lane, in the order `lib/kanban` fixes. A method so the template can call it. */
    laneCards(laneId: string): KanbanCard[] {
      const rendered = this.rendered

      return rendered ? cardsInLane(rendered, laneId) : []
    },

    /** The cards of one lane in one team's row of the swimlanes view, in the lane's own order. */
    cellCards(team: string, laneId: string): KanbanCard[] {
      return this.laneCards(laneId).filter((card) => card.team === team)
    },

    /**
     * Switch layout, and REFETCH WHEN THAT CHANGES THE FILTERS IN EFFECT: a team chosen on Board is
     * not applied in Swimlanes, so the board it fetched holds one team's cards and the other rows
     * would be empty. The team itself is kept and applies again on the way back to Board.
     */
    setView(view: KanbanView) {
      const before = this.effectiveFilters

      this.view = view
      this.revealTeam = ''
      if (!sameFilters(before, this.effectiveFilters)) void this.load()

      try {
        globalThis.localStorage?.setItem(KanbanViewKey, view)
      } catch {
        // A browser that will not store it still switches; it just forgets on reload.
      }
    },

    /**
     * THIS CARD'S MEMBER IS WAITING FOR A WIP SLOT. The member's snapshot says so (`held`), or the
     * ledger names it waiting - either is enough, and the ledger is what moves first.
     */
    memberHeld(card: KanbanCard): boolean {
      if (!card.member) return false

      const team = useConsoleStore().teams.find((entry) => entry.id === card.team)
      const snapshot = team?.containers.find((container) => container.id === card.member)

      return snapshot?.held === true || useWipStore().isWaiting(card.team, card.member)
    },

    /**
     * Somebody typed in the search box. NO FETCH, WHICH IS THE WHOLE POINT OF IT BEING HERE.
     *
     * Every other control on the bar is a filter the server resolves, so it refetches; this one
     * narrows the board already on screen, instantly and per keystroke. It is therefore the only
     * narrowing in this feature that `harness kanban board` cannot reproduce - which is exactly
     * why the bar says on screen what it matches.
     */
    setText(text: string) {
      this.text = text
    },

    /**
     * Show the board: CLEAR EVERY FILTER, then set the team to the one handed in.
     *
     * UNCONDITIONAL, not only when nothing is filtered yet. "Do not silently undo a filter somebody
     * set" is defensible on its own, but it would make the button mean two different things
     * depending on what the board was last narrowed to. Clicking
     * Kanban on a team's tab strip is a person saying "show me this team's work"; landing instead
     * on last week's status-and-member filter over a different team looks like the click did
     * nothing.
     *
     * An empty team clears everything and leaves the tenant-wide board, which is a real state and
     * where a person with no active team genuinely is.
     *
     * THE SEARCH BOX GOES WITH THEM. It is not a `KanbanFilters` field, so "clear every filter"
     * does not reach it by itself - and a board still narrowed by text somebody typed a tab ago,
     * under a bar that has just been reset, is the same "the click did nothing" this action was
     * made unconditional to avoid.
     *
     * IN SWIMLANES THE LAYOUT STAYS AND NO ROW IS HIDDEN. The team is still set, for when
     * the person goes back to Board, but Swimlanes does not apply it - so "show me this team" means
     * scrolling to that team's row, which the board does off `revealTeam`.
     */
    showKanban(team = '') {
      useConsoleStore().view = 'kanban'
      useConsoleStore().rememberTabs()

      this.filters = defaultKanbanFilters(team)
      this.text = ''
      this.revealTeam = this.view === 'swimlanes' ? team : ''

      void this.load()
    },

    /**
     * The ribbon's kanban button.
     *
     * ONE BEHAVIOUR, TWO NAMES, AND THE NAMES BELONG TO THE CALLERS. The ribbon action and the tab
     * button are two entry points and they must agree: both are a person naming a team and asking
     * to see it, so both clear what was there and set that team. A separate body that merged the
     * team into the existing filters would make the same icon mean different things from the two
     * places.
     */
    openForTeam(team: string) {
      this.showKanban(team)
    },

    setFilters(patch: KanbanFilterPatch) {
      this.filters = withFilters(this.filters, patch)
      void this.load()
    },

    /**
     * Clear means CLEAR: the three the server resolves, and the box that narrows what is drawn. The
     * team kept but not applied in Swimlanes goes too - a person pressing Clear wants nothing set.
     */
    clearFilters() {
      this.filters = {}
      this.text = ''
      void this.load()
    },

    /**
     * Fetch the board.
     *
     * IT READS THE FILTERS IN EFFECT AND NOTHING ELSE - in Swimlanes that is without the team, so
     * the board holds every team's cards. There is no active-team guard and no
     * `No team is active` refusal: a board with no team filter is every team the caller reaches,
     * which is what a per-tenant board means. The
     * server bounds it - `/api/kanban/board` resolves the effective set itself - so the browser has
     * no authority question to ask and nothing to refuse on its behalf.
     *
     * `fetchSeq` is the staleness guard, matching how the console store drops a token or workflow
     * response that arrived after a team switch: a slow answer to an old filter must not repaint
     * over a fast answer to the new one.
     */
    async load() {
      const ticket = ++this.fetchSeq
      this.loading = true

      try {
        const board = await getKanbanBoard(this.effectiveFilters)

        if (ticket !== this.fetchSeq) return

        this.board = board
        this.error = ''
      } catch (cause) {
        if (ticket !== this.fetchSeq) return

        // The board stays as it was rather than blanking. A refetch that failed is a stale board,
        // and a stale board beside a stated error is more use than an empty one.
        this.error = describe(cause)
      } finally {
        if (ticket === this.fetchSeq) this.loading = false
      }
    },

    /**
     * The team a card belongs to, taken off the CARD.
     *
     * EVERY PER-CARD CALL IS ADDRESSED WITH THIS AND NEVER WITH THE ACTIVE TEAM. Those two were
     * indistinguishable while the board could only ever hold one team's cards; on a tenant-wide
     * board they are routinely different, and the active team would 404 a card that is plainly on
     * screen - or, worse, address a same-named card belonging to somebody else.
     *
     * Null means the board does not hold that card. There is then no address to go to, which is a
     * different thing from a refusal and is said differently.
     */
    teamOfCard(id: string): TeamId | null {
      const found = this.board?.cards.find((entry) => entry.id === id)

      return (found?.team as TeamId | undefined) ?? null
    },

    /**
     * Open one card's side panel.
     *
     * The card itself is already on the board; this fetches the TRAIL and the attributes, which the
     * board payload does not carry. The panel opens immediately on the card the board holds, so it
     * is never a spinner over nothing.
     */
    async select(id: string) {
      const team = this.teamOfCard(id)

      this.selectedId = id
      this.detail = null
      this.detailError = ''
      this.detailLoading = true

      if (!team) {
        this.detailError = UnknownCard
        this.detailLoading = false
        return
      }

      try {
        this.detail = await getKanbanCard(team, id)
      } catch (cause) {
        this.detailError = describe(cause)
      } finally {
        this.detailLoading = false
      }
    },

    closeCard() {
      this.selectedId = ''
      this.detail = null
      this.detailError = ''
    },

    /**
     * The three human edits, each addressed under the CARD'S own team.
     *
     * A card the board does not hold THROWS rather than returning quietly. The caller is a form
     * with a field to put an error beside, and a write that silently does nothing is the failure
     * mode this whole store is built to avoid: the panel would close on a "saved" that never was.
     */
    async move(id: string, laneId: string, note?: string) {
      const team = this.teamOfCard(id)
      if (!team) throw new Error(UnknownCard)

      await this.write(() => moveKanbanCard(team, id, laneId, note))
    },

    async edit(id: string, changes: KanbanEditRequest) {
      const team = this.teamOfCard(id)
      if (!team) throw new Error(UnknownCard)

      await this.write(() => editKanbanCard(team, id, changes))
    },

    async comment(id: string, text: string) {
      const team = this.teamOfCard(id)
      if (!team) throw new Error(UnknownCard)

      await this.write(() => commentKanbanCard(team, id, text))
    },

    /**
     * One shape for every human edit: append, refetch the board, refetch the open card.
     *
     * NO OPTIMISTIC LOCAL EDIT. The change becomes a message on the log and the projection decides
     * what it means - including setting `awaitingManager`, which the browser has no business
     * inventing. Refetching is what makes the board on screen the projection's answer rather than
     * this store's guess at it.
     *
     * Throws on: the caller is a form that has an error to show beside the field someone typed in.
     */
    async write(append: () => Promise<void>) {
      this.busy = true

      try {
        await append()
        await this.load()
        if (this.selectedId) await this.select(this.selectedId)
      } finally {
        this.busy = false
      }
    },

    /**
     * THE PAGE ARRIVED ON THE BOARD. Fetch whatever the filters already say.
     *
     * THE ONLY LOAD PATH HERE THAT IS NOT A REACTION TO A FILTER CHANGING, and the tab had none of
     * it. Every other caller - `showKanban`, `openForTeam`, `setFilters`, `clearFilters` and the
     * two hub paths - is somebody moving a control, so a reload landing straight back on a
     * restored Kanban tab asked the server for nothing and the board rendered its own empty state.
     * That reads as "there is no work" where the truth was "nothing has been fetched", and it was
     * the state a person lands on: no filters set, which is the state that should show everything.
     *
     * The mirror of the console store's `refreshRollupIfShowing`, which exists on the Teams table
     * for the identical reason - the view is persisted, so a reload can land on any of the three.
     *
     * IT READS THE FILTERS AND DOES NOT WRITE THEM. Defaulting the team to the active one here is
     * the obvious repair and it is the wrong one: no team filter MEANS the whole tenant, which is
     * what `load` is documented to invoke and what `showTeamsView` depends on, and the SERVER
     * bounds the set through `/api/kanban/board`. So an arrival that is already filtered fetches
     * under that filter, and the board on screen is never narrower - or wider - than the bar above
     * it says. `load` takes the ticket, so a slow arrival cannot repaint over a newer filter.
     *
     * A no-op while the tab is not showing, by the same rule as `refreshIfActive`: a board fetched
     * for a page nobody is looking at is traffic with no reader, and the tab's own button fetches.
     */
    loadIfShowing() {
      if (!this.active) return

      void this.load()
    },

    /**
     * A hub push, or any other reason to think the board moved.
     *
     * DOES NOTHING WHILE THE TAB IS NOT SHOWING. Container activity arrives constantly and the
     * board is one tab of several - fetching it for a page nobody is looking at is traffic with no
     * reader. Arriving on the tab loads it, so there is nothing to catch up on - and `loadIfShowing`
     * is what makes that second sentence true, including for a reload that restores the tab.
     */
    refreshIfActive() {
      if (!this.active) return

      void this.load()
    },
  },
})

/** A thrown value as a line a person can read. `Unauthorized` is a state, not a stack trace. */
function describe(cause: unknown): string {
  if (cause instanceof Unauthorized) return 'Not signed in.'

  return cause instanceof Error ? cause.message : String(cause)
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useKanbanStore, import.meta.hot))
}
