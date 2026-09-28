import { describe, expect, it } from 'vitest'
import { ACTION_LABELS, actionLabel, bringCurrentAndMergeAdvice, didRefreshOrigin, formatBytes, LADDER_RUNGS, nextStep, openWorktrees, refreshFailureReason, RUNG_LABELS, rungState, unintegratedWorktrees } from '../repoLadder'
import type { LadderAction, LadderRung } from '../repoLadder'
import { formatMergedState, formatPushedState, mergeState, repoHeadline } from '../repoStatus'
import type { RepoActionResult, RepoStatus, WorktreeStatus } from '../../api/types'

const base = (o: Partial<RepoStatus> = {}): RepoStatus => ({
  defaultBranch: 'main',
  name: 'Harness', clonePath: null, mainSha: 'aaaaaaa', teamSha: 'bbbbbbb',
  mainAhead: 0, mainBehind: 0, dirty: false, headCheckout: 'main',
  teamBranch: 'team/alpha', teamPushed: true, teamPushedFrom: 'origin/team/alpha',
  teamMergedToMain: true, cloneMainOnTeamBranch: null, teamCommitsNotOnMain: null,
  ...o,
} as RepoStatus)

describe('nextStep', () => {
  it('asks for a fetch before anything else when this open has not refreshed', () => {
    const step = nextStep(base(), false)
    expect(step.rung).toBe('refreshed')
    expect(step.action).toBe('fetch')
  })

  it('offers a REBASE when the clone is both ahead and behind', () => {
    const step = nextStep(base({ mainAhead: 3, mainBehind: 4 }), true)
    expect(step.rung).toBe('current')
    expect(step.action).toBe('rebase')
  })

  it('offers BRING CURRENT when the clone is only behind', () => {
    const step = nextStep(base({ mainAhead: 0, mainBehind: 4 }), true)
    expect(step.action).toBe('bring-current')
  })

  it('refuses to act while the clone has uncommitted changes', () => {
    const step = nextStep(base({ dirty: true }), true)
    expect(step.rung).toBe('current')
    expect(step.action).toBeNull()
    expect(step.reason).toMatch(/uncommitted/)
  })

  it('refuses to act on a detached HEAD', () => {
    const step = nextStep(base({ headCheckout: 'detached' }), true)
    expect(step.rung).toBe('current')
    expect(step.action).toBeNull()
  })

  it('asks for a fetch when drift is unknown (mainAhead null)', () => {
    const step = nextStep(base({ mainAhead: null }), true)
    expect(step.rung).toBe('current')
    expect(step.action).toBe('fetch')
  })

  it('offers PUSH when the team branch is not on origin', () => {
    const step = nextStep(base({ teamPushed: false, teamMergedToMain: false }), true)
    expect(step.rung).toBe('pushed')
    expect(step.action).toBe('push')
  })

  it('offers MERGE TO MAIN once pushed but not yet merged', () => {
    const step = nextStep(base({ teamPushed: true, teamMergedToMain: false }), true)
    expect(step.rung).toBe('merged')
    expect(step.action).toBe('merge-to-main')
  })

  it('offers DELETE REMOTE BRANCH once merged, while the team branch is still pushed', () => {
    const step = nextStep(base({ teamMergedToMain: true, teamPushed: true }), true)
    expect(step.rung).toBe('tidied')
    expect(step.action).toBe('delete-remote-branch')
  })

  it('offers CLEANUP WORKTREES once merged and unpushed, when the team left worktrees', () => {
    const step = nextStep(
      base({
        teamMergedToMain: true,
        teamPushed: false,
        worktrees: [{ path: 'C:\\wt', branch: 'team/alpha', sha: 'ccccccc', member: 'dev', aheadMain: 0, behindMain: 0, commitsNotOnMain: 0 }],
      }),
      true,
    )
    expect(step.rung).toBe('tidied')
    expect(step.action).toBe('cleanup-worktrees')
  })

  it('is DONE when everything is satisfied and no worktrees remain', () => {
    const step = nextStep(base({ teamMergedToMain: true, teamPushed: false, worktrees: [] }), true)
    expect(step.rung).toBe('done')
    expect(step.action).toBeNull()
  })
})

/**
 * THE STATUS THE SERVER REALLY PRODUCES AFTER A SUCCESSFUL DELETE, AND WHY IT IS WRITTEN OUT IN FULL.
 *
 * MEASURED IN A BROWSER: the whole ladder driven against a real diverged clone - Rebase, Push,
 * Merge to main - and then `Delete the branch on origin`. Read wrongly, this status sends the
 * ladder BACKWARDS to Pushed, offering `Push`, captioned "the work is not on origin yet", about
 * work that is already on main. Pushing would recreate the branch from main, Merged would say
 * already-merged, and Tidied would offer the delete again: rungs 3 <-> 5, forever.
 *
 * A STATUS BUILT FROM `base()` CANNOT SHOW THIS - `base()` always has a `teamSha`, and the
 * never-cycles property enumerates everything EXCEPT that field. The server does not work that
 * way: merged-ness is derived from the team ref, so deleting the ref takes `teamSha`, `teamPushed` and
 * `teamMergedToMain` to null together, and `null !== true` is the branch for work that was never
 * pushed.
 *
 * DO NOT "SIMPLIFY" THIS BACK TO `base({ ... })`. The point of writing all seventeen fields out is
 * that this shape was read off the wire rather than imagined. The six the case turns on -
 * `mainAhead`, `mainBehind`, `teamSha`, `teamPushed`, `teamMergedToMain`, `worktrees` - are exactly
 * as measured; the rest are the values that necessarily accompany them (the delete route fetches,
 * so `originReachable` is true, and `cloneMainOnTeamBranch` can only be null when `teamSha` is).
 */
const afterASuccessfulDelete: RepoStatus = {
  defaultBranch: 'main',
  name: 'Harness',
  clonePath: 'C:\\Harness\\teams\\walk\\repos\\Harness\\main',
  mainSha: '9d66c6e',
  mainAhead: 0,
  mainBehind: 0,
  dirty: false,
  headCheckout: 'main',
  teamBranch: 'team/Walk',
  teamSha: null,
  teamPushed: null,
  teamPushedFrom: null,
  teamMergedToMain: null,
  cloneMainOnTeamBranch: null,
  // DERIVED, NOT MEASURED - unlike the rest of this fixture. It is null for the
  // same structural reason its two neighbours are: the server only asks the question inside an arm
  // that has a `teamSha`, and the delete removed the ref. Flagged rather than folded in silently,
  // because the value of this fixture is that the rest of it was read off the wire.
  teamCommitsNotOnMain: null,
  originCheckedAt: '2026-09-15T17:41:09.0000000Z',
  originReachable: true,
  originUnreachableReason: null,
  worktrees: [],
}

describe('the last rung - a deleted team branch must not reawaken Push', () => {
  it('is DONE on the real post-delete status, not back at Pushed', () => {
    const step = nextStep(afterASuccessfulDelete, true)
    expect(step.rung).toBe('done')
    expect(step.action).toBeNull()
    expect(step.reason).toContain('nothing left to do')
  })

  /**
   * The rung NAMES ITS INSTRUMENT rather than claiming the tree is clean. `worktrees` comes
   * from `git worktree list`, and on Windows a failed `git worktree remove` can leave an orphan on
   * disk that the listing does not mention - it deregisters before it fails. The product does not
   * walk the filesystem, so "done" is a statement about what git reports, and saying so is the
   * difference between a limitation a reader can account for and one that misleads them.
   */
  it('says what it checked, because the listing is not the disk', () => {
    expect(nextStep(afterASuccessfulDelete, true).reason).toContain('by everything git reports')
  })

  /** The cycle named precisely, so a broken ladder cannot pass by landing on some other wrong rung. */
  it('does not offer to push work that is already on main', () => {
    const step = nextStep(afterASuccessfulDelete, true)
    expect(step.action).not.toBe('push')
    expect(step.rung).not.toBe('pushed')
  })

  /**
   * THE OTHER HALF, AND IT IS WHAT KEEPS THE FIX HONEST. "No team branch" alone must not mean done:
   * with commits of its own that are not on origin/main there IS work to push, and `push` is what
   * creates the branch from main in the first place.
   */
  it('still offers PUSH when there is no team branch but main has its own commits', () => {
    const step = nextStep({ ...afterASuccessfulDelete, mainAhead: 3 }, true)
    expect(step.rung).toBe('pushed')
    expect(step.action).toBe('push')
  })

  it('offers CLEANUP WORKTREES when the branch is gone but worktrees are not', () => {
    const step = nextStep(
      {
        ...afterASuccessfulDelete,
        worktrees: [{ path: 'C:\\wt', branch: 'team/Walk', sha: 'ccccccc', member: 'dev', aheadMain: 0, behindMain: 0, commitsNotOnMain: 0 }],
      },
      true,
    )
    expect(step.rung).toBe('tidied')
    expect(step.action).toBe('cleanup-worktrees')
  })

  /**
   * A TEAM BRANCH THAT STILL EXISTS IS UNCHANGED BY THE FIX, in both directions - the new condition
   * reads `teamSha`, so it can only ever apply where there is no branch left.
   */
  it('leaves a present-but-unmerged branch on the Merged rung', () => {
    const step = nextStep(base({ teamPushed: true, teamMergedToMain: false }), true)
    expect(step.rung).toBe('merged')
    expect(step.action).toBe('merge-to-main')
  })

  it('leaves a present-and-merged branch offering the delete', () => {
    const step = nextStep(base({ teamPushed: true, teamMergedToMain: true }), true)
    expect(step.rung).toBe('tidied')
    expect(step.action).toBe('delete-remote-branch')
  })
})

/**
 * PUSHED BEFORE IT DIVERGED - the state that refused every single rung.
 *
 * `teamPushed` IS NOT "THIS CLONE'S WORK IS ON ORIGIN". The server sets it true the moment
 * `origin/team/{id}` RESOLVES, so it means only that a branch of that name exists there. A team that
 * pushed, then found origin/main had moved, then rebased, has a branch on origin holding the
 * PRE-REBASE copies: merge answered 400 (the branch is not a fast-forward of origin/main), push
 * answered 409 (the rebase rewrote the shas, so pushing would discard what is on origin), and delete
 * answered 409 (the branch is not on origin/main). Three buttons, three refusals, nothing on the
 * card saying why.
 *
 * `cloneMainOnTeamBranch === false` IS THAT STATE, and the server measures it.
 * These fixtures carry the field combinations git really produces, and the `applyAction` model
 * above matches them - `merge-to-main` there integrates LOCAL MAIN and leaves the stale branch
 * exactly where it is.
 */
