// @vitest-environment happy-dom
//
// BRING CURRENT AND MERGE, AND A TEAM BRANCH THAT IS NOT PUSHED, MOUNTED. The ladder decides which
// step is live (`lib/repoLadder.ts`); this asserts the card offers it as its one button, says the
// action is git only (and to run the suites first when both sides changed the same files), asks
// before landing on main, and names the conflicting files and who must resolve them on a refusal.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const client = vi.hoisted(() => ({
  fetchRepoAsync: vi.fn(),
  pushRepoAsync: vi.fn(),
  mergeRepoToMainAsync: vi.fn(),
  bringCurrentAndMergeAsync: vi.fn(),
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
import { ActionRefused } from '../../api/client';
import type { RepoActionResult, RepoStatus } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

/** A pushed team branch that origin/main has moved past: two commits it lacks. */
function behind(over: Partial<RepoStatus> = {}): RepoStatus {
  return {
    defaultBranch: 'main',
    name: 'Harness',
    clonePath: '/repo',
    mainSha: 'a'.repeat(7),
    mainAhead: 0,
    mainBehind: 0,
    dirty: false,
    headCheckout: 'team/alpha',
    teamBranch: 'team/alpha',
    teamSha: 'b'.repeat(7),
    teamPushed: true,
    teamPushedFrom: 'team/alpha',
    teamMergedToMain: false,
    teamMergedToMainBy: null,
    cloneMainOnTeamBranch: false,
    teamCommitsNotOnMain: 3,
    worktrees: [],
    originCheckedAt: new Date().toISOString(),
    originReachable: true,
    originUnreachableReason: null,
    teamBranchBehindDefault: 2,
    filesChangedOnBothSides: [],
    teamBranchUnpushed: false,
    ...over,
  } as RepoStatus;
}

const answer = (repo: RepoStatus, message = 'Done.'): RepoActionResult =>
  ({ repo: 'Harness', status: repo, message, success: true });

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

async function mountCard(repo: RepoStatus) {
  client.fetchRepoAsync.mockResolvedValue(answer(repo, 'Fetched origin.'));
  wrapper = mount(RepoCard, {
    attachTo: document.body,
    props: {
      team: 'alpha' as never,
      repo,
      gitResolves: true,
      anyMemberRunning: false,
      'onUpdated': (next: RepoStatus) => void wrapper?.setProps({ repo: next }),
    },
  });
  await flushPromises();
  return wrapper;
}

function action(card: VueWrapper) {
  const found = card.findAll('.repo-action');
  expect(found, 'the card should offer exactly one action').toHaveLength(1);
  return found[0]!;
}

async function pressAndConfirm(card: VueWrapper) {
  await action(card).trigger('click');
  await flushPromises();
  const confirm = Array.from(document.body.querySelectorAll('button'))
    .find(b => b.textContent?.trim() === 'Bring current and merge' && !b.classList.contains('repo-action'));
  expect(confirm, 'the confirmation should offer its own button').toBeDefined();
  confirm!.click();
  await flushPromises();
}

