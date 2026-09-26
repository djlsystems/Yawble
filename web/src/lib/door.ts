/**
 * Which mode the landing page is in.
 *
 * Three states, decided from what the server already told the session store rather than by
 * rendering a different page. Kept as a pure function so the decision can be tested without a
 * DOM, a router or a browser - the destination is the part that is easy to get subtly wrong, and
 * it is exactly the part a component test would not cover cheaply.
 *
 *   signed in       -> console (the page redirects to /console)
 *   no account yet  -> create  (the form creates the first account)
 *   accounts exist  -> login   (the form logs in)
 *
 * `LandingPage.vue` reads `doorFor` alone; the words for each state live in its template.
 */
export type Door = 'console' | 'create' | 'login';

export interface DoorState {
  user: unknown | null;
  /** The first-account flag from the session store - `needsAdmin` on the wire. */
  needsFirstAccount: boolean;
}

export function doorFor(session: DoorState): Door {
  if (session.user) return 'console';

  return session.needsFirstAccount ? 'create' : 'login';
}
