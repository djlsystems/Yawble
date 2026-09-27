import { diagnosticsQuery } from '../lib/diagnostics'
import type { BuildInfo } from '../lib/buildInfo'
import { normaliseTenantSettings, rejectedField } from '../lib/tenantSettings'

/** Points the Concierge at the workflow the person is looking at. Advisory: a failure here
 * does not change the board. */
export function publishSteering(correlationId: number | null): void {
  void fetch('/api/me/steering', {
    method: 'PUT',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ correlationId }),
  }).catch(() => undefined);
}
import type {
  Agent,
  AgentAuthReport,
  BacklogDispatchView,
  BacklogExecutionStats,
  BacklogItemView,
  ConciergeSettings,
  ApiKey,
  Catalog,
  CreateTriggerRequest,
  CliVersionsAtStart,
  DiagnosticsFilter,
  DiagnosticsView,
  DirectoryListing,
  EventDefinition,
  FolderTestRequest,
  FolderTestResult,
  WatchRootOption,
  MemberId,
  MemberRunsPage,
  PluginHire,
  PluginList,
  ContainerSnapshot,
  MemberDeleted,
  MemberDetail,
  Message,
  MintedKey,
  Overview,
  RootListing,
  SkillDraft,
  SkillKindFilter,
  SkillRecord,
  TeamTrigger,
  Team,
  TeamCloned,
  TeamDeleted,
  TeamId,
  TeamResetRequest,
  TeamRollup,
  TeamTokenTotals,
  TeamWasReset,
  TeamWorkflowTiming,
  TeamWorkflows,
  TenantApiKey,
  TenantLogPage,
  TeamRepoStatus,
  RepoActionResult,
  PullRequestDraft,
  ForkResult,
  UpdateTriggerRequest,
  TenantSettings,
  WipView,
} from './types'

export type { BacklogDispatchView, BacklogExecutionStats, BacklogItemView } from './types'

/**
 * Every call the browser makes, in one place.
 *
 * Paths are relative on purpose: in dev the Quasar dev server proxies them to the
 * Host, and in production the Host serves this bundle itself, so the same string
 * is correct in both modes and there is no base URL to configure or get wrong.
 */

/** A 401. Distinguished from any other failure because being signed out is an ordinary state,
 *  not an error to show someone. */
export class Unauthorized extends Error {
  constructor() {
    super('Not signed in.')
    this.name = 'Unauthorized'
  }
}

/** A team deletion that needs an exact second confirmation because it would discard local-only commits. */
export class TeamDeletionConfirmationRequired extends Error {
  confirmation: string
  losses: string[]

  constructor(message: string, confirmation: string, losses: string[]) {
    super(message)
    this.name = 'TeamDeletionConfirmationRequired'
    this.confirmation = confirmation
    this.losses = losses
  }
}

/**
 * A call that must succeed, with no body expected back. Split out of `json` so that an endpoint
 * answering 204 can still be checked - `response.json()` on an empty body throws a SyntaxError,
 * which would surface a successful call as a parse failure.
 *
 * EXPORTED so a module that keeps its own endpoints in its own file - `api/kanban.ts` - reuses this
 * one convention rather than growing a second fetch helper. A second one would answer a 401 with a
 * different type and a refusal with a status code instead of the server's own `error` string, and
 * the two would drift the first time either changed.
 */
/**
 * A refusal with the server's whole JSON body kept, for a caller that needs more than the sentence -
 * the Git dialog reads a conflicting rebase's `conflicts` list off it. Its message is the `error`
 * field, exactly as the plain Error this replaces carried, so every existing reader is unchanged.
 */
export class ActionRefused extends Error {
  constructor(message: string, readonly body: Record<string, unknown>) {
    super(message)
  }
}

export async function send(path: string, init?: RequestInit): Promise<Response> {
  const response = await fetch(path, init)

  if (!response.ok) {
    if (response.status === 401) throw new Unauthorized()

    // The API answers a refusal with a JSON body carrying `error` - a duplicate
    // team name, an unknown container. Surfacing that beats a bare status code,
    // which tells the person nothing they can act on.
    const body = await response.text()
    let detail = body

    let parsedBody: Record<string, unknown> | null = null

    try {
      const parsed = JSON.parse(body) as { error?: string }
      if (parsed && typeof parsed === 'object') parsedBody = parsed as Record<string, unknown>
      if (parsed.error) detail = parsed.error
    } catch {
      // Not JSON. The raw body, or the status, is the best we have.
    }

    // The status rides along so a dialog can tell a 409 on a name from any other refusal and mark
    // the field; the message stays the server's own words. Read it with `refusalStatus`.
    const message = detail || `${response.status} ${response.statusText}`
    throw Object.assign(parsedBody ? new ActionRefused(message, parsedBody) : new Error(message), {
      status: response.status,
    })
  }

  return response
}

/** The reading half. Exported for the same reason `send` above is. */
export async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await send(path, init)

  return (await response.json()) as T
}

export const getOverview = () => json<Overview>('/api/overview')

/**
 * Ends the run a member currently has in flight. The member stays; only this invocation goes.
 *
 * `stopped: false` is a SUCCESS, not a failure — by the time the click reaches the server the run
 * may have finished on its own, and a person who clicked Stop on a card that had just gone idle has
 * not done anything wrong. Callers should read the flag rather than the status code.
 */
export const stopRun = (team: TeamId, name: MemberId) =>
  json<{ stopped: boolean; message: string }>(
    `/api/teams/${encodeURIComponent(team)}/containers/${encodeURIComponent(name)}/stop`,
    { method: 'POST' },
  )

/**
 * What `openLiveView` found: either a stream of display lines, or the server's one sentence for why
 * there is nothing to watch. A 404 (the member is not running) is thrown by `send` with that
 * sentence as its message, exactly like every other refusal.
 */
/** What the dialog says when the Host gives no reason of its own, so the box is never blank. */
export const NoLiveView = 'This member has no live view.'

export type LiveView =
  | { live: false; reason: string }
  | { live: true; lines: AsyncGenerator<string> }

/**
 * The live view of one member's run. The server answers JSON `{live:false, reason}` when the
 * member's preset has no live view, and otherwise `text/plain` lines that end when the run does. The
 * lines are already readable; nothing here reformats them. Abort `signal` to stop reading.
 */
export async function openLiveView(team: TeamId, member: MemberId, signal?: AbortSignal): Promise<LiveView> {
  const response = await send(
    `/api/teams/${encodeURIComponent(team)}/members/${encodeURIComponent(member)}/live`,
    { signal: signal ?? null },
  )

  if ((response.headers.get('content-type') ?? '').includes('application/json')) {
    const body = (await response.json()) as { live?: boolean; reason?: string }
    return { live: false, reason: body.reason || NoLiveView }
  }

  return { live: true, lines: readLines(response) }
}

