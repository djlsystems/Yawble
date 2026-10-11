import type { AgentAuthReport } from '../api/types';

/**
 * The first-run guide: whether this browser has dismissed it, and what the platform says about
 * whether any agent is signed in.
 *
 * A per-viewer preference, kept the way the theme and the board size are: in `localStorage`, never
 * sent anywhere, behind a wrapper because storage throws in a private window with cookies blocked
 * and a guide is not worth a crash.
 */
const StorageKey = 'harness.firstRunGuide';

export function readGuideDismissed(): boolean {
  try {
    return localStorage.getItem(StorageKey) === 'dismissed';
  } catch {
    return false;
  }
}

export function writeGuideDismissed(): void {
  try {
    localStorage.setItem(StorageKey, 'dismissed');
  } catch {
    // Dismissed for this session only.
  }
}

/** What the guide may say about sign-in. */
export type AgentSignIn = 'signed-in' | 'signed-out' | 'not-measured';

/**
 * Whether ANY agent is signed in, from `GET /api/agents/auth` and nothing else. `null` is the
 * answer that did not arrive.
 *
 * ONE MEASURED YES IS ENOUGH: a person with one signed-in agent can use the Concierge.
 *
 * SIGNED OUT ONLY WHEN EVERY PRESET WAS MEASURED AS NOT SIGNED IN (or as not installed, which
 * cannot be signed in). A single preset the probe could not ask, one held by an update, or an empty
 * list leaves the answer not measured: the guide must never tell a person nothing is signed in on
 * the strength of a question nobody asked.
 */
export function agentSignIn(reports: readonly AgentAuthReport[] | null): AgentSignIn {
  if (!reports || reports.length === 0) return 'not-measured';

  if (reports.some((report) => !report.updating && report.installed === true && report.authenticated === true)) {
    return 'signed-in';
  }

  const measuredOut = (report: AgentAuthReport) =>
    !report.updating && (report.installed === false || (report.installed === true && report.authenticated === false));

  return reports.every(measuredOut) ? 'signed-out' : 'not-measured';
}
