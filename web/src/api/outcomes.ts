import { json, send } from './client'

/**
 * EVERY CALL TO THE OUTCOMES SURFACE: the list with its figures, one outcome with
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
 * | rename, describe, value, present | `PATCH /api/outcomes/{id}` |
 * | confirm, retire, reactivate | `POST /api/outcomes/{id}/{verb}` |
 * | merge (and its preview) | `POST /api/outcomes/{id}/merge[?preview=true]` |
 * | reject | `DELETE /api/outcomes/{id}` |
 * | delete ended outcomes (and its preview) | `POST /api/outcomes/delete[?preview=true]` |
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
  /** Its backlog items by where they stand; `declared` is counted as `money.declaredCountsAs` says. */
  backlog: { notStarted: number; inProgress: number; achieved: number }
  /** Waiting on a person: time its workflows were blocked on a person, from the ledger. Never attention. */
  blockedSeconds: number
  /** Agent time over agent time plus blocked time, 0..1; null when both are 0, never 0. */
  efficiency: number | null
  /** The last eight weeks, oldest first, Monday-start UTC, whatever the period. */
  weekly: OutcomeWeek[]
  /**
   * The sum of each run's estimate, priced from its own snapshot; `amount` is null when no run was priced,
   * never 0. `agentRate` is the weighted rate of its priced agent time (money over hours), null when none
   * was priced - never the tenant rate.
   */
  cost: { amount: number | null; currency: string; legacyRates?: string[]; agentRate?: number | null }
  /** The same eight weeks as `weekly`: its workflows that closed in each, every one and those completed. */
  throughput?: OutcomeThroughputWeek[]
  /** The eight weeks' cost over the workflows completed in them; null is "not measurable", never a division by zero. */
  costPerCompleted?: number | null
  /** On one outcome's detail only: everything up to the end of each of the eight weeks, the last its all-time figure. */
  cumulative?: OutcomeCumulativeWeek[]
  /** People's acts that count under it, in the read's window. Absent from an older Host. */
  human?: HumanFigures
  /** The same eight weeks as `weekly`: the acts in each - intervention frequency. */
  humanWeekly?: HumanWeek[]
}

/**
 * PEOPLE'S ACTS, from `intervention_ledger`, never priced. `interventions` is Human Interventions, each row once;
 * `attentionSeconds` is Human Attention Time - measured durations only - and null when nothing was measured: no
 * measured time, never 0. Waiting on a person is a separate figure and is never added to it.
 */
export interface HumanFigures {
  interventions: number
  measuredInterventions: number
  attentionSeconds: number | null
}

export interface HumanWeek extends HumanFigures {
  weekStart: string
}

/** A team's (or the reached teams') human figures over the eight weeks, with Waiting on a person beside them. */
export interface TeamHumanFigures extends HumanFigures {
  unattributed: HumanFigures
  /** Waiting on a person: time its work waited on a person. Never added to `attentionSeconds`. */
  waitingSeconds: number
}

export interface OutcomeThroughputWeek {
  weekStart: string
  closed: number
  completed: number
  /** That week's cost over its completed workflows; null is "not measurable". */
  costPerCompleted: number | null
}

export interface OutcomeCumulativeWeek {
  weekStart: string
  agentSeconds: number
  cost: number | null
}

/** How a person reads an outcome. A stored null - never picked - reads as `project`. */
export type OutcomePresentation = 'project' | 'operational'

export interface OutcomeWeek {
  weekStart: string
  agentSeconds: number
  billable: number
  measuredRuns: number
  unmeasuredRuns: number
  cost: number | null
}

/** How the instance prices and counts: what the dashboard is read with. */
export interface OutcomeMoney {
  currency: string
  /** Whole currency units an agent hour; null when no rate is set. */
  agentHourlyRate: number | null
  declaredCountsAs: 'achieved' | 'in-progress'
}

export type OutcomeBucket = 'notStarted' | 'inProgress' | 'achieved'

