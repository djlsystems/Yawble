// @vitest-environment happy-dom
//
// THE FETCH ON OPEN IS THE SLOW PART OF THE GIT DIALOG, and a disabled refresh icon alone says
// nothing. The card says it is checking origin, with a bar, until the fetch answers.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  fetchRepoAsync: vi.fn(),
}));

import RepoCard from '../RepoCard.vue';
import { fetchRepoAsync } from '../../api/client';
import '../../test/mountQuasar';
import type { RepoActionResult, RepoStatus } from '../../api/types';

function status(): RepoStatus {
  return {
    defaultBranch: 'main',
    name: 'Harness',
    clonePath: '/repo',
    mainSha: 'a'.repeat(40),
    mainAhead: 0,
    mainBehind: 0,
    dirty: false,
    headCheckout: 'main',
    teamBranch: 'team/alpha',
    teamSha: null,
    teamPushed: false,
    teamPushedFrom: null,
    teamMergedToMain: false,
    cloneMainOnTeamBranch: null,
    teamCommitsNotOnMain: 0,
    worktrees: [],
    originCheckedAt: new Date().toISOString(),
  } as unknown as RepoStatus;
}

let answer: (result: RepoActionResult) => void = () => {};

beforeEach(() => {
  vi.mocked(fetchRepoAsync).mockImplementation(
    () => new Promise<RepoActionResult>((resolve) => { answer = resolve; }),
  );
});

afterEach(() => {
  vi.mocked(fetchRepoAsync).mockReset();
  document.body.innerHTML = '';
});

describe('RepoCard, the fetch on open', () => {
  it('shows it is checking origin until the fetch answers', async () => {
    const wrapper = mount(RepoCard, {
      attachTo: document.body,
      props: { team: 'alpha' as never, repo: status(), gitResolves: true, anyMemberRunning: false },
    });
    await flushPromises();

    expect(wrapper.find('.repo-progress').exists(), 'no progress while fetching').toBe(true);
    expect(wrapper.text()).toContain('Checking origin');

    answer({ repo: 'Harness', status: status(), message: 'Fetched origin.', success: true });
    await flushPromises();

    expect(wrapper.find('.repo-progress').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Checking origin');
  });
});