describe('Bring current and merge', () => {
  it('is the one button when the pushed team branch is behind main, and says it runs no tests', async () => {
    const card = await mountCard(behind());

    expect(action(card).text()).toContain('Bring current and merge');
    expect(card.find('.repo-next-reason').text()).toContain('origin/main has 2 commits team/alpha does not');
    expect(card.find('.repo-bring-merge-advice').text()).toBe('This is git only: it runs no tests.');
  });

  it('recommends running the suites first when both sides changed the same files', async () => {
    const card = await mountCard(behind({ filesChangedOnBothSides: ['src/a.cs', 'web/b.ts'] }));

    const advice = card.find('.repo-bring-merge-advice').text();
    expect(advice).toContain('runs no tests');
    expect(advice).toContain('src/a.cs, web/b.ts');
    expect(advice).toContain('run the suites on the merge before pressing it');
  });

  it('asks first, naming what it does, and runs only once confirmed', async () => {
    const card = await mountCard(behind());

    await action(card).trigger('click');
    await flushPromises();

    expect(client.bringCurrentAndMergeAsync).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain('Bring current and merge to main?');
    expect(document.body.textContent).toContain('pushes team/alpha');
    expect(document.body.textContent).toContain('This is git only: it runs no tests.');

    client.bringCurrentAndMergeAsync.mockResolvedValue(answer(
      behind({ teamMergedToMain: true, teamMergedToMainBy: 'ancestry', teamBranchBehindDefault: 0, cloneMainOnTeamBranch: true }),
      'origin/main merged into team/alpha (ccccccc) and pushed. Team branch merged to main and pushed (from refs/heads/team/alpha)',
    ));
    const confirm = Array.from(document.body.querySelectorAll('button'))
      .find(b => b.textContent?.trim() === 'Bring current and merge' && !b.classList.contains('repo-action'));
    confirm!.click();
    await flushPromises();

    expect(client.bringCurrentAndMergeAsync).toHaveBeenCalledWith('alpha', 'Harness');
    expect(card.find('.repo-success').text()).toContain('merged into team/alpha');
  });

  it('names every conflicting file and who must resolve them, and offers no ask-the-Manager button', async () => {
    const card = await mountCard(behind());
    client.bringCurrentAndMergeAsync.mockRejectedValue(new ActionRefused(
      'Merging origin/main into team/alpha conflicts in 2 files, so nothing was pushed and the clone was not changed.',
      {
        conflicts: ['src/a.cs', 'web/b.ts'],
        detail: 'The team or a person must resolve them on team/alpha: src/a.cs, web/b.ts.',
      },
    ));

    await pressAndConfirm(card);

    expect(card.find('.repo-error').text()).toContain('nothing was pushed');
    expect(card.findAll('.repo-conflict-files li').map(li => li.text())).toEqual(['src/a.cs', 'web/b.ts']);
    expect(card.find('.repo-conflicts-resolve').text())
      .toBe('The team or a person must resolve them on team/alpha: src/a.cs, web/b.ts.');
    expect(card.text()).not.toContain('Ask the team to resolve it');
  });

  it("shows part C's moved-default-branch notice beside it, and the button is still the one step", async () => {
    const moved = 'moved main in the clone; the work is on ' + 'c'.repeat(40) + '; the team branch is team/alpha';
    const card = await mountCard(behind({ defaultBranchMoved: moved }));

    expect(card.find('.repo-moved-text').text()).toBe(moved);
    expect(action(card).text()).toContain('Bring current and merge');
    expect(card.find('.repo-bring-merge-advice').text()).toBe('This is git only: it runs no tests.');
  });

  it('is not offered when the team branch already has everything on main', async () => {
    const card = await mountCard(behind({ teamBranchBehindDefault: 0, cloneMainOnTeamBranch: true }));

    expect(action(card).text()).toContain('Merge to main');
    expect(card.find('.repo-bring-merge-advice').exists()).toBe(false);
  });
});

describe('a team branch that is not pushed', () => {
  const unpushed = (over: Partial<RepoStatus> = {}) => behind({
    teamPushed: false,
    teamBranchUnpushed: true,
    cloneMainOnTeamBranch: true,
    teamBranchBehindDefault: 0,
    mainSha: 'a'.repeat(7),
    teamSha: 'b'.repeat(7),
    ...over,
  });

  it('says "team/<id> is not pushed" and offers Push before anything else', async () => {
    const card = await mountCard(unpushed());

    expect(action(card).text()).toContain('Push');
    expect(action(card).text()).not.toContain('Merge');
    expect(card.find('.repo-next-reason').text()).toMatch(/^team\/alpha is not pushed/);
    expect(card.text()).toContain('team/alpha is not pushed: origin does not have its latest commits.');
  });

  it('Push publishes it and the ladder moves on', async () => {
    const card = await mountCard(unpushed());
    client.pushRepoAsync.mockResolvedValue(answer(
      unpushed({ teamPushed: true, teamBranchUnpushed: false }), 'Pushed team/alpha to origin.'));

    await action(card).trigger('click');
    await flushPromises();

    expect(client.pushRepoAsync).toHaveBeenCalledWith('alpha', 'Harness');
    expect(card.find('.repo-success').text()).toBe('Pushed team/alpha to origin.');
    expect(action(card).text()).toContain('Merge to main');
  });
});