/**
 * A member's finished runs, newest first, one page of 20. `before` is the previous page's
 * `nextBefore`; leave it out for the newest page.
 */
export const listMemberRuns = (team: TeamId, member: MemberId, before?: number | null) =>
  json<MemberRunsPage>(
    `/api/teams/${encodeURIComponent(team)}/members/${encodeURIComponent(member)}/runs` +
      (before == null ? '' : `?before=${before}`),
  )

/**
 * One finished run's whole transcript: the same `<when>` TAB `<line>` display lines the live
 * route streams, read to the end. A transcript that is gone is a 410, thrown by `send` with the
 * server's sentence as its message.
 */
export async function readRunTranscript(team: TeamId, member: MemberId, seq: number): Promise<string[]> {
  const response = await send(
    `/api/teams/${encodeURIComponent(team)}/members/${encodeURIComponent(member)}/runs/${seq}/transcript`,
  )

  return (await response.text()).split('\n').map((line) => line.replace(/\r$/, '')).filter((line) => line !== '')
}

/**
 * Splits a streamed body on newlines, yielding each COMPLETE line as it arrives. A chunk can end
 * mid-line, and a multi-byte character can straddle two chunks - the decoder's `stream: true` and
 * the carried `pending` are what keep both whole. A last line without its newline is still yielded.
 */
export async function* readLines(response: Response): AsyncGenerator<string> {
  if (!response.body) {
    const text = await response.text()
    for (const line of text.split('\n')) if (line !== '') yield line
    return
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let pending = ''

  try {
    for (;;) {
      const { done, value } = await reader.read()
      if (done) break

      pending += decoder.decode(value, { stream: true })

      let cut = pending.indexOf('\n')
      while (cut >= 0) {
        yield pending.slice(0, cut).replace(/\r$/, '')
        pending = pending.slice(cut + 1)
        cut = pending.indexOf('\n')
      }
    }

    pending += decoder.decode()
    if (pending !== '') yield pending
  } finally {
    reader.releaseLock()
  }
}

/** Messages after `after`. Bounded by the caller's cursor, never a full replay. */
/**
 * The team's recent activity, newest-first window.
 *
 * `take` is the VIEWER'S choice of how far back to look - see `lib/boardSize`. The server clamps it,
 * so passing a silly number is the server's problem rather than a thing to guard twice.
 */
export const getMessages = (after: number, take?: number) =>
  json<Message[]>(
    `/api/messages?after=${after}` + (take ? `&take=${take}` : ''),
  )

/**
 * HISTORY: ONE MEMBER's messages with `seq < before` - what it was told and what it published -
 * NEWEST FIRST, the opposite of the forward `after=` window. `team`, `member` and `before` are
 * required together and never mixed with `after` (400 otherwise). An empty page is the end: the
 * first message the member received.
 */
export const getMessagesBefore = (team: string, member: string, before: number, take = 200) =>
  json<Message[]>(
    `/api/messages?team=${encodeURIComponent(team)}&member=${encodeURIComponent(member)}&${cursorQuery(before, take)}`,
  )

/**
 * Token in/out totals across every completed/failed run this team published.
 *
 * A TEAM TOTAL from the message log. Not a sum of the twenty messages a card still holds.
 */
export const getTeamTokens = (team: TeamId) =>
  json<TeamTokenTotals>(`/api/teams/${encodeURIComponent(team)}/tokens`)

/**
 * How long this team's current — or last — workflow has taken.
 *
 * A LOG PROJECTION, the sibling of `getTeamTokens` above and fetched the same way. It returns
 * INSTANTS rather than a computed elapsed number, so the tile counts up with no further traffic;
 * the store keeps the offset between `serverNow` and this browser's clock for exactly that.
 */
export const getTeamWorkflow = (team: TeamId) =>
  json<TeamWorkflowTiming>(`/api/teams/${encodeURIComponent(team)}/workflow`)

/**
 * EVERY workflow this team has run since its floor, at once — OPEN AND CLOSED ALIKE, newest first
 * and capped at fifty — plus `openCount`, `totalCount` and one span over the open ones. The plural
 * sibling of `getTeamWorkflow` above, from `GET /api/teams/{team}/workflows`. The singular route
 * still exists and `getTeamWorkflow` still uses it; this is a second read, not a replacement.
 *
 * IT RETURNS CLOSED WORKFLOWS TOO. A caller wanting the open work must filter on
 * `endedAt === null` — see `soleOpenSpend` in `TeamKpiStrip.vue`, where assuming otherwise would
 * cost a budget bar — and must compare `workflows.length` against `totalCount`,
 * never `openCount`, to tell truncation.
 */
export const getTeamWorkflows = (team: TeamId) =>
  json<TeamWorkflows>(`/api/teams/${encodeURIComponent(team)}/workflows`)

/**
 * Every reachable team's workflow projection, in one read. Fetched only while the Teams table is
 * showing - see `pullRollup` in the console store.
 */
export const getTeamsRollup = () => json<TeamRollup>('/api/teams/rollup')

/**
 * A PERSON'S declaration that one workflow should stop counting as open - NOT the same as a
 * manager's own `workflow-complete`. `reason` is genuinely optional on the wire, matching the
 * route's own refusal to require one; the CLIENT prefills a suggestion from what the platform
 * already knows (`closeReasonPrefill` in `lib/teamKpis.ts`) so a one-click submission still
 * produces a row that accounts for itself - the route's own comment cites exactly this mitigation.
 *
 * 204, so `send()`, never `json<T>()` - that helper calls `response.json()` unconditionally and
 * would report every successful close as a failure while the row was written.
 */
export const closeWorkflow = async (
  team: TeamId,
  correlation: number,
  reason?: string,
): Promise<void> => {
  await send(
    `/api/teams/${encodeURIComponent(team)}/workflows/${correlation}/close`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ reason }),
    },
  )
}

/**
 * Wakes this team's Manager under one workflow, with THAT THREAD'S OWN HISTORY as causation rather
 * than the head of a new job - the one affordance a workflow nobody is currently inside would
 * otherwise lack, since it could then only be closed and never picked back up. See the route's own
 * comment for why the causation is never caller-suppliable.
 *
 * TWO SHAPES, AND THE SECOND ONE IS THE WHOLE REASON THIS DOES NOT RETURN `void`. Ordinarily 204.
 * When the team is PAUSED the route answers 200 carrying `pauseNotice` - the instruction was
 * logged and will run after resume - and a caller that discards that reports a queued nudge as a
 * completed one. That is the failure a pause is most likely to produce: a 200, a row, and nothing
 * happening, on the one screen where the person cannot see the queue for themselves.
 *
 * `send()` either way: it never parses a body, so it handles both without a 204 becoming a parse
 * failure. The read is done HERE, guarded, because a 204 has no body to read at all.
 */