describe('a team that pushed before it diverged', () => {
  /** Rebased: local main carries the replayed work; the branch on origin still holds the originals. */
  const rebased = (o: Partial<RepoStatus> = {}) =>
    base({
      mainAhead: 2,
      mainBehind: 0,
      teamPushed: true,
      teamPushedFrom: 'origin/team/alpha',
      teamMergedToMain: false,
      cloneMainOnTeamBranch: false,
      // SET EXPLICITLY, because `base()` casts its object `as RepoStatus` and never supplies this -
      // so a status built from it has `worktrees: undefined`, and the first arm to read `.length`
      // throws rather than answering. Every fixture reaching the tidied tail has to say so.
      worktrees: [],
      ...o,
    })

  it('offers MERGE TO MAIN, which integrates local main rather than the stale branch', () => {
    const step = nextStep(rebased(), true)
    expect(step.rung).toBe('merged')
    expect(step.action).toBe('merge-to-main')
    expect(step.reason).toMatch(/main has work that the branch on origin does not/)
  })

  /**
   * AFTER THAT MERGE this clone's main is level with origin/main and the branch on origin is
   * orphaned. NO BUTTON, because the delete route refuses this exact state - offering one that
   * must fail is worse than none - and the rung SAYS what is left behind rather than going quiet.
   *
   * THE RUNG IS `merged`, NOT `tidied`: `tidied` would draw Merged as a hollow question mark and
   * Tidied as the rung the reader is standing on - "you have finished merging" - directly beneath a header reading
   * "Pushed, but not merged to main yet". The ladder may not step past Merged while the verdict
   * says the work is not merged.
   *
   * AND THE SENTENCE DOES NOT OPEN "the work is on main" on a status whose header says the
   * opposite - the dialog must never leave the reader to choose which of its own two claims to
   * believe.
   */
  it('explains the orphaned branch instead of offering a delete that must fail', () => {
    const step = nextStep(rebased({ mainAhead: 0, teamCommitsNotOnMain: 2 }), true)
    expect(step.rung).toBe('merged')
    expect(step.action).toBeNull()
    expect(step.reason).toMatch(/holds commits that are not on main/)
    expect(step.reason).toContain('team/alpha')
    expect(step.reason).not.toContain('the work is on main')
  })

  /**
   * THE TWO TIDY JOBS ARE ORDERED THE SAME WAY EVERYWHERE, including when
   * `cloneMainOnTeamBranch === false`: its sibling three describes down - work integrated by
   * cherry-pick, the identical verdict - deletes the branch before cleaning worktrees, and a field
   * that has nothing to do with tidying must not give a second answer. `mergeState` sends both to
   * the tail: delete first, then
   * the worktrees, which the never-cycles property already models as a legitimate tidied -> tidied
   * step.
   */
  it('offers the DELETE first, then the worktrees, exactly as the ordinary Tidied tail does', () => {
    const step = nextStep(
      rebased({
        mainAhead: 0,
        teamCommitsNotOnMain: 0,
        worktrees: [{ path: 'C:\\wt', branch: 'team/alpha', sha: 'ccccccc', member: 'dev', aheadMain: 0, behindMain: 0, commitsNotOnMain: 0 }],
      }),
      true,
    )
    expect(step.rung).toBe('tidied')
    expect(step.action).toBe('delete-remote-branch')
    expect(step.reason).toContain('different commits')
  })

  it('refuses tidy when patch-equivalence was not measured, rather than guessing from sha ancestry', () => {
    const step = nextStep(
      rebased({
        mainAhead: 0,
        teamCommitsNotOnMain: null,
        worktrees: [{ path: 'C:\\wt', branch: 'team/alpha', sha: 'ccccccc', member: 'dev', aheadMain: 0, behindMain: 0, commitsNotOnMain: 0 }],
      }),
      true,
    )

    // `merged`, not `tidied`: an unanswered question is not a rung the reader has climbed past.
    expect(step.rung).toBe('merged')
    expect(step.action).toBeNull()
    expect(step.reason).toContain('not been measured')
  })

  /**
   * NULL IS NOT FALSE. `cloneMainOnTeamBranch` is null whenever the server could not measure it, and
   * reading that as "the branch does not hold this clone's work" would send every unmeasured repo
   * down the fallback path. The ordinary answer must be unchanged.
   */
  it('leaves the ordinary path alone when the field is unmeasured', () => {
    // `teamCommitsNotOnMain: 2` is what makes "but not on main" a MEASURED claim rather than one
    // read off sha ancestry alone; without it the honest sentence is that nobody asked, and the
    // arm below pins that separately.
    const step = nextStep(rebased({ cloneMainOnTeamBranch: null, teamCommitsNotOnMain: 2 }), true)
    expect(step.rung).toBe('merged')
    expect(step.action).toBe('merge-to-main')
    expect(step.reason).toContain('the work is on origin but not on main')
  })

  it("leaves the ordinary path alone when the branch does hold this clone's work", () => {
    const step = nextStep(rebased({ cloneMainOnTeamBranch: true, teamCommitsNotOnMain: 2 }), true)
    expect(step.rung).toBe('merged')
    expect(step.action).toBe('merge-to-main')
    expect(step.reason).toContain('the work is on origin but not on main')
  })

  /**
   * THE MERGE IS STILL OFFERED WHEN CONTENT WAS NEVER MEASURED - withholding it would strand every
   * repo the server has not run `git cherry` for - but the CAPTION must not claim the branch is
   * not on main. The header says "has not been measured yet" over this exact status, and the two
   * sit four lines apart.
   */
  it('offers the merge without claiming not-on-main when content was never measured', () => {
    const step = nextStep(rebased({ cloneMainOnTeamBranch: null, teamCommitsNotOnMain: null }), true)

    expect(step.action).toBe('merge-to-main')
    expect(step.reason).toContain('not been measured')
    expect(step.reason).not.toContain('not on main')
  })

  /** Nothing pushed at all still reaches Push first - this arm sits below that return. */
  it('still asks for a push when there is no branch on origin', () => {
    const step = nextStep(rebased({ teamPushed: false }), true)
    expect(step.rung).toBe('pushed')
    expect(step.action).toBe('push')
  })
})

/**
 * PROPERTY TESTS OVER THE WHOLE STATE SPACE, not by example.
 *
 * A table of examples proves the cases it lists; it says nothing about the ones it did not think
 * of. These two generate every combination the type allows and check the invariants the ladder
 * exists to hold: `nextStep` answers every state it can be handed, and following its action can
 * never lead back to a rung the ladder already climbed past.
 */
