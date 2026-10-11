import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { RouteLocationNormalized } from 'vue-router';
import { useSessionStore } from '../../stores/session';
import { page, returnPath, sessionGuard, siteEntry } from '../guard';

/**
 * A site's entry, `/sites/<team>/<site>/?url=…`, is served by the Host, not the web app: a
 * signed-out bookmarklet visit comes back through the door with it in `next`, and signing in
 * continues there with a full navigation. Nothing else outside the app is let through.
 */
const Entry = '/sites/b0053-site-address/jobs/?url=https%3A%2F%2Fexample.com%2Fjobs%2F1';

describe('a site entry in next', () => {
  it('is accepted with its query, and without one', () => {
    expect(siteEntry({ next: Entry })).toBe(Entry);
    expect(siteEntry({ next: '/sites/team-one/tracker/' })).toBe('/sites/team-one/tracker/');
  });

  it('is refused on another origin, protocol-relative, or with a backslash', () => {
    expect(siteEntry({ next: `https://evil.example${Entry}` })).toBeNull();
    expect(siteEntry({ next: `//evil.example${Entry}` })).toBeNull();
    expect(siteEntry({ next: '/sites/team\\..\\evil/jobs/' })).toBeNull();
    expect(siteEntry({ next: '/sites/team/jobs/?url=a\\b' })).toBeNull();
    expect(returnPath({ next: `//evil.example${Entry}` })).toBe('/console');
  });

  it('is refused when it is not exactly an entry: no site, deeper paths, dot segments', () => {
    expect(siteEntry({ next: '/sites/team/' })).toBeNull();
    expect(siteEntry({ next: '/sites/team/jobs' })).toBeNull();
    expect(siteEntry({ next: '/sites/team/jobs/_c/abc/' })).toBeNull();
    expect(siteEntry({ next: '/sites/../api/' })).toBeNull();
    expect(siteEntry({ next: '/sites/team/../' })).toBeNull();
    expect(siteEntry({ next: '/sites/team/jobs/#x' })).toBeNull();
    expect(siteEntry({ next: '/console' })).toBeNull();
    expect(siteEntry({})).toBeNull();
  });
});

describe('the door, already signed in, with a site entry in next', () => {
  beforeEach(() => setActivePinia(createPinia()));
  afterEach(() => vi.restoreAllMocks());

  it('leaves the app for the entry with a full navigation', async () => {
    const assign = vi.spyOn(page, 'assign').mockImplementation(() => {});
    const session = useSessionStore();
    session.$patch({ checked: true, user: { id: 'u1', email: 'person@example.com' } as never });

    const door = { path: '/', fullPath: '/', query: { next: Entry }, matched: [{ meta: { publicRoute: true } }] };
    expect(await sessionGuard(door as unknown as RouteLocationNormalized)).toBe(false);
    expect(assign).toHaveBeenCalledWith(Entry);
  });
});
