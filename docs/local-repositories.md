# Local repositories

A local repository is a git repository that lives only on the instance. There is no hosting service behind it: no GitHub account, no token, nothing leaves the computer. Use one for work that should stay on the operator's machine, such as a plugin that is not ready to share, an experiment, or when there is no hosting account at all.

## What one is

Each local repository is a bare git repository on the data volume, at `<dataRoot>/repos/<name>.git` (`/data/repos/<name>.git` in the container). The Host owns it. Agents can read it through their team's clone but can never write to it directly: as with a remote, agents commit in their worktrees and the platform does the pushing.

A new local repository starts with the default branch `main` and one empty first commit, so a team can clone it and branch from it straight away.

## `local:<name>`

A team refers to a local repository as `local:<name>`, anywhere a repository URL is accepted: New Team, Team settings → repositories, and the `repo` tools. `local:` is a reference scheme, not a path. You never type a folder. The name follows the same rules as a repository folder name: it cannot be empty, contain `/`, or be `.`, `..` or `.git`. An illegal name, or one the instance does not have, is refused and named.

After that, the team works on it as it would on any repository, with the bare repository as `origin`. Its clone, a worktree per card, Fetch, Bring current, Rebase, Push, Merge to main, Delete remote branch and the stored default branch all behave as they do for a remote. The default branch is read from the repository, never assumed.

## Creating, attaching and deleting (people only)

All of these are for people signed in to the web app. A Manager or any other agent cannot create or delete a local repository.

- **Create.** New Team and Team settings have **Create a local repository** beside the URL field. Give it a name; it is checked like a repository folder name.
- **Attach.** The same places list the existing local repositories. Choose one to attach it to the team as `local:<name>`. Several teams can use the same one.
- **See them all.** **Admin → Repositories** lists each local repository with its name, size, default branch, last commit, and the teams using it.
- **Delete.** Also in Admin → Repositories. Delete is refused while any team uses the repository, and it asks before deleting.

Creating and deleting are both recorded in the instance's event log.

## Getting the code out

The operator CLI copies a local repository onto your computer so you can open it in an editor. The instance must be running (`yawble up`), and git must be installed on your computer.

```sh
yawble repo list                              # name, local:<name>, default branch, last commit, size
yawble repo list --json
yawble repo clone my-plugin                   # into ./my-plugin
yawble repo clone my-plugin ~/code/my-plugin  # into a folder you name
```

`repo clone` copies `<dataRoot>/repos/<name>.git` out of the instance through the container engine, then clones it on your computer. It works the same with Podman and with Docker.

- The folder must not already exist. If it does, `clone` refuses and leaves it untouched.
- Every branch on the instance becomes a local branch in the clone (`main`, and each `team/<team>` branch). The default branch is checked out.
- The clone has **no remote**. Pushing back from your computer to the instance is not supported. To take newer work, run `repo clone` again into a new folder.

## What does not apply

- **Pull requests.** A local repository has no hosting service, so there is nothing to open a pull request on. The pull request actions say so rather than failing. Merge to main is how work lands.
- **Contributor mode.** Upstreams, forks, "Fork it for me" and DCO sign-off with a pull request all belong to hosted repositories. For a local repository, contributor mode says it does not apply. `GH_TOKEN` is never involved.
- **Access from outside.** Nothing outside the instance can reach a local repository over the network, and nothing outside can push into it. The CLI's copy-out is the only way out.
- **Publishing later** to a hosted service (adding a remote and pushing every branch) is not built yet.

## Backups

`<dataRoot>/repos` is on the data volume, so `yawble backup` carries every local repository along with everything else, and `yawble restore` brings them back. See [ops/backups-logs-and-versions.md](ops/backups-logs-and-versions.md). The daily database copy does not include them: it covers `messages.db` only.
