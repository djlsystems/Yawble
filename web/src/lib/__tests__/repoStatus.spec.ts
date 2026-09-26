import { describe, it, expect } from 'vitest'
import type { Prerequisite, RepoStatus, TeamRepoStatus } from '../../api/types'
import {
  formatMainDelta,
  formatHeadCheckout,
  formatPushedState,
  formatMergedState,
  formatOriginCheckedAt,
  formatWorktreeDelta,
  getMergeToMainDisabledReason,
  cloneMainFinding,
  mergeState,
  repoHeadline,
  pushState,
  repoPanel,
} from '../repoStatus'
import { nextStep } from '../repoLadder'
import type { WorktreeStatus } from '../../api/types'
import { unintegratedWorktrees } from '../repoLadder'

const baseStatus: RepoStatus = {
  defaultBranch: 'main',
  name: 'Harness',
  clonePath: '/path/to/repo',
  mainSha: '2079965abcdef0123456789abcdef0123456789',
  mainAhead: 0,
  mainBehind: 0,
  dirty: false,
  headCheckout: 'main',
  teamBranch: 'team/os-test',
  teamSha: '2079965abcdef0123456789abcdef0123456789',
  teamPushed: true,
  teamPushedFrom: null,
  teamMergedToMain: false,
  cloneMainOnTeamBranch: null,
  teamCommitsNotOnMain: null,
  originCheckedAt: new Date().toISOString(),
  originReachable: true,
  originUnreachableReason: null,
  worktrees: [],
}

describe('formatHeadCheckout', () => {
  it('returns the branch name when HEAD is a branch', () => {
    const status = { ...baseStatus, headCheckout: 'team/os-xplat' }
    expect(formatHeadCheckout(status)).toBe('team/os-xplat')
  })

  it('says detached rather than printing a number', () => {
    const status = { ...baseStatus, headCheckout: 'detached' }
    expect(formatHeadCheckout(status)).toBe('detached')
    expect(formatHeadCheckout(status)).not.toMatch(/\d/)
  })

  it('returns unknown when headCheckout is null', () => {
    const status = { ...baseStatus, headCheckout: null }
    expect(formatHeadCheckout(status)).toBe('unknown')
  })
})

describe('formatMainDelta', () => {
  it('returns "unknown" when mainAhead is null', () => {
    const status = { ...baseStatus, mainAhead: null, mainBehind: 0 }
    expect(formatMainDelta(status)).toBe('unknown')
  })

  it('returns "unknown" when mainBehind is null', () => {
    const status = { ...baseStatus, mainAhead: 0, mainBehind: null }
    expect(formatMainDelta(status)).toBe('unknown')
  })

  it('never returns "0" or treats unknown as zero', () => {
    const status = { ...baseStatus, mainAhead: null, mainBehind: null }
    const result = formatMainDelta(status)
    expect(result).not.toMatch(/^0$/)
    expect(result).toBe('unknown')
  })

  it('returns "level with origin/main" when both are 0', () => {
    const status = { ...baseStatus, mainAhead: 0, mainBehind: 0 }
    expect(formatMainDelta(status)).toBe('level with origin/main')
  })

  it('returns "ahead N" when only mainAhead > 0', () => {
    const status = { ...baseStatus, mainAhead: 3, mainBehind: 0 }
    expect(formatMainDelta(status)).toBe('3 ahead of origin/main')
  })

  it('returns "behind N" when only mainBehind > 0', () => {
    const status = { ...baseStatus, mainAhead: 0, mainBehind: 5 }
    expect(formatMainDelta(status)).toBe('5 behind origin/main')
  })

  it('returns both ahead and behind when both > 0', () => {
    const status = { ...baseStatus, mainAhead: 2, mainBehind: 4 }
    expect(formatMainDelta(status)).toBe('Diverged — 2 ahead, 4 behind origin/main')
  })
})

describe('formatPushedState', () => {
  it('returns "pushed unknown" when teamPushed is null', () => {
    const status = { ...baseStatus, teamPushed: null }
    expect(formatPushedState(status)).toBe('pushed unknown')
  })

  it('returns "pushed" when teamPushed is true', () => {
    const status = { ...baseStatus, teamPushed: true }
    expect(formatPushedState(status)).toBe('pushed')
  })

  it('returns "not on origin" when teamPushed is false', () => {
    const status = { ...baseStatus, teamPushed: false }
    expect(formatPushedState(status)).toBe('not on origin')
  })

  it('never returns the word "unpushed"', () => {
    const testValues = [true, false, null] as const
    testValues.forEach((pushed) => {
      const status = { ...baseStatus, teamPushed: pushed }
      expect(formatPushedState(status)).not.toContain('unpushed')
    })
  })
})

/**
 * THE ROW IS ONE OF THE CARD'S THREE VOICES, and it reads the same `mergeState` the headline and
 * the ladder do. Reading `teamMergedToMain` raw would let a row saying `not merged to main` sit
 * directly under a headline saying whether every change reached main "has not been measured yet"
 * - two answers to one question, from one `RepoStatus`.
 */
