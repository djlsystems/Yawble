# Releases and updates

Two scripts, both run from a checkout of `main` on the machine that runs Podman. There is no hosted CI.

## Updating the local instance: `scripts/update.ps1`

```powershell
scripts\update.ps1           # waits for running agents
scripts\update.ps1 -Force    # does not wait
```

1. Refuses when the checkout is not on `main` or has uncommitted or untracked changes.
2. `git pull --ff-only`. A local commit that is not on origin stops it here.
3. Lists the agent CLIs (`claude`, `codex`, `copilot`, `grok`, `agy`) running in the container every 30 seconds and names each one as `team/member`, or `concierge`, until none is left. Replacing the container kills every agent in it. `-Force` skips the wait. An open Concierge terminal counts as a running agent, so close it or use `-Force`.
4. Runs `scripts/dev-up.ps1 -Yes`, which builds the CLI and an image from this checkout and replaces the container with `yawble up`.
5. Waits for `/healthz` to answer 200 (asked from inside the container, up to 15 minutes, because a first boot installs the CLIs), then prints what `GET /api/version` reports.

## Cutting a release: `scripts/release.ps1`

The release is the owner's act. Once, before the first release:

```powershell
gh auth login                     # if gh is not signed in yet
gh auth refresh -s write:packages # the token pushes the image to ghcr.io
```

Each release, with the container running (the .NET suite runs inside it):

```powershell
git switch main
git pull --ff-only
scripts\release.ps1 -DryRun   # both suites, then names the version; publishes nothing
scripts\release.ps1
```

What it does:

1. Refuses unless the checkout is on `main`, clean, and at the commit `origin` has for `main`, and unless `go` and `sh` are on PATH (the CLI in `cli/` is built with them).
2. Runs the web suite here (`npm ci`, `npm test` in `web/`). Then it copies the commit into the container as a git bundle, clones it into `/tmp/release-<stamp>` with `TMPDIR` set inside that folder, builds `tests/Harness.Tests` and runs the test dll directly. The folder is removed afterwards. Then `go vet` and `go test` in `cli/`. A failure in any suite stops the release.
3. Reads the `v*` tags on origin and takes today's next `yyyy.mm.dd.N`, starting at 1. Today is the local date of the machine it runs on.
4. Makes the annotated tag locally and builds the image from a temporary worktree at that tag, with `--build-arg HARNESS_VERSION=<version>` (no `v`) and `--build-arg HARNESS_COMMIT=<full sha of the tag commit>`, the same names `dev-up.ps1` passes. The Containerfile turns those into the `org.opencontainers.image.version` and `.revision` labels; the script adds only `org.opencontainers.image.source`. A failed build deletes the local tag, so nothing has been published yet.
5. Pushes the tag, signs Podman in to ghcr.io with `gh auth token`, and pushes `ghcr.io/<owner>/<repo>:<version>` and `:latest`. The owner and repository come from the `origin` remote, lowercased.
6. Runs `cli/scripts/release.sh <tag>`, which builds the CLI for every platform with that version, pins the image just pushed, and creates the one GitHub Release for the tag (`--generate-notes`) with the CLI archives and `checksums.txt` attached. The Release is created last so that GitHub's latest Release always has a CLI to download. If this step fails, the image stands and the script names the command that finishes it (see [cli-releases-and-install.md](cli-releases-and-install.md)).

**One version number, one repository.** Image and CLI are always released together at the same `v<yyyy.mm.dd.N>`: the web portal shows that tag beside the logo (the full build version, commit and build time on hover), and `yawble version` prints the same tag.

The package's visibility is a separate setting on GitHub, under the package's settings. To fetch a private image:

```powershell
gh auth token | podman login ghcr.io -u <github user> --password-stdin
podman pull ghcr.io/<owner>/<repo>:latest
```

If a step after the tag push fails, the tag is already on origin. Re-running would cut the next number, so finish that step by hand with the commands the script names, or delete the tag and the Release on GitHub first.

The version arithmetic, the remote parsing and the agent detection are checked offline by `scripts/tests/release-functions.tests.ps1`.
