import type { Prerequisite, RepoStatus, TeamRepoStatus, WorktreeStatus } from '../api/types'

/**
 * THE REPOSITORY'S STORED DEFAULT BRANCH IS WHAT EVERY "main" ON THE CARD MEANS. The server
 * reads it from origin's HEAD on clone and Fetch, or a person sets it in Team settings. Null is NOT
 * KNOWN, and nothing here substitutes `main` for it: the words below say it is not known instead.
 */
export function defaultBranchOf(status: Pick<RepoStatus, 'defaultBranch'>): string | null {
  return status.defaultBranch ?? null
}

/** The default branch in a sentence: its name, or words saying it is not known. Never a ref. */
export function branchWord(status: Pick<RepoStatus, 'defaultBranch'>): string {
  return status.defaultBranch ?? 'the default branch'
}

/** Origin's copy of the default branch in a sentence, as `branchWord`. */
export function originBranchWord(status: Pick<RepoStatus, 'defaultBranch'>): string {
  return status.defaultBranch ? `origin/${status.defaultBranch}` : 'origin\u2019s default branch'
}

/**
 * The ref ahead/behind is measured against, in a sentence: `upstream/<default>` for a
 * repository in contributor mode (it has an upstream, and its origin is a fork), else as
 * `originBranchWord`. Every `mainAhead` / `mainBehind` sentence names this one.
 */
export function baseBranchWord(status: Pick<RepoStatus, 'defaultBranch' | 'upstreamUrl'>): string {
  if (!status.upstreamUrl) return originBranchWord(status)
  return status.defaultBranch ? `upstream/${status.defaultBranch}` : 'upstream\u2019s default branch'
}

/** What the card says when the default branch is not known. */
export const DEFAULT_BRANCH_NOT_KNOWN =
  'the default branch is not known: Fetch reads it from origin, or set it in Team settings'

/**
 * Format main branch delta for display.
 *
 * CRITICAL: unknown must render as "unknown", NEVER as "0" or "level".
 * Pushing a fake zero is the specific failure this feature exists to stop.
 */
/**
 * What HEAD is, when it is not main. Detached is a word, never a number.
 */
export function formatHeadCheckout(status: RepoStatus): string {
  if (!status.headCheckout) {
    return 'unknown'
  }

  if (status.headCheckout === 'detached') {
    return 'detached'
  }

  return status.headCheckout
}

export function formatMainDelta(status: RepoStatus): string {
  if (defaultBranchOf(status) === null) {
    return 'default branch not known'
  }

  if (status.mainAhead === null || status.mainBehind === null) {
    return withOriginAge('unknown', status)
  }

  const origin = baseBranchWord(status)

  if (status.mainAhead === 0 && status.mainBehind === 0) {
    return withOriginAge(`level with ${origin}`, status)
  }

  if (status.mainAhead > 0 && status.mainBehind === 0) {
    return withOriginAge(`${status.mainAhead} ahead of ${origin}`, status)
  }

  if (status.mainAhead === 0 && status.mainBehind > 0) {
    return withOriginAge(`${status.mainBehind} behind ${origin}`, status)
  }

  // DIVERGED IS NOT TWO NUMBERS, it is a different ACTION. `repoLadder` records why: behind-only is
  // a fast-forward and diverged is a rebase, and rendering both as a pair of counts is what let a
  // reader reach for the button git then refused. The word does the work the numbers cannot.
  return withOriginAge(
    `Diverged — ${status.mainAhead} ahead, ${status.mainBehind} behind ${origin}`, status)
}

/**
 * Format team branch pushed state.
 *
 * Pushed and merged are separate questions with separate answers.
 *
 * THE ROW IS ONE OF THE THREE VOICES ABOUT PUSHED-NESS, so it reads `pushState` and the header and
 * the ladder read the same record - exactly as the merged-ness row already reads `mergeState`.
 * The origin age is suffixed HERE rather than inside the record: it is a fact about when the row
 * was measured, not part of the answer, and the header and the step line do not carry it.
 */
export function formatPushedState(status: RepoStatus): string {
  return withOriginAge(pushState(status).label, status)
}

