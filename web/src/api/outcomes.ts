import { json, send } from './client'

/**
 * EVERY CALL TO THE OUTCOMES SURFACE (B0022's routes): the list with its figures, one outcome with
 * its workflows and link history, a person's edits and status changes, the merge and its preview,
 * and a workflow's link.
 *
 * A SEPARATE FILE FROM `client.ts`, like `sites.ts`, borrowing `send`/`json` so a 401 is still
 * `Unauthorized` and a refusal still surfaces the server's own `error` sentence.
 *
 * EVERY FIGURE IS THE ROUTE'S. Nothing here, and nothing that reads these types, adds, averages or
 * estimates: agent time is the route's sum, elapsed its median and longest, and an unmeasured run is
 * the route's count, never a zero.
 *
 * | operation | route |
 * |---|---|
 * | list with figures | `GET /api/outcomes?status=&from=&to=` |
 * | one outcome | `GET /api/outcomes/{id}` |
 * | create | `POST /api/outcomes` |
 * | rename, describe, target | `PATCH /api/outcomes/{id}` |
 * | confirm, retire, reactivate | `POST /api/outcomes/{id}/{verb}` |
 * | merge (and its preview) | `POST /api/outcomes/{id}/merge[?preview=true]` |
 * | reject | `DELETE /api/outcomes/{id}` |
 * | move a workflow | `PUT /api/teams/{team}/workflows/{correlation}/outcome` |
 */

export type OutcomeStatus = 'proposed' | 'active' | 'retired' | 'merged'

export interface OutcomeTeam {
  id: string
  name: string
  /** A team that is gone, named from the link's snapshot. */
  deleted: boolean
}

/** One outcome's (or the unlinked workflows') figures, read from the usage ledger. */
export interface OutcomeFigures {
  workflows: { open: number; completed: number; closed: number; total: number }
  /** The sum over runs: parallel runs add up, so it can exceed elapsed. */
  agentSeconds: number
  waitingSeconds: number
  /** Per workflow, of the closed ones. Never summed. */
  elapsed: { medianSeconds: number | null; longestSeconds: number | null; workflows: number }
  /** Billable over measured runs; `unmeasuredRuns` reported none and are counted, never zero. */
  tokens: { billable: number; measuredRuns: number; unmeasuredRuns: number }
  runs: number
  teams: OutcomeTeam[]
  lastWorkedAt: string | null
}

export interface Outcome {
  id: string
  name: string
  description: string
  status: OutcomeStatus
  mergedInto: string | null
  source: string
  targetMetric: string | null
  targetUnit: string | null
  targetValue: string | null
  createdBy: string
  createdByKind: string
  createdAt: string
  updatedAt: string
  confirmedBy: string | null
  confirmedAt: string | null
  teamWorkflows?: number | null
  figures: OutcomeFigures
}

export interface OutcomeList {
  ledgerStartedAt: string | null
  outcomes: Outcome[]
  noOutcome: { name: string; figures: OutcomeFigures }
}

/** One workflow the outcome serves, as its detail lists it. */
export interface OutcomeWorkflowLine {
  correlation: number
  team: OutcomeTeam | null
  state: string
  startedAt: string | null
  elapsedSeconds: number | null
  agentSeconds: number
  billableTokens: number
  unmeasuredRuns: number
  how: string | null
  setBy: string | null
  setByKind: string | null
  setAt: string | null
}

/** One row of `workflow_outcome_links`, with the names it was made under. */
export interface OutcomeLinkRow {
  id: number
  correlation: number
  outcomeId: string
  teamId: string | null
  teamNameAtLink: string | null
  outcomeNameAtLink: string
  setBy: string
  setByKind: string
  setAt: string
  how: string
}

/** One `outcome.*` tenant row about the outcome or one merged into it: what was done, by whom, when. */
export interface OutcomeEvent {
  seq: number
  at: string
  action: string
  outcomeId: string | null
  /** The outcome's name after the act. */
  name: string | null
  /** A rename's previous name, when a row before it recorded one. */
  from: string | null
  /** The actor's email, or id. */
  by: string | null
  detail: Record<string, unknown> | null
}

export interface OutcomeDetail {
  outcome: Outcome
  /** A merged outcome: the outcome holding its figures now. */
  resolvedTo: string | null
  mergedFrom: { id: string; name: string }[]
  workflows: OutcomeWorkflowLine[]
  history: OutcomeLinkRow[]
  events: OutcomeEvent[]
  ledgerStartedAt: string | null
}

export interface OutcomeFields {
  name?: string
  description?: string
  targetMetric?: string
  targetUnit?: string
  targetValue?: string
}

export interface MergePreview {
  preview: true
  from: { id: string; name: string; status: OutcomeStatus }
  into: { id: string; name: string; status: OutcomeStatus }
  /** The sentence the merge would be refused with, or null. */
  refusal: string | null
  moves: { links: number; figures: OutcomeFigures }
}

export interface OutcomeListQuery {
  status?: OutcomeStatus[]
  from?: string | null
  to?: string | null
}

const at = (id: string) => `/api/outcomes/${encodeURIComponent(id)}`

const body = (value: unknown): RequestInit => ({
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify(value),
})

export function listOutcomes(query: OutcomeListQuery = {}): Promise<OutcomeList> {
  const params = new URLSearchParams()
  if (query.status?.length) params.set('status', query.status.join(','))
  if (query.from) params.set('from', query.from)
  if (query.to) params.set('to', query.to)
  const suffix = params.toString()

  return json<OutcomeList>(`/api/outcomes${suffix ? `?${suffix}` : ''}`)
}

export const getOutcome = (id: string) => json<OutcomeDetail>(at(id))

export const createOutcome = (fields: OutcomeFields) =>
  json<Outcome>('/api/outcomes', { method: 'POST', ...body(fields) })

export const editOutcome = (id: string, fields: OutcomeFields) =>
  json<Outcome>(at(id), { method: 'PATCH', ...body(fields) })

export type OutcomeTransition = 'confirm' | 'retire' | 'reactivate'

export const transitionOutcome = (id: string, verb: OutcomeTransition) =>
  json<Outcome>(`${at(id)}/${verb}`, { method: 'POST' })

export const previewMerge = (id: string, into: string) =>
  json<MergePreview>(`${at(id)}/merge?preview=true`, { method: 'POST', ...body({ into }) })

export const mergeOutcome = (id: string, into: string) =>
  json<Outcome>(`${at(id)}/merge`, { method: 'POST', ...body({ into }) })

export const rejectOutcome = async (id: string): Promise<void> => {
  await send(at(id), { method: 'DELETE' })
}

/** A person's link: the workflow now serves `outcome` (an id), `how: person`. */
export const linkWorkflowOutcome = (team: string, correlation: number, outcome: string) =>
  json<OutcomeLinkRow>(
    `/api/teams/${encodeURIComponent(team)}/workflows/${correlation}/outcome`,
    { method: 'PUT', ...body({ outcome }) },
  )
