import type { BacklogItemView } from '../api/client'
import type { BacklogInFlight, BacklogLanded } from '../api/types'
import { MAXIMUM_BACKLOG_TITLE_LENGTH } from './rules'

/**
 * The sortable columns on the backlog table.
 *
 * `position` IS THE ONLY ONE A DRAG MEANS ANYTHING UNDER, which is why it is a value here rather
 * than an implicit default - see {@link canDrag}.
 */
export type BacklogSort = 'position' | 'id' | 'title' | 'team' | 'state' | 'created' | 'updated' | 'archived'

export interface BacklogSortState {
  by: BacklogSort
  descending: boolean
}

/**
 * DRAGGING IS PERMITTED ONLY WHILE SORTED BY POSITION, and the control is disabled with a reason
 * under any other sort.
 *
 * A drag while sorted by Modified has no meaning to give it: the drop lands the row between two
 * neighbours that are not its POSITION neighbours, so either the item moves somewhere the person
 * did not indicate, or nothing happens. Every tool that offers both makes this restriction; making
 * it explicit here is what stops it being discovered as a bug.
 */
export function canDrag(sort: BacklogSortState): boolean {
  return sort.by === 'position'
}

/** Said to the person, where the control is. Never a silent disable. */
export function whyDragDisabled(sort: BacklogSortState): string {
  return canDrag(sort)
    ? ''
    : 'Sort by # to reorder. Under any other sort a drop has no position to mean.'
}

/**
 * What the LINK says - who may SEE an item. NOT the Team column, which names who the item was
 * dispatched to; see {@link dispatchedTeamLabel} for the difference and why it is not one fact.
 *
 * THREE ANSWERS, NOT TWO. An item with no team is the tenant's; an item whose team is GONE is not
 * the same thing and must not read as one - the item survived its team, which is the whole reason
 * the object has no foreign key, and showing a bare id or a blank would lose that.
 */
export function teamLabel(item: Pick<BacklogItemView, 'team' | 'teamName' | 'teamGone'>): string {
  if (item.team === null) return '(no team)'
  if (item.teamGone) return '(team deleted)'

  return item.teamName ?? item.team
}

/**
 * The long form of the link marker, for a hover or a screen reader.
 *
 * IT SAYS WHAT THE LINK IS FOR, because the screen shows two teams on one row and a person who
 * could not tell them apart would read the marker as a second, contradictory answer to "who is
 * doing this".
 */
export function linkTitle(item: Pick<BacklogItemView, 'team' | 'teamName' | 'teamGone'>): string {
  return `Visible to ${teamLabel(item)}. The link decides who SEES this item and is never rewritten `
    + 'by a dispatch - it is not where the item runs.'
}

/**
 * THE STATES, mirroring `BacklogStates` on the server so a grep for the name finds both halves.
 *
 * `pending` IS "NOT REVIEWED" AND `ready` IS "A PERSON HAS READ THIS". They are two words because
 * a board that cannot tell a draft from a decided spec dispatches the draft - which
 * costs a Manager run plus every member run it spawns before anybody notices.
 *
 * `declared` IS "A MANAGER SAID IT WAS DELIVERED", AND THAT IS ALL IT SAYS. It is the only one of
 * the four the platform writes - `OnWorkflowCompletedAsync`, when a dispatched workflow is declared
 * complete - and it exists because a declaration is not a landing. Were that write to go
 * straight to `implemented`, an item could read `implemented` while the only copy of its work was
 * unpushed in one team's clone, one destructive click from being lost.
 *
 * `implemented` IS "THE WORK IS IN THE PRODUCT" AND IS A PERSON'S VALUE ALONE. Nothing
 * automatic writes it, and an item a person put here is never overwritten by a later
 * declaration.
 *
 * EVERY ONE OF THESE IS SOMETHING SOMEBODY SAID. Where the work actually IS is the derived
 * {@link landedMark}, which is a different fact on a different field and is rendered beside this
 * rather than folded into it.
 */
export const BacklogStates = {
  Pending: 'pending',
  Ready: 'ready',
  Declared: 'declared',
  Implemented: 'implemented',
} as const

/**
 * The two states the review control moves an item between, in the order they are read.
 *
 * `implemented` IS DELIBERATELY ABSENT. A person marking their own item implemented from a review
 * toggle would be using one control for two unrelated decisions, and the platform sets that state
 * itself from `OnWorkflowCompletedAsync`.
 */
