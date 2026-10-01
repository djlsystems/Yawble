import { json, send } from './client'
import type { TeamId } from './types'

/**
 * What the board, its filter and the pickers need of an outcome: the result a workflow serves.
 *
 * `name` IS TEXT. A person or a Manager typed it, and every template binds it with `{{ }}` or a
 * q-select label - never `v-html`.
 */
export interface OutcomeRef {
  /** A GUID, with its hyphens. */
  id: string
  name: string
  status: 'proposed' | 'active' | 'retired' | 'merged'
}

/**
 * The outcomes a workflow may be linked to: active and proposed, in the route's order (by name
 * when no team is given). What every picker and the board's Outcome filter offer.
 */
export async function listLiveOutcomes(): Promise<OutcomeRef[]> {
  const answer = await json<{ outcomes: OutcomeRef[] }>('/api/outcomes?status=active,proposed')

  return answer.outcomes.map(({ id, name, status }) => ({ id, name, status }))
}

/**
 * Link a workflow to an outcome, as the person signed in: a new link row, `how: person`. The
 * card menu's "Change outcome…" sends this.
 */
export async function setWorkflowOutcome(team: TeamId | string, correlation: number, outcome: string): Promise<void> {
  await send(`/api/teams/${encodeURIComponent(team)}/workflows/${correlation}/outcome`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ outcome }),
  })
}

/** How a picker labels an outcome: a proposed one says so, since only a person confirms it. */
export function outcomeLabel(outcome: Pick<OutcomeRef, 'name' | 'status'>): string {
  return outcome.status === 'proposed' ? `${outcome.name} (proposed)` : outcome.name
}
