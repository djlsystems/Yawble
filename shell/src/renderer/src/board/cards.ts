import type { BoardCard, CardStatus } from '../../../shared/api';
import { memberStateOf, type MemberState } from '../faces/faces';

/** How each status reads and its colour token (the lane comes from the core). */
export const StatusLabel: Record<CardStatus, string> = {
  queued: 'Queued', running: 'Running', blocked: 'Blocked', 'needs-decision': 'Needs your decision', failed: 'Failed',
  completed: 'Run finished', handback: 'Handed back', done: 'Done',
};

export const StatusColor: Record<CardStatus, string> = {
  queued: '#64748b', running: '#2563eb', blocked: '#b45309', 'needs-decision': '#c2410c', failed: '#b91c1c',
  completed: '#0f766e', handback: '#0f766e', done: '#15803d',
};

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