export const REVIEW_OPTIONS: ReadonlyArray<{ label: string; value: string }> = [
  { label: 'Pending', value: BacklogStates.Pending },
  { label: 'Ready', value: BacklogStates.Ready },
]

/**
 * Whether the review toggle applies at all - it does not to an item already `declared` or
 * `implemented`.
 *
 * TWO STATES IT CANNOT SHOW, NOT ONE. The toggle has exactly two values and neither of those two
 * states is one of them, so on either it would render with nothing selected and read as broken.
 * ABSENT, NOT DISABLED - the caption below says what
 * the state means and {@link canReopen} is the way back.
 */
export function canReview(item: Pick<BacklogItemView, 'state'>): boolean {
  return item.state === BacklogStates.Pending || item.state === BacklogStates.Ready
}

/**
 * WHETHER A PERSON MAY STILL SAY `implemented` BY HAND - and they may, from anywhere but here.
 *
 * THIS IS THE ONLY THING THAT CAN SET THAT STATE. A Manager's declaration stops at
 * `declared`, so the last step is a person's and the control has to be on the screen in front of
 * them while they read the landing beside it. The derivation NEVER overrides them either: an item
 * set here by hand is left alone by a later declaration.
 *
 * IT IS NOT THE REVIEW TOGGLE AND MUST NOT BECOME ONE. {@link REVIEW_OPTIONS} still refuses to
 * offer `implemented`, because one control for two unrelated decisions is how somebody marks their
 * own draft done by reaching for the nearest thing.
 */
export function canMarkImplemented(item: Pick<BacklogItemView, 'state'>): boolean {
  return item.state !== BacklogStates.Implemented
}

/**
 * WHETHER THE WAY BACK IS OFFERED - on exactly the two states the review toggle cannot show.
 *
 * NOT ON `pending` OR `ready`, because the toggle is already there carrying Pending and a second
 * button for the same move would be two controls for one decision. Nothing automatic ever moves an
 * item out of `declared`: there is no sweeper, no timeout and no expiry, so declared-and-never-
 * landed is a real and permanent reading and this is the only exit from it.
 */
export function canReopen(item: Pick<BacklogItemView, 'state'>): boolean {
  return item.state === BacklogStates.Declared || item.state === BacklogStates.Implemented
}

/**
 * ONLY A `ready` ITEM MAY BE DISPATCHED, and this is the client's copy of the rule the two dispatch
 * routes refuse with a 409.
 *
 * TWO COPIES OF ONE RULE IS THE POINT HERE, not an oversight: the server is the boundary and the
 * client is the explanation. A screen that let the click through and rendered the refusal would be
 * a control that exists to fail, and one that disabled the button without the refusal's words would
 * be the silent disable this codebase keeps writing rules about.
 */
export function canDispatch(item: Pick<BacklogItemView, 'state'>): boolean {
  return item.state === BacklogStates.Ready
}

/**
 * WHY THE DISPATCH CONTROL IS DEAD, in words, ON THE ROW - never only in a tooltip.
 *
 * A DISABLED `<button>` FIRES NO MOUSE EVENTS, so a `q-tooltip` inside one never opens: the hover
 * explanation for a disabled control is the explanation that structurally cannot be read. And a
 * phone has no hover either. So this string is rendered as text beside the state, which is the same
 * choice the in-flight mark made and for the same reason.
 *
 * IT NAMES THE WAY OUT, not just the refusal. "Not reviewed" alone leaves a person looking for a
 * control they have not found yet.
 */
export function whyDispatchDisabled(item: Pick<BacklogItemView, 'state'>): string {
  if (canDispatch(item)) return ''

  switch (item.state) {
    case BacklogStates.Implemented:
      return 'Implemented - mark it ready to dispatch it again.'
    // A FOURTH STATE NEEDS ITS OWN ARM AND NOT THE DEFAULT. "Not reviewed" on an item a Manager
    // has just declared contradicts the state rendered directly above it, and that is exactly what
    // a new state added to an `if/else` produces if nobody writes this line. IT IS ALSO THE
    // `declared` READING ON THE ROW: the word alone does not say who said it or what it does not
    // claim, and this is the sentence next to it that does.
    case BacklogStates.Declared:
      return 'A Manager declared this delivered - that is a claim, not a landing. Check where the '
        + 'work is, then mark it ready to dispatch it again.'
    default:
      return 'Not reviewed - mark it ready to dispatch.'
  }
}

