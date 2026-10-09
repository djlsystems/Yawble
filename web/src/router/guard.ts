import type { LocationQuery, RouteLocationNormalized, RouteLocationRaw } from 'vue-router';
import { useSessionStore } from '../stores/session';

/**
 * THE SIGN-IN GATE, and where a signed-out visitor goes back to afterwards.
 *
 * A signed-out visit to a gated route is sent to the front door with the route it asked for in
 * `?next=`; signing in continues to it rather than to the Console. That is what lets a deep link
 * (`#/solutions/install?folder=…`) survive the sign-in it needs. `next` is only ever an in-app path:
 * anything else - another origin, `//host`, the door itself - falls back to the Console, so the
 * query cannot send a person somewhere they did not ask to go.
 */

/** Where signing in continues to: the remembered in-app route, else the Console. */
export function returnPath(query: LocationQuery): string {
  const next = Array.isArray(query.next) ? query.next[0] : query.next;
  if (typeof next !== 'string') return '/console';
  if (!next.startsWith('/') || next.startsWith('//') || next.includes('\\')) return '/console';
  if (next === '/' || next.startsWith('/?') || next.startsWith('/login')) return '/console';
  return next;
}

/** The front door, remembering `to` unless it is the Console itself (the door's own default). */
export function signInRedirect(to: Pick<RouteLocationNormalized, 'fullPath' | 'path'>): RouteLocationRaw {
  return to.path === '/console' ? '/' : { path: '/', query: { next: to.fullPath } };
}

/**
 * Asked once, not per navigation: /me is cheap, but re-checking on every route change makes every
 * navigation wait on a round trip. `checked` is what distinguishes "signed out" from "have not
 * asked yet" - without it the first navigation redirects away before /me has answered.
 */
export async function sessionGuard(to: RouteLocationNormalized): Promise<true | RouteLocationRaw> {
  const session = useSessionStore();
  const isPublic = to.matched.some((record) => record.meta.publicRoute);

  if (!session.checked) {
    try {
      await session.refresh();
    } catch {
      // A guard that rejects renders nothing at all - App.vue is a bare router-view with no
      // error boundary - so a backend that is down would leave a blank page with no way forward.
      // The landing page needs no backend to render, so it is the one safe destination; its
      // button will surface the real error the moment it is pressed.
      return isPublic ? true : signInRedirect(to);
    }
  }

  // Someone already signed in has no business on the front door: `/` and `/login` go on to where
  // they were headed, decided here - the session is already known - so the sign-in form never
  // draws for a moment before the page's own redirect, which a person saw as a flash.
  if ((to.path === '/' || to.path === '/login') && session.user) return returnPath(to.query);

  if (isPublic) return true;

  return session.user ? true : signInRedirect(to);
}
