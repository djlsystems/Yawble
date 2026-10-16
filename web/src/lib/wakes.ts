import type { PendingWake } from '../api/types'
import { crossesDays, localMinute, wireInstant } from './localTime'

/**
 * A PENDING WAKE IN WORDS, one way on every screen that shows one - the member's card, the Kanban
 * card and the Monitor: "wakes at 9:41 PM: check the certificate". The time is the reader's clock
 * to the minute, with its date in front when it is not today; the Host sends it in UTC.
 */
export function wakeTime(dueAt: string, now = Date.now()): string {
  const due = wireInstant(dueAt)
  if (Number.isNaN(due)) return dueAt

  return localMinute(due, { date: crossesDays(now, due) })
}

/** "wakes at 9:41 PM: <note>", or without the note when there is none. */
export function wakeLine(wake: Pick<PendingWake, 'dueAt' | 'note'>, now = Date.now()): string {
  const at = `wakes at ${wakeTime(wake.dueAt, now)}`
  const note = wake.note.trim()

  return note === '' ? at : `${at}: ${note}`
}
