import type { WorkflowSummary } from '../../shared/api';
import { errorCode, errorText } from './api';

/**
 * WORKFLOW EXPIRY: an open workflow nobody touches for the team's workflowExpiryDays (default 3) is closed by
 * the app as "expired". The core says when an idle one would expire (expiresAt; null while a run, a wake or a trigger
 * is pending). One that waits on the person's answer is warned about a day before: a Needs-you item with Extend.
 */

/** How far ahead of expiresAt the warning shows: the core warns at N-1 days of N. */
export const ExpiryWarnMs = 24 * 3600_000;

/** An open workflow that will expire within a day. */
export function expiresSoon(w: Pick<WorkflowSummary, 'state' | 'expiresAt'>, now = Date.now()): boolean {
  if (w.state !== 'open' || !w.expiresAt) return false;
  const at = Date.parse(w.expiresAt);
  return !Number.isNaN(at) && at - now < ExpiryWarnMs;
}

/** A future time compactly: "in 5 h", "in 20 min", "within a minute". */
export function until(iso: string, now = Date.now()): string {
  const s = (Date.parse(iso) - now) / 1000;
  if (Number.isNaN(s) || s < 60) return 'within a minute';
  if (s < 3600) return `in ${Math.floor(s / 60)} min`;
  return `in ${Math.floor(s / 3600)} h`;
}

/** The Needs-you item's words: "#12 will expire tomorrow; it waits on your answer to question 3". */
export function expiryWarningText(w: Pick<WorkflowSummary, 'id' | 'question'>): string {
  return `#${w.id} will expire tomorrow; it waits on your answer to ${w.question != null ? `question ${w.question}` : 'a question'}`;
}

/** An Extend's error: the core refuses an ended workflow with already_ended (it ended meanwhile). */
export function extendErrorText(e: unknown, workflow: number): string {
  return errorCode(e) === 'already_ended'
    ? `#${workflow} has already ended, so there is nothing to extend. Telling its team something in it reopens it.`
    : errorText(e);
}
