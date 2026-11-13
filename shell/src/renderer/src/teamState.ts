import type { TeamSummary } from '../../shared/api';

/** What a team is doing, for its card and its sidebar dot: running beats queued, a pause beats idle. */
export type TeamState = 'running' | 'queued' | 'paused' | 'idle';

export function teamState(t: Pick<TeamSummary, 'running' | 'queued' | 'paused'>): TeamState {
  if (t.running > 0) return 'running';
  if (t.paused) return 'paused';
  if (t.queued > 0) return 'queued';
  return 'idle';
}

export const teamStateLabel: Record<TeamState, string> = { running: 'Running', queued: 'Queued', paused: 'Paused', idle: 'Idle' };