/**
 * Format team branch merged state. Returned only when teamPushed is true; caller decides whether
 * to render.
 *
 * LOWER CASE, BECAUSE EMPHASIS MUST NOT DO A COLOUR'S JOB.
 *
 * `MERGED TO MAIN` and `NOT MERGED TO MAIN` in capitals would shout equally, so the two states -
 * one of which means "safe to delete" and the other "this work is not integrated" - would carry
 * identical weight on the row. Caps as emphasis also stops working the moment everything is
 * capitalised.
 *
 * The distinction is made where a reader looks first: the card's headline names the
 * consequence, and this row states the fact quietly beneath it.
 *
 * THE ROW IS ONE OF THE THREE VOICES, so it reads the same `mergeState` the headline and the
 * ladder do. Reading `teamMergedToMain` raw would let the row say `not merged to main` while the
 * headline above it said whether every change reached main "has not been measured yet" - two
 * answers to one question, from one `RepoStatus`.
 */
export function formatMergedState(status: RepoStatus): string {
  if (status.upstreamUrl) return pullRequestLabel(status)
  return mergeState(status).label
}

/**
 * WHEN GITHUB LAST SAID, in the reader's own time, or null when it never has.
 */
export function pullRequestReadAt(status: RepoStatus): string | null {
  const at = status.pullRequest?.readAt
  if (!at) return null
  const date = new Date(at)
  return Number.isNaN(date.getTime()) ? null : date.toLocaleString()
}

/**
 * THE TEAM BRANCH'S ROW IN CONTRIBUTOR MODE: the recorded pull request's state, never
 * merged-ness by ancestry - origin is the fork, and a squash upstream leaves no ancestry to read.
 */
export function pullRequestLabel(status: RepoStatus): string {
  const pull = status.pullRequest
  if (!pull) return 'no pull request yet'
  switch (pull.landing) {
    case 'landed': return `pull request #${pull.number} merged upstream`
    case 'in-review': return `pull request #${pull.number} in review`
    case 'declined': return `pull request #${pull.number} closed without merging`
    default: return `pull request #${pull.number}: GitHub could not say (last: ${pull.state})`
  }
}

/**
 * THE HEADLINE IN CONTRIBUTOR MODE, from the recorded pull request. Landed is GitHub's word
 * that it was merged; unreachable is unknown, never a guess.
 */
function contributorHeadline(status: RepoStatus): RepoHeadline | null {
  const pull = status.pullRequest
  if (!pull) return null
  switch (pull.landing) {
    case 'landed':
      return { text: `Pull request #${pull.number} was merged upstream — the work has landed.`, tone: 'done' }
    case 'in-review':
      return { text: `Pull request #${pull.number} is open upstream, in review.`, tone: 'working' }
    case 'declined':
      return { text: `Pull request #${pull.number} was closed upstream without merging — the work has not landed.`, tone: 'attention' }
    default:
      return { text: `GitHub could not say what pull request #${pull.number} is now.`, tone: 'unknown' }
  }
}

/**
 * Gate for the "merge to main" button. THE GATE MIRRORS THE ENDPOINT; it does not guess.
 *
 * MergeToMainAsync refuses seven ways. Three of them are answerable from RepoStatus and are
 * below. The other four - a busy member, a missing repo, a missing origin/main, and a team ref
 * that is not a fast-forward of origin/main - need the server, so the button stays enabled and
 * the endpoint's own message does the work. That is deliberate, not a gap: a client that guesses
 * at a refusal it cannot see gets it wrong.
 *
 * It does NOT require a local refs/heads/team/{id}: the endpoint falls back to origin/team/{id},
 * and a gate requiring the local ref would grey the button for the only state that fallback
 * exists for.
 *
 * Of the three below, only 'no team branch' and the two tree conditions are endpoint refusals.
 * 'already merged to main' is a courtesy - the push would be a no-op that succeeds.
 */
