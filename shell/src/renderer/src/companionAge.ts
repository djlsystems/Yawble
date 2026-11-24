/**
 * B141: how long ago a companion request was made, for its row in the pending list: "asked 3 min ago", "asked 2 h ago",
 * "asked 2 days ago". `asked` is the core's ISO UTC time (companion.list actions[].asked); absent (an older core) or
 * unreadable: null, and the row shows no age. An old request is never hidden or greyed for its age.
 */
export function askedAgo(asked: string | null | undefined, now: number = Date.now()): string | null {
  if (!asked) return null;
  const t = new Date(asked).getTime();
  if (Number.isNaN(t)) return null;
  const s = Math.max(0, (now - t) / 1000);
  if (s < 60) return 'asked just now';
  if (s < 3600) return `asked ${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `asked ${Math.floor(s / 3600)} h ago`;
  const d = Math.floor(s / 86400);
  return `asked ${d} ${d === 1 ? 'day' : 'days'} ago`;
}