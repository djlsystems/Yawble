import type {
  KanbanBoard,
  KanbanCard,
  KanbanCardDetail,
  KanbanFilters,
  KanbanLane,
  KanbanStatus,
} from '../api/kanban'
import type { Outcome } from '../api/outcomes'

/**
 * Everything the board decides that is NOT a fetch and NOT a template.
 *
 * It lives in `lib/` for the reason `lib/ribbon.ts` does: this repository can mount a component
 * under test now, and a rule written as a template binding is still a rule better pinned here than
 * rediscovered from rendered output. Colour, lane placement, the default filter and the sample-data
 * switch are all rules, and all of them are pure functions here rather than expressions in a
 * `.vue` file.
 */

/**
 * THE LANE FOR WORK WAITING ON A PERSON, which the board labels Needs You. Its id is `blocked`
 * because a lane id is a durable reference in the log (`KanbanLanes.HumanActionLaneLabel`); the
 * proposed outcomes a person has to settle are listed and counted in it too.
 */
export const NeedsYouLaneId = 'blocked'

/**
 * THE LOCKED PALETTE, as words. The design brief fixes these and a template may override the
 * per-status choice later - which is why the card carries a `color` and this is the fallback,
 * rather than the UI deriving colour from status and ignoring what the server said.
 */
export const KanbanStatusColour: Record<KanbanStatus, KanbanColour> = {
  queued: 'slate',
  running: 'blue',
  blocked: 'amber',
  'needs-decision': 'orange',
  failed: 'red',

  // TEAL, THE WORD `completed` CARRIES, shared rather than minted - the server's
  // `KanbanTemplates.GetColourForStatus` makes the same choice and the two must agree. The palette
  // is locked and `KanbanColours` below has one class per word, so a new colour here would
  // render as a colourless card until a stylesheet caught up.
  handback: 'teal',
  completed: 'teal',
  done: 'green',
}

/**
 * The colour words this build can render - WORDS ONLY. What each word looks like is `app.scss`'s:
 * `.os-kanban-<word>` sets `--os-kanban-accent` and `--os-kanban-tint` from the theme tokens, so a
 * card is the same word in light and dark and a different colour in each. This list carries no hex
 * values, so the palette lives in one place: the theme.
 *
 * A word that is not in here is not a colour the stylesheet has a class for, so `colourFor` falls
 * back rather than emitting a class that resolves to nothing - which renders as a colourless card
 * with nothing failing, the exact silent-drift shape the ribbon parser exists to avoid.
 * `kanban-palette.spec.ts` reads `app.scss` and fails for a word here without a class there.
 */
export const KanbanColours = ['slate', 'blue', 'amber', 'orange', 'red', 'teal', 'green'] as const

export type KanbanColour = (typeof KanbanColours)[number]

export function isKanbanColour(word: string | null | undefined): word is KanbanColour {
  return !!word && (KanbanColours as readonly string[]).includes(word)
}

/** The class that paints a colour word, spelled once. See `.os-kanban-*` in `app.scss`. */
export function kanbanColourClass(word: string): string {
  return `os-kanban-${word}`
}

export const KanbanStatusLabel: Record<KanbanStatus, string> = {
  queued: 'Queued',
  running: 'Running',
  blocked: 'Blocked',
  'needs-decision': 'Needs decision',
  failed: 'Failed',
  handback: 'Handed back',
  completed: 'Completed',
  done: 'Done',
}

/**
 * The colour word for one card.
 *
 * The SERVER'S choice wins when this build knows how to draw it, because the template owns the
 * palette. An unknown word falls through to the status map, and an unknown status to slate: a card
 * always has a colour, and the failure mode of a backend that invents one is a plain card rather
 * than an invisible one.
 */
export function colourFor(card: Pick<KanbanCard, 'color' | 'status'>): KanbanColour {
  if (isKanbanColour(card.color)) return card.color

  return KanbanStatusColour[card.status] ?? 'slate'
}

