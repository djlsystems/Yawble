import { Unauthorized } from '../api/client'

/**
 * A TEAM CREATE CAN OUTLAST THE WAIT FOR IT. Its repository is cloned before the answer comes, and a
 * large clone takes minutes. The Host finishes the create (and a dispatch into it) even when the
 * browser stops waiting, so a dialog says so while it waits, and says so again if the wait is cut
 * off, instead of reporting a failure that did not happen.
 */

/** What a dialog shows while a create is in flight. */
export function creatingLine(name: string): string {
  const team = name.trim() || 'the team'
  return (
    `Creating ${team}: cloning its repository. This can take a few minutes for a large ` +
    'repository; closing this window does not stop it.'
  )
}

/** What a dialog says when the wait was cut off: the create carries on on the server. */
export function carriesOnMessage(name: string): string {
  const team = name.trim() || 'the team'
  return (
    `The connection was lost while ${team} was being created. The create carries on on the ` +
    'server: the team appears on the Teams list when it is done.'
  )
}

/**
 * WHETHER THE WAIT WAS CUT OFF rather than answered. A refusal carries the HTTP status it came with
 * (see `send` in `api/client.ts`); a lost connection or an aborted request has none. A gateway that
 * gave up waiting (504) did not stop the Host either.
 */
export function waitWasCutOff(cause: unknown): boolean {
  if (cause instanceof Unauthorized) return false
  const status = (cause as { status?: unknown } | null)?.status
  return typeof status !== 'number' || status === 504
}