describe('formatMergedState', () => {
  it('returns "merged unknown" when teamMergedToMain is null', () => {
    const status = { ...baseStatus, teamMergedToMain: null }
    expect(formatMergedState(status)).toBe('merged unknown')
  })

  it('says merged to main, quietly, when teamMergedToMain is true', () => {
    const status = { ...baseStatus, teamMergedToMain: true }
    expect(formatMergedState(status)).toBe('merged to main')
  })

  /**
   * THE FIXTURE CARRIES THE CONTENT ANSWER, AND THAT IS THE POINT. `teamMergedToMain: false` alone
   * is ancestry saying no over a patch-equivalence question nobody asked, and a row that convicted
   * on it would disagree with the headline above it. `teamCommitsNotOnMain: 2` is a branch really
   * measured to be carrying changes main does not have.
   */
  it('says not merged to main, in the same voice, when that is what was MEASURED', () => {
    const status = { ...baseStatus, teamMergedToMain: false, teamCommitsNotOnMain: 2 }
    expect(formatMergedState(status)).toBe('not merged to main')
  })

  it('says unknown, not "not merged", when ancestry says no and content was never asked', () => {
    const status = { ...baseStatus, teamMergedToMain: false, teamCommitsNotOnMain: null }
    expect(formatMergedState(status)).toBe('merged unknown')
    expect(formatMergedState(status)).not.toContain('not merged')
  })
})

/**
 * `teamMergedToMainBy`, AND THE ONLY PLACE EITHER MERGED-NESS FIELD IS READ.
 *
 * `teamMergedToMainBy` tells the two MECHANISMS apart - the server answers `teamMergedToMain` true
 * for both - and 'content' is the one that is easy to get wrong: rewritten shas whose changes are
 * all upstream ARE merged, and every sentence about them has to say so plainly rather than imply a
 * failure.
 */
describe('mergeState', () => {
  const by = (
    teamMergedToMainBy: 'ancestry' | 'content' | null,
    over: Partial<RepoStatus> = {},
  ): RepoStatus => ({ ...baseStatus, teamMergedToMain: true, teamMergedToMainBy, ...over })

  it("takes 'ancestry' as merged, and says so in the plainest words there are", () => {
    const state = mergeState(by('ancestry'))

    expect(state.verdict).toBe('merged')
    expect(state.by).toBe('ancestry')
    expect(state.label).toBe('merged to main')
    expect(state.headline.tone).toBe('done')
    expect(state.clause).toBe('the work is on main')
  })

  /** THE WORK IS MERGED. Every string here has to read as an outcome, never as a problem. */
  it("takes 'content' as merged, naming the mechanism rather than implying a failure", () => {
    const state = mergeState(by('content'))

    expect(state.verdict).toBe('merged')
    expect(state.by).toBe('content')
    expect(state.label).toContain('under different commits')
    expect(state.headline.text).toContain('already on main')
    expect(state.headline.text).toContain('safe to delete')
    expect(state.headline.tone).toBe('done')
    expect(state.clause).toContain('already on main')
    expect(state.clause).toContain('different commits')
    // The three voices, and not one of them may read as a refusal.
    for (const said of [state.label, state.headline.text, state.clause]) {
      expect(said).not.toContain('not merged')
      expect(said).not.toContain('not on main')
    }
  })

  /**
   * NULL IS "NOT MERGED, **OR** NOT MEASURED", so it decides nothing by itself and the fields that
   * did measure something answer instead. Reading it as a conviction is the mistake this whole
   * card exists to stop.
   */
  it('lets null fall through to the fields that measured something, rather than convicting', () => {
    expect(mergeState(by(null, { teamMergedToMain: true })).verdict).toBe('merged')
    expect(mergeState(by(null, { teamMergedToMain: false, teamCommitsNotOnMain: 0 })).verdict)
      .toBe('merged')
    expect(mergeState(by(null, { teamMergedToMain: false, teamCommitsNotOnMain: 2 })).verdict)
      .toBe('not-merged')
    expect(mergeState(by(null, { teamMergedToMain: false, teamCommitsNotOnMain: null })).verdict)
      .toBe('unknown')
  })

  /**
   * A HOST THAT PREDATES THE FIELD SENDS NOTHING AT ALL, and `undefined` must behave exactly like
   * null. `status.teamMergedToMainBy == null` is what covers both; a `=== null` would have let an
   * absent field fall past every arm.
   */
  it('treats an absent field exactly as it treats null', () => {
    const absent = { ...baseStatus, teamMergedToMain: false, teamCommitsNotOnMain: 0 }
    expect('teamMergedToMainBy' in absent).toBe(false)
    expect(mergeState(absent).verdict).toBe('merged')
    expect(mergeState(absent).by).toBe('content')
  })

  /** `teamMergedToMain` is TRUE for both mechanisms, so it cannot name one. */
  it('reports the mechanism as unknown when only the boolean answered', () => {
    const state = mergeState({ ...baseStatus, teamMergedToMain: true })

    expect(state.verdict).toBe('merged')
    expect(state.by).toBeNull()
  })

  /** A measured refusal keeps naming the branch, because that is the object of the sentence. */
  it('names the branch when it really is carrying work that is not on main', () => {
    const state = mergeState({ ...baseStatus, teamMergedToMain: false, teamCommitsNotOnMain: 3 })

    expect(state.verdict).toBe('not-merged')
    expect(state.label).toBe('not merged to main')
    expect(state.clause).toContain('team/os-test')
    expect(state.headline.tone).toBe('attention')
  })
})

