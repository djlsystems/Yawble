import type { Message } from '../api/types'

/** Why a finished run woke nobody, as the feed and the run list mark it. */
export interface RunMark {
  label: string
  tooltip: string
}

/** A run that finished quietly: its `completed` row carries `quiet: true`. */
export const QuietMark: RunMark = {
  label: 'quiet',
  tooltip: 'This run finished quietly: nobody was woken by it.',
}

/**
 * A run whose workflow the platform declared complete as it ended, because its owner cannot declare
 * (a person told a plugin member directly): its `completed` row carries `workflowDeclared: true`,
 * and the Manager was not woken into the closed workflow.
 */
export const WorkflowDeclaredMark: RunMark = {
  label: 'workflow declared',
  tooltip: 'The platform declared this workflow complete when this run finished, so the Manager was not woken.',
}

/**
 * The mark for a feed row, or null. Only a `completed` row is ever marked: a failure always wakes.
 * A payload that does not parse is not marked, which is how a row from before the keys reads.
 */
export function runMarkOf(message: Message): RunMark | null {
  if (message.type !== 'agentContainer.completed') return null

  try {
    const payload: unknown = JSON.parse(message.payload)
    if (typeof payload !== 'object' || payload === null) return null

    const row = payload as Record<string, unknown>
    if (row.quiet === true) return QuietMark
    if (row.workflowDeclared === true) return WorkflowDeclaredMark
    return null
  } catch {
    return null
  }
}