export function getMergeToMainDisabledReason(status: RepoStatus): string | null {
  // The server refuses when the default branch is not known, so the button does too.
  if (defaultBranchOf(status) === null) {
    return 'default branch not known'
  }
  if (status.teamSha === null) {
    return 'no team branch'
  }
  if (status.dirty) {
    return 'clone has uncommitted changes'
  }
  if (status.headCheckout === 'detached') {
    return 'clone is on a detached HEAD'
  }
  // THE COURTESY ARM, AND IT ASKS `mergeState` RATHER THAN ONE FIELD. Reading `teamMergedToMain`
  // alone would offer `Merge to main` over work that is already upstream under rewritten shas - the
  // server then refuses it, which is the one thing a client-side gate exists to avoid.
  const merge = mergeState(status)
  if (merge.verdict === 'merged') {
    return merge.by === 'content'
      ? `already on ${branchWord(status)}, under different commits`
      : `already merged to ${branchWord(status)}`
  }
  return null
}

/**
 * Format origin freshness timestamp.
 *
 * Load-bearing: everything derived from origin/* is exactly as old as this timestamp.
 */
export function formatOriginCheckedAt(status: RepoStatus): string {
  const age = originAgeShort(status)
  if (age === null && !status.originCheckedAt) {
    return 'origin not checked'
  }

  if (age === null) {
    return 'origin checked just now'
  }

  return `origin checked ${age}`
}

/**
 * Worktree position relative to local main. Zeros are measured answers, not blanks.
 * Detached (or otherwise uncounted) stays unknown. This is a local comparison, so
 * it does not carry origin's fetch age.
 */
/**
 * WHAT A WORKTREE HOLDS THAT NOTHING ELSE DOES - and nothing at all when the answer is none.
 *
 * `aheadMain > 0` IS THE WHOLE TEST, as `unintegratedWorktrees` already says: a worktree at ahead 0
 * is fully integrated however far behind it has fallen. Printing `ahead 0, behind 1` would make an
 * integrated worktree look like a problem. Behind is noise here and is not rendered.
 *
 * NULL IS NOT MEASURED, and `unknown` would be a word that raises the question instead of answering it.
 * A detached worktree has no branch to compare against main; saying so is the whole explanation.
 *
 * "not on main" MEANS PATCH-ID WHEREVER PATCH-ID WAS MEASURED. `unintegratedWorktrees`
 * asks `commitsNotOnMain`, so a caption counting shas would say `5 not on main` beside a tidy the
 * ladder offers for a rebased worktree. The caption answers from the same field the action does.
 *
 * AN UNMEASURED CONTENT COUNT FALLS BACK, IT DOES NOT ACQUIT - the conservative direction
 * `unintegratedWorktrees` takes, and for the same reason: a failed `git cherry` is not evidence of
 * zero. `== null` catches both null and the `undefined` a Host without the field produces.
 */
export function formatWorktreeDelta(
  wt: Pick<WorktreeStatus, 'aheadMain' | 'behindMain' | 'commitsNotOnMain'>,
  defaultBranch: string | null = null,
): string {
  const main = defaultBranch ?? 'the default branch'
  if (defaultBranch === null && wt.aheadMain === null) {
    return 'default branch not known'
  }

  if (wt.aheadMain === null) {
    return `no branch to compare against ${main}`
  }

  const outstanding = wt.commitsNotOnMain == null ? wt.aheadMain : wt.commitsNotOnMain

  if (outstanding === 0) {
    return ''
  }

  return `${outstanding} not on ${main}`
}

/**
 * The one state that means somebody has worked in the shared clone: HEAD is main
 * and main is ahead of origin/main. Either condition alone is silent.
 *
 * WHETHER THOSE COMMITS ARE ON A TEAM BRANCH IS A THIRD QUESTION, and HEAD and mainAhead alone
 * cannot answer it. The server measures it (cloneMainOnTeamBranch); say only what is known:
 *
 *   false -> the commits are on no team branch
 *   true  -> nothing. The team committed on main and pushed that tip as its branch.
 *   null  -> claim only what mainAhead measured, and do not say "team branch" at all.
 */