/** What the detail panel says about the state it is showing, under the review control. */
export function reviewCaption(item: Pick<BacklogItemView, 'state'>): string {
  switch (item.state) {
    case BacklogStates.Ready:
      return 'Ready: somebody has read this and a Manager could cut it into cards without asking '
        + 'follow-up questions.'
    // THE WHOLE POINT OF THE STATE, IN THE WORDS A PERSON READS. It must not read as shipped: this
    // is an agent's claim about a workflow, and one word must not carry that claim and "the work
    // is in the product" at the same time. Where the work IS is the landing
    // mark beside this, and the caption sends the reader to it rather than guessing for them.
    case BacklogStates.Declared:
      return 'Declared: a Manager said this was delivered. That is a claim about a workflow and '
        + 'not evidence the work landed anywhere - read the landing beside it. Nothing moves this '
        + 'on by itself: reopen it as pending, or mark it implemented yourself once you have '
        + 'checked.'
    case BacklogStates.Implemented:
      return 'Implemented. Reopen it as pending to review it again - nothing but a '
        + 'person sets this, because a Manager declaring a workflow complete stops at declared.'
    default:
      return 'Pending means nobody has reviewed this yet. Read the spec, then mark it ready - only '
        + 'a ready item may be dispatched.'
  }
}

/**
 * THE LANDING VOCABULARY, mirroring the server's derivation so a grep for a word finds both halves.
 *
 * FOUR ANSWERS TO ONE QUESTION - where is the work? - AND THEY ARE NOT A SCALE. `landed`, `pushed`
 * and `local` are three answers somebody computed, in descending order of safety. `unknown` is not
 * a fourth rung below `local`: it is the ABSENCE of an answer, and it is a value rather than a null
 * so that "we asked and could not tell" stays visible instead of vanishing into the same blank as
 * "there was nothing to ask about".
 */
export const LandedStates = {
  Landed: 'landed',
  Pushed: 'pushed',
  Local: 'local',
  Unknown: 'unknown',
  // Contributor mode: the recorded pull request is open, or was closed without merging.
  InReview: 'in-review',
  Declined: 'declined',
} as const

/** What the screen draws for one item's landing. Everything the template needs, from one call. */
export interface LandedMark {
  /**
   * The vocabulary word, NORMALISED - anything unrecognised has already become `unknown` here. It
   * is what the template puts on `data-landed`, which is how a reader's eye and a test both tell
   * the four apart without parsing words.
   */
  state: string

  /** The few words on the row. */
  text: string

  /** The glyph beside them. Four different ones, deliberately. */
  icon: string

  /** The long form, for a hover or a screen reader, with the server's own sentence on the end. */
  title: string
}

/**
 * THE HEADLINES AND THE LONG FORMS, one entry per reading.
 *
 * `unknown` IS WRITTEN AS A MISSING ANSWER AND NEVER AS A NEGATIVE ONE. That is the landing
 * requirement and the rule this codebase holds for spend - {@link tokensLine} renders
 * `(unknown)` rather than `0`, because nothing measured is not a measurement of zero. A person
 * reading "cannot tell" goes and looks; a person reading "not landed" believes something nobody
 * checked.
 */
const LANDING_READINGS: Record<string, { text: string; icon: string; long: string }> = {
  [LandedStates.Landed]: {
    text: 'On the default branch',
    icon: 'check_circle',
    long: 'On the default branch. The work is in the product.',
  },
  [LandedStates.Pushed]: {
    text: 'Pushed, not merged',
    icon: 'cloud_done',
    long: 'Pushed, but not merged. The work is off the machine and on the remote, and it is not '
      + 'on the default branch yet.',
  },
  [LandedStates.Local]: {
    text: "Only in the team's clone",
    icon: 'warning',
    long: "Only in the team's clone. Nothing has been pushed, so this exists on one disk and "
      + 'deleting the team would take it.',
  },
  [LandedStates.InReview]: {
    text: 'Pull request in review',
    icon: 'hourglass_empty',
    long: 'A pull request is open upstream and in review. The work is on the fork and has not landed yet.',
  },
  [LandedStates.Declined]: {
    text: 'Pull request declined',
    icon: 'block',
    long: 'The pull request was closed upstream without merging. The work has not landed, and will not '
      + 'from that pull request.',
  },
  [LandedStates.Unknown]: {
    text: 'Cannot tell',
    icon: 'help',
    long: 'Cannot tell, WHICH IS NOT THE SAME AS NOT LANDED. Nobody has answered the question - '
      + 'this is a missing answer rather than a negative one, and the work may be anywhere.',
  },
}

