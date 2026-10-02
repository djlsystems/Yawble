// @vitest-environment happy-dom
//
// A REPOSITORY WITH NO WORKING CLONE READS NOT READY. `cloneReady: false` - no clone was made, or an
// interrupted clone left only an empty `.git` - leads the card with "Not ready" and offers Fetch as
// its one action, which makes the clone; it stays the offer while the clone is still not made.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const client = vi.hoisted(() => ({
  fetchRepoAsync: vi.fn(),
  getRepoStatus: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  ...client,
}));

import RepoCard from '../RepoCard.vue';
import type { RepoStatus } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

/** What repo-status answers for a repository with no working clone. */
function notReady(): RepoStatus {
  return {
    name: 'Widget',
    clonePath: '/teams/alpha/repos/Widget/main',
    mainSha: null,
    teamSha: null,
    mainAhead: null,
    mainBehind: null,
    dirty: false,
    headCheckout: null,
    teamBranch: 'team/alpha',
    teamPushed: null,
    teamPushedFrom: null,
    teamMergedToMain: null,
    teamMergedToMainBy: null,
    cloneMainOnTeamBranch: null,
    teamCommitsNotOnMain: null,
    originCheckedAt: null,
    originReachable: null,
    originUnreachableReason: null,
    worktrees: [],
    defaultBranch: null,
    cloneReady: false,
  } as RepoStatus;
}

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  for (const fn of Object.values(client)) fn.mockReset();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  resetBody();
});

describe('A repository with no working clone', () => {
  it('reads Not ready and offers Fetch as its one action', async () => {
    // The fetch on open could not make the clone either: the card must still say so and offer it.
    client.fetchRepoAsync.mockRejectedValue(new Error('The repository could not be cloned: fatal: unreachable'));
    wrapper = mount(RepoCard, {
      attachTo: document.body,
      props: { team: 'alpha' as never, repo: notReady(), gitResolves: true, anyMemberRunning: false },
    });
    await flushPromises();

    expect(wrapper.find('.repo-headline').text()).toContain('Not ready');
    expect(wrapper.find('.repo-headline').text()).toContain('Fetch makes the clone');

    const actions = wrapper.findAll('.repo-action');
    expect(actions, 'the card should offer exactly one action').toHaveLength(1);
    expect(actions[0]!.text()).toContain('Fetch');
    expect(wrapper.find('.repo-next-reason').text()).toContain('the repository has not been cloned');
  });
});
