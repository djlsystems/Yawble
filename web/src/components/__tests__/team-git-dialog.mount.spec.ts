// @vitest-environment happy-dom
//
// `repoPanel(props.repoStatus)` is a pure prop-to-DOM function and `cloneMainFinding` decides which
// warning sentences appear. Both are tested in `lib/__tests__/repoStatus.spec.ts`; this asks
// whether the dialog renders what they return.
//
// THE `gitResolves: false` CASE EARNS ITS PLACE TWICE. `repoPanel` empties `repos` when git does
// not resolve, so "git is missing" and "the team has no repos" render the same empty state from
// two very different facts - and when this panel's data shape is read wrongly, the whole Repos
// panel can render exactly one thing, on every team, with nothing anywhere saying so.
//
// RepoCard is stubbed FOR THE PANEL-WIRING CASES. It mounts with an API call of its own, and
// everything `repoStatus` decides for those lives in TeamGitDialog - so stubbing the child keeps
// them about the parent's wiring.
//
// THE LAST DESCRIBE DOES NOT STUB IT, AND MUST NOT. What a person reads is the dialog and the card
// TOGETHER: a contradiction between a header, a branch row and a step line rendered in one panel
// is invisible to a spec that stubs the card away, which can see at most one of the three.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  fetchRepoAsync: vi.fn(),
}));

import TeamGitDialog from '../TeamGitDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { fetchRepoAsync } from '../../api/client';
import { cloneMainFinding, repoPanel } from '../../lib/repoStatus';
import type { Prerequisite, RepoActionResult, RepoStatus, TeamRepoStatus } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

const stubs = { RepoCard: true };

const resolves = (yes: boolean): Prerequisite =>
  ({ command: 'git', resolves: yes, message: yes ? 'git version 2.4' : 'Not found' }) as Prerequisite;

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
  teamPushedFrom: null,
  teamMergedToMain: true,
  cloneMainOnTeamBranch: null,
  teamCommitsNotOnMain: 0,
  originCheckedAt: null,
  originReachable: null,
  worktrees: [],
  behindMain: 0,
  ...overrides,
}) as RepoStatus;

const status = (repos: RepoStatus[], gitResolves = true): TeamRepoStatus =>
  ({ git: resolves(gitResolves), gh: resolves(true), repos }) as TeamRepoStatus;

const props = (repoStatus: TeamRepoStatus | null) =>
  ({ repoStatus, teamId: 'alpha', anyMemberRunning: false });

