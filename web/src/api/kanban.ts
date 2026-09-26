import { json, send } from './client'
import type { TeamId } from './types'

/**
 * The Kanban module's half of the API contract, as the browser sees it.
 *
 * A SEPARATE FILE FROM `client.ts` ON PURPOSE. The board is a module that plugs in: deleting this
 * file, `lib/kanban.ts`, `stores/kanban.ts` and the four `Kanban*.vue` components removes the
 * feature and leaves the console working. It borrows `send`/`json` from `client.ts` rather than
 * re-implementing them, so a 401 is still `Unauthorized` and a refusal still surfaces the server's
 * own `error` string instead of a bare status code.
 *
 * Paths are relative for the same reason the rest are: the dev server proxies them to the Host and
 * in production the Host serves this bundle, so one string is correct in both.
 */

/**
 * The seven states a card can be in, exactly as the design brief locks them.
 *
 * `completed` is the MEMBER finishing; `done` is the workflow being declared finished by the
 * Manager. They are two facts and the board shows both - a member that delivered and a workflow
 * that is over are not the same thing, and collapsing them is how a review lane empties itself.
 */
export type KanbanStatus =
  | 'queued'
  | 'running'
  | 'blocked'
  | 'needs-decision'
  | 'failed'
  /**
   * A WORKER'S OWN PART FINISHED — `agentContainer.handback`. A success, and named here
   * rather than left to fall through `colourFor`'s slate default: an unknown status renders a
   * colourless card with nothing failing, which is the silent drift this file's own comments warn
   * about. It shares `completed`'s colour and its lane because both say the work is done and
   * neither needs a person; what tells them apart is who is speaking, which the label carries.
   */
  | 'handback'
  | 'completed'
  | 'done'

export const KanbanStatuses: readonly KanbanStatus[] = [
  'queued',
  'running',
  'blocked',
  'needs-decision',
  'failed',
  'handback',
  'completed',
  'done',
]

export interface KanbanLane {
  id: string
  title: string

  /**
   * This lane's WIP limit, from `kanban.wipLimits`, or absent/null for none.
   *
   * The In Progress lane's limit is `wip.maxRunning` - the ledger's own - and the header reads it as
   * `running / limit`. Every other lane's is ADVISORY: the header turns amber over it, and nothing is
   * blocked, because a person moves cards there.
   */
  wipLimit?: number | null
}

/** One `container.progress` line, folded onto the card it belongs to. */
export interface KanbanProgressItem {
  at: string
  text: string
}

export interface KanbanCard {
  /** Derived from (tenant, correlationId, member), so it SURVIVES a replay. Never a fresh UUID. */
  id: string

  /** The seq of the tell that started the workflow. What a person can quote back to a manager. */
  workflowSeq: number

  team: string

  /**
   * Who is on this card NOW, or `null` when nobody is.
   *
   * NULLABLE, AND THE `body` PRECEDENT BELOW DOES NOT TRANSFER - that is the whole argument. `body`
   * is empty-never-null because a card whose instruction fits in its title has ONE state, so a null
   * check would be bought for nothing. A member has genuinely TWO: never assigned, and assigned.
   * Telling them apart is what lets the Todo lane render at all.
   *
   * It is the CURRENT assignment, not the history. A deleted member returns its cards to null; the
   * TRAIL is what still says who did what.
   */
  member: string | null

  /**
   * The backlog item this card is a piece of, or `null` for a card that belongs to none.
   *
   * The card names the item and the item never holds a list of cards - a list is a fact that has to
   * be rewritten every time it grows, and the message log is append-only.
   */
  item?: number | null

  /** The instruction's subject: at most 80 characters, and never marked with an ellipsis. */
  title: string

  /**
   * The rest of the instruction - what the title did not have room for.
   *
   * ALWAYS A STRING, `''` when the whole instruction fit in the title. The server writes empty
   * rather than omitting it, so nothing here needs an optional-chain for a field that is merely
   * empty. It is not a copy of the title either: the two are read together.
   */
  body: string

  status: KanbanStatus
  laneId: string