describe('nextStep — properties over the whole state space', () => {
  const bools = [true, false]
  const trilean = [true, false, null] as const

  function* allStates(): Generator<{ status: RepoStatus; refreshed: boolean }> {
    for (const refreshed of bools) {
      for (const dirty of bools) {
        // 'main', 'detached', and null (unknown — the doc on RepoStatus.headCheckout says so
        // explicitly) are all distinct legal values. Only 'detached' is special-cased in
        // nextStep; null falls through exactly like 'main' does, so it must be enumerated rather
        // than assumed to behave the same by inspection.
        for (const headCheckout of ['main', 'detached', null] as const) {
          for (const mainAheadKnown of bools) {
            for (const mainAhead of mainAheadKnown ? [0, 3] : [null]) {
              for (const mainBehind of mainAheadKnown ? [0, 4] : [null]) {
                for (const teamPushed of trilean) {
                  for (const teamMergedToMain of trilean) {
                    // ENUMERATED, NOT FIXED. This field decides a whole arm of
                    // `nextStep` - whether the branch on origin actually holds this clone's work -
                    // and leaving it fixed at null would hide a rung 3 <-> 5 cycle: a property
                    // test cannot see a field it never varies.
                    for (const cloneMainOnTeamBranch of trilean) {
                    for (const worktreeCount of [0, 1]) {
                      yield {
                        refreshed,
                        status: base({
                          dirty,
                          headCheckout,
                          mainAhead,
                          mainBehind,
                          teamPushed,
                          teamMergedToMain,
                          cloneMainOnTeamBranch,
                          worktrees:
                            worktreeCount > 0
                              ? [{ path: 'C:\\wt', branch: 'team/alpha', sha: 'ccccccc', member: 'dev', aheadMain: 0, behindMain: 0, commitsNotOnMain: 0 }]
                              : [],
                        }),
                      }
                    }
                    }
                  }
                }
              }
            }
          }
        }
      }
    }
  }

  it('answers every reachable state without throwing, with a valid rung and a non-empty reason', () => {
    // `action` is a single nullable field of `LadderStep`, so "at most one action" is enforced
    // by the TYPE and cannot be what this test observes. What it actually checks — and the only
    // thing a fixed set of examples cannot promise — is that `nextStep` returns for every state
    // in the whole enumerated space instead of throwing or falling off the end of an `if` chain
    // with `undefined`, and that whatever it returns is a real rung with a reason a person could
    // read, not just a happy path this file's authors thought to write down.
    for (const { status, refreshed } of allStates()) {
      const step = nextStep(status, refreshed)
      expect(['refreshed', 'current', 'pushed', 'merged', 'tidied', 'done']).toContain(step.rung)
      expect(typeof step.reason).toBe('string')
      expect(step.reason.length).toBeGreaterThan(0)
    }
  })

  it('never cycles: every non-fetch action strictly advances the rung', () => {
    // Rung order the ladder climbs. FETCH IS THE ONE EXCEPTION, deliberately not asserted here:
    // it flips `refreshedThisOpen`, a PARAMETER rather than a `RepoStatus` field, so its real
    // effect (origin's numbers becoming known) cannot be modelled by a status transition at all
    // — a plateau there is a limit of THIS TEST'S model, not a defect in the ladder. Every other
    // action changes RepoStatus directly, and its claimed effect must never leave the reader at
    // the rung they started from or an earlier one — that is the whole point of the property:
    // a `>=` here would not catch a push/tidied cycle reintroduced by a later edit.
    const order: LadderRung[] = ['refreshed', 'current', 'pushed', 'merged', 'tidied', 'done']
    const indexOf = (r: LadderRung) => order.indexOf(r)

    for (const { status, refreshed } of allStates()) {
      const step = nextStep(status, refreshed)
      if (step.action === null || step.action === 'fetch') continue

      const after = applyAction(status, refreshed, step.action)
      const nextStepResult = nextStep(after.status, refreshed)

      // TIDIED HAS TWO JOBS, NOT ONE, and this is the sole legitimate same-rung transition among
      // the non-fetch actions: deleting the finished branch can reveal worktrees the team left
      // behind, and cleaning those up is still 'tidied' — the ladder's six rungs have no slot
      // narrower than that. Measured: `delete-remote-branch` with worktrees present next reports
      // `{ rung: 'tidied', action: 'cleanup-worktrees' }`, index unchanged (4 -> 4). This is
      // ADVANCE, not cycle: the branch that was there is gone, and the only next-step is the
      // other tidy job. The failure this whole property exists to catch is specifically THIS
      // action reawakening Push — deleting the remote branch sets `teamPushed` back to false, and
      // a ladder that re-reads that as "not pushed yet" sends the reader in a circle between
      // rungs 2 and 4 forever.
      if (step.action === 'delete-remote-branch') {
        // THE ONE DOCUMENTED EXCEPTION TO STRICT ADVANCE, and it is a real return to an earlier
        // rung rather than a hole punched to make a test pass. With commits on `main` that are not
        // on `origin/main`, deleting the finished branch REVEALS work that still needs pushing -
        // and `push` is the action that recreates the branch from main. Going back to Push there is
        // the correct answer, so it is asserted EXACTLY rather than waved through: the ladder must
        // offer push, not merely something other than a cycle.
        //
        // IT TERMINATES, which the model cannot show. `merge-to-main` pushes main as well as
        // merging it, so the next lap has `mainAhead === 0` and lands on `done`; `applyAction`
        // models only the team-side fields, so this loop cannot follow it that far. The cycle this
        // property really guards - Push offered when there is NOTHING to push - is the `else`
        // below, and the measured post-delete status pins it by example in `the last rung`.
        if (after.status.mainAhead !== null && after.status.mainAhead > 0) {
          expect(nextStepResult.rung).toBe('pushed')
          expect(nextStepResult.action).toBe('push')
          continue
        }

        expect(nextStepResult.action).not.toBe('push')
        if (nextStepResult.rung === 'tidied') {
          expect(nextStepResult.action).toBe('cleanup-worktrees')
          continue
        }
      }

      // A STEP WITH NO ACTION CANNOT CYCLE, because there is nothing left to click. Such a step is
      // where the ladder STOPS and says why - an orphaned branch on origin that the server will not
      // delete, a dirty clone, a detached HEAD - and it is reached, by construction, exactly once.
      //
      // NARROWED RATHER THAN WAIVED: it must still never go BACKWARDS. Cleaning up the last
      // worktrees of a repo whose branch on origin is stale leaves `tidied -> tidied`, same index,
      // no action - the worktrees that were there are gone and what remains is a sentence. That is
      // the same "advance, not cycle" shape as the delete-remote-branch case above. Landing on an
      // EARLIER rung with no action would be a real defect, and this still catches it.
      if (nextStepResult.action === null) {
        expect(indexOf(nextStepResult.rung)).toBeGreaterThanOrEqual(indexOf(step.rung))
        continue
      }

      expect(indexOf(nextStepResult.rung)).toBeGreaterThan(indexOf(step.rung))
    }
  })
})

/**
 * Models what each action does to the status, for the never-cycles property above. This is not the
 * real endpoint — it is the minimal state transition the ladder itself claims each action performs,
 * so the property can check the ladder is honest about its own effects.
 */
function applyAction(
  status: RepoStatus,
  refreshed: boolean,
  action: LadderAction,
): { status: RepoStatus; refreshed: boolean } {
  switch (action) {
    case 'fetch':
      return { status, refreshed: true }
    case 'bring-current':
      return { status: { ...status, mainBehind: 0 }, refreshed }
    case 'rebase':
      return { status: { ...status, mainAhead: 0, mainBehind: 0 }, refreshed }
    case 'push':
      return { status: { ...status, teamPushed: true, teamPushedFrom: 'origin/team/alpha' }, refreshed }
    /**
     * TWO DIFFERENT ACTS BEHIND ONE ACTION NAME, and modelling only the first is what a corrected
     * model has to avoid. When the branch on origin does NOT hold this clone's work
     * (`cloneMainOnTeamBranch === false`) the server integrates LOCAL MAIN, so main lands on
     * origin/main - `mainAhead` goes to 0 - and the stale branch is untouched: still pushed, still
     * not merged. Writing `teamMergedToMain: true` there would model an integration that did not
     * happen and hide the very state the Tidied rung has to explain.
     */
    case 'merge-to-main':
      return status.cloneMainOnTeamBranch === false
        ? { status: { ...status, mainAhead: 0 }, refreshed }
        : { status: { ...status, teamMergedToMain: true }, refreshed }
    /**
     * WHAT THE SERVER REALLY DOES, measured in a browser after a real delete: `teamSha`,
     * `teamPushed` AND `teamMergedToMain` come back NULL together, because merged-ness is derived
     * from the team ref and deleting the ref unanswers it.
     *
     * MODEL IT AS THE SERVER DOES, NOT AS `{ teamPushed: false, teamPushedFrom: null }`. A property
     * test checks the ladder's model against the ladder's own model; the moment one action is
     * mis-modelled here, the whole enumeration stops being able to see that action's defect, and
     * the closed loop reads as thorough coverage. Modelling it correctly costs the strict-advance
     * exception below - which is the honest price, not a reason to leave the model wrong.
     */
    case 'delete-remote-branch':
      return {
        status: { ...status, teamSha: null, teamPushed: null, teamPushedFrom: null, teamMergedToMain: null },
        refreshed,
      }
    // The team branch takes origin/main by a merge and is pushed, then merged to main.
    case 'bring-current-and-merge':
      return {
        status: {
          ...status, teamBranchBehindDefault: 0, filesChangedOnBothSides: [], teamMergedToMain: true,
          cloneMainOnTeamBranch: true, mainAhead: 0,
        },
        refreshed,
      }
    case 'cleanup-worktrees':
      return { status: { ...status, worktrees: [] }, refreshed }
    // Opening one records it, open: in review until somebody upstream decides.
    case 'open-pull-request':
      return {
        status: {
          ...status,
          pullRequest: {
            url: 'https://github.com/project/Widget/pull/1', number: 1, state: 'open',
            readAt: '2026-09-26T00:00:00Z', landing: 'in-review', unknownReason: null,
          },
        },
        refreshed,
      }
  }
}

/**
 * WHAT THE CARD PAINTS. The five rungs and their states are derived HERE rather than in the
 * component, for the reason `repoPanel` is: a `.vue` file can be mounted in this suite, and a
 * ladder that ticked the wrong rungs would still be clearest here, not in a test inferring them
 * back out of the template.
 */
describe('the rungs the card paints', () => {
  it('shows five rungs, in the order they are climbed, and no "done" rung', () => {
    expect([...LADDER_RUNGS]).toEqual(['refreshed', 'current', 'pushed', 'merged', 'tidied'])
    expect(LADDER_RUNGS).not.toContain('done')
  })

  it('marks the rungs below the step satisfied, the step itself current, and the rest pending', () => {
    const status = base({ teamPushed: false, teamMergedToMain: false })
    const step = nextStep(status, true)
    expect(step.rung).toBe('pushed')

    expect(rungState('refreshed', step, status)).toBe('satisfied')
    expect(rungState('current', step, status)).toBe('satisfied')
    expect(rungState('pushed', step, status)).toBe('current')
    expect(rungState('merged', step, status)).toBe('pending')
    expect(rungState('tidied', step, status)).toBe('pending')
  })

  /**
   * A BLOCKED RUNG IS `current`, NOT `satisfied`. A dirty clone stops the ladder AT Current; ticking
   * it off would tell the reader the one thing standing in their way was already behind them.
   */
  it('reports a blocked rung as current rather than satisfied', () => {
    const status = base({ dirty: true })
    const step = nextStep(status, true)
    expect(step.action).toBeNull()
    expect(rungState('current', step, status)).toBe('current')
  })

  /**
   * `done` IS NOT IN `LADDER_RUNGS`, so an implementation reaching for `indexOf` without this arm
   * answers -1 and paints the FINISHED repository as the untouched one.
   */
  it('satisfies every rung when the ladder is done', () => {
    const status = base({ teamMergedToMain: true, teamPushed: false, worktrees: [] })
    const step = nextStep(status, true)
    expect(step.rung).toBe('done')
    for (const rung of LADDER_RUNGS) {
      expect(rungState(rung, step, status)).toBe('satisfied')
    }
  })

  it('answers a state for every rung of every reachable step, and labels every rung', () => {
    // The unrefreshed open is the state the card is in the instant it mounts, so it must paint.
    for (const refreshed of [false, true]) {
      const status = base()
      const step = nextStep(status, refreshed)
      for (const rung of LADDER_RUNGS) {
        expect(['satisfied', 'current', 'pending', 'unknown']).toContain(rungState(rung, step, status))
        expect(RUNG_LABELS[rung].length).toBeGreaterThan(0)
      }
    }
  })

  /**
   * EVERY ACTION THE LADDER CAN RETURN MUST HAVE A LABEL. The `Record` makes a missing one a compile
   * error; this checks the compiler was not satisfied by an empty string.
   */
  it('labels all nine actions', () => {
    const actions: LadderAction[] = [
      'fetch', 'bring-current', 'rebase', 'push',
      'merge-to-main', 'delete-remote-branch', 'cleanup-worktrees', 'open-pull-request',
      'bring-current-and-merge',
    ]
    for (const action of actions) {
      expect(ACTION_LABELS[action].length).toBeGreaterThan(0)
    }
    expect(new Set(Object.values(ACTION_LABELS)).size).toBe(actions.length)
  })
})

