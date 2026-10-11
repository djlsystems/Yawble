import type { TeamWorkflows } from '../api/types';
import type { TeamStatus } from './teamKpis';
import { teamStatusText } from './teamsTable';

/**
 * THE ICON BESIDE A TEAM'S ACTIVITY CHART, on the Teams table and on the team's own strip.
 *
 * ONE WORD, ONE ICON, AND THE WORD IS THE STATUS COLUMN'S. The icon is only a quicker way to read
 * the roster answer `teamStatus` already gives, so it can never disagree with the Status column.
 *
 * `idle` IS SPLIT BY HOW THE LATEST WORKFLOW ENDED, and only by that. The platform cannot tell
 * finished work from work that stopped part-way; only a declaration says which. A Manager's own
 * declaration (`Completed`) is the green check; a person's close (`Closed`) is a grey one, finished
 * but not confirmed by the team; a team that has run no workflow gets no icon at all, because there
 * is nothing it could have finished.
 */
export type StatusTone = 'positive' | 'neutral' | 'active' | 'warning' | 'negative';

export interface TeamStatusIcon {
  icon: string;
  tone: StatusTone;
  words: string;
  /** Turns while the team works. */
  spin?: boolean;
}

export function teamStatusIcon(
  status: TeamStatus,
  workflows: TeamWorkflows | null,
  held: readonly string[] = [],
): TeamStatusIcon | null {
  const words = teamStatusText(status, held);

  switch (status) {
    case 'misconfigured':
      return { icon: 'warning', tone: 'negative', words };
    case 'failed':
      return { icon: 'error', tone: 'negative', words };
    case 'blocked':
      return { icon: 'block', tone: 'warning', words: 'blocked: waiting on a person' };
    case 'undeclared':
      return { icon: 'help', tone: 'warning', words: 'undeclared: nothing is running and nobody has said it is finished' };
    case 'paused':
      return { icon: 'pause_circle', tone: 'neutral', words };
    case 'running':
      return { icon: 'sync', tone: 'active', words, spin: true };
    case 'waiting':
    case 'queued':
      return { icon: 'hourglass_top', tone: 'neutral', words };
    case 'idle': {
      const latest = workflows?.workflows[0]?.state ?? null;
      if (latest === 'Completed') return { icon: 'check_circle', tone: 'positive', words: 'completed' };
      if (latest === 'Closed') return { icon: 'check_circle', tone: 'neutral', words: 'closed by a person' };
      return null;
    }
  }
}