describe('formatOriginCheckedAt', () => {
  it('returns "origin not checked" when originCheckedAt is null', () => {
    const status = { ...baseStatus, originCheckedAt: null }
    expect(formatOriginCheckedAt(status)).toBe('origin not checked')
  })

  it('returns "origin checked just now" for recent timestamps', () => {
    const now = new Date()
    const status = { ...baseStatus, originCheckedAt: now.toISOString() }
    expect(formatOriginCheckedAt(status)).toBe('origin checked just now')
  })

  it('returns "origin checked Nm ago" for minute-old timestamps', () => {
    const ago = new Date(new Date().getTime() - 5 * 60000)
    const status = { ...baseStatus, originCheckedAt: ago.toISOString() }
    const result = formatOriginCheckedAt(status)
    expect(result).toMatch(/origin checked \d+m ago/)
  })

  it('returns "origin checked Nh ago" for hour-old timestamps', () => {
    const ago = new Date(new Date().getTime() - 2 * 3600000)
    const status = { ...baseStatus, originCheckedAt: ago.toISOString() }
    const result = formatOriginCheckedAt(status)
    expect(result).toMatch(/origin checked \d+h ago/)
  })

  it('returns "origin checked Nd ago" for day-old timestamps', () => {
    const ago = new Date(new Date().getTime() - 3 * 24 * 3600000)
    const status = { ...baseStatus, originCheckedAt: ago.toISOString() }
    const result = formatOriginCheckedAt(status)
    expect(result).toMatch(/origin checked \d+d ago/)
  })
})

describe('pushed vs merged separation', () => {
  it('shows pushed but not merged correctly', () => {
    // MEASURED not-merged, not ancestry-says-no-and-nobody-asked-content. See `formatMergedState`.
    const status = {
      ...baseStatus, teamPushed: true, teamMergedToMain: false, teamCommitsNotOnMain: 2,
    }
    const pushed = formatPushedState(status)
    const merged = formatMergedState(status)
    expect(pushed).toBe('pushed')
    expect(merged).toBe('not merged to main')
    // Verify these are distinct facts rendered separately
    expect(pushed).not.toContain('MERGED')
    expect(merged).not.toContain('pushed')
  })

  it('shows not pushed correctly', () => {
    const status = { ...baseStatus, teamPushed: false, teamMergedToMain: false }
    const pushed = formatPushedState(status)
    expect(pushed).toBe('not on origin')
  })

  it('handles unknown states independently', () => {
    const status = { ...baseStatus, teamPushed: null, teamMergedToMain: true }
    const pushed = formatPushedState(status)
    const merged = formatMergedState(status)
    expect(pushed).toBe('pushed unknown')
    expect(merged).toBe('merged to main')
  })
})

/**
 * THE PANEL READS THE PREREQUISITES THE SERVER ACTUALLY SENDS.
 *
 * The server sends `{ git, gh, repos }` — two NAMED prerequisites. `json<T>()` casts rather than
 * checks, so a client type in any other shape would read `undefined` at runtime on every team,
 * forever: the prerequisite card would always win, every RepoCard would be unreachable, and the
 * message would interpolate `undefined`.
 *
 * The derivation lives here rather than in the component for the reason every other decision does:
 * a pure function is the place a test can pin the whole rule directly.
 */
const prerequisite = (command: string, resolves: boolean): Prerequisite => ({
  command,
  resolves,
  message: resolves
    ? `${command} is installed on this machine's PATH.`
    : `${command} was not found on this machine's PATH.`,
  usedBy: command === 'git' ? 'platform' : 'agents',
})

const teamRepoStatus = (
  gitResolves: boolean,
  repos: RepoStatus[] = [],
): TeamRepoStatus => ({
  git: prerequisite('git', gitResolves),
  gh: prerequisite('gh', true),
  repos,
})

const aWorktree = (over: Partial<WorktreeStatus> = {}): WorktreeStatus => ({
  path: '/wt',
  branch: 'feature',
  sha: 'abc1234',
  member: 'Dev',
  aheadMain: null,
  behindMain: null,
  commitsNotOnMain: null,
  ...over,
})

describe('formatWorktreeDelta', () => {
  it('names the commits that are only here, because those are the ones at risk', () => {
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 1, behindMain: 0 }), 'main')).toBe('1 not on main')
  })

  /**
   * A BLANK, NOT `ahead 0, behind 0`. `repoLadder` establishes that `aheadMain > 0` is the whole
   * test and `behindMain` is noise, and rendering the two together makes an integrated worktree
   * look like a problem. A row saying nothing is the correct answer for a worktree holding nothing.
   */
  it('says nothing when a worktree holds no unintegrated commits', () => {
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 0, behindMain: 0 }), 'main')).toBe('')
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 0, behindMain: 5 }), 'main')).toBe('')
  })

  it('explains a detached worktree rather than printing "unknown" at somebody', () => {
    expect(
      formatWorktreeDelta(aWorktree({ branch: null, aheadMain: null, behindMain: null }), 'main'),
    ).toBe('no branch to compare against main')
  })

  /**
   * `unintegratedWorktrees` asks patch-id, so the ladder OFFERS a tidy for a rebased
   * worktree - and a caption reading `aheadMain` would sit beside it saying "5 not on main". One
   * card contradicting itself in two lines a reader sees at once.
   *
   * WHEN THE CONTENT COUNT IS MEASURED, THAT IS WHAT "not on main" MEANS. Measured zero is the
   * same nothing-to-say the function already returns for an integrated worktree, and where the
   * two disagree the content count is the one that is true - a rebase is exactly the case where
   * they disagree.
   */
  it('says nothing about a rebased worktree whose commits are all on main by patch-id', () => {
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 5, behindMain: 0, commitsNotOnMain: 0 }), 'main')).toBe('')
  })

  it('counts what is really outstanding, not how many shas differ', () => {
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 5, behindMain: 0, commitsNotOnMain: 2 }), 'main')).toBe(
      '2 not on main',
    )
  })

  /**
   * NULL IS NOT MEASURED AND MUST NOT ACQUIT - the same conservative direction
   * `unintegratedWorktrees` takes, for the same reason: a failed `git cherry` is not evidence of
   * zero. An unmeasured content count falls back to the sha reading rather than silencing it, so
   * a Host that never sends the field reads by sha alone.
   */
  it('falls back to the sha reading when the content count was not measured', () => {
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 3, behindMain: 0, commitsNotOnMain: null }), 'main')).toBe(
      '3 not on main',
    )
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 0, behindMain: 0, commitsNotOnMain: null }), 'main')).toBe('')
  })

  /**
   * THE CAPTION AND THE LADDER, ON ONE WORKTREE. These are the two lines the card renders together,
   * and a contradiction is only visible in the pair - each function is self-consistent alone.
   */
  it('never captions outstanding work on a worktree the ladder offers to tidy', () => {
    const rebased = aWorktree({ aheadMain: 5, behindMain: 0, commitsNotOnMain: 0 })

    expect(unintegratedWorktrees({ ...baseStatus, worktrees: [rebased] })).toEqual([])
    expect(formatWorktreeDelta(rebased, 'main')).toBe('')
  })
})

