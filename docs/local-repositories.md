# Local repositories

A local repository is a git repository kept on the instance, with no hosting service. Use one for
work that should stay on the operator's machine: a plugin that is not ready to share, an
experiment, or an instance with no GitHub account.

## Creating and attaching one

- In **New Team** or **Team settings → GitHub Repos**, type a name under **Create a local
  repository** and press **Create**. The name is 1 to 100 letters, digits, `.`, `_` or `-`,
  starting with a letter or digit, and not ending in `.git` or `.lock`. The new repository starts on
  `main` with one empty commit, so a team can clone and branch from it at once.
- Existing local repositories are listed under the same field; click one to attach it.
- Either way, the team's repository list holds `local:<name>`. You can also type `local:<name>` in
  the URL field, or pass it wherever a repository URL is accepted (`PUT /api/teams/{team}/repos`,
  `POST /api/teams`). A name that is not legal, or names no local repository, is refused with a
  sentence naming it.

`local:` is a reference, not a path: the Host finds the repository by name, and no path from a
request reaches the filesystem.

## Working on one

The team's clone uses the local repository as `origin`. Fetch, Bring current, Rebase, Push, Merge to
main and Delete remote branch in the Git dialog work as they do for a remote. The default branch is
read from the local repository's `HEAD` and stored, as for a remote; it is never assumed.

Pull requests and contributor mode do not apply to a local repository. Asking for either answers a
sentence saying so, and `GH_TOKEN` is never used.

## Admin → Repositories

Lists each local repository: its name, size on disk, default branch, last commit and the teams
using it. **Delete** asks first. It is refused while any team's repository list names the
repository, and the refusal names those teams.

Creating and deleting a local repository each append a row to the tenant log
(`local-repo.created`, `local-repo.deleted`). The routes are a person's only
(`GET`/`POST /api/local-repos`, `DELETE /api/local-repos/{name}`); a Manager or member credential is
refused, and agents get no tool for them.

## On the volume

Each local repository is a bare repository at `<dataRoot>/repos/<name>.git`. It is owned by the
Host's user (`harness`), readable by the `agent` group and never writable by it. Agents can clone
and fetch from it but never push: when a person pushes or merges, or the platform publishes a card,
the Host fetches the commits into the bare repository as itself. Pushes are never forced. The entrypoint
(`scripts/prepare-volume.sh`) creates `repos` when it is missing and resets these owners and modes
on every start.
