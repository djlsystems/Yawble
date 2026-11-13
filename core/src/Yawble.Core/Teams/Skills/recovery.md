---
name: recovery
description: How to pick work back up after an interruption - a run cut off by the app closing, a failed run, a nudge, or the reminder that an open workflow has nobody working it.
roles: [manager, member]
---
# Recovery

Work stopped part-way. Your job is to resume it, not to start it again.

## When this applies

- The app closed or restarted mid-run: the run-ended message says the run "was interrupted", or you are woken
  once after a restart because your run was cut off.
- A run failed: "... run failed: <reason>". A failure is the run not finishing, not a verdict on the work.
- The person nudged you, or the app reminded you that a workflow is open with nobody working it.

## Read the state before you touch anything

1. `workflow_show` for the workflow you were woken about: the instruction, every tell, hand-back, block and
   decision so far. Find the last step that really finished.
2. `status`: who is running, who is queued, who is idle. (Manager.)
3. `board` with `action: show`: which cards are done, in progress, or waiting on the person. (Manager.)
4. In your clone: `git status -sb` and `git log --oneline -10`. Committed work survived; uncommitted changes
   may be there too. A member's committed branch is in the shared view only once its run has ended
   (see `team-git`).

## What each state means

- **Handed back**: that step is done. Do not ask for it again.
- **Blocked**: the member decided it could not go on; the reason names what to fix.
- **Needs decision**: the step is not done. Once the person has answered, the step runs again with the answer.
- **Failed or interrupted**: no verdict. Pick up from the last finished step, not from the start. If a tester's
  run failed, the work it was checking is unchecked, not accepted.

## Decide, then act once

- **Continue**: the work is still needed and you can see where it stopped. A member finishes from its last
  commit; a Manager tells the member again with the same card (`card: <id>`) and says what is already done.
- **Hand back**: the work was in fact finished (committed, checked) but the hand-back never went out. Commit if
  needed, then `handback` with the branch and commit.
- **Declare**: everything is delivered and merged and only the declaration was lost. A Manager follows
  `wrap-up`; a member declares only if the person gave the instruction to it directly.
- **Ask**: you cannot tell what the person wants now (the work may be stale, or two paths are open): call
  `needs_decision` with the question.

Never re-hire, start a new card for the same piece, or redo finished work to recover: that throws away what
was done and can overwrite a commit somebody already checked. If a nudge asks for nothing new and nothing is
stuck, say where things stand with `progress` and end your turn.

## Report what you resumed from

With `progress` (and in your hand-back or tell), name the last finished step, what you resumed, and anything
the interruption lost, so the person can check your reasoning without reading the whole thread.