/**
 * The filter a board opens with when nobody has said otherwise: the team you are looking at.
 *
 * An empty active team yields an empty filter rather than `{ team: '' }` - one answer to "unset",
 * which is also what `kanbanQuery` drops. Two spellings of nothing is how a board asks the server
 * for team-named-empty-string and gets no cards with nothing failing.
 */
export function defaultKanbanFilters(activeTeam: string): KanbanFilters {
  return activeTeam ? { team: activeTeam } : {}
}

/**
 * Applies a change to a filter set, dropping anything that became empty.
 *
 * `undefined` and `''` both mean "cleared" coming out of a q-select, and both leave here as an
 * ABSENT key - so an object comparison and a query string agree about what is filtered.
 */
export function withFilters(filters: KanbanFilters, patch: KanbanFilterPatch): KanbanFilters {
  const merged: Record<string, string | null | undefined> = { ...filters, ...patch }

  for (const key of Object.keys(merged)) {
    const value = merged[key]
    if (value === undefined || value === null || value === '') delete merged[key]
  }

  return merged as KanbanFilters
}

/**
 * A change to a filter set, in the shapes the CONTROLS actually emit.
 *
 * A cleared `q-select` emits `null` and a cleared `q-input` emits `undefined` - neither of which
 * `KanbanFilters` admits, because a filter that is set is a string. Widening only the PATCH is
 * what lets `withFilters` be the single place all three spellings of "cleared" become one absent
 * key, instead of every call site coercing first and getting it subtly different.
 */
export type KanbanFilterPatch = { [Key in keyof KanbanFilters]?: string | null | undefined }

/** One board, two layouts: the lanes side by side, or one row per team with the lanes as columns. */
export type KanbanView = 'board' | 'swimlanes'

/** What the disabled Team filter says in Swimlanes, as its caption and its tooltip. */
export const SwimlanesTeamCaption = 'Swimlanes show every team'

/**
 * THE FILTERS IN EFFECT FOR A LAYOUT: what is fetched, counted and drawn.
 *
 * SWIMLANES DROPS THE TEAM. The layout exists to show every team's work at once, so a team filter
 * there would leave one row and no sign of why. The team is dropped HERE rather than cleared from
 * the store: `filters.team` is kept, and applies again the moment the person is back on Board.
 * Member and status apply in both layouts.
 */
export function effectiveFilters(filters: KanbanFilters, view: KanbanView): KanbanFilters {
  const effective = withFilters(filters, {})

  if (view === 'swimlanes') delete effective.team

  return effective
}

/** Two filter sets narrow the board the same way. Used to refetch only when a layout switch changes it. */
export function sameFilters(left: KanbanFilters, right: KanbanFilters): boolean {
  const a = withFilters(left, {}) as Record<string, string>
  const b = withFilters(right, {}) as Record<string, string>
  const keys = Object.keys(a)

  return keys.length === Object.keys(b).length && keys.every((key) => a[key] === b[key])
}

/**
 * How many things are narrowing the board. Drives the "clear" affordance and reads in a test.
 *
 * THE SEARCH BOX COUNTS, THOUGH IT IS NOT ONE OF THE FILTERS. It is not a `KanbanFilters` field -
 * it never reaches the server - but it narrows what a person can see exactly as they do, and a
 * narrowing with no affordance to undo it is a board with missing cards and nothing on screen to
 * explain them. So it is a second argument rather than a fourth key: counted here, cleared by the
 * same button, and still impossible to put on a query string.
 */
export function activeFilterCount(filters: KanbanFilters, text = ''): number {
  return Object.keys(withFilters(filters, {})).length + (text.trim() === '' ? 0 : 1)
}

/**
 * Every piece of a card the search box reads, as one string.
 *
 * A NULL MEMBER CONTRIBUTES NOTHING, not the word "null" - the same rule `membersOf` follows for
 * the same reason: nobody-assigned-yet is a state, and a search for `null` listing every unclaimed
 * card is a board answering a question nobody asked. `item` is a NUMBER and joins as its digits,
 * which is how a person quotes a backlog id back at you.
 */
function searchableText(card: KanbanCard): string {
  const parts = [
    card.title,
    card.body,
    card.member ?? '',
    card.team,
    card.item === null || card.item === undefined ? '' : String(card.item),
    card.status,
    card.id,
  ]

  return parts.join('\n').toLowerCase()
}