  /** The template's colour word for this status. Falls back to the status map when absent. */
  color?: string | null

  progress: KanbanProgressItem[]
  createdAt: string
  updatedAt: string

  /**
   * Somebody outside this team's containers touched this card - moved, edited or commented on it -
   * and no container of the team has answered yet. Rendered as a ring, not a colour.
   *
   * THE SERVER CLEARS IT AS WELL AS SETTING IT, and the browser must not invent either. An edit
   * wakes the team's Manager, whose answer is itself a card event; the projection reads the actor
   * off that row's source and lowers the ring. A client that only ever saw this go up would render
   * a card that had been answered as still waiting.
   */
  awaitingManager: boolean

  /**
   * This card's WORKFLOW is paused - it spent its per-workflow budget - so nothing will move this
   * card until a person resumes that workflow.
   *
   * A FLAG BESIDE `awaitingManager`, NOT A `status` VALUE. `status` is a per-member RUN outcome
   * that `container.started` resets; folding a pause into it would erase a member's own `blocked`
   * mark and would need a new lane and colour in every template. This is a fact about the card
   * that is not its run outcome, which is the shape the board already has.
   *
   * THE SERVER CLEARS IT AS WELL AS SETTING IT, from the `workflow.paused` and `workflow.resumed`
   * rows, and the browser must not invent either.
   */
  paused: boolean
}

/**
 * The filter set. Every field is optional and an empty string means "not filtered" - the query
 * builder drops both, so there is one answer to "unset" rather than two that differ by endpoint.
 *
 * THREE FIELDS, AND NO `workflow`, `from` OR `to`. `/api/kanban/board` does not
 * bind them, so a client that sent them would be narrowing nothing while looking like it narrowed
 * something. The UI and the route agree on this list; keeping them in step is what stops a filter
 * from failing silently.
 *
 * The free-text box on the bar is NOT a fourth field here, deliberately. It never reaches the
 * server, and a value that must not be sent has no business in the record that is serialised onto
 * the query string. It lives on the store as view state; see `cardsMatchingText` in `lib/kanban`.
 */
export interface KanbanFilters {
  team?: string
  member?: string
  status?: string
}

export interface KanbanBoard {
  lanes: KanbanLane[]
  cards: KanbanCard[]

  /** What the server understood the filter to be. Echoed back, so the UI can show the truth. */
  filters?: KanbanFilters
}

/**
 * One row of a card's activity trail - a message off the log, oldest-first.
 *
 * `text` is the projection's summary of that message: the instruction, the progress line, the
 * output, the comment, and the NOTE that went with a move or an edit. Everything else is tolerated
 * rather than required: this is the part of the contract most likely to gain fields, and a board
 * that threw on an unfamiliar trail row would be a board that goes blank when the backend improves.
 */
export interface KanbanTrailEntry {
  seq: number
  type: string
  source?: string | null
  occurredAt?: string | null
  text?: string | null
  payload?: string | null
}

/** Workflow, member, timestamps, log excerpt paths, artifact hints - plus anything else sent. */
export interface KanbanCardAttributes {
  workflowSeq?: number
  team?: string
  member?: string
  createdAt?: string
  updatedAt?: string
  logExcerpts?: string[]
  artifacts?: string[]
  [key: string]: unknown
}

export interface KanbanCardDetail extends KanbanCard {
  /**
   * This card's own messages, oldest-first - a story rather than a feed.
   *
   * OPTIONAL, THOUGH THE SERVER ALWAYS SENDS IT, so a card without one still parses; without a
   * trail the panel falls back to progress lines, and comments and notes are not shown. `trailRows` in `lib/kanban.ts` is the one place that absence is
   * turned into what to draw.
   */
  trail?: KanbanTrailEntry[]

  /** Not sent by the server yet - see the panel's own empty state. */
  attributes?: KanbanCardAttributes
}

/**
 * What an edit may change.
 *
 * THERE IS NO `instruction` FIELD. The server's edit request record has no such property, so JSON
 * binding would drop it. None is needed, because the edit itself is the instruction: a
 * `kanban.card.edited` message wakes the team's Manager, so saving a note IS how a person hands
 * something back from the board.
 */
