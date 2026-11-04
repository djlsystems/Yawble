// @vitest-environment happy-dom
//
// THE TEAM'S SITES BESIDE ITS NAME: one globe link per published site of the team, opening the
// site's own address in a new tab; nothing for an unpublished site, nothing for a team with none,
// nothing when the read fails; re-read for another team and when the window regains focus.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';

import TeamSiteLinks from '../TeamSiteLinks.vue';
import { asTeamId } from '../../api/types';
import '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, type Call } from '../../test/solutionFixtures';

const site = (team: string, name: string, liveVersion: number | null) => ({
  team,
  name,
  liveVersion,
  publishedAt: liveVersion === null ? null : '2026-10-03T01:12:00Z',
  publishedBy: liveVersion === null ? null : 'dana@example.com',
  createdAt: '2026-10-03T01:12:00Z',
  createdBy: 'dana@example.com',
  documents: 6,
  dataBytes: 3600,
  url: `/sites/${team}/${name}/`,
});

let calls: Call[] = [];
let sites: Record<string, unknown> = {};

beforeEach(() => {
  calls = [];
  sites = {
    JobTracker: [site('JobTracker', 'jobs', 1), site('JobTracker', 'drafts', null)],
    Quiet: [],
  };
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) => (call.method === 'POST' && call.url === '/api/teams/JobTracker/sites/jobs/open'
            ? reply(200, { url: '/sites/JobTracker/jobs/_c/CfDJ8tok/' })
            : undefined),
          (call) => {
            const match = /^\/api\/teams\/([^/]+)\/sites$/.exec(call.url);
            if (!match || call.method !== 'GET') return undefined;
            const answer = sites[decodeURIComponent(match[1]!)];
            return answer === undefined ? reply(500, { error: 'broken' }) : reply(200, answer);
          },
        ],
        calls,
      ),
    ),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function links(team: string) {
  const wrapper = mount(TeamSiteLinks, { props: { team: asTeamId(team) } });
  await settle();
  return wrapper;
}

describe("the team's site links", () => {
  it('shows one link per published site, to its own address, in a new tab', async () => {
    const wrapper = await links('JobTracker');

    const anchors = wrapper.findAll('[data-team-site]');
    expect(anchors.map((a) => a.attributes('data-team-site'))).toEqual(['jobs']);
    expect(anchors[0]!.attributes('href')).toBe('/sites/JobTracker/jobs/');
    expect(anchors[0]!.attributes('target')).toBe('_blank');
    expect(anchors[0]!.attributes('rel')).toContain('noopener');
    expect(anchors[0]!.attributes('aria-label')).toBe('Open the jobs site');
  });

  // W27510: a plain click opens the site through a capability the app asks for, so the tab does
  // not depend on the browser sending the session cookie with its first request.
  it('opens the site on a plain click through a capability, in a tab cut from its opener', async () => {
    const wrapper = await links('JobTracker');
    const tab = { opener: 'the app' as unknown, location: { replace: vi.fn() } };
    const opened = vi.spyOn(window, 'open').mockReturnValue(tab as unknown as Window);

    const click = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 });
    wrapper.find('[data-team-site="jobs"]').element.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);
    expect(opened).toHaveBeenCalledWith('about:blank', '_blank');
    expect(tab.opener).toBeNull();
    await settle();

    expect(calls.filter((call) => call.method === 'POST').map((call) => call.url)).toEqual(['/api/teams/JobTracker/sites/jobs/open']);
    expect(tab.location.replace).toHaveBeenCalledWith(new URL('/sites/JobTracker/jobs/_c/CfDJ8tok/', window.location.href).href);
    opened.mockRestore();
  });

  it('leaves a modified click to the browser\'s own link', async () => {
    const wrapper = await links('JobTracker');
    const opened = vi.spyOn(window, 'open').mockReturnValue(null);
    // The test page has no browser to follow the link with: stop it after the component has seen it.
    const anchor = wrapper.find('[data-team-site="jobs"]').element;
    anchor.addEventListener('click', (event) => event.preventDefault(), { once: true });

    const click = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ctrlKey: true });
    anchor.dispatchEvent(click);
    await settle();

    expect(opened).not.toHaveBeenCalled();
    expect(calls.some((call) => call.method === 'POST')).toBe(false);
    opened.mockRestore();
  });

  it('shows nothing for a team with no site, and nothing when the read fails', async () => {
    expect((await links('Quiet')).find('[data-team-site-links]').exists()).toBe(false);
    expect((await links('Broken')).find('[data-team-site-links]').exists()).toBe(false);
  });

  it("reads the other team's sites when the team changes", async () => {
    const wrapper = await links('Quiet');
    expect(wrapper.find('[data-team-site-links]').exists()).toBe(false);

    await wrapper.setProps({ team: asTeamId('JobTracker') });
    await settle();

    expect(wrapper.findAll('[data-team-site]')).toHaveLength(1);
  });

  it('reads again when the window regains focus, so a site published meanwhile appears', async () => {
    const wrapper = await links('Quiet');
    expect(wrapper.find('[data-team-site-links]').exists()).toBe(false);

    sites.Quiet = [site('Quiet', 'status', 2)];
    window.dispatchEvent(new Event('focus'));
    await settle();

    expect(wrapper.findAll('[data-team-site]').map((a) => a.attributes('data-team-site'))).toEqual(['status']);
    wrapper.unmount();
  });
});