/**
 * The cards to RENDER, narrowed by what somebody typed. Case-insensitive, trimmed, and an empty
 * box narrows nothing.
 *
 * CLIENT-SIDE, AND THAT IS A DECISION RATHER THAN AN OVERSIGHT. It is
 * instant, it changes no route, and it composes with the three filters that do reach the server.
 * It stops being right the moment a board needs paging - a box that searches only the page you
 * were sent is a box that lies - so paging the board means moving this to the server.
 *
 * IT NARROWS A COPY. The board is a derived read model and the store holds none of its own: a
 * second source of truth for team work is the one thing the design brief forbids outright, so this
 * returns a new array and the fetched `board.cards` is left exactly as the server answered.
 */
export function cardsMatchingText(cards: readonly KanbanCard[], text: string): KanbanCard[] {
  const needle = text.trim().toLowerCase()

  if (needle === '') return [...cards]

  return cards.filter((card) => searchableText(card).includes(needle))
}

/**
 * The lanes to draw, which is the TEMPLATE'S lanes plus a home for anything that does not fit.
 *
 * A card naming a lane the template does not have would otherwise be fetched, held and never
 * rendered - present in the payload, absent from the screen, and impossible to notice. The
 * projection should not produce one; when it does, this shows it rather than eating it.
 */
export function lanesToRender(board: Pick<KanbanBoard, 'lanes' | 'cards'>): KanbanLane[] {
  const known = new Set(board.lanes.map((lane) => lane.id))
  const orphaned = board.cards.some((card) => !known.has(card.laneId))

  return orphaned
    ? [...board.lanes, { id: UnplacedLaneId, title: 'Unplaced' }]
    : [...board.lanes]
}

/** The lane orphaned cards are shown in. Not a template lane and never sent to the server. */
export const UnplacedLaneId = 'unplaced'

/**
 * The cards in one lane, in a STABLE order.
 *
 * Newest activity first, then by id so two cards updated in the same second do not swap places on
 * every refetch - a list that reorders itself under a FLIP animation reads as cards flying about
 * for no reason.
 */
export function cardsInLane(
  board: Pick<KanbanBoard, 'lanes' | 'cards'>,
  laneId: string,
): KanbanCard[] {
  const known = new Set(board.lanes.map((lane) => lane.id))

  const cards =
    laneId === UnplacedLaneId
      ? board.cards.filter((card) => !known.has(card.laneId))
      : board.cards.filter((card) => card.laneId === laneId)

  return [...cards].sort((left, right) => {
    if (left.updatedAt === right.updatedAt) return left.id < right.id ? -1 : 1

    return left.updatedAt < right.updatedAt ? 1 : -1
  })
}

/**
 * Every member with a card on the board, sorted, for the member filter's options.
 *
 * A NULL MEMBER CONTRIBUTES NO OPTION, which is the right answer rather than a special one: a
 * planned card nobody has claimed belongs to no member, so there is nothing to filter by. The
 * predicate is explicit rather than `.filter(Boolean)` because that also drops `''`, and empty is
 * not a state a member has.
 */
/**
 * A board filter's values: the comma-separated string the store, the query string and the route
 * carry (`KanbanFilter.Values` on the server), as a list. Empty parts are dropped.
 */
export function filterValues(value: string | undefined): string[] {
  return (value ?? '').split(',').map((part) => part.trim()).filter(Boolean)
}

export function membersOf(board: Pick<KanbanBoard, 'cards'>): string[] {
  const named = board.cards
    .map((card) => card.member)
    .filter((member): member is string => member !== null && member !== undefined)

  return [...new Set(named)].sort()
}

/** Every team with a card on the board. Used to offer a team filter even before the overview lands. */
export function teamsOf(board: Pick<KanbanBoard, 'cards'>): string[] {
  return [...new Set(board.cards.map((card) => card.team).filter(Boolean))].sort()
}

