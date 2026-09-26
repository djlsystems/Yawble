// @vitest-environment happy-dom
//
// WHAT THE CARD SAYS IN EACH STATE, MOUNTED.
//
// Its neighbour `repo-card-ladder.mount.spec.ts` covers the one action and what it calls; what the
// ladder MEANS lives in `lib/repoLadder.ts` and is tested there over the whole state space. This
// file is what a person reads - whether the answer is on the card at all, and whether the sequence
// still renders as a form control.
//
// BOTH MATTER. Correct refs alone do not tell a person whether a team is safe to delete, and five
// rungs drawn with `radio_button_checked` / `radio_button_unchecked` read as a radio group.
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
// Importing this module is what installs the Quasar plugin - it registers a `beforeAll` hook at
// MODULE level, so the import itself is the setup.
import '../../test/mountQuasar';
import type { RepoActionResult, RepoStatus } from '../../api/types';

// AN UNMOCKED `fetch` IN HAPPY-DOM REACHES A REAL SOCKET. This card fetches from origin when it
// opens - deliberately, so the ladder is never derived from a stale view - and that request would
// otherwise reject AFTER the assertions have run, failing the FILE while every case in it passes.
beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('null', {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })));
  vi.mocked(fetchRepoAsync).mockResolvedValue({
    repo: 'Harness',
    status: status(),
    message: 'Fetched origin.',
    success: true,
  } satisfies RepoActionResult);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function status(over: Partial<RepoStatus> = {}): RepoStatus {
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
    teamSha: 'b'.repeat(40),
    teamPushed: true,
    teamPushedFrom: null,
    teamMergedToMain: true,
    cloneMainOnTeamBranch: null,
    teamCommitsNotOnMain: null,
    worktrees: [],
    originCheckedAt: null,
    ...over,
  } as RepoStatus;
}

function card(over: Partial<RepoStatus> = {}) {
  return mount(RepoCard, {
    props: {
      team: 'alpha' as never,
      repo: status(over),
      gitResolves: true,
      anyMemberRunning: false,
    },
  });
}

async function cardAfterFetch(over: Partial<RepoStatus> = {}) {
  vi.mocked(fetchRepoAsync).mockResolvedValueOnce({
    repo: 'Harness',
    status: status(over),
    message: 'Fetched origin.',
    success: true,
  } satisfies RepoActionResult);

  const wrapper = card(over);
  await Promise.resolve();
  await wrapper.vm.$nextTick();
  return wrapper;
}

function rung(wrapper: ReturnType<typeof card>, label: string) {
  const found = wrapper.findAll('.repo-rung')
    .find((entry) => entry.text().includes(label));

  if (!found) {
    throw new Error(`no ladder rung labelled "${label}"`);
  }

  return found;
}

describe('the repo card answers before it evidences', () => {
  it('leads with what the state MEANS, not with the refs', () => {
    expect(card().text()).toContain('safe to delete');
  });

  // `teamCommitsNotOnMain: 2` is what makes this MEASURED not-merged. `teamMergedToMain: false`
  // on its own is sha ancestry answering a question about content that nobody asked, and the card
  // says "not been measured yet" for it - in the headline AND on the row, which is the point.
  it('distinguishes pushed-but-not-merged, which the refs alone cannot', () => {
    const text = card({ teamMergedToMain: false, teamCommitsNotOnMain: 2 }).text();

    expect(text).toContain('not merged');
    expect(text).not.toContain('safe to delete');
  });

  /**
   * DIVERGED IS A DIFFERENT ACTION, NOT A BIGGER NUMBER. Conflating it with behind-only attempts a
   * fast-forward git refuses, so the card must not render both as a pair of counts.
   */
  it('names a diverged clone, and says a rebase is what it needs', async () => {
    const text = (await cardAfterFetch({ mainAhead: 6, mainBehind: 4 })).text();

    expect(text).toContain('Diverged');
    expect(text).toContain('rebase');
    expect(text).toContain('push and merge');
  });
});