export const nudgeWorkflow = async (
  team: TeamId,
  correlation: number,
): Promise<string | null> => {
  const response = await send(
    `/api/teams/${encodeURIComponent(team)}/workflows/${correlation}/nudge`,
    { method: 'POST' },
  )

  if (response.status === 204) return null

  // A renderer over a wire payload does not get to assume the sender's nullability, and this one
  // cannot even assume a body: a 200 from a future arm of this route need not carry the field.
  try {
    const body = (await response.json()) as { pauseNotice?: string | null }
    return typeof body.pauseNotice === 'string' && body.pauseNotice.length > 0
      ? body.pauseNotice
      : null
  } catch {
    return null
  }
}

/**
 * Lets ONE paused workflow run again - the workflow that spent its per-workflow budget, and no
 * other. Its siblings on the same team were never stopped and are not touched.
 *
 * NOT `resumeTeam`. That clears a whole-TEAM pause and would start three workflows because one
 * was stopped. This is per correlation, and the route is `.HumansOnly()` for the reason the nudge
 * route is: resuming restarts the spend window, so a runaway must not be able to rescue itself.
 *
 * THE SAME TWO SHAPES AS `nudgeWorkflow`, and for the same reason it does not return `void`.
 * Ordinarily 204. When the TEAM is paused the route answers 200 carrying `pauseNotice` - the
 * instruction was logged and will run once the team resumes - and a caller that discards that
 * reports a queued resume as a completed one, on the one screen where the person cannot see the
 * queue for themselves.
 *
 * `send()` either way: it never parses a body, so a 204 does not become a parse failure. The read
 * is done HERE, guarded, because a 204 has no body to read at all.
 */
export const resumeWorkflow = async (
  team: TeamId,
  correlation: number,
): Promise<string | null> => {
  const response = await send(
    `/api/teams/${encodeURIComponent(team)}/workflows/${correlation}/resume`,
    { method: 'POST' },
  )

  if (response.status === 204) return null

  // A renderer over a wire payload does not get to assume the sender's nullability, and this one
  // cannot even assume a body: a 200 from a future arm of this route need not carry the field.
  try {
    const body = (await response.json()) as { pauseNotice?: string | null }
    return typeof body.pauseNotice === 'string' && body.pauseNotice.length > 0
      ? body.pauseNotice
      : null
  } catch {
    return null
  }
}

/**
 * Ends the run of every member currently Running UNDER THIS WORKFLOW, leaving every other member -
 * including one Running under a different workflow on the same team - alone. The correlation-scoped
 * sibling of `stopRun` above, which stops one named member regardless of what it is running.
 *
 * 204, so `send()`.
 */
export const stopWorkflow = async (team: TeamId, correlation: number): Promise<void> => {
  await send(
    `/api/teams/${encodeURIComponent(team)}/workflows/${correlation}/stop`,
    { method: 'POST' },
  )
}

/**
 * `name` is what a PERSON typed — free text. The server derives the identifier from it and answers
 * with the whole team, which is the only place the caller can learn what that identifier is: it is
 * never asked for and never shown, but `setActiveTeam` and the console binding both need it.
 *
 * THERE IS NO CONCIERGE PARAMETER, and its absence is deliberate rather than an omission. The
 * Concierge is the TENANT's, so a parameter here would let making a team silently repoint the
 * Concierge for every team that already exists. There is one Concierge for the instance and one
 * route that sets it, `setConcierge` below.
 *
 * `root` is genuinely optional: omitted (`undefined`, which `JSON.stringify` drops) means the box
 * was never touched, so the server stores NULL and the team follows the instance root if it ever
 * moves. Sending `""` or a resolved default here would be a second, client-side answer to what
 * "unset" means.
 */
export const createTeam = (
  name: string,
  agent: string,
  memberAgents?: string[],
  root?: string,
  repos?: string[],

  /**
   * Tokens for ONE workflow, input and output together - this team's own choice, stored exactly as
   * typed. `null` (or omitted) means the team chooses nothing and runs on the instance's
   * `WorkflowSpendLimit`; `0` means it explicitly chooses unlimited. The two are NOT the same
   * answer and neither is collapsed into the other on the way out.
   *
   * IN THIS REQUEST, NEVER A FOLLOW-UP PUT. A second call leaves a window where the team exists on
   * a figure nobody chose, and the chosen one is silently lost if that call never lands.
   */
  budgetTokens?: number | null,

  /**
   * How many of this team's workers may run at once, besides the Manager. Null or 0 means the
   * team has chosen nothing. Applied server-side right after creation, the same one-request shape
   * the create call already has - never a follow-up `PATCH .../max-concurrent` from here,
   * which would leave a window where the team exists on the tenant default and a chosen number is
   * silently lost if that second call never lands.
   */

  /**
   * Words appended after the built-in role prompt for this team's Manager and members. Omitted when
   * blank: the server stores NULL and nothing is appended. Never a replacement for the role prompt.
   */
  additionalInstructions?: string,

  /**
   * Each contributed repository's upstream, keyed by its URL in `repos`. In this request,
   * so the clone is made with its `upstream` remote. Omitted when empty.
   */
  upstreams?: Record<string, string>,
) =>
  json<Team>('/api/teams', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },

    // What its Manager runs and what its members may run. What either is TOLD is not a choice: the
    // role prompt comes from the build, and `additionalInstructions` is only ever appended.
    // `root` is genuinely optional: omitted, the team follows the instance root wherever it goes.
    body: JSON.stringify({
      name,
      agent,
      memberAgents,
      root,
      repos,
      budgetTokens,
      additionalInstructions: additionalInstructions?.trim() || undefined,
      upstreams: upstreams && Object.keys(upstreams).length > 0 ? upstreams : undefined,
    }),
  })

/**
 * Clones a team. `name` is what a PERSON typed; the server derives the identifier from it and
 * refuses a collision with 409 naming the existing team.
 *
 * `json<T>()`, not `send()`: 201 with what it carried, for the same reason `deleteTeam` reads its
 * body - a member it could not hire is named rather than swallowed, and the team exists either way.
 */
export const cloneTeam = (team: TeamId, name: string) =>
  json<TeamCloned>(`/api/teams/${encodeURIComponent(team)}/clone`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ name }),
  })

/**
 * The Host folder picker's four routes. Gated by `.HumansOnly()` alone —
 * any signed-in person may call these, and CONTAINMENT against the configured allowlist
 * (`FileBrowser:Roots`) is the entire security model, enforced server-side on every path. There is
 * nothing for the client to check before calling; a refusal comes back as a readable message
 * through `send()`'s error handling and is rendered rather than swallowed.
 */

/** The allowlisted folders the picker may open, and what it may do inside each one. */
export const fileSystemRoots = () => json<RootListing>('/api/fs/roots')