/**
 * The description to draw under a card's title, or `''` for a card that has none.
 *
 * A CARD IS A TITLE AND THE REST OF THE INSTRUCTION, and both halves are on screen. A body the
 * projection writes, the endpoint sends and the store holds, but no component binds, looks exactly
 * like a field that is empty, so nothing would look wrong anywhere.
 *
 * VERBATIM WHEN THERE IS ANYTHING TO READ. The body is not re-trimmed here and must not be: an
 * instruction's list or command example is indented on purpose, the server carries that shape
 * through the split deliberately, and the browser is the last place it could be flattened. The
 * template pairs this with `white-space: pre-wrap`.
 *
 * WHITESPACE IS NOTHING TO READ, so it answers `''` and the panel draws no section rather than a
 * heading over a blank - which reads as a body that failed to load. The card may also be absent
 * altogether while the detail is in flight, or come from a host old enough to send no `body`.
 */
export function bodyToRender(card: Pick<KanbanCard, 'body'> | null | undefined): string {
  const body = card?.body ?? ''

  return body.trim() === '' ? '' : body
}

/** One line of the panel's Trail section, ready to render. */
export interface KanbanTrailRow {
  /** Unique within the list. Vue reuses a row's node otherwise, across different messages. */
  key: string
  at: string | null
  type: string

  /** Always a string. `''` renders as an empty cell; `undefined` renders as the word undefined. */
  text: string

  /**
   * TRUE ONLY FOR THE FALLBACK. A row built from the board card's own progress lines because the
   * server answered no trail - not the same claim as a trail row that happens to be a progress
   * line, which is why it is a flag rather than something inferred from `type`.
   */
  fallback: boolean
}

/**
 * The rows to draw under Trail: the SERVER'S trail when it sent one, and the board card's own
 * progress lines only when it did not.
 *
 * IT IS A RULE, SO IT LIVES HERE. As two `v-if`s in `KanbanCardPanel.vue` it would be a rule with
 * no test - and taking the wrong branch silently drops every comment and note, which are not
 * progress lines. The fallback is for a card whose detail carries no trail; it is the exception
 * rather than every fetch.
 *
 * The ORDER the server sent is preserved rather than re-sorted. Oldest-first is the projection's
 * guarantee and it holds the seq that decides it; a second sort here would be a second store of one
 * rule, and the client's copy is the one nobody would notice going stale.
 */
export function trailRows(
  detail: Pick<KanbanCardDetail, 'trail'> | null | undefined,
  card: Pick<KanbanCard, 'progress'> | null | undefined,
): KanbanTrailRow[] {
  const trail = detail?.trail ?? []

  if (trail.length > 0) {
    return trail.map((entry) => ({
      key: `trail-${entry.seq}`,
      at: entry.occurredAt ?? null,
      type: entry.type,

      // The payload when a row carried no prose - a row that says nothing at all is worse than one
      // showing the envelope, and the server sends `text` for every type it knows how to summarise.
      text: entry.text || entry.payload || '',
      fallback: false,
    }))
  }

  return (card?.progress ?? []).map((item, index) => ({
    key: `progress-${index}`,
    at: item.at,
    type: 'agentContainer.progress',
    text: item.text,
    fallback: true,
  }))
}

/** One swimlane row: a team, and whether it is on the console's team list or known only from a card. */
export interface SwimlaneTeam {
  id: string
  name: string
}

/**
 * THE ROWS OF THE SWIMLANES VIEW: one per team on the console's list, EMPTY TEAMS INCLUDED - a team
 * with no cards is a fact the view is for - then any team a card names that the list has not got
 * (a team deleted since, or a list not yet loaded), so no card is ever left without a row.
 *
 * A team filter narrows to that one row, matching the cards the server answered with.
 */
export function swimlaneTeams(
  teams: readonly SwimlaneTeam[],
  cards: readonly Pick<KanbanCard, 'team'>[],
  teamFilter = '',
): SwimlaneTeam[] {
  const rows: SwimlaneTeam[] = teams.map((team) => ({ id: team.id, name: team.name }))
  const known = new Set(rows.map((team) => team.id))

  for (const card of cards) {
    if (known.has(card.team)) continue
    known.add(card.team)
    rows.push({ id: card.team, name: card.team })
  }

  return teamFilter ? rows.filter((team) => team.id === teamFilter) : rows
}

