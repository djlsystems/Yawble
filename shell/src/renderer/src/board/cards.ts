import type { BoardCard, CardStatus } from '../../../shared/api';
import { memberStateOf, type MemberState } from '../faces/faces';

/** How each status reads and its colour token (the lane comes from the core). */
export const StatusLabel: Record<CardStatus, string> = {
  queued: 'Queued', running: 'Running', blocked: 'Blocked', 'needs-decision': 'Needs your decision', failed: 'Failed',
  completed: 'Run finished', handback: 'Handed back', done: 'Done',
};

/** The colour is a theme variable (styles.css --status-*), so it has a light and a dark value. */
export const StatusColor: Record<CardStatus, string> = {
  queued: 'var(--status-queued)', running: 'var(--status-running)', blocked: 'var(--status-blocked)',
  'needs-decision': 'var(--status-decision)', failed: 'var(--status-failed)',
  completed: 'var(--status-completed)', handback: 'var(--status-completed)', done: 'var(--status-done)',
};

/** The core's reason for a run the person stopped (Bus BusEngine.StoppedReason), as a card's "failed" trail line says it. */
export const StoppedReason = 'stopped by the person';

/**
 * B097: the card's run was stopped by the person, not failed. The core marks such a card "failed" (its member's run
 * was recorded failed, class "stopped"); the member's state says stopped (attention "stopped", or class "stopped").
 */
export function cardStopped(card: Pick<BoardCard, 'status' | 'memberState'>): boolean {
  if (card.status !== 'failed' || !card.memberState) return false;
  const m = card.memberState;
  return m.attention === 'stopped' || (m.attention === 'failed' && m.lastRunFailureClass === 'stopped');
}

/** The neutral look of a stopped run (B097): the member state's own colour, never the failure red. */
export const StoppedLook = { label: 'Stopped', color: 'var(--state-stopped)' } as const;

/** How a card's status reads and its colour: a stopped run reads "Stopped" in the neutral colour (B097). */
export function cardStatusLook(card: Pick<BoardCard, 'status' | 'memberState'>): { label: string; color: string; stopped: boolean } {
  if (cardStopped(card)) return { ...StoppedLook, stopped: true };
  return { label: StatusLabel[card.status], color: StatusColor[card.status], stopped: false };
}

/** A trail line's kind as shown: the core's "failed" line for a run the person stopped reads "stopped" (B097). */
export function trailKind(t: { kind: string; text: string }): string {
  return t.kind === 'failed' && t.text.trim().toLowerCase() === StoppedReason ? 'stopped' : t.kind;
}

/** The ring a card's member wears: the member's own state when it is known, otherwise the card's. */
export function cardFace(card: BoardCard): MemberState {
  if (card.memberState) return memberStateOf(card.memberState);
  switch (card.status) {
    case 'running': return 'running';
    case 'needs-decision': return 'decision';
    case 'blocked': return 'blocked';
    case 'failed': return 'failed';
    default: return 'idle';
  }
}

/** Newest change first within a lane, then by id. */
export function inLane<T extends { lane: string; updatedAt: string; id: number }>(cards: T[], lane: string): T[] {
  return cards.filter((c) => c.lane === lane).sort((a, b) => (a.updatedAt < b.updatedAt ? 1 : a.updatedAt > b.updatedAt ? -1 : b.id - a.id));
}

export function ago(at: string): string {
  const s = Math.max(0, (Date.now() - new Date(at).getTime()) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return new Date(at).toLocaleDateString();
}
