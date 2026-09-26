// @vitest-environment happy-dom
//
// A TEAM WHOSE WORK REACHED MAIN THROUGH A REBASE.
//
// Both halves must hold: the headline says the work is merged (a measured merge outranks a measured
// not-pushed) AND the tidy step does not refuse (the guard reads `commitsNotOnMain`, measured per
// worktree by the server). The cases marked GUARD pin the other direction: a guard that stops
// firing for real loss is a worse outcome than a spurious refusal.
//
// THE TRAP, IN ONE SENTENCE. Counting commits by SHA (`aheadMain > 0`) is wrong after a rebase: a
// rebase replays commits onto a new base, so every one of them gets a new sha while its CHANGE is
// already on origin/main. That count is >0 for work that is fully integrated, and a guard reading
// it would refuse to remove a worktree that holds nothing at risk.
//
// MOUNTED THROUGH THE DIALOG, NOT THE HELPER. What this file exists to capture
// is the HEADLINE and the LADDER in the same rendered panel, agreeing. Only a mounted case sees
// both at once, which is why the last describe of `team-git-dialog.mount.spec.ts` does not stub the
// card away and why this one does not either.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  fetchRepoAsync: vi.fn(),
}));

import TeamGitDialog from '../TeamGitDialog.vue';
import { fetchRepoAsync } from '../../api/client';
import type {
  Prerequisite, RepoActionResult, RepoStatus, TeamRepoStatus, WorktreeStatus,
} from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

const resolves: Prerequisite =
  ({ command: 'git', resolves: true, message: 'git version 2.4' }) as Prerequisite;

const teamStatus = (repos: RepoStatus[]): TeamRepoStatus =>
  ({ git: resolves, gh: resolves, repos }) as TeamRepoStatus;

const props = (repoStatus: TeamRepoStatus) =>
  ({ repoStatus, teamId: 'alpha', anyMemberRunning: false });

const repo = (overrides: Partial<RepoStatus> = {}): RepoStatus => ({
  defaultBranch: 'main',
  name: 'core',
  clonePath: 'C:/teams/alpha/repos/core',
  mainSha: 'aaa',
  mainAhead: 0,
  mainBehind: 0,
  dirty: false,
  headCheckout: 'main',
  teamBranch: 'team/alpha',
  teamSha: 'bbb',
  teamPushed: true,
  teamPushedFrom: 'origin/team/alpha',
  teamMergedToMain: true,
  teamMergedToMainBy: 'ancestry',
  cloneMainOnTeamBranch: null,
  teamCommitsNotOnMain: 0,
  originCheckedAt: null,
  originReachable: null,
  worktrees: [],
  ...overrides,
}) as RepoStatus;

/**
 * A MEMBER'S WORKTREE AFTER THE REBASE. Three commits by sha that local main does not have; every
 * one of those changes is already on origin/main under a different sha.
 *
 * `commitsNotOnMain: 0` IS THE MEASURED PATCH-ID ANSWER on `WorktreeStatus`, and the Host answers it from `git cherry origin/main <branch>`. It disagreeing with `aheadMain` is
 * not a fixture quirk: a rebase is exactly the case where sha ancestry and content diverge, and
 * that divergence is the whole point of this file.
 *
 * TYPED, NOT CAST. The field is REQUIRED, and a cast is how a fixture stops being checked against
 * the interface it claims to model - and vitest never typechecks.
 */
const rebasedWorktree: WorktreeStatus = {
  path: 'C:/teams/alpha/repos/core/wt_DeveloperKwame',
  branch: 'member-worktree-patch-equivalence',
  sha: 'ccc',
  member: 'DeveloperKwame',
  aheadMain: 3,
  behindMain: 0,
  commitsNotOnMain: 0,
};

/** Real loss: two commits that exist in this checkout and nowhere else. */
const strandedWorktree: WorktreeStatus = {
  path: 'C:/teams/alpha/repos/core/wt_Solo',
  branch: 'solo/guarded-recs',
  sha: 'ddd',
  member: 'Solo',
  aheadMain: 2,
  behindMain: 0,
  commitsNotOnMain: 2,
};

/**
 * THE HEADLINE, VERBATIM AND WHOLE. The requirement is one sentence, so the sentence is what is asserted - an em dash, a full stop, and
 * no regex that a half-fixed headline could satisfy by accident.
 */
const SAFE_HEADLINE = 'Merged to main — this team is safe to delete.';

/** The tidy refusal, named so a reworded refusal cannot slip past. */
const REFUSES_TIDY = /Integrate it before tidying|work is only on a member's branch/;

