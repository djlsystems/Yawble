---
name: wrap-up
description: How a Manager finishes a workflow cleanly - cards settled, accepted work merged into the team branch, tests run, a report, then workflow_complete.
roles: [manager]
---
# Wrap-up

Finishing is a short procedure with checks. Each check exists because skipping it hands the person work nobody
verified. A refusal is a real outcome: if a check fails, fix it or say so; do not paper over it.

Work in your own clone, on your branch `team/<team>`. Quote every path.

## Check first

1. `status`: nobody is still running or queued on this workflow. If someone is, end your turn; you will be
   woken when that run ends.
2. `board` with `action: show`: every card of this workflow is Done, or you are leaving it undone on purpose
   and will say why. A card in Needs You means the person still owes an answer: do not declare over it.
3. `git status -sb`: you are on `team/<team>` and the tree is clean.
4. Every member branch you accepted is merged into your branch (`git log --oneline` shows it). Merge any
   that is missing, one at a time, as in the Manager's "Repositories" section:

   ```powershell
   git fetch "$env:YAWBLE_TEAM_BUNDLE" members/<team>/developerpriya
   git merge --no-edit FETCH_HEAD
   ```

   Merge only accepted work. On a conflict, `git merge --abort` and send it back to the members; do not
   fix their code yourself.

## Verify

Build and run the tests on your merged branch (`dotnet test`, `npm test`, whatever the repository uses).
Members tested their own branches, not the merge. If the tests fail, the work is not finished: tell the
member whose change broke it what failed and how to reproduce it.

## Report

When the work is more than a line or two, write a short report into `YAWBLE_TEAM_DOCS` (Markdown): what was
asked, what was delivered, how it was checked, what was left out and why. The folder is not
in your clone, so nothing there is committed.

## Declare

Call `workflow_complete` with:

- your branch `team/<team>` and its commit (`git rev-parse HEAD`), and each member branch it includes;
- how it was checked (the test command and its result);
- the FULL PATH of anything the person can run or open, and of the report;
- what the person must do next: for example, merge `team/<team>` to main with the app's Git dialog, or
  review a pull request on origin.

Never commit to, merge into or move main, and never force anything: landing your branch on main is the
person's job. If `workflow_complete` says the workflow is already declared, stop.
