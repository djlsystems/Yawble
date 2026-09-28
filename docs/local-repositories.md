# Local repositories

A local repository is a git repository kept on the instance, with no hosting service behind it: no GitHub account, no token, nothing leaves the computer. Use one for work that should stay on the operator's machine: a plugin that is not ready to share, an experiment, or an instance with no GitHub account.

## What one is

Each local repository is a bare git repository on the data volume, at `<dataRoot>/repos/<name>.git` (`/data/repos/<name>.git` in the container). It is owned by the Host's user (`harness`), readable by the `agent` group and never writable by it. Agents can clone and fetch from it but never push: as with a remote, agents commit in their worktrees, and when a person pushes or merges, or the platform publishes a card, the Host fetches the commits into the bare repository as itself. Pushes are never forced. The entrypoint (`scripts/prepare-volume.sh`) creates `repos` when it is missing and resets these owners and modes on every start.

A new local repository starts on `main` with one empty commit, so a team can clone and branch from it at once.

## `local:<name>`

A team refers to a local repository as `local:<name>`, anywhere a repository URL is accepted: the URL field in New Team and Team settings, `PUT /api/teams/{team}/repos`, `POST /api/teams`, and the `repo` tools. `local:` is a reference, not a path: the Host finds the repository by name, and no path from a request reaches the filesystem. You never type a folder.

The name is 1 to 100 letters, digits, `.`, `_` or `-`, starting with a letter or digit, not ending in `.git` or `.lock` (in any case), with no `..` anywhere. So `.hidden`, `a..b` and `widget.git` are not names. A name that is not legal, or names no local repository, is refused with a sentence naming it. The web app, the API and the CLI all apply this rule.

## Working on one

The team's clone uses the local repository as `origin`. Fetch, Bring current, Rebase, Push, Merge to main and Delete remote branch in the Git dialog work as they do for a remote. The default branch is read from the local repository's `HEAD` and stored, as for a remote; it is never assumed.

## Creating, attaching and deleting (people only)

All of these are for a person signed in to the web app. The routes (`GET`/`POST /api/local-repos`, `DELETE /api/local-repos/{name}`) refuse a Manager or member credential, and agents get no tool for them.

- **Create.** In **New Team** or **Team settings → GitHub Repos**, type a name in **Create a local repository**, beside the URL field, and press **Create**. The name is checked as the Host checks it. The new repository joins the team's list as `local:<name>`.
- **Attach.** Under the same field, **Local repositories:** shows a chip for each one the instance has. Click one to attach it; one already in the team's list is shown as attached. Several teams can use the same local repository.
- **No upstream.** A local repository has no upstream field. In Team settings its row says "A local repository on this instance: contributor mode and pull requests do not apply."
- **See them all.** **Admin → Repositories** lists each local repository: its name, size (the total length of its files), default branch, last commit and the teams using it.
- **Delete.** Also in Admin → Repositories. **Delete** asks first ("Delete <name>?"; it cannot be undone). It is refused while any team's repository list names the repository, and the refusal names those teams.

Creating and deleting a local repository each append a row to the tenant log (`local-repo.created`, `local-repo.deleted`).

## A team's own local repository

A team created with no repository gets a local repository named after it, attached as `local:<team id>`. When a local repository of that name exists and no team uses it, it is reused; when a team uses it, `-2`, `-3` and so on are tried. The team and its repository are one unit: if the repository cannot be made, the team is not created and the reason is named. `POST /api/teams` takes `localRepository: false` to make none, as does the `team_create` tool. For an existing team, `POST /api/teams/{team}/local-repo` makes its local repository the same way (a person's action).

Before a team is created with a repository URL, and before a URL is attached, the Host reads it with `git ls-remote` (a github.com URL with `GH_TOKEN`, as Fetch). A URL that cannot be read is refused with a sentence naming it and git's reason, and nothing is created. A person may then create it on GitHub as a private repository (github.com only, when the instance's token can create repositories), use a local repository instead, or attach it anyway (a network failure only). An agent is offered only the local repository.

Deleting a team keeps its local repository. It is listed as unused in Admin → Repositories, where a person may delete it.

## Getting the code out

The operator CLI copies a local repository onto your computer so you can open it in an editor. The instance must be running (`yawble up`), and git must be installed on your computer.

```sh
yawble repo list                              # name, local:<name>, default branch, last commit, size
yawble repo list --json
yawble repo clone my-plugin                   # into ./my-plugin
yawble repo clone my-plugin ~/code/my-plugin  # into a folder you name
```

`repo list` shows size the way Admin → Repositories does: the total length of the repository's files, in B, KB, MB, GB or TB of 1024. It does not show which teams use each repository; Admin → Repositories does.

`repo clone` copies `<dataRoot>/repos/<name>.git` out of the instance through the container engine, then clones it on your computer. It works the same with Podman and with Docker.

- The folder must not already exist. If it does, `clone` refuses and leaves it untouched.
- An illegal name (by the rule above) is refused as not a local repository name, naming it, before anything is asked of the instance. A legal name the instance does not have is refused naming it and listing the names the instance has.
- Every branch on the instance becomes a local branch in the clone (`main`, and each `team/<team>` branch). The default branch is checked out.
- The clone has **no remote**. Pushing back from your computer to the instance is not supported. To take newer work, run `repo clone` again into a new folder.

## What does not apply

- **Pull requests.** A local repository has no hosting service, so there is nothing to open a pull request on. Asking for one answers a sentence saying so. Merge to main is how work lands.
- **Contributor mode.** Upstreams, forks and pull requests belong to hosted repositories. A local repository has no upstream field, and asking for contributor mode answers a sentence saying it does not apply. `GH_TOKEN` is never used.
- **Access from outside.** Nothing outside the instance can reach a local repository over the network, and nothing outside can push into it. The CLI's copy-out is the only way out.
- **Publishing later** to a hosted service (adding a remote and pushing every branch) is not built yet.

## Backups

`<dataRoot>/repos` is on the data volume, so `yawble backup` carries every local repository along with everything else, and `yawble restore` brings them back. See [ops/backups-logs-and-versions.md](ops/backups-logs-and-versions.md). The daily database copy does not include them: it covers `messages.db` only.