/**
 * WHAT THE SCREEN SHOWS ABOUT WHERE THE WORK IS, or null when there is nothing to show.
 *
 * NULL IN, NULL OUT, AND THAT IS NOT `unknown`. An item no dispatch has ever touched has no landing
 * question to answer, and the row draws no mark - exactly as it draws none for `inFlight: null`.
 * `unknown` is the answer when the question was asked and could not be answered, and it ALWAYS
 * returns a mark: hiding the row when nobody can tell is the one thing this must not do.
 *
 * AN UNRECOGNISED WORD IS READ AS `unknown`. The server may grow a fifth reading before this screen
 * does, and the safe default is the one that claims nothing. Falling through to `local` would have
 * an older client telling people their work was on one disk on no evidence at all.
 */
export function landedMark(landed: BacklogLanded | null | undefined): LandedMark | null {
  if (!landed) return null

  const state = landed.state in LANDING_READINGS ? landed.state : LandedStates.Unknown
  const reading = LANDING_READINGS[state]!

  // The server's own sentence goes on the end rather than replacing the headline: it says WHICH
  // branch and WHICH remote, which is the detail, and the headline is what the reading MEANS.
  const detail = (landed.detail ?? '').trim()

  return {
    state,
    text: reading.text,
    icon: reading.icon,
    title: detail === '' ? reading.long : `${reading.long} ${detail}`,
  }
}

/**
 * THE TEAM AN ITEM WAS DISPATCHED TO, as an id, or null when it never was.
 *
 * THIS IS THE COLUMN AND THE FILTER, and it is a DIFFERENT FACT from {@link teamLabel}'s link. The
 * link says who may SEE the item and is set by `--team` at `add`; this says who was given the work.
 * They can differ, they are not merged, and the row shows both - the column for this one, a small
 * marker for the link on the rare item that carries one.
 *
 * TWO SOURCES, IN PRIORITY ORDER, AND THE SECOND IS A FLOOR RATHER THAN THE ANSWER:
 *
 * - `dispatchedTeam` is the CURRENT dispatch whether or not its workflow is still open. It is what
 *   this column wants and it is OWED BY THE SERVER - `BacklogEndpoints.Render` has the latest
 *   dispatch in hand on the list path (`latest`) and does not yet put it on the wire, which is why
 *   the field is optional here rather than required.
 * - `inFlight` is the same team, but ONLY while the workflow is open - `BacklogInFlightState` drops
 *   a dispatch whose correlation has closed. So until the field above lands, an item dispatched and
 *   finished reads as never dispatched. That is a blank cell, not a wrong one, and it closes the
 *   moment the server sends the field.
 */
export function dispatchedTeamId(
  item: Pick<BacklogItemView, 'inFlight' | 'dispatchedTeam'>,
): string | null {
  return item.dispatchedTeam ?? item.inFlight?.teamId ?? null
}

/**
 * What the Team column renders: the dispatched team's name, or NOTHING.
 *
 * AN ITEM NEVER DISPATCHED SHOWS AN EMPTY CELL, not `(no team)`. `(no team)` is an answer about the
 * LINK and putting it in this column said "no team is working this" in words that mean something
 * else entirely.
 */
export function dispatchedTeamLabel(
  item: Pick<BacklogItemView, 'inFlight' | 'dispatchedTeam' | 'dispatchedTeamName'>,
): string {
  const id = dispatchedTeamId(item)

  if (id === null) return ''

  return item.dispatchedTeamName ?? item.inFlight?.teamName ?? id
}

