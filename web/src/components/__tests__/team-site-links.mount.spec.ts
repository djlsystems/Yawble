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

  it('links the Team picture like any live site, named as the Team picture, and nothing once it is off', async () => {
    sites.Pictured = [site('Pictured', 'team-picture', 3), site('Pictured', 'jobs', 1)];
    const wrapper = await links('Pictured');

    const picture = wrapper.find('[data-team-site="team-picture"]');
    expect(picture.attributes('href')).toBe('/sites/Pictured/team-picture/');
    expect(picture.attributes('target')).toBe('_blank');
    expect(picture.attributes('aria-label')).toBe('Open the Team picture');
    expect(wrapper.find('[data-team-site="jobs"]').attributes('aria-label')).toBe('Open the jobs site');

    // Turned off: unpublished, so no link, though the site and its data are kept.
    sites.Pictured = [site('Pictured', 'team-picture', null), site('Pictured', 'jobs', 1)];
    window.dispatchEvent(new Event('focus'));
    await settle();
    expect(wrapper.find('[data-team-site="team-picture"]').exists()).toBe(false);
    wrapper.unmount();
  });
});