describe('TeamGitDialog, mounted', () => {
  // FOUR STATES, NOT THREE, and two of them render an empty repo list from completely different
  // facts. `repoPanel` answers `gitResolves: null` for "not read yet" and `false` for "git is not
  // on this machine", and the dialog must not collapse either into "this team has no repositories".
  // NULL MEANS NOT MEASURED is a rule this codebase applies everywhere else; this is its DOM half.

  it('says it is still CHECKING when no status has been read yet', async () => {
    expect(repoPanel(null).gitResolves).toBeNull();

    const wrapper = await mountDialog(TeamGitDialog, props(null), { global: { stubs } });

    expect(bodyText()).toContain('Checking git');
    expect(bodyText()).not.toContain('No repositories configured');
    // A wait that shows only words reads as a stuck dialog; it spins while it reads.
    expect(document.body.querySelector('.git-empty .q-spinner'), 'no spinner while checking').not.toBeNull();

    wrapper.unmount();
  });

  it('says the team has none when git resolves and the list really is empty', async () => {
    const none = status([]);

    expect(repoPanel(none).repos).toHaveLength(0);

    const wrapper = await mountDialog(TeamGitDialog, props(none), { global: { stubs } });

    expect(bodyText()).toContain('No repositories configured');

    wrapper.unmount();
  });

  it('renders one card per repo the lib function returns', async () => {
    const withTwo = status([repo({ name: 'core' }), repo({ name: 'docs' })]);

    expect(repoPanel(withTwo).repos).toHaveLength(2);

    const wrapper = await mountDialog(TeamGitDialog, props(withTwo), { global: { stubs } });

    expect(wrapper.findAllComponents({ name: 'RepoCard' })).toHaveLength(2);

    wrapper.unmount();
  });

  it('blames GIT, not the team, when git does not resolve - however many repos the team has', async () => {
    const gitMissing = status([repo({ name: 'core' }), repo({ name: 'docs' })], false);

    // The repos are present in the payload and repoPanel empties them anyway. A dialog that read
    // `status.repos` directly would render two cards over a machine with no git.
    expect(gitMissing.repos).toHaveLength(2);
    expect(repoPanel(gitMissing).repos).toHaveLength(0);

    const wrapper = await mountDialog(TeamGitDialog, props(gitMissing), { global: { stubs } });

    expect(wrapper.findAllComponents({ name: 'RepoCard' })).toHaveLength(0);
    expect(bodyText()).toContain('Not found on this machine');
    expect(bodyText()).not.toContain('No repositories configured');

    wrapper.unmount();
  });

  it('shows the clone-main finding sentence when one applies', async () => {
    const drifted = repo({ headCheckout: 'main', mainAhead: 2, cloneMainOnTeamBranch: false });
    const finding = cloneMainFinding(drifted);

    expect(finding).not.toBeNull();

    const wrapper = await mountDialog(TeamGitDialog, props(status([drifted])), {
      global: { stubs },
    });

    expect(bodyText()).toContain(finding!);

    wrapper.unmount();
  });

  it('shows NO finding sentence for a clone that is where it should be', async () => {
    const clean = repo({ mainAhead: 0 });

    expect(cloneMainFinding(clean)).toBeNull();

    const wrapper = await mountDialog(TeamGitDialog, props(status([clean])), {
      global: { stubs },
    });

    expect(document.body.querySelector('.git-finding')).toBeNull();

    wrapper.unmount();
  });
});

/**
 * `gh` SPEAKS ONLY WHEN IT IS MISSING.
 *
 * Silent when gh resolves, under the rule this dialog's own dirty flag already follows: a mark that
 * is usually absent gets read when it appears, and one that says "fine" every time gets read by
 * nobody.
 *
 * NEVER SILENT WHEN IT IS MISSING, which is why both cases are pinned here. `Prerequisite` carries
 * `resolves` and a real `message`, and the caption uses both. A missing gh breaks nothing the HOST
 * does, since every operation here is plain git, but it does break what AGENTS do with it, and this
 * is the only mention of gh anywhere in the product.
 */
describe('the gh prerequisite', () => {
  const missing: Prerequisite = {
    command: 'gh',
    resolves: false,
    message: 'gh was not found on this machine\u2019s PATH.',
    usedBy: 'agents',
  };

  it('says nothing at all when gh resolves', async () => {
    const wrapper = await mountDialog(
      TeamGitDialog, props(status([])), { global: { stubs } });

    // ASSERTED ON THE WORDING THAT IS ACTUALLY RENDERED, so the case fails if the block is made
    // always-on. A phrase the component never renders would make this pass unconditionally.
    expect(bodyText()).not.toContain('Agents use it');

    wrapper.unmount();
  });

  it('names the problem, and whose problem it is, when gh is missing', async () => {
    const withMissingGh = { ...status([]), gh: missing } as TeamRepoStatus;
    const wrapper = await mountDialog(
      TeamGitDialog, props(withMissingGh), { global: { stubs } });

    expect(bodyText()).toContain('gh was not found');
    expect(bodyText()).toContain('Agents use it');

    wrapper.unmount();
  });
});

