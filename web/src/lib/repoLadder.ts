import type { RepoActionResult, RepoStatus, WorktreeStatus } from '../api/types'
import {
  branchWord, DEFAULT_BRANCH_NOT_KNOWN, defaultBranchOf, getMergeToMainDisabledReason, mergeState,
  originBranchWord, pushState,
} from './repoStatus'

export type LadderRung = 'refreshed' | 'current' | 'pushed' | 'merged' | 'tidied' | 'done'

export type LadderAction =
  | 'fetch' | 'bring-current' | 'rebase' | 'push'
  | 'merge-to-main' | 'delete-remote-branch' | 'cleanup-worktrees'
  | 'open-pull-request'

export interface LadderStep {
  readonly rung: LadderRung
  /** null when the rung is reached but nothing can be done about it yet — see `reason`. */
  readonly action: LadderAction | null
  readonly reason: string
}

/**
 * WHICH RUNG IS NEXT, AND WHY.
 *
 * ONE LIVE ACTION IS THE WHOLE POINT. It teaches the sequence, and it makes the wrong states
 * unreachable by clicking rather than reachable and then refused.
 *
 * THE DIVERGED CASE IS THE ONE TO GET RIGHT: ahead AND behind is a REBASE, behind-only is a
 * fast-forward. Conflating them attempts a fast-forward that git refuses for a reason the reader
 * cannot act on. It is the state a team is most often stranded in.
 *
 * FRESHNESS IS A PRECONDITION OF EVERYTHING BELOW IT. `mainBehind` comes from the clone's last
 * fetch, so disabling `Bring current` on `mainBehind === 0` would disable the only control that
 * fetches by the stale number it exists to correct.
 */
/**
 * WORKTREES HOLDING COMMITS THIS CLONE'S MAIN DOES NOT HAVE.
 *
 * The ladder's rungs are all derived from the TEAM branch, and a team's work is not always
 * there. A team can finish with its commits on a member branch, unpushed, and no team branch
 * created - and then the push and merge rungs are rightly passed (nothing to push IS nothing to
 * push), leaving `Clean up worktrees` as the only step. Removing that worktree takes away the one
 * working tree for work that is nowhere else.
 *
 * `behindMain` IS NOISE HERE: a worktree with nothing outstanding is fully integrated however far
 * behind it has fallen, and reading the two together makes `ahead 0, behind 1` look like a problem.
 *
 * THE TEST IS CONTENT, NOT SHA ANCESTRY. `aheadMain` is `rev-list` against main, so a
 * REBASE — which gives the same change a different sha — leaves a worktree reading ahead while
 * every one of its commits is already on origin/main. `tidyStep` REFUSES rather than warns, so
 * counting by sha would leave a team that did everything right unable to be closed out through
 * the product at all.
 *
 * `commitsNotOnMain` is the patch-id answer, `git cherry origin/main <branch>` counted server-side
 * exactly as the server counts it for the team ref. It is CONSERVATIVE by construction — a commit
 * whose patch was modified during integration still counts as outstanding — and that direction is
 * load-bearing, because this is the guard on the one action that can destroy a checkout.
 *
 * NULL IS NOT MEASURED, AND HERE IT MUST NOT ACQUIT. A failed `git cherry` is not evidence of zero,
 * so an unmeasured content count FALLS BACK to the sha reading rather than overriding it: a
 * worktree that is ahead by sha and unmeasured by patch-id keeps refusing the tidy. When neither
 * was measured — a detached HEAD, an unmeasurable tree — nothing convicts, which is the
 * recoverable direction: `cleanup-worktrees` runs `git worktree remove`, which never deletes a
 * branch, so a tidy that should not have happened costs a checkout and no commits.
 *
 * `== null` catches BOTH null and the `undefined` a Host without the field produces.
 */
export function unintegratedWorktrees(status: RepoStatus): readonly WorktreeStatus[] {
  // An OPEN card's tree is never touched by the tidy, so it cannot hold the tidy back —
  // with a tree per card, an open card's branch is ahead of main as a matter of course.
  return (status.worktrees ?? []).filter(wt => wt.open !== true).filter(wt =>
    wt.commitsNotOnMain == null
      ? wt.aheadMain !== null && wt.aheadMain > 0
      : wt.commitsNotOnMain > 0)
}

