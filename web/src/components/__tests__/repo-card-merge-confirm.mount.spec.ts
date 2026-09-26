// @vitest-environment happy-dom
//
// MERGE TO MAIN ASKS ONCE, PLAINLY, MOUNTED. The first click opens the confirmation and runs
// nothing; the dialog's own button is what merges.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  fetchRepoAsync: vi.fn(),
  mergeRepoToMainAsync: vi.fn(),
}));

import RepoCard from '../RepoCard.vue';
import { fetchRepoAsync, mergeRepoToMainAsync } from '../../api/client';
import '../../test/mountQuasar';
import type { RepoActionResult, RepoStatus } from '../../api/types';

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('null', {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })));
  vi.mocked(fetchRepoAsync).mockResolvedValue({
    repo: 'Harness', status: mergeReady(), message: 'Fetched origin.', success: true,
  } satisfies RepoActionResult);
  vi.mocked(mergeRepoToMainAsync).mockResolvedValue({
    repo: 'Harness', status: mergeReady(), message: 'Merged.', success: true,
  } satisfies RepoActionResult);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(mergeRepoToMainAsync).mockReset();
  document.body.innerHTML = '';
});

/** The state the ladder calls MERGE: current with origin, pushed, and not yet on main. */
function mergeReady(over: Partial<RepoStatus> = {}): RepoStatus {
  return {
    defaultBranch: 'main',
    name: 'Harness',
    clonePath: '/repo',
    mainSha: '1544d0fd8d7c964ef8289119749c1883ee26e83e',
    mainAhead: 10,
    mainBehind: 0,
    dirty: false,
    headCheckout: 'main',
    teamBranch: 'team/alpha',
    teamSha: 'b'.repeat(40),
    teamPushed: true,
    teamPushedFrom: null,
    teamMergedToMain: false,
    cloneMainOnTeamBranch: null,
    teamCommitsNotOnMain: 10,
    worktrees: [],
    originCheckedAt: new Date().toISOString(),
    ...over,
  } as RepoStatus;
}

async function card() {
  const wrapper = mount(RepoCard, {
    attachTo: document.body,
    props: {
      team: 'alpha' as never,
      repo: mergeReady(),
      gitResolves: true,
      anyMemberRunning: false,
    },
  });

  await flushPromises();

  return wrapper;
}

function mergeButton(wrapper: Awaited<ReturnType<typeof card>>) {
  return wrapper.findAll('button').find(b => b.text().toLowerCase().includes('merge'));
}

describe('RepoCard, Merge to main', () => {
  it('asks before merging, without mentioning a gate', async () => {
    const wrapper = await card();

    await mergeButton(wrapper)!.trigger('click');
    await flushPromises();

    expect(mergeRepoToMainAsync).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain('Merge to main?');
    expect(document.body.textContent).toContain('team/alpha');
    expect(document.body.textContent?.toLowerCase()).not.toContain('gate');
    expect(document.body.textContent?.toLowerCase()).not.toContain('verdict');
  });

  it('merges once the confirmation is accepted', async () => {
    const wrapper = await card();

    await mergeButton(wrapper)!.trigger('click');
    await flushPromises();

    const confirm = Array.from(document.body.querySelectorAll('button'))
      .find(b => b.textContent?.trim() === 'Merge');
    expect(confirm).toBeDefined();
    confirm!.click();
    await flushPromises();

    expect(mergeRepoToMainAsync).toHaveBeenCalledOnce();
    expect(mergeRepoToMainAsync).toHaveBeenCalledWith('alpha', 'Harness');
  });
});