describe('formatPushedState names the ref that answered', () => {
  it('names origin/team/{id} when that ref answered', () => {
    const status = {
      ...baseStatus,
      teamPushed: true,
      teamPushedFrom: 'origin/team/os-tests',
    }
    expect(formatPushedState(status)).toBe('pushed · origin/team/os-tests')
  })

  it('names the local team branch when that ref answered', () => {
    const status = {
      ...baseStatus,
      teamPushed: true,
      teamPushedFrom: 'team/os-tests',
    }
    expect(formatPushedState(status)).toBe('pushed · team/os-tests')
  })
})

describe('getMergeToMainDisabledReason gates on what endpoint requires', () => {
  it('returns null when button should be enabled: teamSha set, local team branch answered', () => {
    const status = {
      ...baseStatus,
      teamSha: 'abc123def456',
      teamBranch: 'team/os-tests',
      teamPushedFrom: 'team/os-tests',
      teamMergedToMain: false,
    }
    expect(getMergeToMainDisabledReason(status)).toBeNull()
  })

  // The endpoint does not require a local refs/heads/team/{id}: it falls back to
  // origin/team/{id} and pushes origin/team/{id}:main. The gate must mirror that, or it greys
  // the one state this fallback exists for.
  it('enabled when the work lives only on origin/team/{id}, which the endpoint merges', () => {
    const status = {
      ...baseStatus,
      teamSha: 'abc123def456',
      teamBranch: 'team/os-tests',
      teamPushedFrom: 'origin/team/os-tests',
      teamMergedToMain: false,
    }
    expect(getMergeToMainDisabledReason(status)).toBeNull()
  })

  // Two more of MergeToMainAsync's seven refusals are already on RepoStatus and cost nothing.
  it('greyed when the clone has uncommitted changes, which the endpoint refuses', () => {
    const status = {
      ...baseStatus,
      teamSha: 'abc123def456',
      teamBranch: 'team/os-tests',
      teamPushedFrom: 'team/os-tests',
      teamMergedToMain: false,
      dirty: true,
    }
    expect(getMergeToMainDisabledReason(status)).toBe('clone has uncommitted changes')
  })

  it('greyed when the clone is on a detached HEAD, which the endpoint refuses', () => {
    const status = {
      ...baseStatus,
      teamSha: 'abc123def456',
      teamBranch: 'team/os-tests',
      teamPushedFrom: 'team/os-tests',
      teamMergedToMain: false,
      headCheckout: 'detached',
    }
    expect(getMergeToMainDisabledReason(status)).toBe('clone is on a detached HEAD')
  })

  // The other four refusals need the server. The button stays enabled and the endpoint's own
  // message does the work; a client that guesses at a refusal it cannot see is how the
  // origin/team/{id} arm got written.
  it('enabled when only server-answerable refusals could apply', () => {
    const status = {
      ...baseStatus,
      teamSha: 'abc123def456',
      teamBranch: 'team/os-tests',
      teamPushedFrom: 'origin/team/os-tests',
      teamMergedToMain: false,
      dirty: false,
      headCheckout: 'main',
    }
    expect(getMergeToMainDisabledReason(status)).toBeNull()
  })

  it('greyed when no team branch at all', () => {
    const status = {
      ...baseStatus,
      teamSha: null,
      teamBranch: 'team/os-tests',
      teamPushedFrom: null,
      teamMergedToMain: false,
    }
    expect(getMergeToMainDisabledReason(status)).toBe('no team branch')
  })

  it('greyed when already merged to main', () => {
    const status = {
      ...baseStatus,
      teamSha: 'abc123def456',
      teamBranch: 'team/os-tests',
      teamPushedFrom: 'team/os-tests',
      teamMergedToMain: true,
    }
    expect(getMergeToMainDisabledReason(status)).toBe('already merged to main')
  })

  it('not greyed when merged status is unknown', () => {
    const status = {
      ...baseStatus,
      teamSha: 'abc123def456',
      teamBranch: 'team/os-tests',
      teamPushedFrom: 'team/os-tests',
      teamMergedToMain: null,
    }
    expect(getMergeToMainDisabledReason(status)).toBeNull()
  })
})

