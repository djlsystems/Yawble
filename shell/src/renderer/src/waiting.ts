import type { WorkerWaitState } from '../../shared/api';
import { displayName } from './faces/faces';
export { cardState } from './faces/faces';

// What a member's not-started work waits for, in the same words on every screen (the Workflows page's Who column
// and status tip, the member pane, the team tiles). Only "queued" is a wait for a run slot; the rest never were.

export const WorkerWaitStates: readonly WorkerWaitState[] = ['queued', 'waits-for-run', 'held', 'pending'];
export const isWaitState = (s: unknown): s is WorkerWaitState => WorkerWaitStates.includes(s as WorkerWaitState);

/**
 * "queued for a run slot", "waits for Developer Ada's run to end", "waits: team paused", "has work waiting". A
 * waits-for-run without a sender says "another member's run"; a state this build does not know reads as pending.
 */
export function waitSays(state: string, waitsFor?: string | null): string {
  switch (state) {
    case 'queued': return 'queued for a run slot';
    case 'waits-for-run': return `waits for ${waitsFor?.trim() ? `${displayName(waitsFor.trim())}'s` : "another member's"} run to end`;
    case 'held': return 'waits: team paused';
    default: return 'has work waiting';
  }
}

/** A workflow worker's words: running, the heavy-work lease, or what its delivery waits for. */
export function workerSays(m: { state: string; waitsFor?: string | null }): string {
  if (m.state === 'running') return 'running';
  if (m.state === 'lease') return 'waiting for the heavy-work lease';
  return waitSays(m.state, m.waitsFor);
}