export interface KanbanEditRequest {
  title?: string
  status?: string
  note?: string
}

/**
 * The query string for a board fetch, or '' when nothing is filtered.
 *
 * The ORDER is fixed rather than object-key order so two equal filters produce one URL - which is
 * what makes a cache, a test and a network log all able to compare them by string.
 */
export function kanbanQuery(filters: KanbanFilters): string {
  // `team` IS A FILTER, AND FIRST, BECAUSE THE BOARD IS NOT ADDRESSED UNDER A TEAM.
  //
  // On `/api/teams/{team}/kanban/board` (the route the CLI uses) the team is a path segment
  // `TeamGate` reads, and a `team=` beside it would be a second, UNGATED answer to the same
  // question - so this query is never built for that route.
  //
  // `GET /api/kanban/board` declares no
  // `{team}` and resolves the caller's effective teams itself, the way `/api/overview` and
  // `/api/teams/rollup` do - so there is no path segment for a query parameter to contradict, and
  // `team` can only narrow a set the server already bounded. Nothing here can widen anything.
  //
  // THE ORDER IS ALSO THE WHOLE LIST, which is what stops a removed parameter riding along: a key
  // that is not named here is not written, so a stray `workflow` or `from` left on an object by
  // some caller reaches no URL even though `encodeURIComponent` would happily have sent it.
  const order: (keyof KanbanFilters)[] = ['team', 'member', 'status']

  const parts = order
    .map((key) => [key, filters[key]] as const)
    .filter(([, value]) => typeof value === 'string' && value !== '')
    .map(([key, value]) => `${key}=${encodeURIComponent(value as string)}`)

  return parts.length === 0 ? '' : `?${parts.join('&')}`
}

/**
 * The board, across every team the caller reaches.
 *
 * IT TAKES NO TEAM ARGUMENT, deliberately. A team in the PATH would have to come from the
 * console's ACTIVE team, and the board would show whichever team the tab strip was on regardless
 * of the filter bar.
 *
 * The team travels as a filter to a route that has no path segment for it. An empty filter set
 * is the whole tenant, which is what this board has always been documented to be.
 */
export const getKanbanBoard = (filters: KanbanFilters = {}) =>
  json<KanbanBoard>(`/api/kanban/board${kanbanQuery(filters)}`)

/**
 * One card, under ITS OWN team.
 *
 * A card carries the team it belongs to, and every call below is addressed with that - never with
 * whatever team the console happens to have active. On a tenant-wide board those are routinely
 * different, and using the active one would 404 a card that is plainly on screen.
 */
export const getKanbanCard = (team: TeamId, id: string) =>
  json<KanbanCardDetail>(
    `/api/teams/${encodeURIComponent(team)}/kanban/cards/${encodeURIComponent(id)}`,
  )

export const moveKanbanCard = (team: TeamId, id: string, laneId: string, note?: string) =>
  write(`/api/teams/${encodeURIComponent(team)}/kanban/cards/${encodeURIComponent(id)}/move`, 'POST', {
    laneId,
    ...(note ? { note } : {}),
  })

export const editKanbanCard = (team: TeamId, id: string, changes: KanbanEditRequest) =>
  write(
    `/api/teams/${encodeURIComponent(team)}/kanban/cards/${encodeURIComponent(id)}/edit`,
    'POST',
    changes,
  )

export const commentKanbanCard = (team: TeamId, id: string, text: string) =>
  write(
    `/api/teams/${encodeURIComponent(team)}/kanban/cards/${encodeURIComponent(id)}/comment`,
    'POST',
    { text },
  )

/**
 * A write, with no body expected back.
 *
 * `send` rather than `json` because these append a message and may answer 204 - and calling
 * `.json()` on an empty body throws a SyntaxError, which would surface a successful append as a
 * parse failure. The caller refetches the board; it does not read a response shape that the
 * contract does not pin down.
 */
async function write(path: string, method: 'POST' | 'PUT', body: unknown): Promise<void> {
  await send(path, {
    method,
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })
}