/**
 * Lists a path on the HOST's own filesystem — backs the folder picker for `root` above and for
 * `HostPathPicker.vue` generally. A non-null `parent` in the response is guaranteed itself
 * browsable; `null` means the path listed is the top of its root, and an "up" affordance must hide
 * rather than offer a click the server will refuse.
 */
export const browseFileSystem = (path: string) =>
  json<DirectoryListing>(`/api/fs/browse?path=${encodeURIComponent(path)}`)

/** Creates one directory on the Host's own filesystem. Refused with 403 unless the containing
 *  root's `allowCreate` is set — which the shipped default (the instance's own data root, read
 *  only) never is, so this reaches nothing until an operator configures a writable root. */
export const createHostDirectory = (parent: string, name: string) =>
  json<{ path: string }>('/api/fs/directory', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ parent, name }),
  })

/**
 * Uploads one file to a folder on the Host's own filesystem. Same `allowCreate` gate as
 * `createHostDirectory` above.
 *
 * No content-type header: the browser sets it, and the multipart boundary it generates is part of
 * that value. Setting it by hand produces a body the server cannot parse — the same rule
 * `uploadDocument` in `api/documents.ts` already follows for team documents.
 */
export const uploadHostFile = (parent: string, file: File) => {
  const form = new FormData()
  form.append('file', file)
  form.append('parent', parent)

  return json<{ path: string }>('/api/fs/upload', { method: 'POST', body: form })
}

/**
 * Sets what a team may spend on ONE workflow, in tokens, input and output together.
 *
 * NULL IS UNLIMITED and so is 0 — the server stores both as null, so there is one spelling of
 * "no bound" on the wire and no caller has to know two.
 */

/** Replaces a team's ordered Git repository URL list. */
export const setTeamRepos = (team: TeamId, repos: string[]) =>
  json<Team>(`/api/teams/${encodeURIComponent(team)}/repos`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(repos),
  })

/**
 * Set a repository's default branch, or clear it with null so the branch origin's HEAD
 * named is used again. Kept across every Fetch until cleared.
 */
export const setRepoDefaultBranch = (team: TeamId, repo: string, branch: string | null) =>
  json<Team>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/default-branch`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ branch }),
    },
  )

/**
 * A repository's contributor settings. An upstream URL puts it in contributor mode (the clone
 * gets an `upstream` remote; null removes it and nothing else). Turning `dcoSignOff` on is refused
 * with 409 when the instance has no git identity. Answers the team.
 */
export const setRepoContributor = (
  team: TeamId,
  repo: string,
  settings: {
    upstreamUrl: string | null
    forkOwner: string | null
    dcoSignOff: boolean
    claSignedNote: string | null
  },
) =>
  json<Team>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/contributor`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(settings),
    },
  )

/**
 * What this team may spend on ONE workflow, stored exactly as it was typed.
 *
 * ITS OWN ROUTE rather than folded into a wider team write, mirroring `setMemberAgents`: a caller
 * changing the budget should not have to resend unrelated fields.
 *
 * `null` AND `0` ARE DIFFERENT ANSWERS and both are sent as given - `null` is "this team chooses
 * nothing" (the instance figure applies) and `0` is "this team chooses unlimited". Collapsing
 * 0 to null at the write would, under the one-of rule, silently turn a person's unlimited into
 * the instance figure, so nothing is normalised on the way out.
 *
 * NOTHING IS CLAMPED HERE EITHER. A figure above the instance's own `WorkflowSpendLimit` is sent
 * as typed. The route refuses a NEGATIVE with 400,
 * which is a refusal a person can read rather than a number quietly changed under them.
 *
 * Answers the whole `TeamSummary`, which is how the caller learns what the server resolved.
 */
export const setTeamWorkflowBudget = (team: TeamId, budgetTokens: number | null) =>
  json<Team>(`/api/teams/${encodeURIComponent(team)}/budget`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ budgetTokens }),
  })

/**
 * The words appended after the built-in role prompt for this team's Manager and members. ITS OWN
 * ROUTE, mirroring `setTeamWorkflowBudget`. An empty string clears it, and then nothing is appended;
 * it never replaces the role prompt, which no person chooses.
 */
export const setTeamAdditionalInstructions = (team: TeamId, additionalInstructions: string) =>
  json<Team>(`/api/teams/${encodeURIComponent(team)}/additional-instructions`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ additionalInstructions: additionalInstructions.trim() || null }),
  })

/** Stops new work being delivered to this team until somebody resumes it. */
export const pauseTeam = (team: TeamId) =>
  send(`/api/teams/${encodeURIComponent(team)}/pause`, { method: 'POST' })

/** Lets this team start receiving new work again. */
export const resumeTeam = (team: TeamId) =>
  send(`/api/teams/${encodeURIComponent(team)}/resume`, { method: 'POST' })

/** What `GET /api/backlog/{id}` answers: the item, its dispatches, and what each one did. */
export interface BacklogItemDetail {
  item: BacklogItemView
  dispatches: BacklogDispatchView[]
  stats: BacklogExecutionStats[]
}

/**
 * The backlog, or the archive. `archived` is what decides which - never the item's state.
 *
 * `page` is for the archive only: ids below `before`, newest first. The backlog itself is
 * one ordered list a person drags, and is always read whole.
 */
export const backlogItems = (archived = false, page?: { before?: number | undefined; take: number }) =>
  json<BacklogItemView[]>(
    `/api/backlog?archived=${archived ? 'true' : 'false'}` + (page ? `&${cursorQuery(page.before, page.take)}` : ''),
  )

export const backlogItem = (id: number) => json<BacklogItemDetail>(`/api/backlog/${id}`)

export const createBacklogItem = (body: { title: string; body?: string; team?: string | null }) =>
  json<BacklogItemView>('/api/backlog', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })

export const updateBacklogItem = (
  id: number,
  body: { title?: string; body?: string; state?: string },
) =>
  json<BacklogItemView>(`/api/backlog/${id}`, {
    method: 'PATCH',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })

/**
 * `after` and `before` are the ids of the item's NEW NEIGHBOURS; either may be absent for an end.
 * The item lands at their midpoint, so a reorder writes ONE row rather than renumbering the list.
 *
 * 204, so `send()` - calling this through `json<T>()` would report every successful move as a
 * failure while the write landed.
 */
export const moveBacklogItem = (id: number, after: number | null, before: number | null) =>
  send(`/api/backlog/${id}/position`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ after, before }),
  })

export const archiveBacklogItem = (id: number) =>
  send(`/api/backlog/${id}/archive`, { method: 'POST' })

export const restoreBacklogItem = (id: number) =>
  send(`/api/backlog/${id}/restore`, { method: 'POST' })

/** Permanent, and the server refuses it for an item that is not archived. */
export const deleteBacklogItem = (id: number) =>
  send(`/api/backlog/${id}`, { method: 'DELETE' })