/**
 * THE HINGE, TESTED FOR REAL.
 *
 * `refreshedThisOpen` is set from `didRefreshOrigin` and from nothing else. Put it in the wrong
 * branch and the ladder ticks Refreshed off on a fetch that read nothing — the exact stale-origin
 * state this feature exists to delete, wearing a green tick. No source-reading spec over the card
 * can see that, which is why the decision is here.
 */
describe('didRefreshOrigin', () => {
  const result = (o: Partial<RepoActionResult> = {}): RepoActionResult => ({
    repo: 'Harness',
    status: base(),
    message: 'origin fetched',
    success: true,
    ...o,
  })

  it('is true when the fetch reached origin', () => {
    expect(didRefreshOrigin(result())).toBe(true)
  })

  /**
   * THE 200-WITH-`success:false` ARM. An unreachable origin is REPORTED rather than refused, so this
   * promise RESOLVES — an implementation that treated "did not throw" as "refreshed" would pass
   * every other test in this file.
   */
  it('is false on the reported-unreachable arm, which does not throw', () => {
    expect(
      didRefreshOrigin(
        result({
          success: false,
          message: 'Origin was not reachable. Existing status is unchanged.',
          status: base({ originReachable: false, originUnreachableReason: 'fatal: could not read Username' }),
        }),
      ),
    ).toBe(false)
  })
})

describe('refreshFailureReason', () => {
  const failed = (status: Partial<RepoStatus>, message = 'Origin was not reachable.'): RepoActionResult => ({
    repo: 'Harness',
    status: base(status),
    message,
    success: false,
  })

  it("prefers git's own stderr, which names the actual cause", () => {
    expect(refreshFailureReason(failed({ originUnreachableReason: 'fatal: could not read Username' })))
      .toBe('fatal: could not read Username')
  })

  it("falls back to the server's message when git said nothing", () => {
    expect(refreshFailureReason(failed({ originUnreachableReason: null })))
      .toBe('Origin was not reachable.')
  })

  /** NEVER EMPTY: a failure notice with no text reads as a rendering bug, not as an unreachable origin. */
  it('is never empty', () => {
    expect(refreshFailureReason(failed({ originUnreachableReason: null }, '')).length).toBeGreaterThan(0)
  })
})

/**
 * Every question on the Merged rung is about SHA ANCESTRY, and a cherry-pick makes a different
 * sha for the same change - so a team integrated that way reads as unmerged forever while all of
 * its work is already upstream, and the server correctly refuses the merge it is then offered.
 *
 * THESE NUMBERS WERE MEASURED, on a repository built from scratch for the purpose: a branch of two
 * commits, both cherry-picked onto main, then
 *
 *     git merge-base --is-ancestor team/T main   -> exit 1   (ancestry: UNMERGED)
 *     git cherry main team/T                     -> "- ...", "- ..."   (content: 0 outstanding)
 *
 * which is the divergence in two lines. A fixture written by someone sharing the code's model
 * passes whatever the code gets wrong; this one was read off git.
 */
describe('work integrated by cherry-pick is on main, and the ladder says so', () => {
  const cherryPicked = (o: Partial<RepoStatus> = {}): RepoStatus => base({
    teamPushed: true,
    teamMergedToMain: false,   // ancestry, and it is right - the shas differ
    teamCommitsNotOnMain: 0,   // content, by patch-id - nothing outstanding
    cloneMainOnTeamBranch: null,
    mainAhead: 0,
    mainBehind: 0,
    worktrees: [],
    ...o,
  })

  it('does not offer a merge, because there is nothing left to merge', () => {
    expect(nextStep(cherryPicked(), true).action).not.toBe('merge-to-main')
  })

  it('names the mechanism, because "merged" and "every change is upstream" are different facts', () => {
    const step = nextStep(cherryPicked(), true)
    expect(step.reason).toContain('already on main')
    expect(step.reason).toContain('different commits')
  })

  it('reaches the Tidied rung rather than parking on Merged', () => {
    expect(nextStep(cherryPicked(), true).rung).toBe('tidied')
  })

  /**
   * OFFERS THE DELETE, and this is only legal because the SERVER matches. The
   * precondition on `delete-remote-branch` accepts patch-equivalence as well as ancestry, so this
   * is an action the server will take rather than refuse - which is the property the whole ladder
   * rests on. If the server ever accepts ancestry alone, this test is the one that must fail.
   */
  it('offers the DELETE, so a cherry-picked team can actually be closed out', () => {
    expect(nextStep(cherryPicked(), true).action).toBe('delete-remote-branch')
  })

  it('deletes the branch before cleaning worktrees, as the ordinary Tidied tail does', () => {
    const step = nextStep(cherryPicked({
      worktrees: [{ path: 'C:/x', branch: 'feature/a', sha: 'abc1234', member: null, aheadMain: 0, behindMain: 0, commitsNotOnMain: 0 }],
    }), true)
    expect(step.action).toBe('delete-remote-branch')
  })

  it('NULL is not measured and must never be read as merged', () => {
    // The Tidied cycle is exactly this mistake on a different field: `null !== true` walking into
    // the arm meant for work that was never integrated.
    const step = nextStep(cherryPicked({ teamCommitsNotOnMain: null }), true)
    expect(step.reason).not.toContain('different commits')
  })

  it('a genuinely unmerged branch is untouched - the positive control', () => {
    // Without this, "reports equivalence" and "reports equivalence always" are the same test.
    const step = nextStep(cherryPicked({ teamCommitsNotOnMain: 2 }), true)
    expect(step.rung).toBe('merged')
    expect(step.reason).not.toContain('different commits')
  })

  it('a PARTIALLY equivalent branch stays unmerged, which is the conservative direction', () => {
    // A real shape: one commit whose patch was modified while being integrated, so
    // `git cherry` reports it. Erring toward "not equivalent" errs toward refusing, and that is
    // the direction this design tolerates on purpose.
    expect(nextStep(cherryPicked({ teamCommitsNotOnMain: 1 }), true).rung).toBe('merged')
  })
})

/**
 * `teamMergedToMainBy` - THE FIELD THAT TELLS THE TWO MECHANISMS APART.
 *
 * The server answers `teamMergedToMain` TRUE for both, so it cannot say HOW. 'content' is the case
 * every voice on the card must get right at once: rewritten shas whose changes are all on main
 * are MERGED, and the ladder's job is to light the rung, allow the tidy, and say
 * plainly that the work arrived under different commits.
 *
 * TIDIED STAYS A PERSON'S CLICK. "Allowed" here means the action is OFFERED; nothing in this file
 * or in `RepoCard.vue` runs it, and the one destructive action still confirms separately.
 */
describe("teamMergedToMainBy: 'content' is merged, and every rung says so", () => {
  const contentMerged = (o: Partial<RepoStatus> = {}): RepoStatus => base({
    teamPushed: true,
    // EXACTLY THE SHAPE THE CONTRACT DESCRIBES: the boolean is true for a content merge too, and
    // only the `By` field distinguishes it. A ladder reading the boolean alone cannot.
    teamMergedToMain: true,
    teamMergedToMainBy: 'content',
    // DELIBERATELY NULL. The 'content' answer must stand on its own - if this test only passed
    // because `teamCommitsNotOnMain === 0` was also set, it would be testing the other field.
    teamCommitsNotOnMain: null,
    cloneMainOnTeamBranch: false,
    mainAhead: 0,
    mainBehind: 0,
    worktrees: [],
    ...o,
  })

  it('LIGHTS the Merged rung rather than leaving a question mark on it', () => {
    const status = contentMerged()
    const step = nextStep(status, true)

    expect(rungState('merged', step, status)).toBe('satisfied')
    expect(rungState('pushed', step, status)).toBe('satisfied')
  })

  it('ALLOWS the tidy, because deleting that branch loses nothing', () => {
    const status = contentMerged()
    const step = nextStep(status, true)

    expect(step.rung).toBe('tidied')
    expect(step.action).toBe('delete-remote-branch')
    expect(rungState('tidied', step, status)).toBe('current')
  })

  /** Plainly that the work is on main, never a sentence shaped like a failure. */
  it('says the work is on main under different commits, not that something went wrong', () => {
    const reason = nextStep(contentMerged(), true).reason

    expect(reason).toContain('already on main')
    expect(reason).toContain('different commits')
    expect(reason).not.toContain('not merged')
    expect(reason).not.toContain('is not safe')
    expect(reason).not.toContain('stale')
  })

  /** The tidy is offered; NOTHING here performs it. Tidied is never automatic. */
  it('offers the tidy without taking it', () => {
    const step = nextStep(contentMerged(), true)

    // `LadderStep` carries an action NAME and no callable - the card wires the click. This is the
    // whole of what the ladder may do about a destructive step.
    expect(Object.keys(step).sort()).toEqual(['action', 'reason', 'rung'])
    expect(typeof step.action).toBe('string')
  })

  it("'ancestry' is merged too, and keeps the plainest wording of the two", () => {
    const status = contentMerged({ teamMergedToMainBy: 'ancestry' })
    const step = nextStep(status, true)

    expect(rungState('merged', step, status)).toBe('satisfied')
    expect(step.action).toBe('delete-remote-branch')
    expect(step.reason).toContain('the work is on main')
  })

  /**
   * NULL IS NOT MEASURED. The contract is explicit - "Never read null as definitely not merged" -
   * so a null `By` must fall through to the fields that did measure something rather than convict
   * on its own. With the boolean false and content unasked, the answer is UNKNOWN.
   */
  it('does not read a null mechanism as "not merged"', () => {
    const unknown = contentMerged({ teamMergedToMainBy: null, teamMergedToMain: false })
    const step = nextStep(unknown, true)

    expect(step.action).not.toBe('delete-remote-branch')
    expect(step.reason).toContain('not been measured')
    expect(rungState('merged', step, unknown)).toBe('current')
  })
})

/**
 * THE CONTRADICTION, AS A PROPERTY RATHER THAN AS A LIST OF SCREENSHOTS.
 *
 * The shape this rules out, from ONE `RepoStatus` whose work IS on main:
 *
 *   header      "Pushed, but not merged to main yet."
 *   step line   "the work is on main, but team/b24-… on origin holds commits that are not"
 *   rungs        Merged greyed, Tidied lit
 *
 * Fixing individual sentences closes INSTANCES; what closes the CLASS is that all three read one
 * `mergeState`, and these are the invariants that holds. An edit that introduces a hand-written
 * integration sentence fails here rather than in a screenshot.
 */
