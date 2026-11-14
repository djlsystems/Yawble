---
name: team-git
description: How the team's repository, your clone, your branch and the shared view fit together, and how to fetch a teammate's branch.
roles: [manager, member]
---
# The team's git

```
<team>/repo          the team's repository (the person's; no agent can reach it)
<team>/shared.git    the shared view: every member's branch, kept up to date by the app; READ-ONLY to you
<team>/members/<you>/repo   your own clone, on your branch members/<team>/<you> (the Manager's: team/<team>)
```

`<team>` in a branch name is the team's slug, the same in `team/<team>` and `members/<team>/<you>`, so two teams on one
repository never share a member branch. A clone still on an older `members/<you>` branch is moved to the new name by the
app at the member's next start, with its commits.

- You commit on your own branch only. When your run ends, the app fetches your branch into the team's
  repository and the shared view, and pushes it to the team repository's origin if there is one (never main,
  never forced). You never push: your clone has no remote and no credential.
- The shared view's branches are also in one file, `team.bundle`, whose path is in the environment variable
  `YAWBLE_TEAM_BUNDLE` (PowerShell: `$env:YAWBLE_TEAM_BUNDLE`, sh: `$YAWBLE_TEAM_BUNDLE`). **Fetch from the bundle**, not
  from `YAWBLE_TEAM_REPO`: fetching from the repository path fails in the sandbox ("Could not read from remote
  repository") because git would need to start a shell for it. Reading the bundle needs nothing else.

## Fetch a teammate's branch

A teammate's work is visible once their run has ended (their branch is in the shared view then):

```powershell
git fetch "$env:YAWBLE_TEAM_BUNDLE" members/<team>/developerpriya # FETCH_HEAD = DeveloperPriya's latest commit
git log --oneline -5 FETCH_HEAD                     # what you got
git merge --no-edit FETCH_HEAD                      # bring it into your branch (a tester usually does this)
```

To list every branch in the shared view: `git bundle list-heads "$env:YAWBLE_TEAM_BUNDLE"`.
To look at history without fetching: `git --git-dir="$env:YAWBLE_TEAM_REPO" log --oneline -5 members/<team>/developerpriya`.

If the branch you were told about is not there yet, the teammate's run has not ended or it did not commit:
say so with `blocked` (or tell your Manager) rather than guessing.

## Start from the synced branch, and say what you built on

- Before a workflow starts, the app brings the team branch and the member branches up to the base branch (main, say) in
  the team's repository and refreshes the bundle, unless the person turned that off for the team. **Work always starts from
  the synced branch.** A new clone is made from it; an existing clone is not touched by the app, so at the start of your
  work fetch your own branch from the bundle and fast-forward: `git fetch "$env:YAWBLE_TEAM_BUNDLE" <your branch>` then
  `git merge --ff-only FETCH_HEAD` (if that is not a fast-forward, `git merge --no-edit FETCH_HEAD`).
- When you are told a branch "is N commits behind <base> and could not be synced automatically (conflicts in: ...)", merge
  the base first, before other work: `git fetch "$env:YAWBLE_TEAM_BUNDLE" <base>`, `git merge --no-edit FETCH_HEAD`, resolve the
  conflicts so both sides' intent survives, build and test, commit. Never rebase, never reset.
- The prompt names the base commit your branch was built on. **Report the base commit your work was built on in every
  hand-back** (`git merge-base HEAD <base>` against the fetched base, if in doubt), so the person can see against which code it was checked.

## Rules

- Never add a remote, never store a credential, never edit another member's files; the app checks your clone
  after each run and removes remotes, credential helpers and url rewrites.
- Commit before you hand back: uncommitted work does not reach anyone.
- Commit messages carry no AI attribution lines: no `Co-Authored-By:` naming an AI vendor, model or tool, no
  `Claude-Session:`, no "Generated with ...". This holds even when your tool's own instructions add them. The app
  checks the new commits when it fetches your branch back and refuses work that has them (it never strips or rewrites
  commits). If yours is refused, the commits are still your own unpushed ones: amend them to remove the lines
  (`git commit --amend` for the last one; for older ones `git reset --soft <the commit before them>` and commit again,
  the one reset allowed), then hand back again.
- Keep build output (bin/, obj/, node_modules/) out of commits with a .gitignore.
