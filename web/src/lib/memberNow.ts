import type { Message } from '../api/types'
import { detail } from './summarise'

/** The rows that end a run, whose words are the run's outcome. */
const OutcomeTypes = new Set([
  'agentContainer.completed',
  'agentContainer.failed',
  'agentContainer.blocked',
  'agentContainer.needsDecision',
])

const ProgressType = 'agentContainer.progress'
const StartedType = 'agentContainer.started'

/** What the member card's "now" line says, and which of the two it is. */
export interface MemberNow {
  kind: 'progress' | 'outcome'
  text: string
}

/** The first line of a row's words that has any, trimmed; '' when none does. */
function firstLine(message: Message): string {
  return detail(message).split('\n').map((line) => line.trim()).find((line) => line !== '') ?? ''
}

/**
 * WHAT A MEMBER IS DOING, IN ONE LINE, from the rows its card already holds (newest first) - never a
 * second stream. While running: the newest progress text of the run in flight (none before its
 * first report: an earlier run's progress is not what it is doing now). While idle: the first line
 * of its last outcome - finished, failed, blocked or needs you. Nothing when the rows say neither.
 */
export function memberNow(feed: readonly Message[], running: boolean): MemberNow | null {
  for (const message of feed) {
    if (running) {
      if (message.type === StartedType) return null
      if (message.type !== ProgressType) continue
      const text = firstLine(message)
      if (text !== '') return { kind: 'progress', text }
      continue
    }

    if (!OutcomeTypes.has(message.type)) continue
    const text = firstLine(message)
    return text === '' ? null : { kind: 'outcome', text }
  }

  return null
}