describe('the ladder and the verdict cannot disagree', () => {
  const order: LadderRung[] = ['refreshed', 'current', 'pushed', 'merged', 'tidied', 'done']

  /**
   * DENSE OVER THE MERGE FIELDS, SPARSE ELSEWHERE. The neighbouring property tests already sweep
   * the clone-side space; what this one has to cover exhaustively is every combination of the
   * three fields merged-ness is derived from, including the states the server should never
   * send (`teamMergedToMain: false` beside `teamMergedToMainBy: 'content'`) - a client that only
   * holds when the server is self-consistent is not holding anything.
   */
  function* mergeStates(): Generator<RepoStatus> {
    for (const teamMergedToMain of [true, false, null] as const) {
      for (const teamMergedToMainBy of ['ancestry', 'content', null] as const) {
        for (const teamCommitsNotOnMain of [0, 2, null]) {
          for (const teamPushed of [true, false, null] as const) {
            for (const cloneMainOnTeamBranch of [true, false, null] as const) {
              for (const teamSha of ['bbbbbbb', null]) {
                for (const mainAhead of [0, 3]) {
                  for (const worktreeCount of [0, 1]) {
                    yield base({
                      teamMergedToMain,
                      teamMergedToMainBy,
                      teamCommitsNotOnMain,
                      teamPushed,
                      cloneMainOnTeamBranch,
                      teamSha,
                      mainAhead,
                      mainBehind: 0,
                      worktrees: worktreeCount > 0
                        ? [{ path: 'C:\\wt', branch: 'team/alpha', sha: 'ccccccc', member: 'dev', aheadMain: 0, behindMain: 0, commitsNotOnMain: 0 }]
                        : [],
                    })
                  }
                }
              }
            }
          }
        }
      }
    }
  }

  /**
   * THE HEADER AND THE STEP LINE, WORD FOR WORD. `repoHeadline` and `nextStep` are the two
   * functions the screenshot caught disagreeing, and neither may assert integration the other
   * denies. Asserted on the PHRASES a reader actually sees rather than on the verdict, because
   * the verdict agreeing with itself is not the property that failed.
   */
  it('never says the work is on main in one voice and not merged in the other', () => {
    const saysMerged = /the work is on main|already on main|Merged to main|safe to delete/
    const saysNotMerged = /not merged to main yet|holds commits that are not on main|but not on main/

    for (const status of mergeStates()) {
      const said = `${repoHeadline(status).text} ${nextStep(status, true).reason} ${formatMergedState(status)}`

      expect(
        saysMerged.test(said) && saysNotMerged.test(said),
        `contradictory pair rendered together: ${said}`,
      ).toBe(false)
    }
  })

  /**
   * THE RUNG IS THE THIRD VOICE, and it speaks independently of the other two: `Merged` drawn
   * hollow while `Tidied` is lit is the ladder telling a reader they have finished merging,
   * under a header saying they have not started.
   */
  it('never steps past Merged while a branch on origin is not merged', () => {
    for (const status of mergeStates()) {
      if (mergeState(status).verdict === 'merged') continue
      if (status.teamPushed !== true) continue

      const step = nextStep(status, true)

      expect(
        order.indexOf(step.rung),
        `stepped to ${step.rung} on an unmerged branch: ${step.reason}`,
      ).toBeLessThanOrEqual(order.indexOf('merged'))
    }
  })

  it('ticks the Merged rung for exactly the statuses the verdict calls merged', () => {
    for (const status of mergeStates()) {
      const merged = mergeState(status).verdict === 'merged'
      const painted = rungState('merged', nextStep(status, true), status)

      if (merged) {
        expect(painted, `merged verdict painted ${painted}`).toBe('satisfied')
      } else {
        expect(painted, 'a rung ticked over work that is not on main').not.toBe('satisfied')
      }
    }
  })
})

/**
 * THE PUSHED-NESS SIBLING OF THE SWEEP ABOVE, which is scoped to MERGED-NESS PHRASES ONLY - so it
 * would stay green through a header and a step line flatly contradicting each other about whether
 * anything had reached origin.
 *
 * WHAT THAT SWEEP CANNOT SEE, from one `RepoStatus`:
 *
 *   header   "Whether this team's branch reached origin has not been measured yet."
 *   row      "pushed unknown"
 *   step     Push - "the work is not on origin yet"
 *
 * and, sharper, with no local team ref at all and a clone three ahead of origin/main:
 *
 *   header   "There is no team/alpha to push, merge or delete."
 *   step     Push
 *
 * a header saying there is nothing to push above the one live button, which is Push. Both are the
 * same mistake as the merged-ness one: `null` is NOT MEASURED, and a confident "not on origin yet"
 * must not be written from it.
 *
 * PUSH IS STILL THE RIGHT OFFER with the question open. It is non-destructive and it is what
 * resolves the question; only the CAPTION is at stake, so these properties constrain the words
 * and not the action.
 */
describe('the ladder and the header cannot disagree about pushed-ness', () => {
  /**
   * DENSE OVER THE PUSHED-NESS FIELDS - `teamPushed`, `teamSha`, `teamPushedFrom` and the
   * `mainAhead` that decides whether this clone has anything to publish at all - and sparse but
   * present over the merge fields, because the push rung is only reached through the merge gate.
   */
  function* pushStates(): Generator<RepoStatus> {
    for (const teamPushed of [true, false, null] as const) {
      for (const teamSha of ['bbbbbbb', null]) {
        for (const teamPushedFrom of ['origin/team/alpha', null]) {
          for (const mainAhead of [0, 3]) {
            for (const teamMergedToMain of [true, false, null] as const) {
              for (const teamMergedToMainBy of ['content', null] as const) {
                for (const teamCommitsNotOnMain of [0, 2, null]) {
                  for (const cloneMainOnTeamBranch of [true, false, null] as const) {
                    yield base({
                      teamPushed,
                      teamSha,
                      teamPushedFrom,
                      mainAhead,
                      mainBehind: 0,
                      teamMergedToMain,
                      teamMergedToMainBy,
                      teamCommitsNotOnMain,
                      cloneMainOnTeamBranch,
                      worktrees: [],
                    })
                  }
                }
              }
            }
          }
        }
      }
    }
  }

  /** Everything about pushed-ness a reader sees in the one panel, in one string. */
  const rendered = (status: RepoStatus): string =>
    `${repoHeadline(status).text} | ${nextStep(status, true).reason} | ${formatPushedState(status)}`

  /**
   * THE PHRASES, SCOPED TO PUSHED-NESS ON PURPOSE. "has not been measured yet" alone also belongs
   * to the merged-ness clause, and matching it here would redden honest cards instead of dishonest
   * ones - which is the failure mode of a sweep written too loosely, and the reason the merged-ness
   * one is worded as tightly as it is.
   */
  const saysNotPushed = /not on origin yet|nothing is pushed/
  const saysPushedUnmeasured = /pushed unknown|reached origin has not been measured/

  it('never claims the work is not on origin while pushed-ness is unmeasured', () => {
    for (const status of pushStates()) {
      const said = rendered(status)

      expect(
        saysNotPushed.test(said) && saysPushedUnmeasured.test(said),
        `contradictory pushed-ness rendered together: ${said}`,
      ).toBe(false)
    }
  })

  /**
   * THE SHARP ONE, AND IT IS A PHRASE AGAINST AN ACTION rather than a phrase against a phrase -
   * which is exactly why the phrase-only sweep above cannot catch it. A header that says
   * there is nothing to push, over a live Push button, is the same contradiction in a different
   * pair of voices.
   */
  it('never offers Push beneath a header saying there is nothing to push', () => {
    for (const status of pushStates()) {
      const step = nextStep(status, true)
      if (step.action !== 'push') continue

      expect(
        /There is no .* to push/.test(repoHeadline(status).text),
        `Push offered under a header denying there is anything to push: ${rendered(status)}`,
      ).toBe(false)
    }
  })

  /** THE POSITIVE CONTROL: a measured `false` still gets the plain sentence, unchanged. */
  it('still says the work is not on origin when the server actually answered false', () => {
    const step = nextStep(
      base({ teamPushed: false, teamMergedToMain: false, teamCommitsNotOnMain: 2 }), true)

    expect(step.action).toBe('push')
    expect(step.reason).toContain('the work is not on origin yet')
  })

  /** And an unmeasured one still OFFERS the push - the caption changed, the offer did not. */
  it('still offers Push when pushed-ness is unmeasured', () => {
    const step = nextStep(
      base({ teamPushed: null, teamMergedToMain: false, teamCommitsNotOnMain: 2 }), true)

    expect(step.rung).toBe('pushed')
    expect(step.action).toBe('push')
    expect(step.reason).not.toContain('not on origin yet')
  })
})

/**
 * THE MIRROR OF THE TIDIED CYCLE, IN THE OTHER ARM, and one more instance of a class of mistake
 * this ladder guards against: a FLAG that answers one question while the ACTION does another.
 *
 * Where a LOCAL `team/{id}` exists, the server answers `teamPushed` by asking whether that local
 * branch is on `origin/team/{id}`, while `push` publishes **main** (`main:team/{id}`). A clone
 * holding a local team branch with a commit main does not have would sit on this rung forever -
 * every click answering 200 and "Pushed main to team/X." while the card still said the work was
 * not on origin, and the second push printing "Everything up-to-date". Measured against real git.
 *
 * THE DESIGN: push keeps publishing what the ladder is talking about. Making it publish some other
 * ref would let the flag and the action drift apart, which IS the defect.
 */
/**
 * PUSH PUBLISHES A LOCAL TEAM BRANCH THAT CARRIES MAIN, so a team ref strictly ahead of main is no
 * longer a dead end: it is "team/X is not pushed", and Push is offered first.
 */