describe('cloneMainFinding', () => {
  const sentence =
    'the team has commits on this clone\'s main that are on no team branch'

  const unmeasured =
    "the team has commits on this clone's main that are not on origin/main"

  it('volunteers the sentence when the commits are MEASURED not to be on the team branch', () => {
    const status = {
      ...baseStatus,
      headCheckout: 'main',
      mainAhead: 4,
      mainBehind: 0,
      cloneMainOnTeamBranch: false,
    }
    expect(cloneMainFinding(status)).toBe(sentence)
  })

  // The defect: it tested HEAD and mainAhead, then made a claim about team branches it had
  // never asked about. On os-concierge-name-team all three commits were on the team branch.
  it('says nothing when the commits ARE on the team branch', () => {
    const status = {
      ...baseStatus,
      headCheckout: 'main',
      mainAhead: 3,
      mainBehind: 0,
      cloneMainOnTeamBranch: true,
    }
    expect(cloneMainFinding(status)).toBeNull()
  })

  it('claims only what mainAhead measured when the team branch was not measured', () => {
    const status = {
      ...baseStatus,
      headCheckout: 'main',
      mainAhead: 4,
      mainBehind: 0,
      cloneMainOnTeamBranch: null,
    }
    expect(cloneMainFinding(status)).toBe(unmeasured)
  })

  it('never says "team branch" in the unmeasured sentence', () => {
    const status = {
      ...baseStatus,
      headCheckout: 'main',
      mainAhead: 4,
      mainBehind: 0,
      cloneMainOnTeamBranch: null,
    }
    expect(cloneMainFinding(status)).not.toContain('team branch')
  })

  it('does not volunteer the sentence when HEAD is main but main is not ahead', () => {
    const status = { ...baseStatus, headCheckout: 'main', mainAhead: 0, mainBehind: 0 }
    expect(cloneMainFinding(status)).toBeNull()
  })

  it('does not volunteer the sentence when main is ahead but HEAD is not main', () => {
    const status = {
      ...baseStatus,
      headCheckout: 'team/os-tests',
      mainAhead: 4,
      mainBehind: 0,
    }
    expect(cloneMainFinding(status)).toBeNull()
  })
})

describe('origin age sits on the figures a reader would act on', () => {
  const elevenHoursAgo = new Date(new Date().getTime() - 11 * 3600000).toISOString()

  it('suffixes an origin-derived main delta with the fetch age', () => {
    const status = {
      ...baseStatus,
      mainAhead: 4,
      mainBehind: 0,
      originCheckedAt: elevenHoursAgo,
    }
    expect(formatMainDelta(status)).toBe('4 ahead of origin/main (11h ago)')
  })

  it('suffixes pushed state with the fetch age', () => {
    const status = {
      ...baseStatus,
      teamPushed: true,
      teamPushedFrom: 'origin/team/os-tests',
      originCheckedAt: elevenHoursAgo,
    }
    expect(formatPushedState(status)).toBe('pushed · origin/team/os-tests (11h ago)')
  })

  it('does not suffix when origin was checked just now', () => {
    const status = { ...baseStatus, mainAhead: 4, mainBehind: 0 }
    expect(formatMainDelta(status)).toBe('4 ahead of origin/main')
  })

  it('does not put origin age on a worktree delta, which is a local comparison', () => {
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 1, behindMain: 0 }), 'main')).toBe('1 not on main')
  })
})

describe('repoPanel', () => {
  it('reports git as available when the server says it is', () => {
    const panel = repoPanel(teamRepoStatus(true))

    expect(panel.gitResolves).toBe(true)
    expect(panel.actionsEnabled).toBe(true)
  })

  it('reports git as unavailable in the SERVER’S words, not words of its own', () => {
    const panel = repoPanel(teamRepoStatus(false))

    expect(panel.gitResolves).toBe(false)
    expect(panel.gitMessage).toBe("git was not found on this machine's PATH.")
    expect(panel.actionsEnabled).toBe(false)
  })

  /**
   * ABSENCE IS ITS OWN STATE, AND THIS IS THE SECOND DEFECT IN THE SAME THREE LINES.
   * `!gitPrerequisite?.resolves` read "I have no data" as "the prerequisite failed", so a payload
   * that had not arrived asserted that a working machine had no git. A guard fails in whichever
   * direction is recoverable: unknown withholds the actions AND withholds the accusation.
   */
  it('does not claim git is missing when it simply does not know yet', () => {
    const panel = repoPanel(null)

    expect(panel.gitResolves).toBeNull()
    expect(panel.gitMessage).toBeNull()
    expect(panel.actionsEnabled).toBe(false)
  })

  it('carries the repos through once git resolves, which is what was unreachable', () => {
    const panel = repoPanel(teamRepoStatus(true, [baseStatus]))

    expect(panel.repos).toHaveLength(1)
    expect(panel.repos[0]!.name).toBe('Harness')
  })

  it('carries gh separately, since it belongs to the agents rather than the Host', () => {
    const panel = repoPanel(teamRepoStatus(true))

    expect(panel.gh?.usedBy).toBe('agents')
  })
})

/**
 * THE SENTENCE A PERSON OPENS THIS CARD FOR.
 *
 * A `main / behind 5` row and a `pushed · … · MERGED TO MAIN` run-on can both be correct and still
 * not answer whether a team is safe to delete.
 *
 * IT NAMES THE CONSEQUENCE, NOT THE FACT. "Merged to main" is on the row below; "safe to delete" is
 * why anybody asked.
 */