describe('a team whose work reached main through a rebase', () => {
  beforeEach(() => {
    // AN UNMOCKED `fetch` IN HAPPY-DOM REACHES A REAL SOCKET, and RepoCard fetches from origin on
    // mount. That request would otherwise reject AFTER the assertions, failing the FILE while
    // every case in it passes.
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('null', {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.mocked(fetchRepoAsync).mockReset();
  });

  /** The card's own fetch-on-open answers the SAME status, so the ladder is past `Refreshed`. */
  async function openWith(over: Partial<RepoStatus>) {
    const one = repo(over);

    vi.mocked(fetchRepoAsync).mockResolvedValue({
      repo: one.name,
      status: one,
      message: 'Fetched origin.',
      success: true,
    } satisfies RepoActionResult);

    return mountDialog(TeamGitDialog, props(teamStatus([one])));
  }

  const rung = (label: string) =>
    [...document.body.querySelectorAll('.repo-rung')]
      .find((r) => r.textContent?.includes(label))?.className ?? '';

  /**
   * STEP ONE OF THE JOURNEY. The branch is still on origin, the server has measured
   * patch-equivalence, and the headline says so.
   *
   * The worktree row already contradicts it - `3 not on main`, in the attention colour, under a
   * headline reading "safe to delete" - but the Tidied step at this point is the DELETE, so the
   * worktree guard has not been consulted yet. It is consulted one click later, below.
   */
  it('says safe to delete while the branch is still on origin, and offers the delete', async () => {
    const wrapper = await openWith({ worktrees: [rebasedWorktree] });
    const said = bodyText();

    expect(said).toContain(SAFE_HEADLINE);
    expect(said).toContain('Delete the branch on origin');
    expect(rung('Merged')).toContain('repo-rung-satisfied');
    expect(REFUSES_TIDY.test(said)).toBe(false);

    wrapper.unmount();
  });

  /**
   * CRITERION. ONE CLICK LATER: the branch on origin is gone, the local `team/alpha` ref is still
   * there and every change on it is on main by patch-id. This is the state under test - the
   * work reached main through a rebase - and it is where the Tidied step reaches the worktree
   * guard.
   *
   * THREE VOICES, ONE STATUS. `teamPushed === false` is exactly what the server answers once
   * `origin/team/alpha` is deleted, so `repoHeadline` must let a MEASURED merge outrank
   * pushed-ness rather than return a not-pushed sentence ("nothing is pushed") beside Pushed ✓
   * Merged ✓ rungs. And the tidy guard must read the content count, not the sha count, or the step
   * refuses over "work is only on a member's branch". Both halves are asserted, on one card.
   *
   * `teamMergedToMainBy: 'ancestry'` IS THE PRODUCIBLE SPELLING
   * for this shape: the TEAM ref was rebased and went into main, so `merge-base --is-ancestor
   * teamSha origin/main` succeeds and `RepoEndpoints` answers Ancestry. The member WORKTREES were
   * not rebased - they still hold the pre-rebase shas, which is why they read ahead by sha and 0
   * by patch-id. The `content` spelling is a real state too and is covered directly below.
   */
  it('CRITERION: reads safe to delete, and offers the tidy rather than refusing it', async () => {
    const wrapper = await openWith({
      teamPushed: false,
      teamPushedFrom: 'team/alpha',
      teamMergedToMain: true,
      teamMergedToMainBy: 'ancestry',
      teamCommitsNotOnMain: 0,
      cloneMainOnTeamBranch: false,
      worktrees: [rebasedWorktree],
    });
    const said = bodyText();

    // The card really painted - otherwise every assertion below is vacuous.
    expect(said).toContain('team/alpha');

    // SOFT, SO ONE RUN RECORDS ALL THREE VOICES. A hard `expect` stops at the headline and the
    // run then says nothing about the ladder - and the headline and the ladder DISAGREEING is the
    // defect, so a failure report that names only one of them has told half the story.

    // THE HEADLINE HALF - the sentence, whole.
    expect.soft(said).toContain(SAFE_HEADLINE);
    expect.soft(said).not.toContain('nothing is pushed');

    // THE RUNG HALF.
    expect.soft(rung('Merged')).toContain('repo-rung-satisfied');
    expect.soft(rung('Tidied')).toContain('repo-rung-current');

    // THE TIDY STEP'S OWN HALF - the button, and the sentence printed under it. `Clean up
    // worktrees` alone would pass on a DISABLED button, so the step's reason is asserted too:
    // offered reads "the team left worktrees behind", refused reads REFUSES_TIDY.
    expect.soft(said).toContain('Clean up worktrees');
    expect.soft(said).toContain('the team left worktrees behind');
    expect.soft(REFUSES_TIDY.test(said)).toBe(false);

    wrapper.unmount();
  });

  /**
   * THE SAME CRITERION IN THE OTHER MEASURED-MERGED SPELLING. Where the team ref is NOT an
   * ancestor of origin/main but every change on it is there by patch-id - main moved on, or the
   * integration was a cherry-pick - `RepoEndpoints` answers `Content` and `mergeState` says so in
   * more words. It is still a measured merge, so it must still outrank not-pushed and still reach
   * an offered tidy; only the wording of the headline differs, and it still ends the same way.
   */
  it('CRITERION: the same, when the merge was measured by patch-id rather than ancestry', async () => {
    const wrapper = await openWith({
      teamPushed: false,
      teamPushedFrom: 'team/alpha',
      teamMergedToMain: true,
      teamMergedToMainBy: 'content',
      teamCommitsNotOnMain: 0,
      cloneMainOnTeamBranch: false,
      worktrees: [rebasedWorktree],
    });
    const said = bodyText();

    expect.soft(said).toContain('this team is safe to delete.');
    expect.soft(said).not.toContain('nothing is pushed');
    expect.soft(said).toContain('Clean up worktrees');
    expect.soft(said).toContain('the team left worktrees behind');
    expect.soft(REFUSES_TIDY.test(said)).toBe(false);

    wrapper.unmount();
  });

  /**
   * GUARD 1 - NOT MEASURED MUST STILL REFUSE. The same worktree, three commits ahead by sha, with
   * NOTHING saying its changes are upstream. Null is not zero, and the direction a guard fails in
   * is the whole of its value: `git worktree remove` takes away the one checkout the work lives
   * in.
   *
   * THE STATUS AROUND IT IS THE CRITERION'S OWN - merged, safe to delete, everything else acquitted
   * - so the ONLY thing standing between this card and an offered tidy is the unmeasured worktree.
   * That is what makes this a guard rather than a restatement: a fallback that quietly acquits null
   * would show up here and nowhere else.
   */
  it('GUARD: refuses the tidy when patch-equivalence was not measured for the worktree', async () => {
    const wrapper = await openWith({
      teamPushed: false,
      teamMergedToMain: true,
      teamMergedToMainBy: 'content',
      teamCommitsNotOnMain: 0,
      worktrees: [{
        path: 'C:/teams/alpha/repos/core/wt_DeveloperKwame',
        branch: 'member-worktree-patch-equivalence',
        sha: 'ccc',
        member: 'DeveloperKwame',
        aheadMain: 3,
        behindMain: 0,
        commitsNotOnMain: null,
      }],
    });
    const said = bodyText();

    expect(REFUSES_TIDY.test(said)).toBe(true);
    expect(said).not.toContain('Clean up worktrees');
    // NAMED, NOT MERELY REFUSED. A refusal that does not say whose work it is protecting leaves a
    // reader with nothing to act on, and the fallback count is the sha one because that is the
    // only count there is.
    expect(said).toContain('DeveloperKwame');
    expect(said).toContain('member-worktree-patch-equivalence');
    expect(said).toContain('(3)');

    wrapper.unmount();
  });

  /**
   * GUARD 2 - THE GENUINE CASE. A member branch whose commits exist nowhere else: no team
   * branch was ever created, nothing was pushed, and the two commits in that checkout are the only
   * copy.
   */
  it('GUARD: refuses the tidy for a member branch whose commits exist nowhere else', async () => {
    const wrapper = await openWith({
      teamSha: null,
      teamPushed: false,
      teamPushedFrom: null,
      teamMergedToMain: null,
      teamMergedToMainBy: null,
      teamCommitsNotOnMain: null,
      cloneMainOnTeamBranch: null,
      worktrees: [strandedWorktree],
    });
    const said = bodyText();

    expect(REFUSES_TIDY.test(said)).toBe(true);
    expect(said).toContain('solo/guarded-recs');
    expect(said).toContain('Solo');
    // THE COUNT IS THE ONE THAT CONVICTED, and here both readings say 2. If the content count ever
    // stopped being printed this would still hold, which is why the branch and member are asserted
    // beside it: what a reader needs is WHERE the work is, not how much of it there is.
    expect(said).toContain('(2)');
    expect(said).not.toContain('Clean up worktrees');

    wrapper.unmount();
  });

  /**
   * GUARD 3 - THE REFUSAL MUST NOT BECOME UNCONDITIONAL EITHER. A worktree at `ahead 0` is
   * integrated however far behind it has fallen, and the tidy has always been offered for it.
   * Here so that "make the guard stricter" is not a way to pass GUARD 1 and 2.
   */
  it('GUARD: still offers the tidy for a worktree that holds nothing at all', async () => {
    const wrapper = await openWith({
      teamPushed: false,
      teamMergedToMain: true,
      teamMergedToMainBy: 'content',
      teamCommitsNotOnMain: 0,
      worktrees: [{
        path: 'C:/teams/alpha/repos/core/wt_Idle',
        branch: 'idle/nothing',
        sha: 'eee',
        member: 'Idle',
        // NOTHING OUTSTANDING BY EITHER READING. `aheadMain: 0` and a measured `commitsNotOnMain: 0`
        // agree, so the case tests only that the guard has not become unconditional.
        aheadMain: 0,
        behindMain: 4,
        commitsNotOnMain: 0,
      }],
    });
    const said = bodyText();

    expect(said).toContain('Clean up worktrees');
    expect(REFUSES_TIDY.test(said)).toBe(false);

    wrapper.unmount();
  });
});