describe('a local team branch ahead of main is not pushed, and Push is offered first', () => {
  const localTeamAhead = (o: Partial<RepoStatus> = {}): RepoStatus => base({
    mainSha: 'aaaaaaa',
    teamSha: 'bbbbbbb',          // a different commit from main
    cloneMainOnTeamBranch: true, // main IS on the team ref, so the team ref is strictly ahead
    teamPushed: false,
    teamMergedToMain: false,
    mainAhead: 1,
    mainBehind: 0,
    ...o,
  })

  it('offers Push, saying the team branch is not pushed', () => {
    const step = nextStep(localTeamAhead(), true)
    expect(step.action).toBe('push')
    expect(step.rung).toBe('pushed')
    expect(step.reason).toMatch(/^team\/alpha is not pushed/)
  })

  it('follows the server when it says so, whatever the shas', () => {
    const said = nextStep(localTeamAhead({ teamSha: 'aaaaaaa', teamBranchUnpushed: true }), true)
    expect(said.reason).toMatch(/^team\/alpha is not pushed/)
    expect(nextStep(localTeamAhead({ teamBranchUnpushed: false }), true).reason).not.toMatch(/is not pushed/)
  })

  /**
   * THE POSITIVE CONTROL, and it is the common case: the seeded pattern pushes `main` to
   * `team/{id}` and creates no local branch, so where a local ref does exist it usually EQUALS
   * main. Push must still be offered there, or this fix would strand every ordinary team.
   */
  it('still offers PUSH when the team ref is the same commit as main', () => {
    const step = nextStep(localTeamAhead({ teamSha: 'aaaaaaa' }), true)
    expect(step.action).toBe('push')
  })

  it('still offers PUSH when main is NOT on the team ref, from a Host that does not say', () => {
    // cloneMainOnTeamBranch false means main carries something the team ref lacks. An older Host
    // sends no teamBranchUnpushed and its Push published main there, so the caption says main.
    const step = nextStep(localTeamAhead({ cloneMainOnTeamBranch: false }), true)
    expect(step.action).toBe('push')
    expect(step.reason).toContain("push publishes this clone's main to team/alpha")
  })

  it('says Push publishes the LOCAL team branch when main has moved and the server reports it unpushed', () => {
    // Push always publishes a local team/{id} when one exists, whether or not it carries main, so
    // a moved main must not turn the caption into "publishes this clone's main".
    const step = nextStep(localTeamAhead({ cloneMainOnTeamBranch: false, teamBranchUnpushed: true }), true)
    expect(step.action).toBe('push')
    expect(step.reason).toMatch(/^team\/alpha is not pushed/)
    expect(step.reason).toContain("push publishes this clone's local team/alpha")
    expect(step.reason).not.toContain("publishes this clone's main")
  })

  it('NULL is not measured and must not trigger the refusal', () => {
    expect(nextStep(localTeamAhead({ cloneMainOnTeamBranch: null }), true).action).toBe('push')
  })
})

describe('a rung the ladder cannot evaluate, and work it cannot see', () => {
  /** A worktree row as `git worktree list` plus the server's divergence counts produce one. */
  const wt = (o: Partial<WorktreeStatus> = {}): WorktreeStatus => ({
    path: 'Z:/Harness/teams/alpha/repos/Harness/wt_DeveloperMira',
    branch: 'member-wire-parity', sha: '8337b4e', member: 'DeveloperMira',
    aheadMain: 0, behindMain: 0, commitsNotOnMain: null,
    ...o,
  } as WorktreeStatus)

  /**
   * A REAL STATE. A team finishes exactly as its spec asked - two commits on a member branch,
   * deliberately unpushed, no team branch created. Offering `Clean up worktrees` as the only
   * remaining step would take away the one working tree for work that is nowhere else.
   *
   * `teamSha: null` with `mainAhead: 0` is the `workIsOnMain` shortcut, which walks straight past
   * the push and merge rungs to the tidied tail. It is RIGHT that there is no team branch to push;
   * it is wrong to call that finished while a member is holding commits.
   */
  const memberHoldingWork = base({
    teamSha: null, teamPushed: null, teamMergedToMain: null, teamCommitsNotOnMain: null,
    mainAhead: 0, mainBehind: 0,
    worktrees: [wt({ aheadMain: 2, behindMain: 3 })],
  })

  it('does not offer to tidy away a worktree that is holding unintegrated commits', () => {
    const step = nextStep(memberHoldingWork, true)

    expect(step.action).not.toBe('cleanup-worktrees')
    expect(step.reason).toMatch(/DeveloperMira/)
  })

  it('names the branch the work is on, because that is the only place it exists', () => {
    expect(nextStep(memberHoldingWork, true).reason).toMatch(/member-wire-parity/)
  })

  /**
   * AHEAD IS THE NUMBER THAT MATTERS AND BEHIND IS NOISE. A worktree reading `ahead 0, behind 1`
   * is not a concern: its commit is an ancestor of main. A worktree at `ahead 0` is fully integrated whatever its behind count.
   */
  it('still tidies a worktree that is merely behind', () => {
    const step = nextStep(base({
      teamSha: null, teamPushed: null, teamMergedToMain: null,
      mainAhead: 0, worktrees: [wt({ aheadMain: 0, behindMain: 9 })],
    }), true)

    expect(step.action).toBe('cleanup-worktrees')
  })

  /**
   * NULL IS NOT MEASURED AND MUST NOT CONVICT - the rule this codebase applies to `teamPushed`,
   * `cloneMainOnTeamBranch` and usage alike. It is also the RECOVERABLE direction here: removing a
   * worktree runs `git worktree remove` and never deletes a branch, so a tidy that should not have
   * happened loses a checkout and no commits.
   */
  it('does not block tidy on a worktree whose divergence was never measured', () => {
    const step = nextStep(base({
      teamSha: null, teamPushed: null, teamMergedToMain: null,
      mainAhead: 0, worktrees: [wt({ aheadMain: null, behindMain: null })],
    }), true)

    expect(step.action).toBe('cleanup-worktrees')
  })

  /**
   * WITH NO TEAM BRANCH THERE IS NO EVIDENCE EITHER WAY, so neither rung may be ticked.
   * `Pushed ✓ Merged ✓` over a team that has pushed nothing and merged nothing is not a small
   * cosmetic wrong: it is the reader's only summary of whether the work is safe.
   *
   * `RepoStatus` already models this correctly - `teamMergedToMain` is documented "Null if origin
   * unknown" and `cloneMainOnTeamBranch` "NOT false, which would accuse a team that never pushed".
   * Three states exist on the wire and all three must reach the screen.
   */
  it('reports pushed and merged as UNKNOWN rather than satisfied when no team branch exists', () => {
    const noTeamBranch = base({
      teamSha: null, teamPushed: null, teamMergedToMain: null, teamCommitsNotOnMain: null,
      mainAhead: 0, worktrees: [],
    })
    const step = nextStep(noTeamBranch, true)

    expect(rungState('pushed', step, noTeamBranch)).toBe('unknown')
    expect(rungState('merged', step, noTeamBranch)).toBe('unknown')
  })

  it('still reports pushed and merged as satisfied when the evidence is there', () => {
    const merged = base({ teamPushed: true, teamMergedToMain: true, worktrees: [] })
    const step = nextStep(merged, true)

    expect(rungState('pushed', step, merged)).toBe('satisfied')
    expect(rungState('merged', step, merged)).toBe('satisfied')
  })

  /**
   * A rung the reader has not reached yet is pending, not unknown - absence of evidence is
   * ordinary, and marking it would put a question mark on every fresh repo.
   *
   * THE FIXTURE OVERRIDES `base`'s `teamMergedToMain: true`, because an above-step rung whose
   * evidence holds reports `satisfied` - a card that said "Merged to main" beside a hollow Merged
   * rung would be giving two answers to one question. Absence is what keeps it pending, and this
   * fixture is absent.
   */
  it('leaves rungs above the current step pending when nothing supports them', () => {
    const behind = base({
      mainAhead: 0, mainBehind: 4, worktrees: [],
      teamPushed: false, teamMergedToMain: false, teamCommitsNotOnMain: null,
    })
    const step = nextStep(behind, true)

    expect(rungState('merged', step, behind)).toBe('pending')
  })
})

/**
 * A RUNG ABOVE THE STEP THAT IS ALREADY TRUE SAYS SO.
 *
 * A headline reading "Merged to main - this team is safe to delete" must not sit four lines above
 * a Merged rung rendered as NOT REACHED, from the same `RepoStatus`. A `rungState` that returned
 * `pending` for every rung above the current step WITHOUT asking whether its evidence held would
 * draw Pushed and Merged hollow on a clone that is behind origin - putting the live step at
 * `current` - however plainly both were true.
 *
 * THE LADDER IS A SEQUENCE AND THE FACTS ARE NOT. Which rung is LIVE is still positional: exactly
 * one action, in order, and that is what the button follows. Whether a rung's evidence HOLDS is a
 * separate question with its own answer.
 *
 * PENDING STAYS THE ANSWER WHEN THERE IS NO EVIDENCE, which is what keeps a fresh repository
 * looking untouched rather than sprouting ticks it has not earned.
 */
describe('rungState above the current step', () => {
  const behindStatus = base({
    mainAhead: 0,
    mainBehind: 6,
    teamPushed: true,
    teamMergedToMain: true,
  })

  const atCurrent = { rung: 'current' as LadderRung, action: 'bring-current' as LadderAction, reason: 'behind' }

  it('marks a rung satisfied when its evidence holds, even above the live step', () => {
    expect(rungState('pushed', atCurrent, behindStatus)).toBe('satisfied')
    expect(rungState('merged', atCurrent, behindStatus)).toBe('satisfied')
  })

  it('leaves a rung pending when nothing supports it', () => {
    const unpushed = base({ mainAhead: 0, mainBehind: 6, teamPushed: false, teamMergedToMain: false })

    expect(rungState('pushed', atCurrent, unpushed)).toBe('pending')
    expect(rungState('merged', atCurrent, unpushed)).toBe('pending')
  })

  it('still marks the live step as the one being stood on', () => {
    expect(rungState('current', atCurrent, behindStatus)).toBe('current')
  })

  /** Tidied has no evidence of its own - it is answered from the clone - so it must not tick early. */
  it('does not tick a rung that is merely reachable', () => {
    expect(rungState('tidied', atCurrent, behindStatus)).toBe('pending')
  })
})

/**
 * AFTER A REBASE, A TIDY THAT WOULD LOSE NOTHING IS NOT REFUSED.
 *
 * Filtering on `aheadMain > 0` is pure SHA ANCESTRY: a rebase gives the same change a different
 * sha, so a worktree whose every commit is already on origin/main still reads ahead. That feeds
 * `tidyStep`, which REFUSES rather than warns, so on sha alone a team that did everything right
 * could not be closed out through the product at all. A rebased branch can read 5 commits not on main BY SHA
 * and 0 BY PATCH-ID, with origin/main carrying every one of those changes.
 *
 * The server puts `commitsNotOnMain` on each worktree — `git cherry origin/main <branch>` counted
 * the same way the cherry-pick case counts it for the team ref. This is the client half.
 */
