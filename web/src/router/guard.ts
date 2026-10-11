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
 *
 * The one address outside the app that `next` may name is a site's entry, `/sites/<team>/<site>/`
 * with its query: the Host sends a signed-out visit there (a bookmarklet's `?url=…`) to the door
 * and back. The Host serves it, not the router, so signing in leaves for it with a full navigation.
 */

/** A site's entry and its query, exactly: two plain segments, no dot segments, no fragment. */
const SiteEntry = /^\/sites\/[A-Za-z0-9_-][A-Za-z0-9_.-]*\/[A-Za-z0-9_-][A-Za-z0-9_.-]*\/(\?[^#\\]*)?$/;

/** The page's own navigation, apart so a test can watch it instead of leaving. */
export const page = { assign: (url: string) => window.location.assign(url) };

/** The site entry `next` names, or null when it names anything else. */
export function siteEntry(query: LocationQuery): string | null {
  const next = Array.isArray(query.next) ? query.next[0] : query.next;
  return typeof next === 'string' && SiteEntry.test(next) ? next : null;
}

/**
 * Goes where signing in continues to: a site entry by full navigation, else the in-app route.
 * The door asks twice (the form's done, and the session it sets), so the page is left only once.
 */
export function goOnward(query: LocationQuery, replace: (path: string) => unknown, left = { done: false }): void {
  const entry = siteEntry(query);
  if (entry === null) {
    void replace(returnPath(query));
  } else if (!left.done) {
    left.done = true;
    page.assign(entry);
  }
}

/** Where signing in continues to: the remembered in-app route, else the Console. */
export function returnPath(query: LocationQuery): string {
  const next = Array.isArray(query.next) ? query.next[0] : query.next;
  if (typeof next !== 'string') return '/console';
  if (!next.startsWith('/') || next.startsWith('//') || next.includes('\\')) return '/console';
  if (next === '/' || next.startsWith('/?') || next.startsWith('/login')) return '/console';
  if (next.startsWith('/sites/')) return '/console';
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
export async function sessionGuard(to: RouteLocationNormalized): Promise<boolean | RouteLocationRaw> {
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
  if ((to.path === '/' || to.path === '/login') && session.user) {
    const entry = siteEntry(to.query);
    if (entry === null) return returnPath(to.query);
    page.assign(entry);
    return false;
  }

  if (isPublic) return true;

  return session.user ? true : signInRedirect(to);
}