describe('patch-equivalent integration stays aligned on the card', () => {
  const rebased = {
    mainAhead: 0,
    mainBehind: 0,
    teamMergedToMain: false,
    cloneMainOnTeamBranch: false,
    teamCommitsNotOnMain: 0,
    worktrees: [{
      path: '/wt',
      branch: 'team/alpha',
      sha: 'c'.repeat(40),
      member: null,
      aheadMain: 0,
      behindMain: 0,
      commitsNotOnMain: 0,
    }],
  } satisfies Partial<RepoStatus>;

  // THE DELETE COMES FIRST HERE NOW, as it always did for the cherry-pick case beneath. The two
  // reached different tidy jobs from the same verdict only because a special arm handled
  // `cloneMainOnTeamBranch === false` separately from the ordinary tail; `mergeState` sends both
  // down the tail, and the worktrees are the step after.
  it('uses the patch-equivalence answer in both the headline and the ladder after a rebase-integrated merge', async () => {
    const wrapper = await cardAfterFetch(rebased);
    const text = wrapper.text();

    expect(text).toContain('safe to delete');
    expect(text).toContain('Delete the branch on origin');
    expect(rung(wrapper, 'Merged').classes()).toContain('repo-rung-satisfied');
    expect(rung(wrapper, 'Tidied').classes()).toContain('repo-rung-current');
  });

  it('shows the same safe-to-delete answer when the branch was integrated by cherry-pick rather than by rebase ancestry', async () => {
    const wrapper = await cardAfterFetch({
      ...rebased,
      cloneMainOnTeamBranch: null,
      worktrees: [],
    });
    const text = wrapper.text();

    expect(text).toContain('safe to delete');
    expect(text).toContain('Delete the branch on origin');
    expect(rung(wrapper, 'Merged').classes()).toContain('repo-rung-satisfied');
    expect(rung(wrapper, 'Tidied').classes()).toContain('repo-rung-current');
  });

  it('refuses tidy when patch-equivalence was not measured, rather than guessing from sha ancestry', async () => {
    const wrapper = await cardAfterFetch({ ...rebased, teamCommitsNotOnMain: null });
    const text = wrapper.text();

    expect(text).not.toContain('Clean up worktrees');
    expect(text).not.toContain('safe to delete');
    expect(text).toContain('not been measured');
  });

  it('keeps refusing delete when some team changes are still not on main', async () => {
    const wrapper = await cardAfterFetch({
      ...rebased,
      cloneMainOnTeamBranch: null,
      teamCommitsNotOnMain: 2,
      worktrees: [],
    });
    const text = wrapper.text();

    expect(text).not.toContain('Delete the branch on origin');
    expect(text).not.toContain('safe to delete');
    expect(text).toContain('not merged');
    expect(rung(wrapper, 'Merged').classes()).toContain('repo-rung-current');
  });
});

describe('the ladder reads as a sequence rather than a choice', () => {
  it('draws no radio glyphs', () => {
    const html = card().html();

    expect(html).not.toContain('radio_button_checked');
    expect(html).not.toContain('radio_button_unchecked');
  });
});

describe('a worktree only speaks when it holds something', () => {
  const wt = (over: Record<string, unknown>) => ({
    path: '/wt', branch: 'b', sha: 'c'.repeat(40), aheadMain: 0, behindMain: 0, ...over,
  });

  /** `ahead 0, behind 1` made an integrated worktree look like a problem. It now says nothing. */
  it('says nothing about a worktree that is merely behind', () => {
    expect(card({ worktrees: [wt({ aheadMain: 0, behindMain: 5 })] as never }).text())
      .not.toContain('behind');
  });

  it('names commits that exist nowhere else, because those are the ones at risk', () => {
    expect(card({ worktrees: [wt({ aheadMain: 2 })] as never }).text())
      .toContain('2 not on main');
  });

  it('explains a detached worktree rather than printing "unknown" at somebody', () => {
    expect(card({ worktrees: [wt({ branch: null, aheadMain: null, behindMain: null })] as never }).text())
      .toContain('no branch to compare');
  });
});

