import type { TeamSummary } from '../../shared/api';
import { WaitingLabel } from './reportFormat';

/**
 * What a team is doing, for its card and its sidebar dot. One order everywhere: running > waiting > paused > queued > idle.
 * B130: waiting is a member with an open needs_decision (the core's TeamSummary.waiting; an older core leaves it out: 0).
 */
export type TeamState = 'running' | 'waiting' | 'queued' | 'paused' | 'idle';

export function teamState(t: Pick<TeamSummary, 'running' | 'queued' | 'paused' | 'waiting'>): TeamState {
  if (t.running > 0) return 'running';
  if ((t.waiting ?? 0) > 0) return 'waiting';
  if (t.paused) return 'paused';
  if (t.queued > 0) return 'queued';
  return 'idle';
}

export const teamStateLabel: Record<TeamState, string> = {
  running: 'Running', waiting: WaitingLabel, queued: 'Queued', paused: 'Paused', idle: 'Idle',
};