/** Hands an item to a team. Answers the correlation of the workflow it started. */
export const dispatchBacklogItem = (team: TeamId, id: number) =>
  json<{ correlation: number; dispatch: number }>(
    `/api/teams/${encodeURIComponent(team)}/backlog/${id}/dispatch`,
    { method: 'POST' },
  )

/**
 * Hands an item to a team CREATED FOR IT.
 *
 * `settings` are the values this browser remembers from the last team the person made - the same
 * set the New Team dialog fills itself from, so both paths produce the same kind of team. Nothing
 * is defaulted behind the route: a missing Agent is refused BY NAME.
 *
 * It does not clone an existing team: cloning needs a team to already exist - useless on an
 * instance with none - and inherits that team's ROSTER.
 */
export const dispatchBacklogItemToNewTeam = (
  id: number,
  name: string,
  settings: {
    agent: string | null
    memberAgents: string[]
    root: string | null
    repos: string[]
  },
) =>
  json<{ team: string; teamName: string; correlation: number; dispatch: number }>(
    `/api/backlog/${id}/dispatch-to-new`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ name, ...settings }),
    },
  )

/**
 * A team's own named values - its shared test credentials and anything else every member must
 * agree on.
 *
 * HUMANS ONLY, on both verbs. A member that could read these would enumerate values it was never
 * given; one that could write them would overwrite the team's credentials. Every agent runs with
 * permissions bypassed, so that marker is the whole of the restriction.
 */
export const getTeamEnv = (team: TeamId) =>
  json<Record<string, string>>(`/api/teams/${encodeURIComponent(team)}/env`)

/** Replaces a team's environment wholesale; an empty object clears it. */
export const setTeamEnv = (team: TeamId, env: Record<string, string>) =>
  json<Record<string, string>>(`/api/teams/${encodeURIComponent(team)}/env`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(env),
  })

/**
 * The tenant log, newest first, one page at a time. Human-only.
 *
 * KEYSET, NOT OFFSET: the rows with `seq < before`, or the newest when `before` is omitted.
 * Rows are only ever appended at the top, so a cursor never shifts under a reader the way `skip` did.
 * `total` is still answered, for the caption; the next cursor is the last row's `seq`.
 */
export const readTenantLog = (before?: number, take = 50) =>
  json<TenantLogPage>(`/api/tenant-log?${cursorQuery(before, take)}`)

/** `before=&take=`, with `before` omitted for the top page. There is no `skip` parameter. */
export function cursorQuery(before: number | undefined, take: number): string {
  return (before === undefined ? '' : `before=${before}&`) + `take=${take}`
}

/**
 * WHO HOLDS THE INSTANCE-WIDE WIP SLOTS AND WHO IS WAITING FOR ONE. Read by `stores/wip.ts` and
 * nothing else, so the header strip, the board and the Teams table show one copy.
 */
export const getWip = () => json<WipView>('/api/wip')

/** Every instance-wide setting, with where each value came from, and the file-browser roots. */
export const getTenantSettings = async (): Promise<TenantSettings> =>
  normaliseTenantSettings(await json<unknown>('/api/tenant/settings'))

/**
 * A 400 from `PUT /api/tenant/settings`: the server names the setting it refused, and the dialog
 * puts `message` under that field rather than in a toast about the whole form.
 */
export class TenantSettingRejected extends Error {
  field: string | null

  constructor(message: string, field: string | null) {
    super(message)
    this.name = 'TenantSettingRejected'
    this.field = field
  }
}

/** Saves the settings that moved - a partial map keyed by setting name. Human-only. */
export async function saveTenantSettings(changes: Record<string, unknown>): Promise<void> {
  const init: RequestInit = {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(changes),
  }

  const response = await fetch('/api/tenant/settings', init)

  if (response.status === 400) {
    const text = await response.text()
    let message = text
    let field: string | null = null

    try {
      const parsed = JSON.parse(text) as { error?: string; field?: string; name?: string }
      message = parsed.error ?? text
      field = parsed.field ?? parsed.name ?? null
    } catch {
      // Not JSON. The raw body is the message.
    }

    throw new TenantSettingRejected(message || '400 Bad Request', rejectedField(field, message))
  }

  if (!response.ok) {
    if (response.status === 401) throw new Unauthorized()
    const text = await response.text()
    let detail = text
    try {
      detail = (JSON.parse(text) as { error?: string }).error ?? text
    } catch {
      // Not JSON.
    }
    throw new Error(detail || `${response.status} ${response.statusText}`)
  }
}

/**
 * The diagnostics log — what this instance was doing when it went wrong. Human-only, and never a
 * machine principal: the rows name routes, exception types and internal paths from every corner of
 * the instance, and an agent that can read the platform's internal failures is an agent that can be
 * told about other teams' work.
 *
 * Answers the page AND `hasAny`, which is the single fact that tells an empty grid apart from a
 * broken store. See `lib/diagnostics.ts` for the four answers that come out of it.
 */
export const readDiagnostics = (filter: DiagnosticsFilter = {}, before?: number, take = 50) =>
  json<DiagnosticsView>(`/api/diagnostics${diagnosticsQuery(filter, before, take)}`)

/** Which agent CLI versions each start of this volume had, newest first. Human-only, for the reason
 *  the diagnostics log is. */
export const readCliVersions = (take = 20) =>
  json<CliVersionsAtStart[]>(`/api/diagnostics/cli-versions?take=${take}`)

/** Which build the Host is. Anonymous. */
export const readVersion = () => json<BuildInfo>('/api/version')

/**
 * Deletes a team and everything that names it. Human-only.
 *
 * `json<T>()`, not `send()`: the route answers 200 with what it removed rather than 204, precisely
 * so a caller can report a directory that could NOT be deleted. A deletion with failures is still a
 * deletion — the team is gone — so this resolves rather than throwing, and the caller shows them.
 */
export const deleteTeam = async (team: TeamId, confirmation?: string) => {
  const suffix = confirmation ? `?confirm=${encodeURIComponent(confirmation)}` : ''
  const response = await fetch(`/api/teams/${encodeURIComponent(team)}${suffix}`, { method: 'DELETE' })

  if (!response.ok) {
    if (response.status === 401) throw new Unauthorized()

    const body = await response.text()
    let detail = body
    let parsed: { error?: string; confirmation?: string; losses?: string[] } | null = null

    try {
      parsed = JSON.parse(body) as { error?: string; confirmation?: string; losses?: string[] }
      if (parsed.error) detail = parsed.error
    } catch {
      // Not JSON. The raw body, or the status, is the best we have.
    }

    if (response.status === 409 && parsed?.confirmation) {
      throw new TeamDeletionConfirmationRequired(
        detail || `${response.status} ${response.statusText}`,
        parsed.confirmation,
        parsed.losses ?? [],
      )
    }

    throw new Error(detail || `${response.status} ${response.statusText}`)
  }

  return (await response.json()) as TeamDeleted
}