/**
 * THE MARK MUST MEAN WHAT THE REFUSAL MEANS.
 *
 * The span's TEXT comes from `formatWorktreeDelta`, which reads `commitsNotOnMain`; the mark on
 * that same span must be bound from the same reading, not from `aheadMain`. Otherwise
 * `aheadMain 0 / commitsNotOnMain 3` prints "3 not on main" in the ORDINARY colour directly above a
 * tidy step refusing because of those same three commits, while `3 / 3` prints the identical
 * sentence WITH the mark. The same words, two weights, decided by a count the reader cannot see.
 *
 * THE CLASS IS THE ASSERTION, NOT THE TEXT. Every case here reads `.repo-wt-behind`'s classes:
 * a mismatch never changes a word on the card, so a body-text assertion passes through it.
 */
describe('the worktree mark follows the same predicate the tidy step refuses on', () => {
  const wt = (over: Record<string, unknown>) => ({
    path: '/wt', branch: 'team/alpha', sha: 'c'.repeat(40), member: null,
    aheadMain: 0, behindMain: 0, commitsNotOnMain: 0, ...over,
  });

  function deltas(wrapper: ReturnType<typeof card>) {
    return wrapper.findAll('.repo-wt-behind');
  }

  // Same shape as this file's `rung` helper above, and for the same reason: fail with a sentence
  // naming what was on the card, rather than with `possibly undefined` on the assertion line.
  function onlyDelta(wrapper: ReturnType<typeof card>) {
    const found = deltas(wrapper);
    const only = found[0];

    if (found.length !== 1 || !only) {
      throw new Error(`expected one worktree delta span, found ${found.length}`);
    }

    return only;
  }

  // THE DEFECT ITSELF. Ordinary, not exotic: `aheadMain` is sha ancestry against LOCAL main and
  // `commitsNotOnMain` is `git cherry` against ORIGIN/main, so every team sits here for the whole
  // window between `Merge to main` and the push.
  it('marks a worktree the tidy holds, even when sha ancestry against local main says level', () => {
    const span = onlyDelta(card({ worktrees: [wt({ aheadMain: 0, commitsNotOnMain: 3 })] as never }));

    expect(span.text()).toContain('3 not on main');
    expect(span.classes()).toContain('repo-attention');
  });

  it('gives the control case the same weight, so the sentence does not change meaning with a hidden count', () => {
    const span = onlyDelta(card({ worktrees: [wt({ aheadMain: 3, commitsNotOnMain: 3 })] as never }));

    expect(span.classes()).toContain('repo-attention');
  });

  // A REBASED WORKTREE RENDERS NO SPAN AT ALL, which is why the mark cannot be asserted into
  // existence here - `formatWorktreeDelta` returns '' and the `v-if` drops the element.
  it('renders nothing for a rebased worktree whose content has landed, whatever sha ancestry says', () => {
    expect(deltas(card({ worktrees: [wt({ aheadMain: 3, commitsNotOnMain: 0 })] as never }))).toHaveLength(0);
  });

  /**
   * NOT MEASURED IS NOT AT RISK, and this is the case that rules out the obvious one-liner.
   * Binding the mark from `!!formatWorktreeDelta(wt)` looks equivalent and is not: with
   * `aheadMain` null that helper still says "no branch to compare against main", while
   * `unintegratedWorktrees` EXCLUDES the worktree and the tidy is OFFERED. That binding would
   * paint a detached worktree as at risk beside an offered tidy - the same contradiction, mirrored.
   */
  it('leaves an unmeasured worktree unmarked, because the tidy does not hold it either', () => {
    const span = onlyDelta(card({
      worktrees: [wt({ branch: null, aheadMain: null, behindMain: null, commitsNotOnMain: null })] as never,
    }));

    expect(span.text()).toContain('no branch to compare');
    expect(span.classes()).not.toContain('repo-attention');
  });

  // A Host that predates the field sends no `commitsNotOnMain` at all. Both readers fall back to
  // `aheadMain` there, so the row and the step still agree - which is the whole point of binding
  // them from one predicate.
  it('falls back to sha ancestry together with the tidy when the server sends no content count', () => {
    const span = onlyDelta(card({
      worktrees: [{ path: '/wt', branch: 'team/alpha', sha: 'c'.repeat(40), member: null,
        aheadMain: 2, behindMain: 0 }] as never,
    }));

    expect(span.text()).toContain('2 not on main');
    expect(span.classes()).toContain('repo-attention');
  });

  // ONE PREDICATE, SO THE ROW AND THE STEP CANNOT DRIFT AGAIN: the marked row and the refusal
  // naming the same count, read off one rendered card.
  it('puts the mark and the refusal on the same card, naming the same count', async () => {
    const wrapper = await cardAfterFetch({
      mainAhead: 0,
      mainBehind: 0,
      teamPushed: false,
      teamMergedToMain: false,
      teamCommitsNotOnMain: 0,
      worktrees: [wt({ member: 'DeveloperKwame', aheadMain: 0, commitsNotOnMain: 3 })] as never,
    });

    expect(onlyDelta(wrapper).classes()).toContain('repo-attention');
    expect(wrapper.text()).toContain('DeveloperKwame on team/alpha (3)');
  });
});