describe('a worktree whose commits were rebased onto main', () => {
  const wt = (o: Partial<WorktreeStatus> = {}): WorktreeStatus => ({
    path: 'Z:/Harness/teams/alpha/repos/Harness/wt_DeveloperMira',
    branch: 'member-kanban-fix', sha: '8337b4e', member: 'DeveloperMira',
    aheadMain: 0, behindMain: 0, commitsNotOnMain: null,
    ...o,
  } as WorktreeStatus)

  const withWorktree = (w: WorktreeStatus): RepoStatus => base({
    teamSha: null, teamPushed: null, teamMergedToMain: null, teamCommitsNotOnMain: null,
    mainAhead: 0, mainBehind: 0, worktrees: [w],
  })

  /** THE CASE. Five commits ahead by sha, nothing outstanding by patch-id: tidy it. */
  it('tidies a worktree whose commits are all on main by patch-id', () => {
    const rebased = withWorktree(wt({ aheadMain: 5, behindMain: 0, commitsNotOnMain: 0 }))

    expect(unintegratedWorktrees(rebased)).toEqual([])
    expect(nextStep(rebased, true).action).toBe('cleanup-worktrees')
  })

  /**
   * THE GENUINE CASE MUST STILL BE CAUGHT: two commits on a member branch that exist nowhere
   * else, where removing the checkout would lose them. A guard that also fires for fully integrated work is one people learn to click past,
   * which disarms it for the day it is true.
   */
  it('still refuses when the content count says commits are outstanding', () => {
    const holding = withWorktree(wt({ aheadMain: 2, behindMain: 3, commitsNotOnMain: 2 }))
    const step = nextStep(holding, true)

    expect(unintegratedWorktrees(holding)).toHaveLength(1)
    expect(step.action).not.toBe('cleanup-worktrees')
    expect(step.reason).toMatch(/DeveloperMira/)
  })

  /**
   * NULL IS NOT MEASURED AND MUST NOT ACQUIT. `git cherry` failing is not evidence of zero, so an
   * unmeasured content count falls back to the sha reading rather than overriding it — and the sha
   * reading here says the worktree is holding commits. REFUSING is the recoverable direction:
   * `cleanup-worktrees` runs `git worktree remove`, which never deletes a branch.
   */
  it('does not let an unmeasured content count acquit a worktree that is ahead', () => {
    const unmeasured = withWorktree(wt({ aheadMain: 5, behindMain: 0, commitsNotOnMain: null }))

    expect(unintegratedWorktrees(unmeasured)).toHaveLength(1)
    expect(nextStep(unmeasured, true).action).not.toBe('cleanup-worktrees')
  })

  /**
   * A DETACHED OR UNMEASURABLE WORKTREE MEASURES NEITHER, and the answer stays the same: nothing
   * was measured, nothing convicts, and the tidy costs a checkout and no commits.
   */
  it('still tidies a worktree whose divergence was never measured at all', () => {
    const nothingMeasured = withWorktree(
      wt({ branch: null, aheadMain: null, behindMain: null, commitsNotOnMain: null }))

    expect(unintegratedWorktrees(nothingMeasured)).toEqual([])
    expect(nextStep(nothingMeasured, true).action).toBe('cleanup-worktrees')
  })

  /**
   * THE CONTENT ANSWER OUTRANKS THE SHA ANSWER WHEN BOTH ARE THERE, in the conservative direction
   * too: work that is in this clone's local main but not yet on origin/main reads `aheadMain: 0`
   * and a non-zero cherry count, and it is the cherry count that is about origin/main.
   */
  it('refuses on a non-zero content count even when the sha count is zero', () => {
    const notOnOrigin = withWorktree(wt({ aheadMain: 0, behindMain: 0, commitsNotOnMain: 3 }))

    expect(unintegratedWorktrees(notOnOrigin)).toHaveLength(1)
    expect(nextStep(notOnOrigin, true).action).not.toBe('cleanup-worktrees')
  })

  /**
   * AN OPEN CARD'S TREE IS NEVER TIDIED, so it cannot hold the tidy back. With a tree per
   * card an open card's branch is ahead of main as a matter of course; refusing on it would
   * refuse the clean-up for as long as the team has any work in flight.
   */
  it('does not let an open card\'s tree hold back the tidy', () => {
    const inFlight = withWorktree(wt({ aheadMain: 2, commitsNotOnMain: 2, card: '559', open: true }))

    expect(unintegratedWorktrees(inFlight)).toEqual([])
    expect(openWorktrees(inFlight)).toHaveLength(1)
    expect(nextStep(inFlight, true).action).toBe('cleanup-worktrees')
  })

  it('still refuses over a settled card\'s tree that holds unintegrated commits', () => {
    const settled = withWorktree(wt({ aheadMain: 2, commitsNotOnMain: 2, card: '558', open: false }))

    expect(unintegratedWorktrees(settled)).toHaveLength(1)
    expect(openWorktrees(settled)).toEqual([])
  })
})

describe('formatBytes', () => {
  it('says a size the way a person reads it, and nothing when unmeasured', () => {
    expect(formatBytes(null)).toBe('')
    expect(formatBytes(undefined)).toBe('')
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(3 * 1024 * 1024 + 400 * 1024)).toBe('3.4 MB')
  })
})

/**
 * A RUNG THAT STATES A CONDITION HAS ONLY DONE HALF ITS JOB.
 *
 * THE LADDER'S WHOLE PREMISE IS TEACHING THE SEQUENCE, and a caption that says only what is TRUE
 * NOW teaches nothing about the order: a reader must also learn, for example, that `Merge to main`
 * is not offered at all until this clone is current. Every rung below is asserted for the same
 * clause, because a caption nobody asserts is a caption that drifts.
 *
 * THE CLAUSE MAY NOT OVERCLAIM, AND THAT IS WHY THESE ARE TESTS RATHER THAN A STYLE RULE. The
 * obvious sentence for the Rebase rung - "rebase, because merge-to-main refuses a branch that is
 * not a fast-forward of origin/main" - is false: `MergeToMainAsync` has a merge arm, and a branch
 * merely behind main is joined to it with a real merge commit rather than refused. The unlock said here is the LADDER'S sequencing, which is a fact
 * about this file, and never an endpoint refusal this file cannot see.
 */
describe('every rung says what its button unlocks, not only what is true now', () => {
  const reasonFor = (o: Partial<RepoStatus>): string => nextStep(base(o), true).reason

  it('FETCH: the rungs below are measured against what it reads', () => {
    const step = nextStep(base(), false)

    expect(step.action).toBe('fetch')
    expect(step.reason).toContain('every rung below')
  })

  it('FETCH on unknown drift: it is what decides which of the two Current buttons applies', () => {
    const reason = reasonFor({ mainAhead: null })

    expect(reason).toContain('Bring current')
    expect(reason).toContain('Rebase')
  })

  /**
   * THE TWO BLOCKED CURRENT RUNGS HAVE NO BUTTON, so what they owe the reader is what CLEARING
   * them unlocks. Both conditions are real endpoint refusals: `RebaseRepoAsync` answers 409 for a
   * dirty tree and for any HEAD that is not main, and `MergeToMainAsync` answers 400 for both.
   */
  it('DIRTY: names the two actions that refuse a dirty clone', () => {
    const reason = reasonFor({ dirty: true })

    expect(reason).toMatch(/uncommitted/)
    expect(reason).toMatch(/rebase and merge to main/)
  })

  it('DETACHED HEAD: names the two actions that refuse it, and what to do', () => {
    const reason = reasonFor({ headCheckout: 'detached' })

    expect(reason).toMatch(/rebase and merge to main/)
    expect(reason).toContain('check main out')
  })

  /**
   * THE REBASE RUNG. Stating the condition is not enough; the caption must also say that
   * the ladder offers neither rung below until this one is climbed.
   */
  it('REBASE: says that Push and Merge to main are not offered until the clone is current', () => {
    const reason = reasonFor({ mainAhead: 3, mainBehind: 4 })

    expect(reason).toContain('Push')
    expect(reason).toContain('Merge to main')
    expect(reason).toContain('until this clone is current')
  })

  it('BRING CURRENT: unlocks the same two rungs, and says so', () => {
    const reason = reasonFor({ mainAhead: 0, mainBehind: 4 })

    expect(reason).toContain('Push')
    expect(reason).toContain('Merge to main')
  })

  /**
   * `MergeToMainAsync` refuses outright - 400, "Team branch team/{id} does not exist locally or on
   * origin" - when neither `refs/heads/team/{id}` nor `origin/team/{id}` resolves. Publishing this
   * clone's main to that ref is what creates the branch the merge then integrates, so the unlock
   * is an endpoint precondition and not merely the ladder's ordering.
   */
  it('PUSH: names the branch it publishes as the one Merge to main then integrates', () => {
    const reason = reasonFor({ teamPushed: false, teamMergedToMain: false, teamCommitsNotOnMain: 2 })

    expect(reason).toContain('the work is not on origin yet')
    expect(reason).toContain('team/alpha')
    expect(reason).toContain('Merge to main')
  })

  /** The unmeasured arm keeps its own caveat AND gains the unlock - one sentence, both facts. */
  it('PUSH with pushed-ness unmeasured: keeps the harmless-if-already-there caveat', () => {
    const reason = reasonFor({ teamPushed: null, teamMergedToMain: false, teamCommitsNotOnMain: 2 })

    expect(reason).toContain('not been measured')
    expect(reason).toContain('changes nothing if it is already there')
    expect(reason).toContain('Merge to main')
  })

  /**
   * `DeleteRemoteBranchAsync` refuses with 409 - "origin/team/{id} is not on origin/main yet, so
   * deleting it would lose work" - until ancestry or patch-equivalence holds. The merge is what
   * makes it hold, which is the one thing the Merged rung never said.
   */
  it('MERGE TO MAIN: says the delete on the rung below is what it unlocks', () => {
    const reason = reasonFor({ teamPushed: true, teamMergedToMain: false, teamCommitsNotOnMain: 2 })

    expect(reason).toContain('the work is on origin but not on main')
    expect(reason).toContain('deleted')
  })

  it('MERGE TO MAIN with content unmeasured: gains the unlock without claiming not-on-main', () => {
    const reason = reasonFor({
      teamPushed: true, teamMergedToMain: null, teamMergedToMainBy: null, teamCommitsNotOnMain: null,
    })

    expect(reason).toContain('not been measured')
    expect(reason).toContain('deleted')
    expect(reason).not.toContain('not on main')
  })

  /**
   * THE LOCAL-MAIN ARM UNLOCKS LESS THAN THE ORDINARY ONE, AND MUST SAY SO. `MergeToMainAsync`
   * integrates local main here and its own summary reads "The team branch on origin was stale and
   * was not integrated" - so the Tidied rung's delete still refuses afterwards. A clause promising
   * the delete here would be the caption claiming something the endpoint will refuse.
   */
  it('MERGE TO MAIN from local main: names what it reaches, and what it leaves behind', () => {
    const reason = reasonFor({
      teamPushed: true, teamMergedToMain: false, teamCommitsNotOnMain: 2,
      cloneMainOnTeamBranch: false, mainAhead: 2, mainBehind: 0,
    })

    expect(reason).toMatch(/main has work that the branch on origin does not/)
    expect(reason).toContain('origin/main')
    expect(reason).toMatch(/stale/)
    expect(reason).not.toContain('deleted')
  })

  it('DELETE THE BRANCH ON ORIGIN: says what is left after it', () => {
    const reason = reasonFor({ teamMergedToMain: true, teamPushed: true })

    expect(reason).toContain('finished with')
    expect(reason).toContain('worktrees')
  })

  /**
   * THE ENDPOINT WINS. `MergeToMainAsync` does not require the team ref to be a fast-forward of
   * origin/main, and does not refuse with "bring the branch current and re-gate to merge". No
   * caption may say so in prose: a reader told to rebase
   * before merging would be rebasing the one thing the merge arm exists to avoid rewriting.
   */
  it('never tells a reader that merging requires the branch to be rebased or re-gated', () => {
    const everyCaption = [
      nextStep(base(), false).reason,
      reasonFor({ mainAhead: 3, mainBehind: 4 }),
      reasonFor({ mainAhead: 0, mainBehind: 4 }),
      reasonFor({ teamPushed: true, teamMergedToMain: false, teamCommitsNotOnMain: 2 }),
      reasonFor({
        teamPushed: true, teamMergedToMain: null, teamMergedToMainBy: null, teamCommitsNotOnMain: null,
      }),
    ]

    for (const caption of everyCaption) {
      expect(caption, `caption states a precondition the endpoint does not have: ${caption}`)
        .not.toMatch(/re-gate|rebase (it |the branch )?(first )?(before|to) merge/i)
    }
  })
})