/**
 * The Team filter's options, BUILT FROM THE ITEMS ON THE SCREEN.
 *
 * NOT FROM {@link teamOptions}, WHICH IS THE ADD DIALOG'S LIST. That list is every team on the
 * board, and the filter narrowed on `item.team` - so on an instance whose backlog is all tenant
 * items every option but `All` returned an empty table while the rows visibly named the teams
 * working them. An option that can only ever return nothing must not be offered.
 *
 * `null` IS "NOT DISPATCHED" and is offered only when such a row exists, by the same rule. It is
 * distinct from `undefined`, which the component uses for `All`.
 */
export function dispatchedTeamOptions(
  items: readonly BacklogItemView[],
): Array<{ label: string; value: string | null }> {
  const byId = new Map<string, string>()
  let anyUndispatched = false

  for (const item of items) {
    const id = dispatchedTeamId(item)

    if (id === null) {
      anyUndispatched = true
      continue
    }

    // LAST NAME WINS, which matters not at all when they agree and is the current label when they
    // do not - the same preference `BacklogInFlight.TeamName` documents.
    byId.set(id, dispatchedTeamLabel(item) || id)
  }

  const teams = [...byId.entries()]
    .map(([value, label]) => ({ label, value: value as string | null }))
    .sort((left, right) => left.label.localeCompare(right.label))

  return anyUndispatched ? [...teams, { label: 'Not dispatched', value: null }] : teams
}

/**
 * CROCKFORD'S BASE-32 ALPHABET: the digits and the letters, minus `I`, `L`, `O` and `U`.
 *
 * `I`, `L` and `O` are out because they are the glyphs people misread as `1`, `1` and `0` - and this
 * id is read aloud, typed from a screenshot and put in filenames. `U` is out so a generated id
 * cannot spell something unfortunate. Anybody "tidying" this back to a full 36 is undoing all four
 * on purpose.
 *
 * IT SORTS, AND THAT IS NOT A COINCIDENCE. The alphabet is a strict subset of `0-9A-Z` in ASCII
 * order - digits (48-57) before capitals (65-90) - and removing four letters leaves it monotonic,
 * so an UPPERCASE, ZERO-PADDED label sorts lexically in numeric order. Both halves are load-bearing:
 * lowercase (97-122) sorts AFTER `Z` and breaks it, and an unpadded `B100` sorts before `B17`.
 * Case is not cosmetic here.
 *
 * ORDER MATTERS. The character at index `n` encodes the digit `n`, which is what makes this one
 * constant the whole encoding. `PlatformBacklogId` on the server carries the same string.
 */
const CROCKFORD_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'

/**
 * FOUR CHARACTERS HOLD 1,048,576 IDS. An id above that takes five and is never truncated.
 *
 * THE SORT PROPERTY HOLDS WITHIN THE FOUR-CHARACTER RANGE, 1..1,048,575, AND NOT ACROSS THE WIDTH
 * BOUNDARY. The default string sort compares character by character, and `1` (49) comes before
 * `Z` (90), so `B10000` sorts BEFORE `BZZZZ` even though it is the larger id. That is accepted:
 * no platform id is anywhere near that size, and truncating to keep the width would be worse than
 * a label that is correct but out of order.
 */
const LABEL_WIDTH = 4

/**
 * `B000H` for 17, everywhere a person or an agent reads one. Never a bare integer.
 *
 * RENDERING ONLY. The API sends the integer and the route takes the integer; this is the one place
 * on the client that turns it into a label, and there is no parser on the client. The label is
 * the id in {@link CROCKFORD_ALPHABET}, uppercase, zero-padded to {@link LABEL_WIDTH}, so a list
 * of labels sorts as strings in the order of the integers behind them.
 */
/**
 * The rendered prefix, as a constant rather than inline in the template below, so it has one home
 * on the client - the counterpart of `PlatformBacklogId.Prefix` on the server. The client never
 * PARSES an id, so nothing else here depends on it.
 */
export const LABEL_PREFIX = 'B'

export function itemLabel(id: number): string {
  return `${LABEL_PREFIX}${crockford(id).padStart(LABEL_WIDTH, '0')}`
}

/** The bare base-32 digits of a non-negative integer, most significant first, no padding. */
function crockford(value: number): string {
  let rest = Math.trunc(value)
  let digits = ''

  do {
    digits = CROCKFORD_ALPHABET[rest % 32] + digits
    rest = Math.floor(rest / 32)
  } while (rest > 0)

  return digits
}