describe('repoHeadline', () => {
  it('says a merged branch is safe to delete, because that is why anyone asks', () => {
    const line = repoHeadline({ ...baseStatus, teamPushed: true, teamMergedToMain: true })

    expect(line.text).toContain('Merged to main')
    expect(line.text).toContain('safe to delete')
    expect(line.tone).toBe('done')
  })

  it('separates pushed-but-not-merged from merged', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: true,
      teamMergedToMain: false,
      teamCommitsNotOnMain: 2,
    })

    expect(line.text).toContain('not merged')
    expect(line.tone).toBe('attention')
  })

  it('trusts patch-equivalence when rebased work is already on main under different commits', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: true,
      teamMergedToMain: false,
      cloneMainOnTeamBranch: false,
      teamCommitsNotOnMain: 0,
    })

    expect(line.text).toContain('already on main')
    expect(line.text).toContain('safe to delete')
    expect(line.tone).toBe('done')
  })

  it('trusts the same patch-equivalence answer after a cherry-pick, even when local main is not ahead of the branch', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: true,
      teamMergedToMain: false,
      cloneMainOnTeamBranch: null,
      teamCommitsNotOnMain: 0,
    })

    expect(line.text).toContain('already on main')
    expect(line.text).toContain('safe to delete')
    expect(line.tone).toBe('done')
  })

  it('says when patch-equivalence was not measured, rather than calling it unmerged from ancestry alone', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: true,
      teamMergedToMain: false,
      cloneMainOnTeamBranch: false,
      teamCommitsNotOnMain: null,
    })

    expect(line.text).toContain('not been measured')
    expect(line.tone).toBe('unknown')
  })

  /**
   * A team's first clone, made by Fetch, has no local team ref and a main level with origin/main.
   * The server answers `teamPushed: false` because origin has no team branch, and the card must
   * not warn that the team's work is only in this clone - the clone holds no work.
   */
  it('does not call a fresh clone with no team ref and nothing ahead unpushed work', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamSha: null,
      teamPushed: false,
      mainAhead: 0,
      mainBehind: 0,
    })

    expect(line.text).not.toContain('only in this clone')
    expect(line.text).toContain('no work yet')
  })

  it('still names unpushed work when a fresh team ref is missing but main holds commits', () => {
    const line = repoHeadline({ ...baseStatus, teamSha: null, teamPushed: false, mainAhead: 2, mainBehind: 0 })

    expect(line.text).toContain('only in this clone')
    expect(line.tone).toBe('attention')
  })

  it('names unpushed work as the risk it is', () => {
    const line = repoHeadline({ ...baseStatus, teamPushed: false, teamMergedToMain: false })

    expect(line.text).toContain('only in this clone')
    expect(line.tone).toBe('attention')
  })

  /**
   * DIVERGED OUTRANKS EVERY STATE BELOW IT, because nothing below can be acted on until it is
   * resolved. `repoLadder`: behind-only is a fast-forward, diverged is a rebase, and diverged is
   * the state a team is most often stranded in.
   */
  it('calls a diverged clone diverged, and says a rebase is what it needs', () => {
    const line = repoHeadline({
      ...baseStatus, mainAhead: 6, mainBehind: 4, teamPushed: true, teamMergedToMain: true,
    })

    expect(line.text).toContain('Diverged')
    expect(line.text).toContain('rebase')
    expect(line.tone).toBe('attention')
  })

  /** NULL IS NOT MEASURED AND MUST NOT CONVICT - the rule the rest of this file follows. */
  it('does not invent a state it has not measured', () => {
    const line = repoHeadline({ ...baseStatus, mainAhead: null, mainBehind: null })

    expect(line.tone).toBe('unknown')
    expect(line.text).toContain('not been read')
  })

  /** The new field, read straight through to the sentence a person actually looks at first. */
  it("leads with safe-to-delete on a 'content' merge, because that is what it means", () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: true,
      teamMergedToMain: true,
      teamMergedToMainBy: 'content',
    })

    expect(line.tone).toBe('done')
    expect(line.text).toContain('already on main')
    expect(line.text).toContain('safe to delete')
    expect(line.text).not.toContain('not merged')
  })

  /**
   * `!status.teamPushed` READ NULL AS FALSE, AND THE SERVER ANSWERS NULL ROUTINELY.
   *
   * `delete-remote-branch` removes the ref pushed-ness is derived from, so a successful tidy takes
   * `teamSha`, `teamPushed` and `teamMergedToMain` to null together. The headline then announced
   * "This team's work is only in this clone - nothing is pushed" over work that had just been
   * merged AND pushed, above a row reading `pushed unknown` and a ladder reading "nothing left to
   * do". Three voices, one status, three different stories.
   */
  it('does not accuse a team of having pushed nothing when pushed-ness is simply unknown', () => {
    const afterDelete = {
      ...baseStatus,
      teamSha: null,
      teamPushed: null,
      teamPushedFrom: null,
      teamMergedToMain: null,
      teamCommitsNotOnMain: null,
      mainAhead: 0,
      mainBehind: 0,
    }
    const line = repoHeadline(afterDelete)

    expect(line.text).not.toContain('nothing is pushed')
    expect(line.text).not.toContain('only in this clone')
    expect(line.text).toContain('team/os-test')
  })

  /** THE POSITIVE CONTROL: a branch really measured off origin still gets named as the risk. */
  it('still names unpushed work when the server actually answered false', () => {
    const line = repoHeadline({ ...baseStatus, teamPushed: false, teamMergedToMain: false })

    expect(line.text).toContain('only in this clone')
    expect(line.tone).toBe('attention')
  })

  /** A branch that exists but whose pushed-ness was never answered must not open with "Pushed,". */
  it('does not open with "Pushed," when nobody has said whether it was', () => {
    const line = repoHeadline({
      ...baseStatus, teamSha: 'abc123', teamPushed: null, teamMergedToMain: null,
    })

    expect(line.text).not.toContain('Pushed,')
    expect(line.tone).toBe('unknown')
  })
})

/**
 * THE PUSHED-NESS RECORD, and the three voices that read it rather than answering separately.
 *
 * `repoLadder.spec.ts` holds the INVARIANT - that the header, the row and the step line cannot
 * contradict each other about reaching origin, swept over the whole state space. These are the
 * particulars underneath it: which sentence belongs to which state, and in particular that the one
 * state "There is no team/X to push, merge or delete" is TRUE of - a clone level with origin/main
 * after a successful delete - is the only one that gets it.
 */