export function cloneMainFinding(status: RepoStatus): string | null {
  const main = defaultBranchOf(status)
  if (main === null || status.headCheckout !== main || status.mainAhead === null || status.mainAhead <= 0) {
    return null
  }

  if (status.cloneMainOnTeamBranch === true) {
    return null
  }

  if (status.cloneMainOnTeamBranch === false) {
    return `the team has commits on this clone's ${main} that are on no team branch`
  }

  return `the team has commits on this clone's ${main} that are not on origin/${main}`
}

/**
 * Age of origin/* facts, or null when they were checked just now / never.
 * Suffixed onto the figures a reader would act on, so the expiry is not a
 * footnote below them.
 */
function originAgeShort(status: RepoStatus): string | null {
  if (!status.originCheckedAt) {
    return null
  }

  const then = new Date(status.originCheckedAt)
  const now = new Date()
  const deltaMs = now.getTime() - then.getTime()

  if (deltaMs < 60000) {
    return null
  }

  const minutes = Math.floor(deltaMs / 60000)
  if (minutes < 60) {
    return `${minutes}m ago`
  }

  const hours = Math.floor(minutes / 60)
  if (hours < 24) {
    return `${hours}h ago`
  }

  const days = Math.floor(hours / 24)
  return `${days}d ago`
}

function withOriginAge(label: string, status: RepoStatus): string {
  const age = originAgeShort(status)
  return age ? `${label} (${age})` : label
}

/**
 * WHAT THE REPOS PANEL MEANS. The component renders it and decides nothing.
 *
 * A PURE FUNCTION IN `lib/`, AND THAT IS NOT A PREFERENCE — the panel logic belongs where a spec
 * can exercise it directly instead of teasing it back out of a `.vue` file. Inside the component's
 * `computed`s nothing direct could reach it.
 *
 * THREE STATES, NOT TWO, and the third is the point. `gitResolves === null` means NOBODY HAS
 * ANSWERED YET — the payload has not arrived. Writing `!gitPrerequisite?.resolves` reads absence
 * as failure, so a board that had simply not fetched yet would assert that a working machine had
 * no git and disable every action. A guard fails
 * in whichever direction is RECOVERABLE: unknown withholds the actions and withholds the
 * accusation with them.
 */
export interface RepoPanel {
  /** True, false, or NULL for "not answered yet". Never collapse null into false. */
  gitResolves: boolean | null

  /** The SERVER'S own words, never a sentence composed here. Null while unknown. */
  gitMessage: string | null

  /** `gh` belongs to the AGENTS, not to the Host, and the caption says so. Null while unknown. */
  gh: Prerequisite | null

  /** Empty until git resolves — there is nothing meaningful to show about a repo without git. */
  repos: RepoStatus[]

  /** Whether the repo actions may be offered at all. False while unknown, for the reason above. */
  actionsEnabled: boolean
}

export function repoPanel(status: TeamRepoStatus | null): RepoPanel {
  if (!status?.git) {
    return { gitResolves: null, gitMessage: null, gh: null, repos: [], actionsEnabled: false }
  }

  return {
    gitResolves: status.git.resolves,
    gitMessage: status.git.message,
    gh: status.gh ?? null,
    repos: status.git.resolves ? status.repos : [],
    actionsEnabled: status.git.resolves,
  }
}

/** What the card says first, and how loudly. */
export interface RepoHeadline {
  readonly text: string
  readonly tone: 'done' | 'attention' | 'working' | 'unknown'
}

/** HOW the server established that the team's work is on main. */
export type MergeEvidence = 'ancestry' | 'content'

/**
 * THREE ANSWERS, NOT TWO, and `unknown` is the one that keeps the card honest: a question that was
 * never asked has not been answered "no".
 */
export type MergeVerdict = 'merged' | 'not-merged' | 'unknown'

/**
 * EVERY SENTENCE THE CARD SAYS ABOUT INTEGRATION, DERIVED ONCE.
 *
 * THE CONTRADICTION THIS TYPE EXISTS TO MAKE UNREPRESENTABLE:
 *
 *   header      "Pushed, but not merged to main yet."
 *   step line   "the work is on main, but team/b24-… on origin holds commits that are not"
 *   rungs        Merged greyed, Tidied lit
 *
 * Three pieces of code each answering from the same `RepoStatus` can only be hoped to agree. The
 * header, the row and the ladder are instead three FIELDS OF ONE RECORD: to contradict each other
 * something has to change `verdict` between two reads of the same status, which no arrangement of
 * the callers can do.
 */
