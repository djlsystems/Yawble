// @vitest-environment happy-dom
//
// A REBASE THAT WOULD CONFLICT IS NOT A DEAD END. The dialog names the conflicting files and
// offers to hand them to the team's Manager, rather than stopping safely and offering nothing.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  fetchRepoAsync: vi.fn(),
  rebaseRepoAsync: vi.fn(),
  askTeamToBringCurrent: vi.fn(),
}));

import RepoCard from '../RepoCard.vue';
import { ActionRefused, askTeamToBringCurrent, fetchRepoAsync, rebaseRepoAsync } from '../../api/client';
import '../../test/mountQuasar';
import type { RepoStatus } from '../../api/types';

const Conflicts = ['web/src/components/AuthForm.vue', 'web/src/components/DiagnosticsDialog.vue'];

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('null', {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })));
  // The fetch the card makes on open is what unlocks the next step.
  vi.mocked(fetchRepoAsync).mockResolvedValue({ repo: 'Harness', status: diverged(), message: 'Fetched origin.', success: true });
  vi.mocked(rebaseRepoAsync).mockRejectedValue(
    new ActionRefused('The rebase would conflict, so nothing was changed.', { conflicts: Conflicts }),
  );
  vi.mocked(askTeamToBringCurrent).mockResolvedValue({ seq: 301, correlationId: 301, conflicts: Conflicts });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(rebaseRepoAsync).mockReset();
  vi.mocked(askTeamToBringCurrent).mockReset();
  document.body.innerHTML = '';
});

/** wave5's state: 11 ahead of origin/main and behind it, so the ladder offers Rebase. */
function diverged(): RepoStatus {
  return {
    defaultBranch: 'main',
    name: 'Harness',
    clonePath: '/repo',
    mainSha: 'a'.repeat(40),
    mainAhead: 11,
    mainBehind: 1,
    dirty: false,
    headCheckout: 'main',
    teamBranch: 'team/wave5',
    teamSha: null,
    teamPushed: false,
    teamPushedFrom: null,
    teamMergedToMain: false,
    cloneMainOnTeamBranch: null,
    teamCommitsNotOnMain: 11,
    worktrees: [],
    originCheckedAt: new Date().toISOString(),
  } as unknown as RepoStatus;
}

async function card() {
  const wrapper = mount(RepoCard, {
    attachTo: document.body,
    props: { team: 'wave5' as never, repo: diverged(), gitResolves: true, anyMemberRunning: false },
  });
  await flushPromises();
  return wrapper;
}

const buttonNamed = (wrapper: Awaited<ReturnType<typeof card>>, text: string) =>
  wrapper.findAll('button').find(b => b.text().includes(text));

describe('RepoCard, a rebase that would conflict', () => {
  it('names the conflicting files and offers to ask the team', async () => {
    const wrapper = await card();

    await buttonNamed(wrapper, 'Rebase')!.trigger('click');
    await flushPromises();

    const text = document.body.textContent ?? '';
    expect(text).toContain('The rebase would conflict, so nothing was changed.');
    expect(text).toContain('web/src/components/AuthForm.vue');
    expect(text).toContain('web/src/components/DiagnosticsDialog.vue');
    expect(buttonNamed(wrapper, 'Ask the team to resolve it')).toBeDefined();
  });

  it('sends it to the Manager and says so', async () => {
    const wrapper = await card();

    await buttonNamed(wrapper, 'Rebase')!.trigger('click');
    await flushPromises();
    await buttonNamed(wrapper, 'Ask the team to resolve it')!.trigger('click');
    await flushPromises();

    expect(askTeamToBringCurrent).toHaveBeenCalledWith('wave5', 'Harness');
    expect(document.body.textContent).toContain('Sent to the Manager');
    expect(buttonNamed(wrapper, 'Ask the team to resolve it')).toBeUndefined();
  });

  it('offers nothing to ask when a refusal names no conflicting files', async () => {
    vi.mocked(rebaseRepoAsync).mockRejectedValue(new ActionRefused('The rebase could not be run.', {}));
    const wrapper = await card();

    await buttonNamed(wrapper, 'Rebase')!.trigger('click');
    await flushPromises();

    expect(buttonNamed(wrapper, 'Ask the team to resolve it')).toBeUndefined();
  });
});
