import type { RunOutcome } from '../api/types';

/**
 * HOW AN EARLIER RUN READS IN THE WATCH DIALOG'S LIST: when it started, how long it took and
 * how it ended. Kept out of the component so the words are one place, and testable without a mount.
 */

const Outcomes: Record<RunOutcome, string> = {
  completed: 'completed',
  handedBack: 'handed back',
  blocked: 'blocked',
  failed: 'failed',
};

/** `handedBack` reads `handed back`; an outcome this build does not know is shown as sent. */
export function runOutcomeLabel(outcome: RunOutcome | string): string {
  return Outcomes[outcome as RunOutcome] ?? outcome;
}

/**
 * `42s`, `3m 05s`, `1h 02m`: to the second under an hour, to the minute over it. Null - the run's
 * start is not in the log - is `—`, never a made-up `0s`.
 */
export function runDuration(ms: number | null): string {
  if (ms === null) return '—';
  const seconds = Math.max(0, Math.round(ms / 1000));
  if (seconds < 60) return `${seconds}s`;

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${String(seconds % 60).padStart(2, '0')}s`;

  return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, '0')}m`;
}

/** When a run started, in the viewer's own zone: `26 Sep 09:34`. Null reads `start unknown`, not 1970. */
export function runStartedText(iso: string | null): string {
  if (iso === null) return 'start unknown';
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return iso;

  return at.toLocaleString([], { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });
}

/** The full start time for a tooltip, or none when the start is unknown or unreadable. */
export function runStartedTitle(iso: string | null): string | undefined {
  if (iso === null) return undefined;
  const at = new Date(iso);
  return Number.isNaN(at.getTime()) ? undefined : at.toLocaleString();
}