// A repository in contributor mode has two remotes, and the card names both; an owned one
// shows neither row.
describe('the repo card names both remotes in contributor mode', () => {
  it('shows origin as the fork and the upstream', () => {
    const text = card({
      originUrl: 'https://github.com/fork-owner/Widget.git',
      upstreamUrl: 'https://github.com/project/Widget.git',
    }).text();

    expect(text).toContain('https://github.com/fork-owner/Widget.git');
    expect(text).toContain('(the fork)');
    expect(text).toContain('https://github.com/project/Widget.git');
  });

  it('shows no remote rows for an owned repository', () => {
    expect(card({ originUrl: 'https://github.com/owner/Widget.git' }).text()).not.toContain('(the fork)');
  });
});

// The Git dialog's button: Open pull request in contributor mode, Merge to main for
// an owned repository - never both - with the stored CLA note beside Open pull request.
describe('the repo card offers Open pull request in contributor mode and Merge to main when owned', () => {
  const contributing = {
    upstreamUrl: 'https://github.com/project/Widget.git',
    originUrl: 'https://github.com/fork-owner/Widget.git',
    forkOwner: 'fork-owner',
    teamMergedToMain: false,
    teamCommitsNotOnMain: 2,
    claSignedNote: 'CLA signed 2026-09-20',
  } satisfies Partial<RepoStatus>;

  it('shows Open pull request and the CLA note, and no Merge to main', async () => {
    const wrapper = await cardAfterFetch(contributing);
    const button = wrapper.find('.repo-action');

    expect(button.text()).toContain('Open pull request');
    expect(wrapper.text()).not.toContain('Merge to main');
    expect(wrapper.find('.repo-cla-note').text()).toContain('CLA signed 2026-09-20');
  });

  it('shows Merge to main and no Open pull request for an owned repository', async () => {
    const wrapper = await cardAfterFetch({ teamMergedToMain: false, teamCommitsNotOnMain: 2 });

    expect(wrapper.find('.repo-action').text()).toContain('Merge to main');
    expect(wrapper.text()).not.toContain('Open pull request');
  });

  it('links a recorded pull request with when GitHub was last asked', async () => {
    const wrapper = await cardAfterFetch({
      ...contributing,
      pullRequest: {
        url: 'https://github.com/project/Widget/pull/4', number: 4, state: 'open',
        readAt: '2026-09-26T09:00:00Z', landing: 'in-review', unknownReason: null,
      },
    });

    const link = wrapper.find('.repo-pull-request a');
    expect(link.attributes('href')).toBe('https://github.com/project/Widget/pull/4');
    expect(wrapper.find('.repo-pull-request').text()).toContain('read');
    expect(wrapper.find('.repo-action').exists()).toBe(false);
  });
});