/**
 * The tidied rung, which is the one place a worktree can be destroyed.
 *
 * ONE HELPER BECAUSE THERE ARE THREE OFFER SITES. `cleanup-worktrees` is reachable from the
 * work-is-on-main shortcut, from the post-rebase arm and from the ordinary tail, and a guard
 * written at one of them would leave the other two open.
 *
 * It REFUSES rather than warns, matching the endpoint it fronts: that already refuses a DIRTY
 * worktree outright, and committed-but-unintegrated is the same class of loss with a quieter face.
 */
function tidyStep(status: RepoStatus): LadderStep {
  const held = unintegratedWorktrees(status)

  if (held.length > 0) {
    // THE NUMBER SHOWN IS THE ONE THAT CONVICTED. `commitsNotOnMain` is what this filters on,
    // and printing `aheadMain` beside it would name a count that decides nothing — on a rebased
    // branch the two disagree.
    const where = held
      .map(wt => `${wt.member ?? wt.path} on ${wt.branch ?? 'a detached HEAD'} `
        + `(${wt.commitsNotOnMain ?? wt.aheadMain})`)
      .join(', ')

    return {
      rung: 'tidied',
      action: null,
      reason:
        `work is only on a member's branch — ${where}. Integrate it before tidying: removing the ` +
        'worktree takes away the one checkout it lives in',
    }
  }

  return { rung: 'tidied', action: 'cleanup-worktrees', reason: 'the team left worktrees behind' }
}

/**
 * EVERY CAPTION SAYS WHAT THE BUTTON UNLOCKS, NOT ONLY WHAT IS TRUE NOW.
 *
 * THE LADDER'S WHOLE PREMISE IS TEACHING THE SEQUENCE, so a rung that states its CONDITION and
 * stops has done half its job: a reader must also learn, for example, that `Merge to main` is not
 * offered AT ALL until this clone is current.
 *
 * THE UNLOCK IS THE LADDER'S OWN SEQUENCING WHERE THAT IS ALL THAT IS KNOWN, and an ENDPOINT
 * precondition where there is one to name - `push` creating the ref `merge-to-main` resolves, a
 * merge making `delete-remote-branch`'s ancestry check hold. A caption must never claim something
 * the endpoint will refuse, and the pull the other way is just as real: `MergeToMainAsync` does
 * not require the team ref to be a fast-forward of origin/main - a branch merely behind main is
 * joined to it with a real merge commit - so "rebase before the merge will be accepted" is a
 * sentence that must not be written here.
 *
 * ONE SENTENCE PER RUNG. A caption a person skips teaches nothing either.
 */