/**
 * What the in-flight mark on a backlog row SAYS: the team working it and the workflow, in the
 * vocabulary the rest of the product uses - `#1402` is what the dispatch notification said and what
 * `harness` takes.
 *
 * SHORT, because it sits UNDER THE TITLE on a phone where the table is already four columns wide.
 * A fifth column is ruled out for that reason; the mark goes where the row already has height to
 * give, and costs no width.
 */
export function inFlightText(flight: BacklogInFlight): string {
  return `${flight.teamName} · #${flight.correlation}`
}

/**
 * The long form, for a hover or a screen reader. RUNNING NOW AND OPEN-BUT-IDLE ARE TWO FACTS: a
 * workflow stays open until its Manager declares it or a person closes it, so an idle open workflow
 * is ordinary rather than a fault - but it is not the same thing as somebody running this second.
 */
export function inFlightTitle(flight: BacklogInFlight): string {
  const where = `In flight on ${flight.teamName}, workflow #${flight.correlation}.`

  return flight.running
    ? `${where} A member is running now.`
    : `${where} Open, nobody running at the moment.`
}

/**
 * The rows in the order the table should draw them.
 *
 * THE `#` IS COMPUTED HERE AND IS NEVER SENT, NEVER STORED, AND NEVER ADDRESSES ANYTHING. It is
 * `1..N` over the CURRENT order and renumbers as things move, which is exactly why the id beside it
 * carries a `B`: two bare integers on one row, one of which renumbers under you, is the confusion
 * this layout exists to prevent.
 *
 * The index follows POSITION order rather than the displayed sort, because that is what it means -
 * a person sorting by Modified still wants to know an item is 3rd in the backlog.
 */
export function numbered(
  items: readonly BacklogItemView[],
  sort: BacklogSortState,
): Array<BacklogItemView & { index: number }> {
  const byPosition = new Map(items.map((item, i) => [item.id, i + 1]))

  const sorted = [...items].sort((left, right) => compare(left, right, sort.by))

  if (sort.descending) sorted.reverse()

  return sorted.map((item) => ({ ...item, index: byPosition.get(item.id) ?? 0 }))
}

function compare(left: BacklogItemView, right: BacklogItemView, by: BacklogSort): number {
  switch (by) {
    case 'id':
      return left.id - right.id
    case 'title':
      return left.title.localeCompare(right.title)
    case 'team':
      // THE COLUMN'S OWN FACT, not the link. Sorting the Team column by something other than what
      // the Team column renders is a sort that reads as broken to the only person who can see it.
      return dispatchedTeamLabel(left).localeCompare(dispatchedTeamLabel(right))
    case 'state':
      return left.state.localeCompare(right.state)
    case 'created':
      return left.createdAt.localeCompare(right.createdAt)
    case 'updated':
      return left.updatedAt.localeCompare(right.updatedAt)
    case 'archived':
      // Null sorts last rather than throwing the row to the top. On the Archived tab it is never
      // null, so this arm only ever runs for a list that should not have been sorted by it.
      return (left.archivedAt ?? '').localeCompare(right.archivedAt ?? '')
    case 'position':
    default:
      // ALREADY IN POSITION ORDER - the server returns it that way, and re-deriving it from a
      // `position` field the wire does not carry is impossible by design. Stable, so the original
      // order survives.
      return 0
  }
}

/**
 * The neighbours a dropped row lands between, as ids.
 *
 * <p>
 * BOTH MAY BE NULL, AND THAT IS NOT AN ERROR: dropping at the top has no `after` and dropping at the
 * bottom has no `before`. The server takes them exactly this way.
 * </p>
 *
 * <p>
 * `to` IS THE INDEX THE ROW ENDS UP AT, in the list AFTER removal - which is what every drag
 * library reports and is the one place an off-by-one lives. Computing the neighbours here rather
 * than in a component is what lets that be tested without a DOM.
 * </p>
 */
export function neighboursFor(
  items: readonly BacklogItemView[],
  from: number,
  to: number,
): { after: number | null; before: number | null } {
  const without = items.filter((_, i) => i !== from)

  return {
    after: to > 0 ? (without[to - 1]?.id ?? null) : null,
    before: (without[to]?.id ?? null),
  }
}