/** One backlog item an outcome counts, with where it stands. */
export interface OutcomeBacklogItem {
  /** The citation, as a person reads it. */
  id: string
  /** The number the backlog routes take; the client never parses a citation. */
  number: number | null
  title: string
  state: string
  archived: boolean
  bucket: OutcomeBucket
  team: string | null
  teamName: string | null
  workflow: number | null
  dispatchedAt: string | null
  landedAt: string | null
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
  /** What achieving it is worth, in `money.currency`, as text; null when nobody has said. */
  value: string | null
  /** How a person reads it; null (never picked) reads as project. Absent from an older Host. */
  presentation?: OutcomePresentation | null
  createdBy: string
  createdByKind: string
  createdAt: string
  updatedAt: string
  confirmedBy: string | null
  confirmedAt: string | null
  teamWorkflows?: number | null
  /** How many workflows' current link reaches it, through any outcome merged into it. */
  linkedWorkflows?: number | null
  /** When it was retired or merged; null while it is live, absent when the route does not send it. */
  endedAt?: string | null
  figures: OutcomeFigures
}

export interface OutcomeList {
  ledgerStartedAt: string | null
  money: OutcomeMoney
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
  /** Billable over the measured runs: with `measuredRuns` 0 there is no figure, never a 0. */
  billableTokens: number
  measuredRuns: number
  unmeasuredRuns: number
  how: string | null
  setBy: string | null
  setByKind: string | null
  setAt: string | null
}

/**
 * One row of `workflow_outcome_links`, with the names it was made under. An unlink (a person's
 * "None") names no outcome: `outcomeId` and `outcomeNameAtLink` are null and `isUnlink` is true.
 */
export interface OutcomeLinkRow {
  id: number
  correlation: number
  outcomeId: string | null
  isUnlink?: boolean
  teamId: string | null
  teamNameAtLink: string | null
  outcomeNameAtLink: string | null
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
  money: OutcomeMoney
  backlogItems: OutcomeBacklogItem[]
}

export interface OutcomeFields {
  name?: string
  description?: string
  targetMetric?: string
  targetUnit?: string
  targetValue?: string
  /** An amount of `money.currency`; empty clears it. */
  value?: string
  /** A person's choice; only `project` or `operational` is accepted. */
  presentation?: OutcomePresentation
}

export interface MergePreview {
  preview: true
  from: { id: string; name: string; status: OutcomeStatus }
  into: { id: string; name: string; status: OutcomeStatus }
  /** The sentence the merge would be refused with, or null. */
  refusal: string | null
  moves: { links: number; figures: OutcomeFigures }
}

/** One outcome a delete would remove: one asked for, or one merged into it that goes with it. */
export interface DeletedOutcome {
  id: string
  name: string
  status: OutcomeStatus
  mergedInto: string | null
  /** The outcome being deleted that this one was merged into and goes with; null when it was asked for. */
  cascadedFrom: string | null
  /** This outcome's own share of the totals below. */
  workflowsUnlinked: number
  backlogItemsCleared: number
  triggersCleared: number
}