export function nextStep(status: RepoStatus, refreshedThisOpen: boolean): LadderStep {
  if (!refreshedThisOpen) {
    return {
      rung: 'refreshed',
      action: 'fetch',
      reason:
        'read the current state of origin: every rung below is measured against it, and none is ' +
        'offered until it has been read',
    }
  }

  // EVERY RUNG BELOW IS MEASURED AGAINST THE DEFAULT BRANCH, and every action refuses
  // (409) while it is not known - so no button is offered, and the reason says how to learn it.
  if (defaultBranchOf(status) === null) {
    return {
      rung: 'current',
      action: null,
      reason: `${DEFAULT_BRANCH_NOT_KNOWN}; nothing below is offered until it is known`,
    }
  }

  const main = branchWord(status)
  const origin = originBranchWord(status)

  // THE TWO BLOCKED RUNGS HAVE NO BUTTON, so what they owe the reader is what CLEARING them
  // unlocks - and both conditions are refusals the server really makes: `RebaseRepoAsync` answers
  // 409 for a dirty tree and for any HEAD that is not main, `MergeToMainAsync` 400 for both.
  if (status.dirty) {
    return {
      rung: 'current',
      action: null,
      reason:
        `the clone has uncommitted changes, which rebase and merge to ${main} both refuse: commit ` +
        'or remove them to unlock the rungs below',
    }
  }

  if (status.headCheckout === 'detached') {
    return {
      rung: 'current',
      action: null,
      reason:
        `the clone is on a detached HEAD, which rebase and merge to ${main} both refuse: check ${main} ` +
        'out to unlock the rungs below',
    }
  }

  if (status.mainAhead === null || status.mainBehind === null) {
    return {
      rung: 'current',
      action: 'fetch',
      reason:
        'how far this clone has drifted is unknown: fetching measures it, and that is what ' +
        'decides whether Bring current or Rebase is the next rung',
    }
  }

  if (status.mainBehind > 0) {
    // NEITHER RUNG BELOW IS OFFERED WHILE THIS ONE IS LIVE - one action, in order - which is the
    // half of the sequence the condition alone never taught. Said as the LADDER'S ordering on
    // purpose: the merge endpoint does not refuse a behind clone, and a caption saying it does
    // would send a reader rebasing the very commits the merge arm exists to leave alone.
    return status.mainAhead > 0
      ? {
        rung: 'current',
        action: 'rebase',
        reason:
          `this clone has its own commits AND is behind ${origin}, so rebase first: it is what ` +
          'lets push and merge use the same history afterward, and neither Push nor Merge to ' +
          `${main} is offered until this clone is current`,
      }
      : {
        rung: 'current',
        action: 'bring-current',
        reason:
          `${origin} has moved ahead of this clone, so bring it current: neither Push nor ` +
          `Merge to ${main} is offered until it is`,
      }
  }

  // IN CONTRIBUTOR MODE THE WORK GOES UPSTREAM AS A PULL REQUEST, and Merge to main is
  // never offered: origin is the fork, and merging into its default branch would diverge it from
  // upstream. Everything above this line - refreshed, current - is the same for both.
  if (status.upstreamUrl) return contributorStep(status)

  // NOTHING TO PUSH IS NOT THE SAME AS NOT PUSHED, AND THIS IS WHAT STOPS THE RUNG 3 <-> 5 CYCLE.
  //
  // THE MERGED-NESS GUARD BELOW IS NOT ENOUGH ON ITS OWN. It covers `teamPushed` going false
  // while merged-ness stays TRUE. But the server can only
  // answer merged-ness from a team ref - it derives it by asking whether the team sha is an
  // ancestor of origin/main - and the Tidied rung's `delete-remote-branch` deletes the last ref
  // there is. So the delete takes `teamSha`, `teamPushed` AND `teamMergedToMain` all to NULL at
  // once, and `null !== true` would walk into the same arm as work that was never pushed: the
  // ladder would go BACKWARDS to Pushed and offer to push work that is already on main.
  //
  // THE QUESTION THAT SURVIVES THE DELETE is whether this clone's main is level with origin/main.
  // With no team branch at all and nothing of its own to push, the work IS on main, and what is
  // left is the tidied/done tail. `mainAhead` is non-null here - the unknown-drift return sits
  // above - and THIS question needs no new field: it is answerable from state already on the wire.
  //
  // That is not true of the ladder as a whole. `teamCommitsNotOnMain` exists because
  // patch-equivalence is a measurement only the server can make and no combination of existing
  // fields implies it. A field added to the record can drift between C# and TypeScript, which is
  // why `RepoStatusShape` in `RepoEndpointsTests` lists every field and fails in both directions.
  const workIsOnMain = status.teamSha === null && status.mainAhead === 0

  // THE ONE SOURCE OF TRUTH, AND THE LADDER IS ONE OF ITS THREE READERS. Every sentence below that
  // says anything about integration embeds `merge.clause`; none of them writes its own, so the
  // ladder and the header can never contradict each other about one `RepoStatus`.
  //
  // THE GATE IS THE VERDICT, NOT `teamMergedToMain`. The server answers true for BOTH
  // mechanisms, and reading one field cannot tell a rewritten-but-integrated branch from an
  // unmerged one.
  const merge = mergeState(status)

  if (!workIsOnMain && merge.verdict !== 'merged') {
    if (status.teamPushed !== true) {
      // THE MIRROR OF THE CRITICAL ABOVE, IN THE OTHER ARM. Where a LOCAL `team/{id}` exists,
      // the server answers `teamPushed` by asking whether that LOCAL BRANCH is on
      // `origin/team/{id}` - a fact about the local branch - while `push` publishes **main**
      // (`main:team/{id}`). The two never meet: a clone holding a local team branch with a commit
      // main does not have would sit here forever, every click answering 200 and "Pushed main to
      // team/X." while the card still says the work is not on origin.
      //
      // `cloneMainOnTeamBranch === false` says main carries something the team ref does not, which
      // after a push of main would still be true - so pushing changes nothing and the rung cannot
      // advance. REFUSE AND EXPLAIN rather than offer it. `=== false` only: null is not measured.
      //
      // THE DESIGN DOES NOT let `push` publish some other ref instead. Push publishes what the
      // ladder is talking about, or the flag and the action drift apart.
      // THE CONDITION IS "THE LOCAL TEAM REF IS STRICTLY AHEAD OF MAIN", and it is derivable from
      // fields already on the record - no new measurement needed:
      //
      //   cloneMainOnTeamBranch === true   main IS an ancestor of the team ref (main is ON it)
      //   teamSha !== mainSha              they are not the same commit
      //   => the team ref has commits main does not
      //
      // Pushing main then republishes main, `teamPushed` asks whether the LOCAL team ref is on
      // origin/team/{id}, and it still is not - so the rung never advances however many times it
      // is clicked. Where the team ref EQUALS main (the shape the seeded push pattern produces)
      // the shas match and push is offered.
      if (status.cloneMainOnTeamBranch === true
        && status.teamSha !== null
        && status.teamSha !== status.mainSha) {
        return {
          rung: 'pushed',
          action: null,
          reason:
            `the local ${status.teamBranch} holds commits this clone's ${main} does not, and push ` +
            `publishes ${main} - so it cannot advance this from here; merge or rebase them together `
            + 'first',
        }
      }

      // NULL IS NOT MEASURED HERE TOO. The arm is entered from `teamPushed !== true`, which is
      // REACHED by a null and must not be ANSWERED by one: "the work is not on origin yet" would be
      // a confident claim written from the absence of a measurement, beside a header reading
      // "Whether this team's branch reached origin has not been measured yet" and a row reading
      // `pushed unknown` - one status, two answers.
      //
      // THE OFFER STANDS AND THAT IS DELIBERATE. `push` is non-destructive and it is what
      // SETTLES the open question, so it stays offered with the answer unknown; only the sentence
      // beneath it must not over-claim. `pushState` supplies that sentence, the header and
      // the row - one record, the way `merge.clause` already works one rung below.
      const push = pushState(status)

      // WHAT THE PUSH UNLOCKS IS AN ENDPOINT PRECONDITION, not merely this ladder's order:
      // `MergeToMainAsync` answers 400 - "Team branch team/{id} does not exist locally or on
      // origin" - when neither ref resolves, and publishing main to `team/{id}` is what creates
      // the branch it then integrates. SAID ONCE FOR BOTH VERDICTS: the clause above them differs
      // because pushed-ness differs, but the button does the same thing either way.
      const publishes =
        `push publishes this clone's ${main} to ${status.teamBranch}, which is the branch Merge to ` +
        `${main} then integrates`

      return {
        rung: 'pushed',
        action: 'push',
        reason: push.verdict === 'unknown'
          ? `${push.clause}, and ${publishes} - it changes nothing if it is already there`
          : `${push.clause}, and ${publishes}`,
      }
    }

    // THE BRANCH ON ORIGIN IS NOT ALWAYS WHERE THIS CLONE'S WORK IS, and reading `teamPushed` as if
    // it were dead-ends a team. The server sets it TRUE the moment `origin/team/{id}`
    // RESOLVES - it means "a branch exists on origin", never "this clone's work is on it". After a
    // rebase the branch holds the pre-rebase copies, so merge answers 400, push 409 and delete 409:
    // every rung refuses, with nothing on the card saying why.
    //
    // `cloneMainOnTeamBranch` IS THE FIELD THAT KNOWS. The server measures whether local main
    // is reachable from the team ref. FALSE is exactly the post-rebase state; NULL is not measured and must not be read
    // as false, so this arm asks for `=== false` and the ordinary path is untouched by it.
    if (status.cloneMainOnTeamBranch === false) {
      // Local main carries work origin/main does not, and (mainBehind is 0 by the return above) it
      // fast-forwards origin/main - which is the one thing the server will accept. It integrates
      // LOCAL MAIN here rather than the stale branch, and says which ref it used.
      //
      // THIS ARM UNLOCKS LESS THAN THE ORDINARY MERGE AND THE CAPTION SAYS SO. The endpoint's own
      // summary here reads "The team branch on origin was stale and was not integrated", and the
      // Tidied rung's delete goes on refusing afterwards - so a clause promising the delete would
      // be this caption claiming exactly what the endpoint will refuse.
      if (status.mainAhead > 0) {
        return {
          rung: 'merged',
          action: 'merge-to-main',
          reason:
            `this clone's ${main} has work that the branch on origin does not, so merge to ${main}: ` +
            `that is what puts it on ${origin}, though the stale branch on origin is not ` +
            'integrated by it',
        }
      }

      // THIS CLONE'S MAIN IS LEVEL WITH ORIGIN/MAIN AND THE BRANCH IS STILL NOT INTEGRATED, so
      // there is nothing left to fast-forward with and no action to offer. SAYING SO is the whole
      // obligation here: a button that must fail is worse than no button.
      //
      // THE RUNG IS `merged`, NOT `tidied`. `tidied` would draw Merged as a hollow question mark
      // and Tidied as the rung the reader is standing on - "you have finished merging" - directly
      // under a header saying the work was not merged. The ladder may not step past Merged while
      // the verdict says it is not merged; the position and the sentence are two faces of one answer.
      //
      // THE 'content' CASE CANNOT REACH HERE. Rewritten shas whose changes are all on main are
      // MERGED, so the verdict gate above sends them to the tidied tail with the delete offered -
      // which is the point: that state loses nothing by being closed out.
      return {
        rung: 'merged',
        action: null,
        reason: merge.verdict === 'unknown'
          ? `${merge.clause}, and this clone is level with ${origin} - so there is nothing to ` +
            'merge from here until it is'
          : `${merge.clause}, and this clone is level with ${origin} - so merge or rebase them ` +
            'together before the branch can be closed out',
      }
    }

    // A CHERRY-PICK MAKES A DIFFERENT SHA FOR THE SAME CHANGE, AND THAT IS ANSWERED BY THE VERDICT GATE
    // ABOVE, not by an arm of its own here. A second arm would agree with the headline only for as
    // long as somebody kept the two in step by hand. `mergeState` answers both, so integrated work
    // never enters this block at all.
    const refused = getMergeToMainDisabledReason(status)
    if (refused) {
      return { rung: 'merged', action: null, reason: refused }
    }

    // THE REASON FOLLOWS THE VERDICT HERE TOO. "The work is on origin but not on main" is a claim
    // about content, and it must not be made on a status where content was never measured - the
    // header says "not been measured yet" over exactly that state.
    //
    // AND THE UNLOCK IS THE RUNG BELOW, WHICH IS A REAL REFUSAL RATHER THAN AN ORDERING.
    // `DeleteRemoteBranchAsync` answers 409 - "origin/team/{id} is not on origin/main yet, so
    // deleting it would lose work" - until ancestry or patch-equivalence holds, and the merge is
    // what makes it hold. It is appended to BOTH arms because it is true of the button whether or
    // not content was ever measured, and it says nothing about what is on main.
    return {
      rung: 'merged',
      action: 'merge-to-main',
      reason: merge.verdict === 'unknown'
        ? `the work is on origin, and ${merge.clause}: merging is what lets the branch on origin ` +
          'be deleted afterwards'
        : `the work is on origin but not on ${main}, so merge to ${main}: that is what lets the branch ` +
          'on origin be deleted afterwards',
    }
  }

  // TIDIED. The work is on main; what is left is what the team left behind.
  //
  // THE SENTENCE NAMES THE MECHANISM WHEN THERE IS ONE TO NAME. `merge.clause` reads "the work is
  // on main" for ancestry and "every change on team/X is already on main, under different commits"
  // for a rewritten branch: the reader is told
  // plainly that the work arrived, not that something failed.
  if (status.teamPushed === true) {
    if (merge.verdict === 'merged') {
      // THE LAST THING THIS LADDER DOES TO ORIGIN, and saying what comes after it is the whole
      // unlock: nothing on origin is left to hold the Tidied rung back, and only worktrees on disk
      // can remain. `merge.clause` is embedded, never re-worded - it is the one source of truth
      // for anything about integration, and this is one of its three readers.
      return {
        rung: 'tidied',
        action: 'delete-remote-branch',
        reason:
          `${merge.clause}, so the team branch on origin is finished with: deleting it closes ` +
          'out origin, and only worktrees can remain after it',
      }
    }

    // REACHED ONLY BY THE `workIsOnMain` SHORTCUT, and only with a branch still on origin: no
    // LOCAL team ref (so `teamSha` is null) but `origin/team/{id}` resolves. The shortcut's
    // reasoning - nothing local left to push, so the work is on main - is about this CLONE, and it
    // says nothing about what that branch on origin holds.
    //
    // SO THE DELETE IS NOT OFFERED. Captioning it "the work is on main" would be a
    // sentence-without-evidence - fronting the one action that destroys something.
    return {
      rung: 'merged',
      action: null,
      reason:
        `this clone has nothing left to push, but ${merge.clause} - so the branch on origin ` +
        'cannot be closed out from here',
    }
  }

  if (status.worktrees.length > 0) {
    return tidyStep(status)
  }

  // SAY WHAT WAS CHECKED, NOT THAT THE TREE IS CLEAN.
  //
  // `worktrees` is built from `git worktree list`, so this rung's "done" is a claim about THAT
  // LISTING and never about the disk. On Windows `git worktree remove` can fail with
  // `Filename too long` when the tree holds `web/node_modules` - and it fails AFTER deregistering
  // the worktree and deleting most of the checkout, so the orphan is on disk and gone from the
  // listing. Recorded in the seeded `worktrees` skill, which tells a
  // manager to verify removal against the disk for exactly this reason.
  //
  // The product cannot see it without walking the filesystem, which this derivation deliberately
  // does not do. So the honest thing is to name the instrument rather than overclaim the result:
  // a reader who knows the answer came from `git worktree list` knows what it does not cover.
  return {
    rung: 'done',
    action: null,
    reason: 'nothing left to do, by everything git reports',
  }
}