/**
 * Hands a team a clean slate without deleting it.
 *
 * Open to any signed-in person, like everything else — there is no tier in this codebase — and a
 * reset destroys none of what a deletion does: the team, its documents and every member's Agent,
 * Prompt and label all survive it.
 *
 * `body` verbatim, never normalised here. Absent flags are what the server reads as false, and
 * `forgetHistory` is the one whose absence means TRUE — filling in defaults on this side would put
 * a second answer to "what does a reset do" in the browser.
 *
 * `json<T>()`, not `send()`: the route answers 200 with what it did, for the same reason
 * `deleteTeam` does — a directory it could not empty and a message it could not purge are named
 * rather than swallowed, and the reset happened either way, so this resolves and the caller shows
 * them.
 */
export const resetTeam = (team: TeamId, body: TeamResetRequest) =>
  json<TeamWasReset>(`/api/teams/${encodeURIComponent(team)}/reset`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })

/** Every trigger currently stored for this team, oldest first. */
export const listSchedules = (team: TeamId) =>
  json<TeamTrigger[]>(`/api/teams/${encodeURIComponent(team)}/triggers`)

/**
 * Every trigger currently stored for ONE member, oldest first - what `TriggersDialog` reads,
 * separately from its teammates'. The per-container route, not `listSchedules` narrowed
 * client-side: the server already keys `ListForContainerAsync` on `(team, container)`.
 */
export const listContainerTriggers = (team: TeamId, container: MemberId) =>
  json<TeamTrigger[]>(
    `/api/teams/${encodeURIComponent(team)}/containers/${encodeURIComponent(container)}/triggers`,
  )

/** Creates one durable trigger row. */
export const createSchedule = (team: TeamId, body: CreateTriggerRequest) =>
  json<TeamTrigger>(`/api/teams/${encodeURIComponent(team)}/triggers`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })

/**
 * Changes only the fields supplied in `body`.
 *
 * Absent means unchanged. Nullable fields are cleared by explicitly sending `null`.
 */
export const updateSchedule = (
  team: TeamId,
  id: string,
  body: UpdateTriggerRequest,
) =>
  json<TeamTrigger>(
    `/api/teams/${encodeURIComponent(team)}/triggers/${encodeURIComponent(id)}`,
    {
      method: 'PATCH',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    },
  )

/** 204 on success, so this goes through `send()`. */
export const deleteSchedule = async (team: TeamId, id: string): Promise<void> => {
  await send(`/api/teams/${encodeURIComponent(team)}/triggers/${encodeURIComponent(id)}`, {
    method: 'DELETE',
  })
}

/**
 * Every event type this platform can publish - the catalog a trigger's event picker and its
 * sentence are built from. Fetched here and passed in; `lib/triggers.ts` never calls this itself,
 * so its rendering stays a pure function a test can drive without a network.
 */
export const listEvents = () => json<EventDefinition[]>('/api/events')

/**
 * Lists the folder a folder-change trigger would watch, the way the runner will, and times it -
 * the "Test this folder" action. Nothing is stored. A folder the server will not watch is still a
 * 200, with `ok: false` and the sentence in `refusal`.
 */
export const testTriggerFolder = (team: TeamId, body: FolderTestRequest) =>
  json<FolderTestResult>(`/api/teams/${encodeURIComponent(team)}/triggers/test-folder`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  })

/** The roots a folder trigger may watch: the team's documents, then every `allowWatch` root. */
export const listWatchRoots = (team: TeamId) =>
  json<WatchRootOption[]>(`/api/teams/${encodeURIComponent(team)}/triggers/watch-roots`)

/**
 * Ends your Concierge session — kills the child process.
 *
 * Deliberately separate from closing the panel, which only detaches: `DELETE` here is the only
 * thing that ends the process, so a person who closed the panel by accident has not lost their
 * session. Omitting `user` ends your OWN; naming anyone else is deliberate, because it kills a
 * terminal that person may be typing into.
 *
 * `send`, not `json<T>()` — the route answers 204, and `response.json()` on an empty body throws,
 * which would report every SUCCESSFUL end as a failure.
 */
export const endConcierge = () => send('/api/concierge', { method: 'DELETE' })

/**
 * Which team this PERSON is working on - the one fact the Concierge reads per command.
 *
 * ON THE PERSON, not on this browser and not on a credential: the browser authenticates as the
 * user and the Concierge it drives authenticates as a credential of its own, so anything keyed on
 * a credential would have the browser writing something the agent never reads.
 *
 * `team` is null for NO TEAM, which is a real state rather than a missing value - it is where
 * somebody on the Teams overview is - and `name` is null when the stored team no longer exists,
 * with `team` still naming it so a caller can say WHICH team went.
 */
export const currentTeam = () =>
  json<{ team: TeamId | null; name: string | null }>('/api/me/current-team')

/**
 * Points this person at a team, or at none.
 *
 * 204, so `send` rather than `json<T>()` - that helper calls response.json() unconditionally and
 * would report every successful save as a failure while the write landed.
 */
export const setCurrentTeam = (team: TeamId | null) =>
  send('/api/me/current-team', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ team }),
  })

/**
 * What the tenant-wide Concierge is set to.
 *
 * NO TEAM IS NAMED, and that is why this exists. Every team summary carries the same three
 * values, but reading them off the ACTIVE team would suggest the setting belonged to that team,
 * and would leave an instance with no teams unable to see or fix its own Concierge.
 */
export const concierge = () => json<ConciergeSettings>('/api/concierge')

/**
 * Points the tenant-wide Concierge at another preset.
 *
 * Its own route rather than folded into some wider team-settings write. A team's name is fixed
 * after creation today, so there is no shared PATCH body to hang this on; and even if there were,
 * a caller changing Concierge should not have to resend unrelated fields.
 */
export const setConcierge = (
  body: { agent?: string },
) =>
  send('/api/concierge', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    // `body` verbatim, never normalised here: an ABSENT `agent` means "leave the stored
    // value alone" and an empty string means "clear it" - for `agent`, back to the default. JSON.stringify drops `undefined`, which is
    // exactly the distinction the server reads - filling in a default here would erase it.
    body: JSON.stringify(body),
  })

/**
 * Which Agent this team's NEW members run when nobody says otherwise.
 *
 * Its own route rather than folded into a wider team-settings write, mirroring `setConcierge`
 * above. A caller changing the allowlist should not have to resend unrelated fields, and there is
 * no shared team-rename PATCH to carry them anyway.
 */
export const setMemberAgents = (team: TeamId, agents: string[]) =>
  json<Team>(`/api/teams/${encodeURIComponent(team)}/member-agent`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ agents }),
  })