export interface MergeState {
  /** merged / not-merged / unknown. The one answer. */
  readonly verdict: MergeVerdict
  /** How, when it is merged. Null when the server did not say, or when it is not merged. */
  readonly by: MergeEvidence | null
  /** The row beside the team ref. */
  readonly label: string
  /** The card's leading sentence, for the states where integration is what it leads with. */
  readonly headline: RepoHeadline
  /** The clause `repoLadder` embeds in its step reasons. It never claims more than `verdict`. */
  readonly clause: string
}

/**
 * THE VERDICT, AND NOTHING ELSE READS THESE THREE FIELDS.
 *
 * ORDER IS PRECEDENCE:
 *
 * 1. `teamMergedToMainBy` is the only field that can tell the two MECHANISMS apart - the server
 *    sets `teamMergedToMain` true for both - so it is asked first when it is there.
 * 2. `teamMergedToMain === true` still means merged when `teamMergedToMainBy` is absent; the
 *    mechanism is then simply unreported, which is `by: null` rather than a guess at 'ancestry'.
 * 3. `teamCommitsNotOnMain` is the content answer by patch-id, and `0` is merged.
 *
 * NULL IS NOT MEASURED AND MUST NEVER BE READ AS "NOT MERGED" - the rule this whole file follows.
 * `teamMergedToMainBy` being null or absent decides
 * NOTHING on its own: it falls through to the fields that did measure something.
 */
function weighMerge(
  status: Pick<RepoStatus, 'teamMergedToMain' | 'teamMergedToMainBy' | 'teamCommitsNotOnMain'>,
): { verdict: MergeVerdict; by: MergeEvidence | null } {
  if (status.teamMergedToMainBy === 'ancestry' || status.teamMergedToMainBy === 'content') {
    return { verdict: 'merged', by: status.teamMergedToMainBy }
  }

  if (status.teamMergedToMain === true) {
    return { verdict: 'merged', by: null }
  }

  // `== null` catches BOTH null and the `undefined` a Host without the field produces.
  if (status.teamCommitsNotOnMain == null) {
    return { verdict: 'unknown', by: null }
  }

  return status.teamCommitsNotOnMain === 0
    ? { verdict: 'merged', by: 'content' }
    : { verdict: 'not-merged', by: null }
}

/**
 * WHETHER THE TEAM'S WORK IS ON MAIN, and the words for saying so.
 *
 * THE 'content' WORDING SAYS WHAT HAPPENED, NOT WHAT FAILED. Rewritten shas whose changes are all
 * upstream are MERGED; a sentence implying otherwise sends a reader looking for a merge to run,
 * and the server correctly refuses the one they then reach for.
 */
export function mergeState(status: RepoStatus): MergeState {
  const { verdict, by } = weighMerge(status)
  const main = branchWord(status)

  if (verdict === 'merged') {
    return by === 'content'
      ? {
        verdict,
        by,
        label: `merged to ${main} · under different commits`,
        headline: {
          text:
            `Every change on ${status.teamBranch} is already on ${main}, under different commits — ` +
            'this team is safe to delete.',
          tone: 'done',
        },
        clause: `every change on ${status.teamBranch} is already on ${main}, under different commits`,
      }
      : {
        verdict,
        by,
        label: `merged to ${main}`,
        headline: { text: `Merged to ${main} — this team is safe to delete.`, tone: 'done' },
        clause: `the work is on ${main}`,
      }
  }

  if (verdict === 'unknown') {
    return {
      verdict,
      by,
      label: 'merged unknown',
      headline: {
        text: defaultBranchOf(status) === null
          ? 'Pushed, but whether every change reached the default branch cannot be measured: it is not known.'
          : `Pushed, but whether every change reached ${main} has not been measured yet.`,
        tone: 'unknown',
      },
      clause: `whether every change on ${status.teamBranch} reached ${main} has not been measured yet`,
    }
  }

  return {
    verdict,
    by,
    label: `not merged to ${main}`,
    headline: { text: `Pushed, but not merged to ${main} yet.`, tone: 'attention' },
    clause: `${status.teamBranch} on origin holds commits that are not on ${main}`,
  }
}