/**
 * THE LADDER BELOW `current` FOR A REPOSITORY IN CONTRIBUTOR MODE. Pushed publishes to the
 * fork; Merged is the pull request upstream, merged there by somebody else. So the one action on
 * the Merged rung is Open pull request, and only while none is open: an open one is linked, never
 * doubled. Landed is GitHub's word about the recorded pull request, not ancestry.
 */
function contributorStep(status: RepoStatus): LadderStep {
  const pull = status.pullRequest ?? null
  const base = status.defaultBranch ? `upstream/${status.defaultBranch}` : 'upstream'

  if (pull?.landing === 'landed') {
    if (status.worktrees.length > 0) return tidyStep(status)
    return {
      rung: 'done',
      action: null,
      reason: `pull request #${pull.number} was merged upstream, and nothing is left to do by everything git reports`,
    }
  }

  if (pull?.landing === 'in-review') {
    return {
      rung: 'merged',
      action: null,
      reason:
        `pull request #${pull.number} is open upstream, in review - follow it on GitHub; a second one ` +
        'is not offered while it is open',
    }
  }

  if (status.teamPushed !== true) {
    const push = pushState(status)
    return {
      rung: 'pushed',
      action: 'push',
      reason:
        `${push.clause}, and push publishes this clone's ${branchWord(status)} to ${status.teamBranch} ` +
        'on the fork, which is the branch Open pull request is opened from',
    }
  }

  if (pull?.landing === 'declined') {
    return {
      rung: 'merged',
      action: 'open-pull-request',
      reason:
        `pull request #${pull.number} was closed upstream without merging, so this work has not landed; ` +
        `open another from ${status.teamBranch} when it is ready`,
    }
  }

  if (pull?.landing === 'unknown') {
    return {
      rung: 'merged',
      action: 'open-pull-request',
      reason:
        `GitHub could not say what pull request #${pull.number} is now; Open pull request asks GitHub ` +
        'first and links one that is open rather than opening a second',
    }
  }

  return {
    rung: 'merged',
    action: 'open-pull-request',
    reason:
      `${status.teamBranch} is on the fork, so open a pull request against ${base}: that is how the ` +
      'work goes upstream in contributor mode, and somebody there merges it',
  }
}

