import type { NeedsYouItem } from '../api/types'
import type { DecisionAsk } from './conciergeDecision'
import { itemLabel } from './backlog'

/**
 * NEEDS YOU: THE ONE LIST OF WHAT IS WAITING ON A PERSON, grouped by kind for the header's panel.
 *
 * The rows and their words are the route's (`GET /api/needs-you`); this only orders the groups,
 * names them, and says which way in each row offers. Nothing here is estimated or worded anew.
 */

/** The groups in the order a person meets them: deciding first, tidying up last. */
export const NeedsYouKindOrder = [
  'decision',
  'blocked',
  'failed',
  'waiting-input',
  'merge-ready',
  'stranded',
  'removal',
] as const

/** Each group's heading. A kind this browser does not know is headed by its own wire word. */
export const NeedsYouKindWords: Readonly<Record<string, string>> = {
  decision: 'Decisions',
  blocked: 'Blocked',
  failed: 'Failed',
  'waiting-input': 'Waiting for an input',
  'merge-ready': 'Ready to merge',
  stranded: 'Stranded workflows',
  removal: 'Unfinished removals',
}

export interface NeedsYouGroup {
  kind: string
  words: string
  items: NeedsYouItem[]
}

/** The rows grouped by kind, in `NeedsYouKindOrder`, unknown kinds after; each group keeps the route's order. */
export function groupNeedsYou(items: readonly NeedsYouItem[]): NeedsYouGroup[] {
  const byKind = new Map<string, NeedsYouItem[]>()
  for (const item of items) {
    const rows = byKind.get(item.kind)
    if (rows) rows.push(item)
    else byKind.set(item.kind, [item])
  }

  const known: readonly string[] = NeedsYouKindOrder
  const kinds = [...known.filter((kind) => byKind.has(kind)), ...[...byKind.keys()].filter((kind) => !known.includes(kind))]

  return kinds.map((kind) => ({ kind, words: NeedsYouKindWords[kind] ?? kind, items: byKind.get(kind) ?? [] }))
}

/** Who or what the row is about, after its team: the member, else the item's citation and title. */
export function needsYouSubject(item: NeedsYouItem): string {
  if (item.member) return item.member
  if (item.item) return `${itemLabel(item.item.id)} ${item.item.title}`
  return ''
}

/**
 * The decision a row asks, for Discuss with Concierge, or null: a decision row that names its
 * member, its workflow and the needs-decision row's seq. Anything less is offered Open instead.
 */
export function needsYouDecision(item: NeedsYouItem): DecisionAsk | null {
  if (item.kind !== 'decision' || !item.member) return null

  const seq = item.needsDecisionSeq
  const workflow = item.where.workflow ?? item.workflow
  if (!isRowNumber(seq) || !isRowNumber(workflow)) return null

  return { team: item.team.id, teamName: item.team.name || item.team.id, member: item.member, workflow, seq }
}

const isRowNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value > 0

/** What Open does on a row, by its `where`: one destination, named for the row's tooltip. */
export type NeedsYouOpen =
  | { to: 'member'; team: string; member: string }
  | { to: 'workflows'; team: string }
  | { to: 'card'; card: string }
  | { to: 'item'; item: number }
  | { to: 'git'; team: string }
  | { to: 'solution'; team: string }
  | { to: 'removals' }

export function needsYouOpen(item: NeedsYouItem): NeedsYouOpen | null {
  const { where } = item
  const team = where.team || item.team.id

  switch (where.view) {
    case 'workflow':
      // UX 4's deep link: the member's card on its board; a workflow nobody is named in opens the
      // team's Workflows dialog.
      return item.member ? { to: 'member', team, member: item.member } : { to: 'workflows', team }
    case 'card': {
      const card = where.card ?? item.card
      return card ? { to: 'card', card } : null
    }
    case 'backlog': {
      const id = where.item ?? item.item?.id
      return typeof id === 'number' ? { to: 'item', item: id } : null
    }
    case 'merge':
      return { to: 'git', team }
    case 'solution':
      return { to: 'solution', team }
    case 'removals':
      return { to: 'removals' }
    default:
      return null
  }
}

/** Open's tooltip: where it lands. */
export function needsYouOpenWords(open: NeedsYouOpen): string {
  switch (open.to) {
    case 'member':
      return "Open this member's card on its team's board"
    case 'workflows':
      return "Open this team's workflows"
    case 'card':
      return 'Open this card on the Kanban'
    case 'item':
      return 'Open this item in the Backlog'
    case 'git':
      return "Open this team's Git dialog to merge it"
    case 'solution':
      return "Open this solution's panel"
    case 'removals':
      return 'Open the Teams view, where unfinished removals are listed'
  }
}