/**
 * THREE ANSWERS TO "HAS THIS WORK REACHED ORIGIN?", and the third is the one that keeps being
 * dropped. `teamPushed` is `boolean | null`, the null means NOT MEASURED, and the server answers
 * it routinely - `delete-remote-branch` removes the very ref pushed-ness is derived from.
 */
export type PushVerdict = 'pushed' | 'not-pushed' | 'unknown'

/**
 * EVERY SENTENCE THE CARD SAYS ABOUT REACHING ORIGIN, DERIVED ONCE - the pushed-ness twin of
 * `MergeState`, and it is here for the same reason.
 *
 * WHAT ONE `RepoStatus` RENDERS WHEN THE VOICES ANSWER SEPARATELY:
 *
 *   header   "Whether this team's branch reached origin has not been measured yet."
 *   row      "pushed unknown"
 *   step     Push - "the work is not on origin yet"
 *
 * A correct change to one voice can OPEN a contradiction as easily as close one. That is the
 * argument for the record and against a second hand-written answer: the three voices cannot be
 * kept in step by hand, only by having one source.
 */
export interface PushState {
  /** pushed / not-pushed / unknown. The one answer. */
  readonly verdict: PushVerdict
  /** The row beside the team ref, before the origin age is suffixed onto it. */
  readonly label: string
  /**
   * The card's leading sentence, for the states where pushed-ness is what it leads with.
   *
   * NULL WHEN THE ANSWER IS A SETTLED YES, which is never what the card leads with: work that IS
   * on origin leads with what happened to it afterwards, which is `MergeState`'s question. A
   * sentence invented for that case would be one no reader ever sees, and an unread sentence is
   * the one that drifts.
   */
  readonly headline: RepoHeadline | null
  /** The clause `repoLadder` embeds in its push-rung reasons. It never claims more than `verdict`. */
  readonly clause: string
}

/**
 * WHETHER THE WORK REACHED ORIGIN, and the words for saying so.
 *
 * `mainAhead` IS PART OF THE PUSHED-NESS ANSWER, which is not obvious and is load-bearing: `push`
 * publishes **main** (`main:team/{id}`), so with no local team ref the question "is there anything
 * to push?" is answered by this clone's main and not by the ref that is missing. "There is no
 * team/X to push, merge or delete" belongs only to its one true state - the post-delete clone,
 * level with origin/main, with genuinely nothing left to publish - and never above a live Push
 * button.
 */