/**
 * Adds a member — an Agent Container — to a team.
 *
 * `name` is what a PERSON typed, exactly as it is for a team: the server derives the identifier and
 * never asks for one, so "Data Ingest" becomes `DataIngest` on disk and nobody has to know that.
 * The whole snapshot comes back, which is the only place the caller can learn the identifier.
 *
 * There is deliberately no `subscribes` parameter. Completion types are GLOBAL, and a member
 * subscribed to one wakes on every container's work on its team — two of them wake each other
 * without end, and nothing in the running system bounds that. A worker already gets its own
 * addressed-instruction type appended for it, which is all a worker needs to be reachable. Adding a
 * free-text subscription field here would put that runaway one keystroke away from a person who has
 * no way to know.
 */
export const addMember = (
  team: TeamId,
  name: string,
  agent: string,
  plugin?: PluginHire,
) =>
  json<ContainerSnapshot>(`/api/teams/${encodeURIComponent(team)}/containers`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },

    // No prompt of any kind: a member is told the built-in Member prompt, chosen by role. A plugin
    // member's config and secret KEYS ride the same hire; the server checks them against the
    // manifest and refuses both on an Agent.
    body: JSON.stringify(plugin ? { name, agent, config: plugin.config, secrets: plugin.secrets } : { name, agent }),
  })

/**
 * The plugins installed on this Host, each with its config fields and the NAMES of its secrets -
 * never a value. The Add member dialog offers them beside the Agent presets.
 */
export const listPlugins = () => json<PluginList>('/api/plugins')

/**
 * The STORED row behind a member - its label. `MemberSettingsDialog` calls this on open, so an edit
 * starts from what is actually there.
 */
export const getMember = (team: TeamId, name: MemberId) =>
  json<MemberDetail>(
    `/api/teams/${encodeURIComponent(team)}/containers/${encodeURIComponent(name)}`,
  )

/**
 * Changes what a member is CALLED. What it is TOLD is the built-in role prompt and no route
 * changes it. A key left undefined is dropped by `JSON.stringify` and leaves the stored
 * value alone, so `body` is sent exactly as given.
 *
 * `PATCH` answers 200 with the updated snapshot, unlike `saveAgents` next door, which answers 204 -
 * so this is `json<T>`, not `send`.
 */
export const updateMember = (
  team: TeamId,
  name: MemberId,
  body: { name?: string; agent?: string },
) =>
  json<ContainerSnapshot>(
    `/api/teams/${encodeURIComponent(team)}/containers/${encodeURIComponent(name)}`,
    {
      method: 'PATCH',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    },
  )

/**
 * Deletes one member and everything that names it.
 *
 * Open to any signed-in person, like `deleteTeam` — whoever can add a member can remove one. The
 * team's MANAGER cannot go: the route answers 409, and the members list does not offer the
 * control at all, so the refusal is a backstop rather than the way anyone meets the rule.
 *
 * `json<T>()`, not `send()`: 200 with what it removed, for the same reason `deleteTeam` is — a
 * workspace that could NOT be deleted is named rather than swallowed, and the member is gone either
 * way, so this resolves and the caller shows them.
 */
export const deleteMember = (team: TeamId, name: MemberId) =>
  json<MemberDeleted>(
    `/api/teams/${encodeURIComponent(team)}/containers/${encodeURIComponent(name)}`,
    { method: 'DELETE' },
  )

export const tell =(team: TeamId, container: MemberId, instruction: string) =>
  json<{ seq: number; correlationId: number }>(
    `/api/teams/${encodeURIComponent(team)}/containers/${encodeURIComponent(container)}/tell`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ instruction }),
    },
  )

/**
 * DOCUMENT CALLS LIVE IN `api/documents.ts`. Documents are not a `/api/teams/{team}/...`
 * call: they live under ONE tenant-level root with a folder per team, and the team is NAMED in the
 * path rather than implied by which team is active.
 */

/** `needsAdmin` is the server's name for "no account exists yet". It is the first-account flag,
 *  not a tier: the session store reads it as `needsFirstAccount`. */
export const getAuthState = () => json<{ needsAdmin: boolean }>('/api/auth/state')

/** The signed-in person. Every person is an administrator, so the shape carries no tier. */
export const getMe = () =>
  json<{ id: string; email: string }>('/api/auth/me')

/** Both take the account from the session rather than from the body, which is what keeps them from
 *  being edit-anyone endpoints. The confirm-it-twice fields are checked in the form and never sent:
 *  a re-type catches a typing slip, and a second copy tells the server nothing new. */
export const changeEmail = (currentEmail: string, email: string) =>
  json<{ id: string; email: string }>('/api/auth/email', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ currentEmail, email }),
  })

export const changePassword = async (currentPassword: string, newPassword: string) => {
  // 204 on success, so there is nothing to parse - only a refusal to surface.
  await send('/api/auth/password', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ currentPassword, newPassword }),
  })
}

/** The first account on a fresh instance. The route is still `/api/auth/admin` on the wire; what it
 *  creates is an ordinary person, because every person is an administrator, and it closes
 *  registration behind them. */
export const createFirstAccount = (email: string, password: string) =>
  json<unknown>('/api/auth/admin', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })

export const login = (email: string, password: string) =>
  json<{ id: string; email: string }>('/api/auth/login', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })

/** Gated, deliberately: an anonymous or expired-cookie logout answers 401, not 204. The status is
 *  ignored on purpose - a defensive sign-out on startup must not surface that as a failure. */
export const logout = () =>
  fetch('/api/auth/logout', { method: 'POST' }).then(() => undefined)

/** A user as the accounts screen sees them. Every person reaches every team and every person is an
 *  administrator, so there is no per-team access list and no tier on the row. */
export interface AdminUser {
  id: string
  email: string
}

export const listUsers = () => json<AdminUser[]>('/api/users')

/**
 * The Agent catalog, in full - command lines, `env` and `installations` included. Every person is
 * an administrator, so the route has no redacted arm; a machine principal is refused
 * outright rather than handed a smaller shape.
 */
export const listCatalog = () => json<Catalog>('/api/agents')

/**
 * Whether each preset's CLI is installed AND signed in, as `GET /api/agents/auth` measured it.
 *
 * `authenticated` is `null` when the probe could not measure it - a CLI with no way to ask, or a
 * probe that did not run. A caller must render null as NOT MEASURED, never as a failure: the
 * distinction between "we checked and it is signed out" and "we did not check" is the whole reason
 * the field is nullable, and a tri-state collapsed to a boolean turns every unmeasured preset into a
 * false alarm.
 */
export const getAgentAuth = () => json<AgentAuthReport[]>('/api/agents/auth')

/**
 * One page of skills, newest first, for `useCursorList`: the rows strictly older than
 * `before` (a row's `id`), at most `take` of them. `kind` defaults to `custom` on the server too;
 * `q` searches name and description.
 */