/**
 * THE DIALOG MUST SAY ONE THING. MOUNTED WHOLE, BECAUSE THE RISK IS BETWEEN THE PARTS.
 *
 * THE CONTRADICTION THIS GUARDS, IN ONE PANEL, FROM ONE `RepoStatus`:
 *
 *   header      "Pushed, but not merged to main yet."
 *   explanation "This clone's main merged to origin/main and pushed (from refs/heads/main). The
 *                team branch on origin was stale and was not integrated."
 *   rungs        Merged greyed out, Tidied lit
 *   step line   "the work is on main, but team/b24-… on origin holds commits that are not, so it
 *                is not safe to delete from here"
 *
 * THE WORK IS ON MAIN AND THE DIALOG SAYS IT IS NOT. Nothing is broken in any one of those four
 * strings: each is a correct reading of some field, and only a test that renders the pair can see
 * it. `lib/__tests__/repoLadder.spec.ts` holds the same property over the
 * whole state space; this holds it where a person actually meets it, which is the only place the
 * four are ever in view together.
 *
 * REPOCARD IS NOT STUBBED HERE. Stubbing it leaves the dialog rendering a placeholder, and every
 * assertion below would pass over a card that had never painted.
 */
describe('the Git dialog cannot contradict itself about the merge', () => {
  /** What "the work is on main" looks like in any of the voices that can say it. */
  const SAYS_MERGED = /the work is on main|already on main|Merged to main|safe to delete/;

  /** And what "it is not" looks like in any of theirs. */
  const SAYS_NOT_MERGED = /not merged to main|holds commits that are not on main|but not on main/;

  beforeEach(() => {
    // AN UNMOCKED `fetch` IN HAPPY-DOM REACHES A REAL SOCKET, and RepoCard fetches from origin on
    // mount - deliberately, so the ladder is never derived from a stale view. That request would
    // otherwise reject AFTER the assertions, failing the FILE while every case in it passes.
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('null', {
      status: 200,
      headers: { 'content-type': 'application/json' },
    })));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.mocked(fetchRepoAsync).mockReset();
  });

  /**
   * The card's own fetch-on-open answers the SAME status it was given, so the ladder is derived
   * from a refreshed origin rather than sitting on the Refreshed rung for the whole test - which
   * is where the interesting rungs are.
   */
  async function openWith(over: Partial<RepoStatus>) {
    const one = repo(over);

    vi.mocked(fetchRepoAsync).mockResolvedValue({
      repo: one.name,
      status: one,
      message: 'Fetched origin.',
      success: true,
    } satisfies RepoActionResult);

    return mountDialog(TeamGitDialog, props(status([one])));
  }

  /**
   * THE CONTRADICTORY STATE, FIELD FOR FIELD. A team that pushed, then rebased, so the branch on origin
   * holds the pre-rebase copies (`cloneMainOnTeamBranch: false`) and this clone's main has since
   * been merged and pushed (`mainAhead: 0`). Two of its commits are genuinely still not on main.
   */
  const theScreenshot = {
    mainAhead: 0,
    mainBehind: 0,
    teamBranch: 'team/b24-recorder',
    teamPushed: true,
    teamPushedFrom: 'origin/team/b24-recorder',
    teamMergedToMain: false,
    teamMergedToMainBy: null,
    cloneMainOnTeamBranch: false,
    teamCommitsNotOnMain: 2,
  } satisfies Partial<RepoStatus>;

  it('cannot render "not merged" beside "the work is on main"', async () => {
    const wrapper = await openWith(theScreenshot);
    const said = bodyText();

    // The card really painted - otherwise every assertion below is vacuous.
    expect(said).toContain('team/b24-recorder');

    expect(SAYS_NOT_MERGED.test(said)).toBe(true);
    expect(SAYS_MERGED.test(said)).toBe(false);
    // The exact contradictory sentence, named so a regression cannot pass by rewording it slightly.
    expect(said).not.toContain('the work is on main, but');

    wrapper.unmount();
  });

  /**
   * THE PAIR IS UNRENDERABLE IN BOTH DIRECTIONS, and the second is the one that costs a person
   * their afternoon: a dialog that says the work is safe must not also say it is not merged.
   */
  it('cannot render "the work is on main" beside "not merged" either', async () => {
    const wrapper = await openWith({
      teamMergedToMain: true,
      teamMergedToMainBy: 'ancestry',
      teamCommitsNotOnMain: 0,
    });
    const said = bodyText();

    expect(SAYS_MERGED.test(said)).toBe(true);
    expect(SAYS_NOT_MERGED.test(said)).toBe(false);

    wrapper.unmount();
  });

  /**
   * THE THREE VOICES SWEPT OVER THE STATES THE FIELD ALLOWS. An example proves the example; what
   * has to hold is that no arrangement of these fields renders both halves of the pair.
   */
  it('renders one state across every combination of the merged-ness fields', async () => {
    for (const teamMergedToMainBy of ['ancestry', 'content', null] as const) {
      for (const teamMergedToMain of [true, false, null] as const) {
        for (const teamCommitsNotOnMain of [0, 2, null]) {
          for (const cloneMainOnTeamBranch of [true, false, null] as const) {
            const wrapper = await openWith({
              teamMergedToMainBy,
              teamMergedToMain,
              teamCommitsNotOnMain,
              cloneMainOnTeamBranch,
            });
            const said = bodyText();

            expect(
              SAYS_MERGED.test(said) && SAYS_NOT_MERGED.test(said),
              `both halves rendered for by=${teamMergedToMainBy} merged=${teamMergedToMain} `
              + `notOnMain=${teamCommitsNotOnMain} onBranch=${cloneMainOnTeamBranch}: ${said}`,
            ).toBe(false);

            wrapper.unmount();
            resetBody();
          }
        }
      }
    }
  });

  /**
   * In the 'content' case the Merged step LIGHTS and Tidied is ALLOWED, because
   * deleting that branch loses nothing.
   *
   * ASSERTED ON THE CLASS, NOT THE TEXT. `repo-rung-satisfied` is what draws the tick; the label
   * "Merged" is on screen whatever state the rung is in, so a text assertion here would pass over
   * the greyed-out rung this exists to catch.
   */
  it("lights Merged and allows Tidied when the server answers 'content'", async () => {
    const wrapper = await openWith({
      teamMergedToMain: true,
      teamMergedToMainBy: 'content',
      // NULL ON PURPOSE. If this passed only because the older content field also said zero, it
      // would be testing that field rather than the one this task consumes.
      teamCommitsNotOnMain: null,
      cloneMainOnTeamBranch: false,
    });

    const rungs = [...document.body.querySelectorAll('.repo-rung')];
    const merged = rungs.find((r) => r.textContent?.includes('Merged'));
    const tidied = rungs.find((r) => r.textContent?.includes('Tidied'));

    expect(merged?.className).toContain('repo-rung-satisfied');
    expect(tidied?.className).toContain('repo-rung-current');

    // TIDIED IS OFFERED, NEVER TAKEN: the action is a button a person clicks, and the destructive
    // one asks again before it runs.
    const said = bodyText();
    expect(said).toContain('Delete the branch on origin');
    expect(said).toContain('under different commits');
    expect(said).not.toContain('stale');

    wrapper.unmount();
  });
});

/**
 * A LADDER ACTION RE-READS THE PANEL, WHETHER IT SUCCEEDED OR FAILED. Six of the seven actions run
 * `git fetch` on the server, so the figures on every card are stale afterwards either way; the card
 * says which happened and the dialog does the re-read.
 */
describe('TeamGitDialog re-reads repo status after a ladder action', () => {
  it.each(['action-failed', 'action-succeeded'])('pulls repo status on %s', async (event) => {
    const wrapper = await mountDialog(TeamGitDialog, props(status([repo()])), { global: { stubs } });
    const pullRepoStatus = vi.spyOn(useConsoleStore(), 'pullRepoStatus').mockResolvedValue();

    wrapper.findComponent({ name: 'RepoCard' }).vm.$emit(event);
    await Promise.resolve();

    expect(pullRepoStatus).toHaveBeenCalledTimes(1);

    wrapper.unmount();
  });
});
