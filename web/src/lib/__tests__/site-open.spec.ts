// W27510: OPENING A SITE FROM THE APP. The new tab is sent to a capability the app asked for over
// its own signed-in request, never to the entry: the entry needs the browser to send the
// SameSite=Strict cookie with the tab's first request, and when it did not the tab went to the
// front door and on to the board. Only this site's own capability path is ever followed.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { capabilityFor, capabilityPath, entryParts, NotThisSite, openSiteTab, type TabOpener } from '../siteOpen';
import { fakeHost, reply, type Call } from '../../test/solutionFixtures';

const Site = { team: 'CoreTeam', name: 'team-dashboard', url: '/sites/CoreTeam/team-dashboard/' };
const Capability = '/sites/CoreTeam/team-dashboard/_c/CfDJ8Ab-c_d/';

let calls: Call[] = [];
let answer: Response;

beforeEach(() => {
  calls = [];
  answer = reply(200, { url: Capability });
  vi.stubGlobal('fetch', vi.fn(fakeHost([(call) => (call.method === 'POST' && call.url.endsWith('/open') ? answer : undefined)], calls)));
});

afterEach(() => vi.unstubAllGlobals());

function browser(tab: { opener: unknown; location: { replace: ReturnType<typeof vi.fn> } } | null) {
  const open = vi.fn((_url: string, _target: string, _features?: string) => tab as unknown as Window | null);
  return { open, location: { href: 'http://127.0.0.1:8080/#/console' } } satisfies TabOpener;
}

describe('opening a site from the app', () => {
  it('opens a tab in the click, cuts its opener, and sends it to the capability the Host issued', async () => {
    const tab = { opener: 'the app' as unknown, location: { replace: vi.fn() } };
    const from = browser(tab);

    const opening = openSiteTab(Site, from);
    expect(from.open).toHaveBeenCalledWith('about:blank', '_blank');
    expect(tab.opener).toBeNull();
    await opening;

    expect(calls).toEqual([{ method: 'POST', url: '/api/teams/CoreTeam/sites/team-dashboard/open', body: {} }]);
    expect(tab.location.replace).toHaveBeenCalledWith(`http://127.0.0.1:8080${Capability}`);
  });

  it('sends the tab to the entry when the Host refuses, so the Host says its own sentence', async () => {
    answer = reply(404, { error: 'This site is not published.' });
    const tab = { opener: null as unknown, location: { replace: vi.fn() } };

    await openSiteTab(Site, browser(tab));
    expect(tab.location.replace).toHaveBeenCalledWith(`http://127.0.0.1:8080${Site.url}`);
  });

  it('never follows an answer that is not this site\'s capability path', async () => {
    for (const url of ['https://evil.example/sites/CoreTeam/team-dashboard/_c/x/', '//evil.example/x/',
      '/sites/Other/team-dashboard/_c/x/', '/sites/CoreTeam/team-dashboard/_c/a/b/', '/console']) {
      answer = reply(200, { url });
      const tab = { opener: null as unknown, location: { replace: vi.fn() } };
      await openSiteTab(Site, browser(tab));
      expect(tab.location.replace).toHaveBeenCalledWith(`http://127.0.0.1:8080${Site.url}`);
    }
  });

  it('falls back to the entry the old way when the browser refuses the tab', async () => {
    const from = browser(null);
    await openSiteTab(Site, from);
    expect(from.open).toHaveBeenLastCalledWith(Site.url, '_blank', 'noopener');
    expect(calls).toEqual([]);
  });
});

describe('the capability path rules', () => {
  it('accepts only this site\'s own capability path, one token deep', () => {
    expect(capabilityPath('T', 's', '/sites/T/s/_c/CfDJ8-x_y/')).toBe('/sites/T/s/_c/CfDJ8-x_y/');
    expect(capabilityPath('T', 's', '/sites/T/s/_c//')).toBeNull();
    expect(capabilityPath('T', 's', '/sites/T/s/_c/x/../')).toBeNull();
    expect(capabilityPath('T', 's', '/sites/T/s/_c/x?y/')).toBeNull();
    expect(capabilityPath('T', 's', '/sites/T/other/_c/x/')).toBeNull();
    expect(capabilityPath('T', 's', 42)).toBeNull();
  });

  it('reads an entry exactly as the door accepts one, and carries its query', async () => {
    expect(entryParts('/sites/T/s/?url=a%2Fb')).toEqual({ team: 'T', site: 's', query: '?url=a%2Fb' });
    expect(entryParts('/sites/T/s/_c/x/')).toBeNull();
    expect(entryParts('//evil/sites/T/s/')).toBeNull();
    answer = reply(200, { url: '/sites/T/s/_c/tok/' });
    expect(await capabilityFor('T', 's', '?url=a')).toBe('/sites/T/s/_c/tok/?url=a');
    answer = reply(200, { url: 'https://evil.example/' });
    await expect(capabilityFor('T', 's')).rejects.toThrow(NotThisSite);
  });
});