/**
 * The teams a person may link an item to, with the tenant's own first.
 *
 * A TEAM IS CHOSEN FROM A PRE-MADE LIST AND NEVER TYPED. A free-text team on a create form is a
 * link to something that may not exist, checked by the server and refused - which is a worse
 * version of a picker that could not offer it in the first place.
 *
 * "(no team)" is offered to everyone: every person is an administrator, and the server accepts an
 * unlinked item from any of them.
 */
export function teamOptions(
  teams: ReadonlyArray<{ id: string; name: string }>,
): Array<{ label: string; value: string | null }> {
  const options = teams.map((team) => ({ label: team.name, value: team.id as string | null }))

  return [{ label: '(no team)', value: null }, ...options]
}

/**
 * The tokens line for a dispatch, with what is missing from it.
 *
 * A TOTAL ALONE LIES BY OMISSION, and freezing it would make the lie permanent. Runs whose Agent
 * reported nothing cannot be re-derived from an append-only log, so the count travels beside the
 * figure and is rendered rather than dropped.
 */
export function tokensLine(stats: {
  tokens: number
  runsWithUsage: number
  runsWithoutUsage: number
}): string {
  if (stats.runsWithUsage === 0 && stats.runsWithoutUsage === 0) return '(no runs)'

  // `(unknown)` IS A REAL STATE. Nothing measured means there is no number to show, and showing 0
  // would be inventing one.
  if (stats.runsWithUsage === 0) {
    return `(unknown) · ${stats.runsWithoutUsage} run${stats.runsWithoutUsage === 1 ? '' : 's'} reported nothing`
  }

  const total = stats.tokens.toLocaleString()

  return stats.runsWithoutUsage === 0
    ? `${total} tokens`
    : `${total} tokens · ${stats.runsWithoutUsage} run${stats.runsWithoutUsage === 1 ? '' : 's'} reported nothing`
}

/**
 * Elapsed and tokens in one line - the two figures that say whether a workflow was worth what it
 * cost, which is the success indicator this feature serves.
 *
 * ABSENT IS NOT ZERO. A workflow still running has no elapsed time, and `0s` would be a measurement
 * nobody made.
 */
export function efficiencyLine(stats: {
  elapsedSeconds?: number | null
  tokens: number
  runsWithUsage: number
  runsWithoutUsage: number
}): string {
  const cost = tokensLine(stats)

  if (stats.elapsedSeconds === null || stats.elapsedSeconds === undefined) return cost

  return `${clockFor(stats.elapsedSeconds)} · ${cost}`
}

/**
 * `Ns`, `Mm Ss`, or `Hh Mm Ss` - an hour bucket rather than three-and-more-digit minutes.
 *
 * THIS PLATFORM'S RUNS ARE BOUNDED BY AN IDLE CLOCK, NOT A WALL CLOCK, so a multi-hour workflow is
 * an ordinary shape here rather than a corner case, and `"125m 30s"` for a two-hour run reads as a
 * bug where `"2h 5m 30s"` reads as what happened.
 */
function clockFor(elapsedSeconds: number): string {
  const seconds = Math.round(elapsedSeconds)
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const secs = seconds % 60

  if (hours > 0) return `${hours}h ${minutes}m ${secs}s`

  return seconds < 60 ? `${seconds}s` : `${minutes}m ${secs}s`
}

/**
 * WORDS THAT COST CHARACTERS AND TELL NOBODY ANYTHING, dropped from a derived team name.
 *
 * A team name is read at a glance in a tab strip and is capped at 32 characters by
 * `ContainerId.MaxNameLength`. "a", "the" and "is" spend that budget without distinguishing one
 * team from another, so they go. Deliberately SHORT and deliberately not a real stopword list: the
 * aim is a readable default a person edits, not a search index.
 */
const NAME_STOPWORDS = new Set([
  'a', 'an', 'the', 'is', 'are', 'was', 'were', 'be', 'been', 'and', 'or', 'but', 'so', 'than',
  'that', 'this', 'these', 'those', 'it', 'its', 'in', 'on', 'of', 'to', 'for', 'with', 'from',
  'by', 'at', 'as', 'not', 'no', 'has', 'have', 'had', 'do', 'does', 'did', 'can', 'could',
  'should', 'would', 'will', 'what', 'which', 'when', 'into', 'about',
]);