export const listSkillsPage = (
  query: { kind: SkillKindFilter; q?: string },
  before: number | undefined,
  take: number,
) => {
  const params = new URLSearchParams({ kind: query.kind, take: String(take) })
  if (query.q && query.q.trim()) params.set('q', query.q.trim())
  if (before !== undefined) params.set('before', String(before))
  return json<SkillRecord[]>(`/api/skills?${params.toString()}`)
}

/**
 * Creates a CUSTOM skill. `roles` is required - the server refuses a skill with none, and a name a
 * built-in already has, each with a sentence to show the person.
 */
export const createSkill = (draft: SkillDraft) =>
  json<SkillRecord>(`/api/skills/${encodeURIComponent(draft.name)}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(draft),
  })

/**
 * Edits a CUSTOM skill, addressed by the name it had. A changed `draft.name` renames it. A built-in
 * is refused by the route, and the dialog never offers the call.
 */
export const updateSkill = (name: string, draft: SkillDraft) =>
  json<SkillRecord>(`/api/skills/${encodeURIComponent(name)}`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(draft),
  })

/** Deletes a CUSTOM skill. A built-in is refused by the route. */
export const deleteSkill = async (name: string) => {
  await send(`/api/skills/${encodeURIComponent(name)}`, { method: 'DELETE' })
}

/** REPLACES the whole catalog - human-only. 400 with `{ error }` naming what would break:
 *  an empty array, a duplicate or illegal name, an Agent with neither launch kind, an `env` key
 *  beginning `HARNESS_`, or removing an Agent a member or a team's console still uses. */
export const saveCatalog = async (agents: Agent[]) => {
  // 204 with an EMPTY body, same as deleteUser/resetPassword below - json<T>() calls
  // response.json() unconditionally, and parsing an empty body throws a SyntaxError. That would
  // turn every successful save into a caught failure: the catalog written to disk, but the dialog
  // showing an error, skipping the notification and skipping the refetch. send() is what the other
  // 204 endpoints in this file use for exactly this reason.
  await send('/api/agents', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    // Every preset, built-ins included and unchanged: the route refuses an edit to a built-in, so
    // sending one as it was read is not an edit. The catalog carries no prompts.
    body: JSON.stringify({ agents }),
  })
}

/** Email and password, nothing else: there is no tier to set, and every person reaches every team.
 *  There is no update call either: an existing account has nothing to edit but its password,
 *  which is `resetPassword` below. */
export const createUser = (email: string, password: string) =>
  json<AdminUser>('/api/users', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })

export const deleteUser = async (id: string) => {
  // 204, so there is nothing to parse - only a refusal to surface.
  await send(`/api/users/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

export const resetPassword = async (id: string, password: string) => {
  await send(`/api/users/${encodeURIComponent(id)}/password`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ password }),
  })
}

/** Your own keys, newest first. */
export const listKeys = () => json<ApiKey[]>('/api/keys')

/** Every key in the tenant, with its owner. Human-only; a machine principal gets a 403. */
export const listAllKeys = () => json<TenantApiKey[]>('/api/admin/keys')

/**
 * The response carries the credential, and it is the only time it exists outside the caller's
 * hands — the server keeps only its hash. Whatever calls this owns showing it once.
 */
export const mintKey = (label: string) =>
  json<MintedKey>('/api/keys', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ label }),
  })

/**
 * `send`, not `json<T>()` — the route answers 204, and `response.json()` on an empty body throws a
 * SyntaxError, which would report every SUCCESSFUL revocation as a failure while the key was
 * destroyed. That exact bug shipped once on `PUT /api/agents`.
 */
export const revokeKey = async (id: string): Promise<void> => {
  await send(`/api/keys/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

/** Get repo status for a team, optionally refreshing from origin. */
export const getRepoStatus = (team: TeamId, refresh?: boolean) =>
  json<TeamRepoStatus>(`/api/teams/${encodeURIComponent(team)}/repo-status${refresh ? '?refresh=true' : ''}`)

/** Fetch origin so the card can tell the truth about it. Moves nothing. */
export const fetchRepoAsync = (team: TeamId, repo: string) =>
  json<RepoActionResult>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/fetch`,
    { method: 'POST' },
  )

/** Fetch and fast-forward the clone's default branch from origin (never assumed to be `main`). */
export const bringRepoCurrentAsync = (team: TeamId, repo: string) =>
  json<RepoActionResult>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/bring-current`,
    { method: 'POST' },
  )

/** Merge team branch to main and push. */
export const mergeRepoToMainAsync = (team: TeamId, repo: string) =>
  json<RepoActionResult>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/merge-to-main`,
    { method: 'POST' },
  )

/**
 * Contributor mode's Open pull request: `gh pr create` from team/{id} on the fork, with the
 * title and body the person edited. A person's button only; the platform never opens one by itself.
 */
export const openPullRequestAsync = (team: TeamId, repo: string, title: string, body: string) =>
  json<RepoActionResult>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/pull-request`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ title, body }),
    },
  )

/** The title and body Open pull request starts with. Reads nothing from GitHub. */
export const getPullRequestDraft = (team: TeamId, repo: string) =>
  json<PullRequestDraft>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/pull-request-draft`,
  )

/** Fork it for me: the fork of `upstreamUrl` in the GH_TOKEN account, or `organisation`. */
export const forkUpstream = (upstreamUrl: string, organisation?: string | null) =>
  json<ForkResult>('/api/github/fork', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ upstreamUrl, organisation: organisation?.trim() ? organisation.trim() : null }),
  })

/** Remove worktrees and prune. */
export const cleanupRepoWorktreesAsync = (team: TeamId, repo: string) =>
  json<RepoActionResult>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/cleanup-worktrees`,
    { method: 'POST' },
  )

/** Rebase the clone's main onto origin/main. Aborts cleanly on conflict; never discards work. */
export const rebaseRepoAsync = (team: TeamId, repo: string) =>
  json<RepoActionResult>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/rebase`,
    { method: 'POST' },
  )

/**
 * After a rebase would conflict: send the team's Manager the files and an instruction to merge
 * origin/main into main and resolve them. Starts a new workflow; the clone is not touched here.
 */
export const askTeamToBringCurrent = (team: TeamId, repo: string) =>
  json<{ seq: number; correlationId: number; conflicts: string[] }>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/ask-team`,
    { method: 'POST' },
  )

/** Push the clone's main to the team branch. Refuses rather than forcing. */
export const pushRepoAsync = (team: TeamId, repo: string) =>
  json<RepoActionResult>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/push`,
    { method: 'POST' },
  )

/** Delete the team branch from origin. Refused until the work is on origin/main. */
export const deleteRepoRemoteBranchAsync = (team: TeamId, repo: string) =>
  json<RepoActionResult>(
    `/api/teams/${encodeURIComponent(team)}/repos/${encodeURIComponent(repo)}/delete-remote-branch`,
    { method: 'POST' },
  )
