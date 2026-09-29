// @vitest-environment happy-dom
//
// THE BOARD'S NOTICE FOR A SOLUTION PACKAGE. A `solution.checked` row on the declarer's card reads
// "<name> <version> is ready." with a "Review and install" link to the wizard, composed from the
// folder; a failing one names its problems and offers no link.
import { beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import type { Message } from '../../api/types';

import ActivityFeed from '../ActivityFeed.vue';
import '../../test/mountQuasar';

const folder = '/data/documents/builders/job-tracker-1.0.0';

const checked = (seq: number, payload: Record<string, unknown>): Message => ({
  seq,
  type: 'solution.checked',
  payload: JSON.stringify({ path: folder, name: 'Job Tracker', version: '1.0.0', solution: 'job-tracker', ...payload }),
  source: 'Builders/Manager',
  correlationId: 40,
  causationSeq: 39,
  depth: 1,
  occurredAt: '2026-09-29T10:00:00Z',
});

const mountFeed = (feed: Message[]) =>
  mount(ActivityFeed, { props: { feed, team: 'Builders', name: 'Manager', sinceSeq: 0 } });

beforeEach(() => setActivePinia(createPinia()));

describe('ActivityFeed solution notice', () => {
  it('shows a ready package with a link to review and install it', () => {
    const wrapper = mountFeed([
      checked(41, {
        ok: true,
        text: 'Job Tracker 1.0.0 is ready. **Review and install**',
        link: '#/solutions/install?folder=ignored',
        problems: [],
      }),
    ]);

    expect(wrapper.text()).toContain('Job Tracker 1.0.0 is ready.');
    expect(wrapper.text()).not.toContain('**');

    const link = wrapper.find('a.feed-install');
    expect(link.text()).toBe('Review and install');
    // Built from the folder, not taken from the row.
    expect(link.attributes('href')).toBe(`#/solutions/install?folder=${encodeURIComponent(folder)}`);
  });

  it('names the problems of a failing package and offers no install', () => {
    const wrapper = mountFeed([
      checked(42, {
        ok: false,
        text: 'Job Tracker 1.0.0 did not pass the check: solution.json triggers[0].member: names no member.',
        link: null,
        problems: ['solution.json triggers[0].member: names no member'],
      }),
    ]);

    expect(wrapper.text()).toContain('did not pass the check: solution.json triggers[0].member');
    expect(wrapper.find('a.feed-install').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Review and install');
  });
});