/**
 * THE TEAMS A PROPOSED OUTCOME BELONGS TO: each team whose workflows it serves (`figures.teams`,
 * read from the links) and the team of the Manager who proposed it (`createdBy` is `team/member`
 * for a member). Lower-cased, as the board's team filter is compared. An outcome nobody on a team
 * proposed and no workflow serves belongs to no team.
 */
export function outcomeTeams(outcome: Pick<Outcome, 'createdBy' | 'createdByKind' | 'figures'>): string[] {
  const teams = new Set((outcome.figures?.teams ?? []).map((team) => team.id.toLowerCase()))
  const slash = outcome.createdBy.indexOf('/')

  if (outcome.createdByKind === 'member' && slash > 0) teams.add(outcome.createdBy.slice(0, slash).toLowerCase())

  return [...teams]
}

/**
 * THE PROPOSED OUTCOMES NEEDS YOU DRAWS, under the same filters as the cards beside them. The read
 * that fetches them is instance-wide, so without this a board narrowed to one team still listed
 * every team's proposals in its Needs You lane.
 *
 * Every filter given must hold, any of its values keeps an outcome, ignoring case - the server's
 * rule for cards (`KanbanProjector`):
 * - team: one of the outcome's teams (`outcomeTeams`) is picked;
 * - member: a picked member proposed it;
 * - status: never - a status names what a CARD is doing, and an outcome is not a card;
 * - outcome: its id is picked (`none` keeps nothing: a proposal is an outcome);
 * - the search box: its name, id or proposer contains the text, as a card's fields do.
 *
 * Pass the filters IN EFFECT (`effectiveFilters`): Swimlanes drops the team, and so does this.
 */
export function proposedMatchingFilters(
  outcomes: readonly Outcome[],
  filters: KanbanFilters,
  text = '',
): Outcome[] {
  const teams = filterValues(filters.team).map((team) => team.toLowerCase())
  const members = filterValues(filters.member).map((member) => member.toLowerCase())
  const statuses = filterValues(filters.status)
  const picked = filterValues(filters.outcome)
  const needle = text.trim().toLowerCase()

  return outcomes.filter((outcome) => {
    if (teams.length > 0 && !outcomeTeams(outcome).some((team) => teams.includes(team))) return false
    if (members.length > 0 && !members.includes(proposingMember(outcome))) return false
    if (statuses.length > 0) return false
    if (picked.length > 0 && !picked.includes(outcome.id)) return false
    if (needle !== '' && ![outcome.name, outcome.id, outcome.createdBy].join('\n').toLowerCase().includes(needle)) return false

    return true
  })
}

/** The member who proposed an outcome, lower-cased, or '' when a person or the Concierge did. */
function proposingMember(outcome: Pick<Outcome, 'createdBy' | 'createdByKind'>): string {
  const slash = outcome.createdBy.indexOf('/')

  return outcome.createdByKind === 'member' && slash > 0 ? outcome.createdBy.slice(slash + 1).toLowerCase() : ''
}

/**
 * THE BOARD HEADER'S COUNT: every entry on screen, said as what it is - `2 cards`, or
 * `1 card, 1 proposed outcome` when Needs You lists any. A proposed outcome is drawn in a lane like
 * a card, so a header counting only cards read one short of the board under it.
 */
export function boardCountText(cards: number, proposed: number): string {
  const cardText = `${cards} card${cards === 1 ? '' : 's'}`

  return proposed > 0 ? `${cardText}, ${proposed} proposed outcome${proposed === 1 ? '' : 's'}` : cardText
}

/** An advisory lane is over its limit. A lane with no limit never is. */
export function laneOverLimit(count: number, limit: number | null | undefined): boolean {
  return typeof limit === 'number' && limit > 0 && count > limit
}

/** A card shows the waiting mark while its member is held and the card is still live work. */
export function cardShowsWaiting(card: Pick<KanbanCard, 'status'>, held: boolean): boolean {
  return held && (card.status === 'queued' || card.status === 'running')
}