/** What deleting `ids` would do, before anything changes. */
export interface DeletePreview {
  /** True for the preview; the delete itself answers the same shape with false. */
  preview: boolean
  outcomes: DeletedOutcome[]
  /** Workflows that will count under No outcome afterwards. */
  workflowsUnlinked: number
  backlogItemsCleared: number
  triggersCleared: number
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

/** What deleting these ended outcomes would remove; a refused id refuses the whole request. */
export const previewDeleteOutcomes = (ids: string[]) =>
  json<DeletePreview>('/api/outcomes/delete?preview=true', { method: 'POST', ...body({ ids }) })

/** Deletes these ended outcomes, and every outcome merged into one of them, for real. */
export const deleteOutcomes = async (ids: string[]): Promise<void> => {
  await send('/api/outcomes/delete', { method: 'POST', ...body({ ids }) })
}

/** A person's link: the workflow now serves `outcome` (an id), `how: person`. */
export const linkWorkflowOutcome = (team: string, correlation: number, outcome: string) =>
  json<OutcomeLinkRow>(
    `/api/teams/${encodeURIComponent(team)}/workflows/${correlation}/outcome`,
    { method: 'PUT', ...body({ outcome }) },
  )

/** A person's "None": the workflow serves no outcome, as a new link row (history kept). */
export const unlinkWorkflowOutcome = (team: string, correlation: number) =>
  json<OutcomeLinkRow>(
    `/api/teams/${encodeURIComponent(team)}/workflows/${correlation}/outcome`,
    { method: 'DELETE' },
  )

/**
 * What the board, its filter and the pickers need of an outcome: the result a workflow serves.
 *
 * `name` IS TEXT. A person or a Manager typed it, and every template binds it with `{{ }}` or a
 * q-select label - never `v-html`.
 */
export type OutcomeRef = Pick<Outcome, 'id' | 'name' | 'status'>

/**
 * The outcomes a workflow may be linked to: active and proposed, in the route's order. What every
 * picker and the board's Outcome filter offer.
 */
export async function listLiveOutcomes(): Promise<OutcomeRef[]> {
  const answer = await listOutcomes({ status: ['active', 'proposed'] })

  return answer.outcomes.map(({ id, name, status }) => ({ id, name, status }))
}

/** How a picker labels an outcome: a proposed one says so, since only a person confirms it. */
export function outcomeLabel(outcome: Pick<OutcomeRef, 'name' | 'status'>): string {
  return outcome.status === 'proposed' ? `${outcome.name} (proposed)` : outcome.name
}

/**
 * A CURRENT VALUE NO PICKER OFFERS - a retired or merged outcome - by name, or null when the id
 * names no outcome or the read fails. The pickers list only live outcomes, so without this a
 * person would see the bare id.
 */
export async function findOutcomeRef(id: string): Promise<OutcomeRef | null> {
  try {
    const { outcome } = await getOutcome(id)
    return outcome ? { id: outcome.id, name: outcome.name, status: outcome.status } : null
  } catch {
    return null
  }
}

/** How a current value that is no longer on offer reads: `Old goal (retired)`, `Duplicate goal (merged)`. */
export function endedOutcomeLabel(outcome: Pick<OutcomeRef, 'name' | 'status'>): string {
  return `${outcome.name} (${outcome.status})`
}

// ---- A team's cost, by outcome, over the last eight weeks ---------------------------------------

/** One week of one outcome's work for one team; `cost` is null when no rate is set, never 0. */
export interface TeamCostWeek {
  weekStart: string
  agentSeconds: number
  billable: number
  measuredRuns: number
  unmeasuredRuns: number
  cost: number | null
}

/** Totals over the eight weeks, the same shape for an outcome and for the team. */
export interface TeamCostTotals {
  /** Agent-kind time only, priced or not: a plugin's time is never Agent Hours. */
  agentSeconds: number
  billable: number
  measuredRuns: number
  unmeasuredRuns: number
  /** Every priced run's estimate, agent and plugin, each from its own snapshot; null - never 0 - when none was priced. */
  cost: number | null
  /** The priced agent time's own rate: its estimates over its hours. Null when no agent time was priced. */
  agentRate?: number | null
  /**
   * The figure's footnote: `reconstructed` and `fallback-at-upgrade`, each when a run from before snapshots
   * was priced into it that way. Empty, or absent from an older Host, when none was.
   */
  legacyRates?: string[]
}

/** One outcome the team's workflows served, or `id: null` for its unlinked workflows ("No outcome"). */
export interface TeamCostOutcome {
  id: string | null
  name: string
  /** The outcome's stored value (`outcomes.value_amount`), or null when none is set. No screen reads it: it is no budget. */
  value: number | null
  weekly: TeamCostWeek[]
  totals: TeamCostTotals
  /** The outcome's Estimated Execution Cost across ALL teams, all time. */
  outcomeTotalCost: number | null
  /**
   * The outcome's 1-based place in the fixed order (created, oldest first, ties by id) among every
   * outcome ANY team served in the weeks - the same on every team, so it picks the outcome's colour.
   * Null for No outcome.
   */
  rank: number | null
  /** The team's Human Interventions and Human Attention Time under it over the eight weeks. Absent from an older Host. */
  human?: HumanFigures
}

/** How the cost routes price agent time: the currency, and the rate (null when none is set). */
export type TeamCostMoney = Pick<OutcomeMoney, 'currency' | 'agentHourlyRate'>

/** `GET /api/teams/{team}/cost`: one team's outcome figures over the last eight weeks. */
export interface TeamCost {
  team: string
  money: TeamCostMoney
  /** Eight Monday-start UTC weeks, oldest first. */
  weeks: string[]
  /** No outcome first when present, then each outcome. */
  outcomes: TeamCostOutcome[]
  totals: TeamCostTotals
  /** Its human figures over the eight weeks, with Waiting on a person beside them. Absent from an older Host. */
  human?: TeamHumanFigures
}

/** `GET /api/teams/cost`: every team's figures at once, for the teams list - one call, never one per row. */
export interface TeamsCost {
  money: TeamCostMoney
  weeks: string[]
  teams: Array<Pick<TeamCost, 'team' | 'outcomes' | 'totals' | 'human'>>
}

export const getTeamCost = (team: string) => json<TeamCost>(`/api/teams/${encodeURIComponent(team)}/cost`)

export const getTeamsCost = () => json<TeamsCost>('/api/teams/cost')

// ---- The instance's cost over the last eight weeks, for the summary above the teams list --------

/** One week of every team's work together; `cost` is null when no rate is set, never 0. */
export interface CostSummaryWeek {
  weekStart: string
  agentSeconds: number
  cost: number | null
}

/** Agent seconds and cost over a period; `cost` is null when no run was priced, never 0. */
export interface CostSummaryTotals {
  agentSeconds: number
  cost: number | null
  /** The priced agent time's own rate: its estimates over its hours. Null when no agent time was priced. */
  agentRate?: number | null
  /**
   * The figure's footnote: `reconstructed` and `fallback-at-upgrade`, each when a run from before snapshots
   * was priced into it that way. Empty, or absent from an older Host, when none was.
   */
  legacyRates?: string[]
}

/** One outcome's eight weeks across every team, or `id: null` for work linked to no outcome. */
export interface CostSummaryOutcome {
  id: string | null
  name: string
  /** Its place in the instance-wide fixed order, which picks its colour; null for No outcome. */
  rank: number | null
  agentSeconds: number
  cost: number | null
  /** The outcome's stored value, in the instance's currency; null when none is set. No screen reads it: it is no budget. */
  budget: number | null
  /** Its Estimated Execution Cost across every team, all time. */
  outcomeTotalCost: number | null
  /**
   * The figure's footnote: `reconstructed` and `fallback-at-upgrade`, each when a run from before snapshots
   * was priced into it that way. Empty, or absent from an older Host, when none was.
   */
  legacyRates?: string[]
  /** The reached teams' Human Interventions and Human Attention Time under it over the eight weeks. Absent from an older Host. */
  human?: HumanFigures
}

/** `GET /api/teams/cost/summary`: the four tiles above the teams list, in one answer. */
export interface CostSummary {
  /** The tenant default agent rate as set now; null when none is set. No figure is this rate times hours. */
  rate: number | null
  /** The instance's currency, when the route sends it; the teams cost route's otherwise. */
  currency?: string
  /** Eight Monday-start UTC weeks, oldest first. */
  weeks: CostSummaryWeek[]
  /** The first week's Monday and the last week's last day, `YYYY-MM-DD`. */
  periodStart: string
  periodEnd: string
  total: CostSummaryTotals
  /** The eight weeks before, for the change. */
  prior: CostSummaryTotals
  outcomes: CostSummaryOutcome[]
  rightNow: { openWorkflows: number; membersRunning: number; waitingOnYou: number }
  /** The reached teams' human figures over the eight weeks, with Waiting on a person beside them. Absent from an older Host. */
  human?: TeamHumanFigures
}

export const getCostSummary = () => json<CostSummary>('/api/teams/cost/summary')

// ---- Every team's figures over the period the Teams list picks, by day or month -----------------

/** How the Teams list's period is cut: by UTC day or by calendar month. */
export type CostBucket = 'day' | 'month'

/** A period the Teams list reads: `from` and `to` as `YYYY-MM-DD`, midnight UTC, `to` exclusive. */
export interface CostPeriodRange {
  from: string
  to: string
}

/** One bucket of one series; `cost` is null - never 0 - when nothing in it was priced. */
export interface CostPeriodBucket {
  /** The bucket's first day, `YYYY-MM-DD`. */
  start: string
  agentSeconds: number
  cost: number | null
}

/** One outcome a team's runs served in the period, or `id: null` for Unattributed. */
export interface CostPeriodTeamOutcome {
  id: string | null
  name: string
  rank: number | null
  totals: CostSummaryTotals
  buckets: CostPeriodBucket[]
}

/** One team's figures over the period. A team with no runs has zero seconds, a null cost and no outcomes. */
export interface CostPeriodTeam {
  team: string
  totals: CostSummaryTotals
  buckets: CostPeriodBucket[]
  outcomes: CostPeriodTeamOutcome[]
}

/** `GET /api/teams/cost/period`: every reachable team, the instance's total and its outcomes over one period. */
export interface TeamsCostPeriod {
  money: TeamCostMoney
  period: CostPeriodRange & { bucket: CostBucket }
  /** Each bucket's first day, oldest first. */
  buckets: string[]
  teams: CostPeriodTeam[]
  total: CostSummaryTotals
  /** The reached teams' figure per bucket, for the summary tile's chart. */
  totalBuckets: CostPeriodBucket[]
  /** The same length of time just before the period, for the change. */
  prior: CostSummaryTotals
  /** Unattributed first when there is some, then each outcome served in the period, as the summary gives them. */
  outcomes: CostSummaryOutcome[]
}

export const getTeamsCostPeriod = (range: CostPeriodRange, bucket: CostBucket) =>
  json<TeamsCostPeriod>(
    `/api/teams/cost/period?from=${encodeURIComponent(range.from)}&to=${encodeURIComponent(range.to)}&bucket=${bucket}`)

// ---- One team's figures over the period, broken down: the team page ----------------------------

/** One group's figures over the period: `cost` null - never 0 - when nothing in it was priced. */
export interface TeamPeriodFigures {
  /** Agent-kind time only: a plugin member's is 0. */
  agentSeconds: number
  cost: number | null
  agentRate: number | null
  legacyRates: string[]
  /** Its runs that started and whose span meets the period. */
  executions: number
  /** Tokens of its runs that ended in the period; a run that reported none is in `unmeasuredRuns`. */
  billable: number
  measuredRuns: number
  unmeasuredRuns: number
}

/** The team's figures over the period: its Teams list row's, with tokens, executions and active members. */
export interface TeamPeriodTotals extends CostSummaryTotals {
  billable: number
  measuredRuns: number
  unmeasuredRuns: number
  executions: number
  activeMembers: number
}

/** One outcome the team's runs served (or Unattributed, `id` null): the list row's entry, its stored value and all-time total beside it. */
export interface TeamPeriodOutcome extends CostPeriodTeamOutcome {
  value: number | null
  outcomeTotalCost: number | null
  figures: TeamPeriodFigures
}

export interface TeamPeriodMember {
  member: string
  kind: 'agent' | 'plugin'
  figures: TeamPeriodFigures
  /** A plugin member with no money in the period only: `not-priced` when a run started with no rate of its own,
   *  `unavailable` when every run is from before snapshots. Absent on every other entry. */
  unpriced?: 'not-priced' | 'unavailable'
}

export interface TeamPeriodWorkflow {
  workflow: number
  figures: TeamPeriodFigures
}

/** `GET /api/teams/{team}/cost/period`. */
export interface TeamCostPeriod {
  team: string
  money: TeamCostMoney
  period: CostPeriodRange & { bucket: CostBucket }
  buckets: string[]
  totals: TeamPeriodTotals
  totalBuckets: CostPeriodBucket[]
  outcomes: TeamPeriodOutcome[]
  members: TeamPeriodMember[]
  workflows: TeamPeriodWorkflow[]
}

export const getTeamCostPeriod = (team: string, range: CostPeriodRange, bucket: CostBucket) =>
  json<TeamCostPeriod>(
    `/api/teams/${encodeURIComponent(team)}/cost/period?from=${encodeURIComponent(range.from)}&to=${encodeURIComponent(range.to)}&bucket=${bucket}`)

/** `GET /api/teams/{team}/cost/workflows`: each workflow's figures over all its runs, newest first. */
export interface TeamCostWorkflows {
  team: string
  money: TeamCostMoney
  workflows: TeamPeriodWorkflow[]
}

export const getTeamCostWorkflows = (team: string, workflow?: number) =>
  json<TeamCostWorkflows>(
    `/api/teams/${encodeURIComponent(team)}/cost/workflows${workflow === undefined ? '' : `?workflow=${workflow}`}`)
