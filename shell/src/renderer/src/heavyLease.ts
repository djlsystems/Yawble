import type { AgentCard, LeaseSnapshot } from '../../shared/api';

/** "09:05" in the person's own time zone. */
export function clockTime(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

/**
 * The Monitor's line, team and member so it is unambiguous across teams:
 * "Heavy work: Yawble Engineering Team / DeveloperPriya — full test suite (since 09:05) · 2 waiting". Null (the line is hidden)
 * when nobody holds the lease and nobody waits, or when the core sent none (an older core).
 */
export function heavyLine(heavy: LeaseSnapshot | null | undefined): string | null {
  if (!heavy || (heavy.holders.length === 0 && heavy.queue.length === 0)) return null;
  const held = heavy.holders.length === 0
    ? 'free'
    : heavy.holders.map((h) => `${h.team} / ${h.member} — ${h.what} (since ${clockTime(h.since)})`).join(', ');
  return `Heavy work: ${held}${heavy.queue.length ? ` · ${heavy.queue.length} waiting` : ''}`;
}

/** The member card's words while it holds or waits for the lease; null otherwise. */
export function leaseText(card: Pick<AgentCard, 'lease' | 'leaseWhat' | 'leasePosition'>): string | null {
  if (card.lease === 'holding') return `holds the heavy-work lease${card.leaseWhat ? `: ${card.leaseWhat}` : ''}`;
  if (card.lease === 'waiting') {
    return `waiting for the heavy-work lease${card.leasePosition ? ` (position ${card.leasePosition})` : ''}${card.leaseWhat ? `: ${card.leaseWhat}` : ''}`;
  }
  return null;
}
