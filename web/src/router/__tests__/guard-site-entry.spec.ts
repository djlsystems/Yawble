import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { RouteLocationNormalized } from 'vue-router';
import { useSessionStore } from '../../stores/session';
import { goOnward, page, returnPath, sessionGuard, siteEntry } from '../guard';
import { fakeHost, reply, type Call } from '../../test/solutionFixtures';

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
  // W27510: a new tab the browser sent without the SameSite=Strict cookie reaches the door with the
  // site's entry in `next`. The door must open THAT site, by a capability that needs no cookie, and
  // never send the person to the Console (the board) or back to the entry (which can bounce again).
  const Capability = '/sites/b0053-site-address/jobs/_c/CfDJ8tok_-1/';
  let calls: Call[] = [];
  let answer: Response;

  beforeEach(() => {
    setActivePinia(createPinia());
    calls = [];
    answer = reply(200, { url: Capability });
    vi.stubGlobal('fetch', vi.fn(fakeHost([(call) => (call.method === 'POST' && call.url.endsWith('/open') ? answer : undefined)], calls)));
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('draws the door (to open the site) rather than redirecting to the Console', async () => {
    const assign = vi.spyOn(page, 'assign').mockImplementation(() => {});
    const session = useSessionStore();
    session.$patch({ checked: true, user: { id: 'u1', email: 'person@example.com' } as never });

    const door = { path: '/', fullPath: '/', query: { next: Entry }, matched: [{ meta: { publicRoute: true } }] };
    expect(await sessionGuard(door as unknown as RouteLocationNormalized)).toBe(true);
    expect(assign).not.toHaveBeenCalled();
  });

  it('goes on to the site\'s capability with the entry\'s query, once, and never to the Console', async () => {
    const leave = vi.spyOn(page, 'replace').mockImplementation(() => {});
    const assign = vi.spyOn(page, 'assign').mockImplementation(() => {});
    const route = vi.fn();
    const left = { done: false };

    await goOnward({ next: Entry }, route, left);
    await goOnward({ next: Entry }, route, left);

    expect(calls.map((call) => `${call.method} ${call.url}`)).toEqual(['POST /api/teams/b0053-site-address/sites/jobs/open']);
    expect(leave).toHaveBeenCalledTimes(1);
    expect(leave).toHaveBeenCalledWith(`${Capability}?url=https%3A%2F%2Fexample.com%2Fjobs%2F1`);
    expect(assign).not.toHaveBeenCalled();
    expect(route).not.toHaveBeenCalled();
  });

  it('says the Host\'s refusal on the door and stays, rather than landing on the board', async () => {
    answer = reply(404, { error: 'This site is not published.' });
    const leave = vi.spyOn(page, 'replace').mockImplementation(() => {});
    const route = vi.fn();
    const refused = vi.fn();

    await goOnward({ next: Entry }, route, { done: false }, refused);

    expect(refused).toHaveBeenCalledWith('This site is not published.');
    expect(leave).not.toHaveBeenCalled();
    expect(route).not.toHaveBeenCalled();
  });

  it('does not follow an answer that is not that site\'s capability path', async () => {
    answer = reply(200, { url: 'https://evil.example/sites/b0053-site-address/jobs/_c/x/' });
    const leave = vi.spyOn(page, 'replace').mockImplementation(() => {});
    const refused = vi.fn();

    await goOnward({ next: Entry }, vi.fn(), { done: false }, refused);

    expect(leave).not.toHaveBeenCalled();
    expect(refused).toHaveBeenCalledTimes(1);
  });

  it('keeps every other next to an in-app route: other origins and other /sites/ paths go to the Console', async () => {
    const route = vi.fn();
    for (const next of [`https://evil.example${Entry}`, `//evil.example${Entry}`, '/sites/team/jobs/_c/abc/', '/sites/team/']) {
      await goOnward({ next }, route);
      expect(route).toHaveBeenLastCalledWith('/console');
      expect(returnPath({ next })).toBe('/console');
    }
    expect(calls).toEqual([]);
  });
});