/** `ContainerId.MaxNameLength` - restated here because the client cannot reference it. */
const MAX_TEAM_NAME = 32;

/**
 * THE DEFAULT NAME FOR A TEAM CREATED TO RUN ONE BACKLOG ITEM.
 *
 * DERIVED HERE AND SENT, never derived again on the server. The dialog must PREFILL this so a
 * person can edit it before committing, which means the rule has to run on the client - and a
 * second implementation behind the route would be two stores of one fact, free to disagree the
 * first time either one moved. The server's job is the different question of whether the name it
 * was handed is LEGAL, which `ContainerId.IsLegalName` already answers.
 *
 * THE ID LEADS. A team outlives the conversation that created it, and `b000q-` is the only thing
 * tying it back to the item it was made for. It is also what guarantees a legal, non-empty result
 * when the title yields nothing derivable - the same choice `ContainerId.DeriveName` makes when it
 * generates an identifier rather than refusing a name for its script.
 *
 * AND IT IS SPELLED BY {@link itemLabel}, NOT HERE. A raw `b${item.id}` would produce `b23` for
 * the item the Backlog screen calls `B000Q` - one module rendering one id two ways, so a person
 * could not match a team to its item without converting base 32 in their head. A constant, a regex
 * or a second copy of the alphabet would be that defect with more steps: `itemLabel` is the one
 * answer to "how is this id written", and this asks it.
 *
 * LOWERCASE, DELIBERATELY. Team ids fold case and every generated name in this product is
 * lowercase. The badge on the screen stays uppercase because padded uppercase is what sorts.
 *
 * TRUNCATION IS ON A WORD BOUNDARY, because a name cut mid-word reads as corruption rather than as
 * a default somebody can edit. The lead is FIVE characters, so the 32-character ceiling bites
 * early - the loop below drops whole words, which is pinned by a test.
 */
export function newTeamNameFor(item: { id: number; title: string }): string {
  const lead = itemLabel(item.id).toLowerCase();

  const words = (item.title ?? '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .split(' ')
    // LENGTH > 1, NOT > 0. Splitting on non-alphanumerics turns "doesn't" into `doesn` and `t`,
    // and a stray single letter in an identifier reads as damage rather than as a word.
    .filter((word) => word.length > 1 && !NAME_STOPWORDS.has(word));

  let name = lead;

  for (const word of words) {
    const next = `${name}-${word}`;

    if (next.length > MAX_TEAM_NAME) break;

    name = next;
  }

  return name;
}

/**
 * The largest document the Backlog's upload reads. A spec is a few kilobytes; a megabyte of text
 * is not a spec anybody will read, and the body is sent whole with every dispatch.
 */
export const MAXIMUM_DOCUMENT_BYTES = 1024 * 1024

/** What an uploaded document drafts: the new-item form's title and body, or why it cannot. */
export type DocumentDraft = { title: string; body: string } | { error: string }

/**
 * A NEW ITEM FROM A DOCUMENT, drafted the way a person would by hand: the first
 * level-one heading is the title (else the file name without its extension) and the document, in
 * full, is the body. It only drafts - the form opens with both filled in and the person saves it.
 *
 * Line endings become `\n` and a byte-order mark is dropped, so the stored body is the text and
 * not the editor it came from. A NUL character means the file is not text (a PDF, an image) and
 * is refused by name rather than pasted in as noise.
 */
export function itemFromDocument(fileName: string, text: string): DocumentDraft {
  if (text.includes('\u0000')) {
    return { error: `${fileName} is not a text document. Upload a Markdown or plain-text file.` }
  }

  const body = text.replace(/^﻿/, '').replace(/\r\n?/g, '\n')
  if (body.trim().length === 0) return { error: `${fileName} is empty.` }

  let fenced = false
  let heading: string | null = null
  for (const line of body.split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) {
      fenced = !fenced
      continue
    }
    const match = fenced ? null : /^#[ \t]+(.+?)[ \t#]*$/.exec(line)
    if (match) {
      heading = match[1]!.trim()
      break
    }
  }

  const title = (heading ?? fileName.replace(/\.[^.]+$/, '')).slice(0, MAXIMUM_BACKLOG_TITLE_LENGTH)
  return { title, body }
}