// The ladder measures against the stored default branch and offers nothing while it is not
// known - every action would be refused, and a guessed `main` is never used.
describe('the stored default branch', () => {
  it('offers no action while the branch is not known, and says how to learn it', () => {
    const step = nextStep(base({ defaultBranch: null, mainAhead: null, mainBehind: null }), true)
    expect(step.action).toBeNull()
    expect(step.reason).toContain('default branch is not known')
    expect(step.reason).not.toMatch(/\bmain\b/)
  })

  it('still asks for a fetch first, which is what reads the branch', () => {
    expect(nextStep(base({ defaultBranch: null }), false).action).toBe('fetch')
  })

  it('names the stored branch in the step and on the button', () => {
    const behind = base({ defaultBranch: 'trunk', headCheckout: 'trunk', mainAhead: 1, mainBehind: 1 })
    const step = nextStep(behind, true)
    expect(step.action).toBe('rebase')
    expect(step.reason).toContain('origin/trunk')
    expect(actionLabel('rebase', behind)).toBe('Rebase onto origin/trunk')
    expect(actionLabel('merge-to-main', behind)).toBe('Merge to trunk')
    expect(actionLabel('push', behind)).toBe(ACTION_LABELS.push)
  })
})

/**
 * OPEN PULL REQUEST REPLACES MERGE TO MAIN IN CONTRIBUTOR MODE, and the reverse
 * holds for an owned repository: no state of either ever offers the other's button.
 */
describe('contributor mode offers Open pull request, never Merge to main', () => {
  const contributing = (o: Partial<RepoStatus> = {}) => base({
    upstreamUrl: 'https://github.com/project/Widget.git',
    originUrl: 'https://github.com/fork-owner/Widget.git',
    forkOwner: 'fork-owner',
    teamMergedToMain: false,
    teamCommitsNotOnMain: 2,
    worktrees: [],
    ...o,
  })
  const pull = (landing: string, state = 'open') => ({
    url: 'https://github.com/project/Widget/pull/4', number: 4, state,
    readAt: '2026-09-26T09:00:00Z', landing, unknownReason: landing === 'unknown' ? 'GitHub said: error connecting' : null,
  })

  it('offers Open pull request once the team branch is on the fork', () => {
    const step = nextStep(contributing(), true)
    expect(step.action).toBe('open-pull-request')
    expect(step.rung).toBe('merged')
    expect(actionLabel('open-pull-request', contributing())).toBe('Open pull request')
  })

  it('offers Push, not Open pull request, while the team branch is not on the fork', () => {
    expect(nextStep(contributing({ teamPushed: false, teamSha: null }), true).action).toBe('push')
  })

  it('links an open pull request instead of offering a second', () => {
    const step = nextStep(contributing({ pullRequest: pull('in-review') }), true)
    expect(step.action).toBeNull()
    expect(step.reason).toContain('#4 is open upstream')
  })

  it('offers another after one was declined, and says it never landed', () => {
    const step = nextStep(contributing({ pullRequest: pull('declined', 'closed') }), true)
    expect(step.action).toBe('open-pull-request')
    expect(step.reason).toContain('closed upstream without merging')
  })

  it('reads landed from the pull request, never from ancestry on the fork', () => {
    const merged = contributing({ pullRequest: pull('landed', 'merged') })
    expect(nextStep(merged, true).rung).toBe('done')
    expect(rungState('merged', nextStep(merged, true), merged)).toBe('satisfied')
    expect(repoHeadline(merged).text).toContain('merged upstream')

    // Merged to the fork's default by ancestry is not landed upstream.
    const fork = contributing({ teamMergedToMain: true, teamMergedToMainBy: 'ancestry', teamCommitsNotOnMain: 0 })
    expect(nextStep(fork, true).action).toBe('open-pull-request')
    expect(repoHeadline(fork).text).not.toContain('safe to delete')
  })

  it('never offers Merge to main in any contributor state, and an owned repository never offers Open pull request', () => {
    const states: Partial<RepoStatus>[] = [
      {}, { teamPushed: false }, { teamMergedToMain: true, teamMergedToMainBy: 'ancestry', teamCommitsNotOnMain: 0 },
      { cloneMainOnTeamBranch: false, mainAhead: 1 }, { pullRequest: pull('unknown') },
    ]
    for (const over of states) {
      expect(nextStep(contributing(over), true).action).not.toBe('merge-to-main')
      expect(nextStep(base({ teamMergedToMain: false, teamCommitsNotOnMain: 2, ...over, pullRequest: null }), true).action)
        .not.toBe('open-pull-request')
    }
    expect(nextStep(base({ teamMergedToMain: false, teamCommitsNotOnMain: 2 }), true).action).toBe('merge-to-main')
  })
})

/**
 * MAIN MOVED UNDER A PUSHED TEAM BRANCH. This used to end at "merge or rebase them together" with no
 * button, and the merge was done by hand; Bring current and merge is that button.
 */
describe('a pushed team branch behind a moved main', () => {
  const stranded = (o: Partial<RepoStatus> = {}) => base({
    teamPushed: true,
    teamMergedToMain: false,
    teamCommitsNotOnMain: 2,
    cloneMainOnTeamBranch: false, // the clone's main took the other team's merge; the branch did not
    mainAhead: 0,
    mainBehind: 0,
    teamBranchBehindDefault: 1,
    filesChangedOnBothSides: [],
    ...o,
  })

  it('offers Bring current and merge where the ladder used to stop', () => {
    const step = nextStep(stranded(), true)
    expect(step.rung).toBe('merged')
    expect(step.action).toBe('bring-current-and-merge')
    expect(step.reason).toContain('origin/main has 1 commit team/alpha does not')
    expect(actionLabel('bring-current-and-merge', stranded())).toBe('Bring current and merge')
  })

  it('offers it on the ordinary merge rung too, in place of Merge to main', () => {
    expect(nextStep(stranded({ cloneMainOnTeamBranch: true }), true).action).toBe('bring-current-and-merge')
  })

  it('leaves the old answer to a Host that does not measure it', () => {
    const { teamBranchBehindDefault: _unmeasured, ...older } = stranded()
    const step = nextStep(older, true)
    expect(step.action).toBeNull()
    expect(step.reason).toMatch(/merge or rebase/)
    expect(nextStep(stranded({ teamBranchBehindDefault: null, cloneMainOnTeamBranch: true }), true).action)
      .toBe('merge-to-main')
  })

  it('is never offered in contributor mode, where the work goes upstream as a pull request', () => {
    expect(nextStep(stranded({ upstreamUrl: 'https://github.com/up/Harness.git' }), true).action)
      .toBe('open-pull-request')
  })

  it('is refused while the clone is dirty, as Merge to main is', () => {
    expect(nextStep(stranded({ dirty: true }), true).action).toBeNull()
  })

  it('says it is git only, and to run the suites first when both sides changed the same files', () => {
    const step = nextStep(stranded(), true)
    expect(bringCurrentAndMergeAdvice(stranded(), step)).toBe('This is git only: it runs no tests.')

    const overlapping = stranded({ filesChangedOnBothSides: ['src/a.cs'] })
    expect(bringCurrentAndMergeAdvice(overlapping, nextStep(overlapping, true)))
      .toBe('This is git only: it runs no tests. team/alpha and origin/main both changed src/a.cs - '
        + 'run the suites on the merge before pressing it.')

    expect(bringCurrentAndMergeAdvice(stranded(), nextStep(base(), true))).toBeNull()
  })
})

