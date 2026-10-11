// @vitest-environment happy-dom
//
// THE CLONE'S DEFAULT BRANCH WAS MOVED BY A MANAGER, MOUNTED: the Git dialog's card shows the
// server's sentence when the status carries one, and nothing when it does not.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  fetchRepoAsync: vi.fn(),
  repoStatus: vi.fn().mockResolvedValue(null),
  repoAction: vi.fn(),
}));

import RepoCard from '../RepoCard.vue';
import { fetchRepoAsync } from '../../api/client';
import '../../test/mountQuasar';
import type { RepoStatus } from '../../api/types';

const Moved = `moved trunk in the clone; the work is on ${'c'.repeat(40)}; the team branch is team/alpha`;

function status(over: Partial<RepoStatus> = {}): RepoStatus {
  return {
    defaultBranch: 'trunk',
    name: 'Harness',
    clonePath: '/repo',
    mainSha: 'c'.repeat(40),
    mainAhead: 1,
    mainBehind: 0,
    dirty: false,
    headCheckout: 'trunk',
    teamBranch: 'team/alpha',
    teamSha: null,
    teamPushed: false,
    teamPushedFrom: null,
    teamMergedToMain: null,
    cloneMainOnTeamBranch: null,
    teamCommitsNotOnMain: null,
    worktrees: [],
    originCheckedAt: null,
    ...over,
  } as RepoStatus;
}

// The card fetches from origin when it opens; that answer is the same status, so the notice
// survives the refresh rather than flashing and going.
async function card(over: Partial<RepoStatus> = {}) {
  vi.mocked(fetchRepoAsync).mockResolvedValue({
    repo: 'Harness',
    status: status(over),
    message: 'Fetched origin.',
    success: true,
  });

  const wrapper = mount(RepoCard, {
    props: { team: 'alpha' as never, repo: status(over), gitResolves: true, anyMemberRunning: false },
  });
  await Promise.resolve();
  await wrapper.vm.$nextTick();
  return wrapper;
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('null', {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('a clone whose default branch a Manager moved', () => {
  it("shows the server's sentence naming the branch, the commit and the team branch", async () => {
    const wrapper = await card({ defaultBranchMoved: Moved });

    const notice = wrapper.find('.repo-moved');
    expect(notice.exists()).toBe(true);
    expect(notice.find('.repo-moved-text').text()).toBe(Moved);
    expect(notice.attributes('role')).toBe('alert');
  });

  it('shows nothing when the status says it has not moved', async () => {
    expect((await card({ defaultBranchMoved: null })).find('.repo-moved').exists()).toBe(false);
    expect((await card()).find('.repo-moved').exists()).toBe(false);
  });
});
