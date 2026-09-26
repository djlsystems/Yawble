import type { ConciergeSettings } from '../api/types';

/**
 * WHAT THE CONCIERGE PANEL SAYS BEFORE IT OPENS A SOCKET, decided from `GET /api/concierge`.
 *
 * Three outcomes, and the third is the reason this exists: a launch that cannot start must not
 * reach the person as a socket close reason printed into an empty terminal. It is a sentence
 * and a link to where it is fixed, and no socket is opened at all.
 *
 * - `ready`: nothing to say; attach as always. Also what a server without `effective` gets, since
 *   a missing measurement is not a problem to report.
 * - `notice`: attach, and say one thing above the terminal (a default is in use, or the agent is
 *   not signed in).
 * - `blocked`: do not attach; the sentence is all there is.
 *
 * `lead` is the sentence up to the link; the panel renders `{lead} Tenant Settings.` with those two
 * words as the link, so every sentence here ends by naming where to go.
 */
export type ConciergePreflight =
  | { kind: 'ready' }
  | { kind: 'notice'; lead: string; detail: string | null }
  | { kind: 'blocked'; lead: string; detail: string | null };

export function conciergePreflight(settings: ConciergeSettings | null): ConciergePreflight {
  const effective = settings?.effective;

  if (!effective) return { kind: 'ready' };

  const { agent, auth } = effective;
  const detail = auth.detail;

  if (agent === null) {
    return { kind: 'blocked', lead: 'No installed agent can run the Concierge. Sign one in from a terminal or add a key, then choose it in', detail };
  }

  if (!auth.installed) {
    return { kind: 'blocked', lead: `${agent} is not installed; choose another agent in`, detail };
  }

  if (!auth.signedIn) {
    return { kind: 'notice', lead: `${agent} is installed but not signed in; sign in from a terminal or add a key, or choose another agent in`, detail };
  }

  // What the Concierge is TOLD is the built-in Concierge prompt, chosen by role - so the
  // agent is the only default left to say.
  if (effective.agentSource === 'default') {
    return { kind: 'notice', lead: `Using ${agent}, the first signed-in agent; change it in`, detail: null };
  }

  return { kind: 'ready' };
}
