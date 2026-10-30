import type { Team } from '../api/types'

/**
 * WHAT A MEMBER IS DOING, as its avatar's ring draws it on every view: the Monitor's canvas, the
 * board's member card and the Kanban card. One derivation from the snapshot, so the three agree.
 */
export type MemberState = 'idle' | 'running' | 'waiting' | 'blocked' | 'decision' | 'failed'

/** A member's state from its snapshot: a mark first (failed, then needs you, then blocked), then its run. */
export function memberStateOf(container: Team['containers'][number]): MemberState {
  // Waiting for a person to set something (`needs-person`) is Needs you, never a failure.
  if (container.needsPerson || (container.failed && container.failureClass === 'needs-person')) return 'decision'
  if (container.failed) return 'failed'
  if (container.needsDecision) return 'decision'
  if (container.blocked) return 'blocked'
  if (container.state === 'Running') return container.held ? 'waiting' : 'running'
  if (container.queueDepth > 0 || container.held) return 'waiting'
  return 'idle'
}

/** The state token (`--os-state-*`) a state's ring is drawn in. */
export function stateToken(state: MemberState): string {
  return state === 'decision' ? 'needs-you' : state
}

export function stateWords(state: MemberState): string {
  switch (state) {
    case 'running':
      return 'Running'
    case 'waiting':
      return 'Waiting to start'
    case 'blocked':
      return 'Blocked'
    case 'decision':
      return 'Needs you'
    case 'failed':
      return 'Last run failed'
    default:
      return 'Idle'
  }
}