/**
 * THE FIVE RUNGS A PERSON CLIMBS, in the order they are climbed.
 *
 * `done` is deliberately NOT one of them: it is the state of having climbed all five, not a sixth
 * thing to do, and rendering it as a rung would put a permanently-unreachable row at the bottom of
 * every healthy repository.
 */
export type DisplayRung = Exclude<LadderRung, 'done'>

export const LADDER_RUNGS: readonly DisplayRung[] = ['refreshed', 'current', 'pushed', 'merged', 'tidied']

export type RungState = 'satisfied' | 'current' | 'pending' | 'unknown'

/**
 * WHERE A RUNG STANDS RELATIVE TO THE STEP THE LADDER IS ON.
 *
 * IN `lib/` RATHER THAN IN THE CARD, for the reason every other derivation here is: logic inside a `.vue` file is still logic a direct test reaches only
 * through the rendered card. A ladder that ticks the wrong rungs looks exactly like one that ticks
 * the right ones to a source-reading spec.
 *
 * `current` IS NOT `satisfied`, and that distinction is the whole display. A blocked rung — a dirty
 * clone, a detached HEAD — reports `current`: it is where the reader is standing and what they must
 * deal with, not something already behind them.
 */
/**
 * WHETHER THE EVIDENCE FOR A RUNG EXISTS AT ALL.
 *
 * `Pushed` and `Merged` are answered from the TEAM ref. With no team branch there is nothing to
 * push and nothing to merge, so the ladder steps past them — and every rung below the current
 * step would render as satisfied. `Pushed ✓ Merged ✓` over a team that has pushed nothing is not a
 * cosmetic wrong: it is the reader's only summary of whether the work is safe, shown beside the
 * one button that deletes a worktree.
 *
 * THE WIRE MODELS THIS AND THE LADDER MUST NOT COLLAPSE IT. `teamMergedToMain` is documented
 * "Null if origin unknown"; `cloneMainOnTeamBranch` "NOT false, which would accuse a team that
 * never pushed". Three states cross the wire and all three reach the screen.
 */