describe('pushState', () => {
  it('names the ref on the row and leaves the headline to merged-ness when the answer is yes', () => {
    const push = pushState({
      ...baseStatus, teamPushed: true, teamPushedFrom: 'origin/team/os-test',
    })

    expect(push.verdict).toBe('pushed')
    expect(push.label).toBe('pushed · origin/team/os-test')
    expect(push.headline).toBeNull()
  })

  it('states it plainly when the server actually answered false', () => {
    const push = pushState({ ...baseStatus, teamPushed: false })

    expect(push.verdict).toBe('not-pushed')
    expect(push.label).toBe('not on origin')
    expect(push.clause).toBe('the work is not on origin yet')
    expect(push.headline?.tone).toBe('attention')
  })

  /** A null is an OPEN QUESTION, and no clause derived from one may close it. */
  it('never claims the work is not on origin from a null', () => {
    const unmeasured = { ...baseStatus, teamSha: 'abc123', teamPushed: null }
    const push = pushState(unmeasured)

    expect(push.verdict).toBe('unknown')
    expect(push.label).toBe('pushed unknown')
    expect(push.clause).toContain('has not been measured')
    expect(push.clause).not.toContain('not on origin')
    expect(formatPushedState(unmeasured)).toBe('pushed unknown')
  })

  /**
   * THE POST-DELETE CLONE, which is the one state that sentence is true of: no local team ref and
   * nothing of its own to publish, so there really is nothing here to push.
   */
  it('says there is nothing to push when no ref is left and main is level with origin', () => {
    const afterDelete = {
      ...baseStatus, teamSha: null, teamPushed: null, teamPushedFrom: null, mainAhead: 0,
    }

    expect(pushState(afterDelete).headline?.text).toContain('to push, merge or delete')
  })

  /**
   * AND THE STATE IT IS NOT TRUE OF. `push` publishes **main** (`main:team/{id}`), so a clone
   * holding commits origin/main does not have HAS something to push whether or not a local
   * `team/{id}` exists - and the ladder offers exactly that. The header must not say there is
   * nothing to push above that live button.
   */
  it('does not deny there is anything to push while this clone holds unpublished commits', () => {
    const noRefWorkAhead = {
      ...baseStatus, teamSha: null, teamPushed: null, teamPushedFrom: null, mainAhead: 3,
      mainBehind: 0,
    }
    const line = repoHeadline(noRefWorkAhead)

    expect(line.text).not.toContain('to push')
    expect(line.text).toContain('holds work that origin/main does not')
    expect(pushState(noRefWorkAhead).clause).not.toContain('nothing here left to publish')
  })
})

/**
 * A MEASURED MERGED VERDICT OUTRANKS A MEASURED NOT-PUSHED ONE.
 *
 * If `repoHeadline` asked `pushState` first and returned on `not-pushed`, a team whose every change
 * is already on main - by patch-id, after a rebase - would be announced as "This team's work is
 * only in this clone — nothing is pushed." That is not merely unhelpful, it is FALSE: the work is
 * on origin/main. Merged "settles the question the reader came with", whether pushed-ness is null
 * or measured.
 *
 * IT IS A REQUIREMENT, not polish: a team whose work reached main through a rebase reads "safe to
 * delete" AND has its tidy step offered ON ONE RENDERED CARD. The ladder reaches `tidyStep` only past
 * `teamPushed === true`, and `RepoEndpoints` only ever measures merged-ness inside the branches
 * that also answer `teamPushed` non-null - so the one state where the tidy IS offered is exactly
 * `teamPushed: false`, the state whose headline would otherwise deny the merge.
 *
 * THE WIDENING STOPS AT MEASURED. An unmeasured merge verdict acquits nothing, and the row and the
 * clause about the team REF stay as they are: "not on origin" is an accurate statement about
 * `team/{id}`, and only the HEADLINE would over-claim.
 */