export function pushState(status: RepoStatus): PushState {
  if (status.teamPushed === true) {
    return {
      verdict: 'pushed',
      label: status.teamPushedFrom ? `pushed · ${status.teamPushedFrom}` : 'pushed',
      headline: null,
      clause: 'the work is on origin',
    }
  }

  // FALSE MEANS ORIGIN HAS NO TEAM BRANCH, NOT THAT THERE IS WORK MISSING FROM IT. With no local
  // team ref and nothing on main that origin/main lacks, `push` would publish nothing new - a
  // team's first clone must not be told its work is only in this clone while it holds none.
  if (status.teamPushed === false && status.teamSha === null && status.mainAhead === 0) {
    return {
      verdict: 'not-pushed',
      label: 'not on origin',
      headline: {
        text: `This team has no work yet: there is no ${status.teamBranch}, and ${branchWord(status)} is level with ${baseBranchWord(status)}.`,
        tone: 'unknown',
      },
      clause: `there is no ${status.teamBranch}, and nothing here left to publish`,
    }
  }

  // A LOCAL TEAM BRANCH ORIGIN LACKS, OR HOLDS AN OLDER COMMIT OF, is named: "team/X is not
  // pushed" is the sentence the ladder's Push step and the board's team summary also say.
  if (status.teamPushed === false && status.teamBranchUnpushed === true) {
    return {
      verdict: 'not-pushed',
      label: 'not pushed',
      headline: {
        text: `${status.teamBranch} is not pushed: origin does not have its latest commits.`,
        tone: 'attention',
      },
      clause: `${status.teamBranch} is not pushed`,
    }
  }

  if (status.teamPushed === false) {
    return {
      verdict: 'not-pushed',
      label: 'not on origin',
      headline: {
        text: 'This team\u2019s work is only in this clone — nothing is pushed.',
        tone: 'attention',
      },
      clause: 'the work is not on origin yet',
    }
  }

  // NOT MEASURED FROM HERE DOWN, and nothing below asserts that anything did or did not reach
  // origin. What these say instead is what IS known: whether a local team ref exists at all, and
  // whether this clone's main carries anything origin/main does not.
  if (status.teamSha !== null) {
    return {
      verdict: 'unknown',
      label: 'pushed unknown',
      headline: {
        text: 'Whether this team\u2019s branch reached origin has not been measured yet.',
        tone: 'unknown',
      },
      clause: `whether ${status.teamBranch} reached origin has not been measured yet`,
    }
  }

  if (status.mainAhead !== null && status.mainAhead > 0) {
    return {
      verdict: 'unknown',
      label: 'pushed unknown',
      headline: {
        text:
          `There is no local ${status.teamBranch}, and this clone\u2019s ${branchWord(status)} holds work that ` +
          `${baseBranchWord(status)} does not.`,
        tone: 'attention',
      },
      clause:
        `there is no local ${status.teamBranch}, and this clone's ${branchWord(status)} holds work that ` +
        `${baseBranchWord(status)} does not`,
    }
  }

  return {
    verdict: 'unknown',
    label: 'pushed unknown',
    headline: { text: `There is no ${status.teamBranch} to push, merge or delete.`, tone: 'unknown' },
    clause: `there is no ${status.teamBranch}, and nothing here left to publish`,
  }
}

/**
 * THE ONE SENTENCE A PERSON OPENED THIS CARD FOR.
 *
 * Correct refs alone do not tell a person whether a team is safe to delete. `behind 5` and
 * `pushed · origin/… · MERGED TO MAIN` are facts; neither is an ANSWER. This names the consequence
 * instead, and the refs below it are the supporting detail.
 *
 * ORDER IS PRECEDENCE, and the first two are not negotiable:
 *
 * 1. NOT MEASURED outranks everything. Null means origin has not been read, and a card that guessed
 *    from stale numbers would be confidently wrong at exactly the moment it matters - the rule the
 *    rest of this file follows.
 * 2. DIVERGED outranks every state below it, because nothing below can be acted on until it is
 *    resolved. `repoLadder` is explicit: behind-only is a fast-forward, diverged is a rebase, and
 *    diverged is the state a team is most often stranded in.
 * 3. A MEASURED 'merged' outranks every pushed-ness answer. It is the only verdict that CLOSES the
 *    question rather than narrowing it, and a card that led with risk over a settled answer would
 *    tell a finished team its work was only in this clone. Measured only: a null merge verdict acquits
 *    nothing and falls through.
 * 4. Then the branch's own story, deepest risk first: work that exists nowhere else, then work
 *    whose journey to origin was never measured, then work pushed but not integrated.
 *
 * IT SAYS WHAT THE FACT MEANS, NOT THE FACT. "Merged to main" is on the row below; "safe to delete"
 * is why anybody asked.
 *
 * EVERY INTEGRATION SENTENCE COMES FROM `mergeState`, AND NONE IS WRITTEN HERE. This function
 * decides only which QUESTION the card leads with; what the answer says is one record shared with
 * the row and the ladder. Composing a sentence of its own would let the header and the step
 * line disagree.
 */
/** The card's headline for a repository with no working clone (`cloneReady: false`). */
export const NOT_READY = 'Not ready: this repository has not been cloned. Fetch makes the clone.'