function hasEvidence(rung: DisplayRung, status: RepoStatus): boolean {
  // MERGED IMPLIES PUSHED. Work that reached origin/main got to origin, whatever `teamPushed` says
  // about the team ref - which the delete-remote-branch rung can take to NULL underneath it.
  // THE SAME `mergeState` THE HEADER AND THE ROW READ, so the Merged rung cannot light while the
  // header says not merged, nor stay hollow while the header says safe to delete. In particular a
  // 'content' merge LIGHTS this rung: rewritten shas whose changes are all on main are merged, and
  // a question mark there would send readers looking for a merge to run.
  // In contributor mode Merged is GitHub's word about the pull request, never ancestry.
  if (status.upstreamUrl && (rung === 'pushed' || rung === 'merged')) {
    const landed = status.pullRequest?.landing === 'landed'
    return rung === 'pushed' ? status.teamPushed === true || landed : landed
  }
  if (rung === 'pushed') {
    return status.teamPushed === true
      || mergeState(status).verdict === 'merged'
  }
  if (rung === 'merged') return mergeState(status).verdict === 'merged'

  // Refreshed, Current and Tidied are answered from the clone itself, which is always readable.
  return true
}

export function rungState(rung: DisplayRung, step: LadderStep, status: RepoStatus): RungState {
  // EVERY RUNG IS SATISFIED AT `done`, and this arm is required rather than incidental: `done` is
  // not in LADDER_RUNGS, so `indexOf` would answer -1 and mark every rung pending — the finished
  // repository rendering as the untouched one.
  if (step.rung === 'done') {
    return hasEvidence(rung, status) ? 'satisfied' : 'unknown'
  }

  const here = LADDER_RUNGS.indexOf(rung)
  const now = LADDER_RUNGS.indexOf(step.rung)

  // A RUNG ABOVE THE STEP STILL SAYS WHETHER IT IS TRUE. Returning `pending` unconditionally would
  // make the card say two things at once: a headline reading "Merged to main - safe to delete"
  // above a Merged rung drawn as not reached, both from the same `RepoStatus` - as on a clone that
  // is merely behind origin, which puts the live step at `current`.
  //
  // WHICH RUNG IS LIVE IS STILL POSITIONAL - one action, in order, and the button follows it.
  // Whether a rung's evidence HOLDS is a different question, and answering it here is what stops
  // the two disagreeing.
  //
  // ABSENCE OF EVIDENCE ABOVE STAYS `pending` RATHER THAN `unknown`: a fresh repository must not sprout question marks on rungs nobody has
  // reached yet.
  //
  // ONLY THE TWO RUNGS THAT MEASURE SOMETHING. `hasEvidence` answers TRUE unconditionally for
  // Refreshed, Current and Tidied - they are "answered from the clone itself, which is always
  // readable" - which is the right answer BELOW the step and nonsense above it: it would tick
  // Tidied on a repository nobody has tidied. Pushed and Merged are the rungs whose truth is a
  // fact about the world rather than about the clone being readable.
  if (here > now) {
    const measured = rung === 'pushed' || rung === 'merged'

    return measured && hasEvidence(rung, status) ? 'satisfied' : 'pending'
  }
  if (here === now) return 'current'

  return hasEvidence(rung, status) ? 'satisfied' : 'unknown'
}