describe('repoHeadline: a measured merge outranks a measured not-pushed', () => {
  /**
   * THE POST-REBASE SHAPE, AND IT IS THE ONE THAT MATTERS. The server measured the content
   * answer - every change is upstream under different shas - and measured the team ref as not on
   * origin, because the rebase left the branch behind. Both are true; only one is what the reader
   * came to ask.
   */
  it('leads with safe-to-delete when the content answer is merged and the team ref is not on origin', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: false,
      teamMergedToMain: true,
      teamMergedToMainBy: 'content',
      teamCommitsNotOnMain: 0,
    })

    expect(line.tone).toBe('done')
    expect(line.text).toContain('already on main')
    expect(line.text).toContain('safe to delete')
    expect(line.text).not.toContain('nothing is pushed')
    expect(line.text).not.toContain('only in this clone')
  })

  /** The same precedence with the plain merge, whose headline is the other merged sentence. */
  it('leads with merged-to-main on an ancestry merge the team ref does not reflect', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: false,
      teamMergedToMain: true,
      teamMergedToMainBy: 'ancestry',
    })

    expect(line.tone).toBe('done')
    expect(line.text).toBe('Merged to main — this team is safe to delete.')
  })

  /**
   * MEASURED BY THE COUNT ALONE, on a Host that does not send `teamMergedToMainBy`. `0` is the
   * content answer and the verdict is merged, so the same precedence has to hold - otherwise the
   * rule would depend on a field the wire does not always carry.
   */
  it('outranks not-pushed on a bare teamCommitsNotOnMain of zero', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: false,
      teamMergedToMain: false,
      teamCommitsNotOnMain: 0,
    })

    expect(line.tone).toBe('done')
    expect(line.text).toContain('safe to delete')
  })

  /**
   * THE ACCEPTANCE CRITERION ITSELF, both halves read off ONE status: the sentence the card leads
   * with and the rung the ladder is standing on. Asserted on the RUNG and not on the tidy caption,
   * which is another card's to word.
   */
  it('reads safe to delete on the same status whose ladder offers the tidy', () => {
    const afterRebaseToMain: RepoStatus = {
      ...baseStatus,
      teamPushed: false,
      teamMergedToMain: true,
      teamMergedToMainBy: 'content',
      teamCommitsNotOnMain: 0,
      mainAhead: 0,
      mainBehind: 0,
      worktrees: [{
        path: 'C:\wt_dev', branch: 'team/os-test', sha: 'ccccccc', member: 'dev',
        // NOTHING OUTSTANDING BY EITHER READING: this case asserts the ladder REACHES the tidied
        // rung, so the worktree must not be holding work.
        aheadMain: 0, behindMain: 0, commitsNotOnMain: 0,
      }],
    }

    expect(repoHeadline(afterRebaseToMain).text).toContain('safe to delete')
    expect(nextStep(afterRebaseToMain, true).rung).toBe('tidied')
  })

  /** NULL IS STILL NOT MEASURED: an open merge question acquits nothing, and the accusation stands. */
  it('still says nothing is pushed when the merge verdict is merely unknown', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: false,
      teamMergedToMain: null,
      teamMergedToMainBy: null,
      teamCommitsNotOnMain: null,
    })

    expect(line.tone).toBe('attention')
    expect(line.text).toContain('nothing is pushed')
  })

  /** And a measured NOT-merged is the state the not-pushed sentence was written for. */
  it('still says nothing is pushed when the work is measured as not merged', () => {
    const line = repoHeadline({
      ...baseStatus,
      teamPushed: false,
      teamMergedToMain: false,
      teamCommitsNotOnMain: 2,
    })

    expect(line.tone).toBe('attention')
    expect(line.text).toContain('nothing is pushed')
  })

  /**
   * THE ROW AND THE CLAUSE ARE UNTOUCHED. They are statements about the team REF, they are true,
   * and the ladder's push rung reads them. Only the headline ever over-claimed.
   */
  it('leaves the row and the push clause saying the ref is not on origin', () => {
    const merged: RepoStatus = {
      ...baseStatus, teamPushed: false, teamMergedToMain: true, teamMergedToMainBy: 'content',
      teamCommitsNotOnMain: 0,
    }

    expect(pushState(merged).verdict).toBe('not-pushed')
    expect(pushState(merged).label).toBe('not on origin')
    expect(pushState(merged).clause).toBe('the work is not on origin yet')
    expect(formatPushedState(merged)).toBe('not on origin')
  })

  /** DRIFT STILL OUTRANKS EVERYTHING. A diverged clone cannot act on any of it until it rebases. */
  it('keeps Diverged above a merged verdict', () => {
    const line = repoHeadline({
      ...baseStatus, mainAhead: 3, mainBehind: 2, teamPushed: false, teamMergedToMain: true,
      teamMergedToMainBy: 'content', teamCommitsNotOnMain: 0,
    })

    expect(line.text).toContain('Diverged')
  })

  /** AND SO DOES AN UNREAD ORIGIN: every figure the verdict rests on came from a fetch. */
  it('keeps the unread-origin sentence above a merged verdict', () => {
    const line = repoHeadline({
      ...baseStatus, mainAhead: null, mainBehind: null, teamPushed: false,
      teamMergedToMain: true, teamMergedToMainBy: 'content', teamCommitsNotOnMain: 0,
    })

    expect(line.text).toContain('not been read')
  })
})

// Every "main" on the card is the repository's stored default branch, and a branch that is
// not known is said to be not known - never read as `main`.
describe('the stored default branch', () => {
  const trunk = (over: Partial<RepoStatus> = {}): RepoStatus => ({ ...baseStatus, defaultBranch: 'trunk', ...over })
  const unknown = (over: Partial<RepoStatus> = {}): RepoStatus =>
    ({ ...baseStatus, defaultBranch: null, mainAhead: null, mainBehind: null, ...over })

  it('names the stored branch wherever the card says main', () => {
    expect(formatMainDelta(trunk())).toContain('level with origin/trunk')
    expect(formatMainDelta(trunk({ mainAhead: 2 }))).toContain('2 ahead of origin/trunk')
    expect(mergeState(trunk({ teamMergedToMain: true, teamMergedToMainBy: 'ancestry' })).label).toBe('merged to trunk')
    expect(formatWorktreeDelta(aWorktree({ aheadMain: 1, behindMain: 0 }), 'trunk')).toBe('1 not on trunk')
    expect(cloneMainFinding(trunk({ headCheckout: 'trunk', mainAhead: 1, cloneMainOnTeamBranch: null })))
      .toBe("the team has commits on this clone's trunk that are not on origin/trunk")
  })

  it('says not known, and never main, when the branch is not known', () => {
    const status = unknown({ headCheckout: 'main' })
    expect(formatMainDelta(status)).toBe('default branch not known')
    expect(getMergeToMainDisabledReason(status)).toBe('default branch not known')
    expect(repoHeadline(status).text).toContain('default branch is not known')
    expect(cloneMainFinding({ ...status, mainAhead: 3 })).toBeNull()
    expect(formatWorktreeDelta(aWorktree({ branch: null, aheadMain: null, behindMain: null }), null))
      .toBe('default branch not known')
    for (const text of [formatMainDelta(status), repoHeadline(status).text, mergeState(status).label]) {
      expect(text).not.toMatch(/\bmain\b/)
    }
  })
})