export function repoHeadline(status: RepoStatus): RepoHeadline {
  // NO CLONE OUTRANKS EVEN NOT MEASURED: there is nothing here to measure, and Fetch makes it.
  if (status.cloneReady === false) {
    return { text: NOT_READY, tone: 'attention' }
  }

  if (defaultBranchOf(status) === null) {
    return {
      text: `This repository\u2019s default branch is not known, so nothing here is measured against it — ${DEFAULT_BRANCH_NOT_KNOWN}.`,
      tone: 'unknown',
    }
  }

  if (status.mainAhead === null || status.mainBehind === null) {
    return { text: 'Origin has not been read yet — figures below may be stale.', tone: 'unknown' }
  }

  if (status.mainAhead > 0 && status.mainBehind > 0) {
    return {
      text: `Diverged from ${originBranchWord(status)} — this clone needs a rebase before anything else.`,
      tone: 'attention',
    }
  }

  // THE WORDS COME FROM `pushState`, NOT FROM HERE, so the header, the row and the ladder cannot
  // give two answers from one status. Pushed-ness gets the record merged-ness has.
  const push = pushState(status)

  // In contributor mode the answer is the pull request's, not ancestry on the fork.
  if (status.upstreamUrl) {
    const pulled = contributorHeadline(status)
    if (pulled) return pulled
    if (push.headline && push.verdict !== 'pushed') return push.headline
    return { text: `${status.teamBranch} is on the fork, and no pull request has been opened yet.`, tone: 'attention' }
  }

  const merge = mergeState(status)

  // A MEASURED MERGE OUTRANKS EVERY PUSHED-NESS ANSWER, MEASURED OR NOT. It settles the
  // question the reader came with: work that is on main is safe whatever can still be asked about
  // the team ref. It sits ABOVE the not-pushed arm, not only above an unmeasured push: with every
  // change on main by patch-id, "nothing is pushed" is not merely unhelpful, it is FALSE, and it
  // is the sentence a rebased-then-merged team would get: the server can only measure
  // merged-ness inside the branches that also answer `teamPushed`, so a measured merge always arrives beside a measured
  // pushed-ness, and after a rebase that measurement is `false`.
  //
  // IT IS A REQUIREMENT rather than a preference. `repoLadder` reaches the tidy only past
  // `teamPushed === true`, so `false` is the one state that renders "safe to delete" ABOVE an
  // offered tidy step, and its header must not deny the merge.
  //
  // NULL IS STILL NOT MEASURED. This widens what a measured 'merged' outranks and nothing else:
  // `weighMerge` decides the verdict and an unknown one acquits no card.
  if (merge.verdict === 'merged') {
    return merge.headline
  }

  // `=== false` ONLY. `!status.teamPushed` would read NULL - the value the server answers after
  // `delete-remote-branch` removes the ref it derives pushed-ness from - as "nothing is pushed",
  // over work that is on origin/main, above a row reading `pushed unknown`.
  //
  // THE ROW AND THE CLAUSE ARE NOT WRONG WHERE THIS IS: "not on origin" is an accurate statement
  // about `team/{id}`, which is what the row and the ladder's push rung are talking about. Only the
  // HEADLINE speaks for the whole team, so only it must not over-claim.
  if (push.verdict === 'not-pushed' && push.headline) {
    return push.headline
  }

  // PUSHED-NESS ITSELF UNANSWERED. Every sentence below opens with "Pushed," and claiming that
  // from a null is the same error the not-pushed arm above avoids. After a
  // successful `delete-remote-branch` there is no team ref at all, and saying so is the honest
  // answer - the ladder's own tail says the same thing beneath it, because both read `pushState`.
  if (push.verdict === 'unknown' && push.headline) {
    return push.headline
  }

  return merge.headline
}

/**
 * THE BOARD'S TEAM SUMMARY LINE FOR UNPUSHED WORK: the team branch of every repository whose local
 * `team/{id}` origin lacks, so a finished workflow with unpushed work shows without opening the Git
 * dialog. Null when there is none, or nothing was measured.
 */
export function unpushedTeamBranchesLine(status: TeamRepoStatus | null): string | null {
  const all = status?.repos ?? []
  const repos = all.filter(repo => repo.teamBranchUnpushed === true)
  const first = repos[0]
  if (!first) return null
  const where = all.length === 1 ? '' : ` (${repos.map(repo => repo.name).join(', ')})`
  return `${first.teamBranch} is not pushed${where}`
}