/** What each rung is called on screen. */
export const RUNG_LABELS: Record<DisplayRung, string> = {
  refreshed: 'Refreshed',
  current: 'Current',
  pushed: 'Pushed',
  merged: 'Merged',
  tidied: 'Tidied',
}

/**
 * What the one enabled button says.
 *
 * A `Record` OVER THE UNION RATHER THAN A SWITCH WITH A DEFAULT: an action added to `LadderAction`
 * and not labelled here is a COMPILE error, where a default arm would ship a button reading
 * whatever the fallback said and nothing would fail.
 */
export const ACTION_LABELS: Record<LadderAction, string> = {
  'fetch': 'Fetch origin',
  'bring-current': 'Bring current',
  'rebase': 'Rebase onto origin/main',
  'push': 'Push',
  'merge-to-main': 'Merge to main',
  'delete-remote-branch': 'Delete the branch on origin',
  'cleanup-worktrees': 'Clean up worktrees',
  'open-pull-request': 'Open pull request',
}

/**
 * The button's words for THIS repository: `ACTION_LABELS` with `main` read as the stored
 * default branch. The two labels that name the branch say it; the rest are as above.
 */
export function actionLabel(action: LadderAction, status: RepoStatus): string {
  if (action === 'rebase') return `Rebase onto ${originBranchWord(status)}`
  if (action === 'merge-to-main') return `Merge to ${branchWord(status)}`
  return ACTION_LABELS[action]
}

/**
 * DID THIS RESULT ACTUALLY READ ORIGIN?
 *
 * THE HINGE THE WHOLE LADDER TURNS ON, and the reason it is a function in `lib/` rather than four
 * lines inside the card. `refreshedThisOpen` may only be set from this answer: set it in the wrong
 * branch and the ladder steps off the Refreshed rung on a fetch that reached nothing, and every
 * rung below it is then derived from exactly the stale view of origin the Refreshed rung exists
 * to replace.
 * Nothing about the card's markup would look different, and no source-reading spec can tell the two
 * implementations apart — so the decision lives where a real test can call it.
 *
 * AN UNREACHABLE ORIGIN IS REPORTED, NOT REFUSED: `POST .../fetch` answers **200** carrying
 * `success: false`, with git's own stderr in `status.originUnreachableReason`. It does not throw, so
 * `success` is the one field separating "fetched" from "could not reach origin" and this reads it
 * explicitly rather than treating any resolved promise as a refresh.
 */
export function didRefreshOrigin(result: RepoActionResult): boolean {
  return result.success === true
}

/**
 * WHY A REFRESH DID NOT LAND, in the words closest to the cause.
 *
 * Git's own stderr first — it names the host, the credential or the missing remote, and a sentence
 * composed here could only be vaguer. The server's message second, and a last resort that is never
 * empty: a failure notice with no text reads as a rendering bug rather than as an unreachable
 * origin.
 */
export function refreshFailureReason(result: RepoActionResult): string {
  return result.status?.originUnreachableReason || result.message || 'origin was not reachable'
}

/** The trees whose card is still open, which the tidy leaves in place. */
export function openWorktrees(status: RepoStatus): readonly WorktreeStatus[] {
  return (status.worktrees ?? []).filter(wt => wt.open === true)
}

/** A byte count as a person reads it: `512 B`, `3.4 MB`. Empty when not measured. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) return ''
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return unit === 0 ? `${value} ${units[unit]}` : `${value.toFixed(1)} ${units[unit]}`
}
