# Releases and updates

How a maintainer updates a local instance from source and cuts a release. Two scripts, both run
from a checkout of `main` on the machine that runs Podman. There is no hosted CI.

## Updating the local instance from source: `scripts/update.ps1`

```powershell
scripts\update.ps1           # waits for running agents
scripts\update.ps1 -Force    # does not wait
```

1. Refuses when the checkout is not on `main` or has uncommitted or untracked changes.
2. `git pull --ff-only`. A local commit that is not on origin stops it here.
3. Lists the agent CLIs (`claude`, `codex`, `copilot`, `grok`, `agy`) running in control and in every worker container every 30 seconds (`-PollSeconds`) and names each one as `team/member`, or `concierge`, until none is left. Replacing the containers kills every agent in them. `-Force` skips the wait. An open Concierge terminal counts as a running agent, so close it or use `-Force`.
4. Runs `scripts/dev-up.ps1 -Yes`, which builds the CLI and the images from this checkout and replaces the containers with `yawble up`.
5. Waits for control's `/healthz` to answer 200 (asked from inside the container), then for each worker to report healthy (connected to control), within 15 minutes in all (`-HealthTimeoutMinutes`), because a first boot installs the agent CLIs. Then it prints what `GET /api/version` reports.

To update an instance installed from a release, use `yawble update` instead (see [cli/README.md](../../cli/README.md#updating-remote-access-uninstalling)).

## Cutting a release: `scripts/release.ps1`

Once, before releasing from a machine:

```powershell
gh auth login                     # if gh is not signed in yet
gh auth refresh -s write:packages # the token pushes the images to ghcr.io
```

Each release, with the instance running (the .NET suite runs inside it):

```powershell
git switch main
git pull --ff-only
scripts\release.ps1 -DryRun   # every suite, then names the version and what it would push; publishes nothing
scripts\release.ps1           # the release
```

Options:

| Option | Effect |
|---|---|
| `-DryRun` | Runs the checks and the suites and names the version; tags and publishes nothing. |
| `-Prerelease` | Publishes the GitHub Release as a pre-release. The installers and `yawble update` skip it unless asked for pre-releases (`--prerelease`). |
| `-NoCache` | Rebuilds the images from scratch: no cached layers, and the base images pulled again. |
| `-SkipTests` | Skips the web, .NET and CLI suites. Everything else is unchanged. |
| `-Container <name>` | The running container the .NET suite runs in. By default `yawble-worker-1` when it is running (the control image carries no .NET SDK), else `yawble`. |

The image build reuses cached layers: the toolchain in the Containerfile's worker stage is built once and reused until it changes, and the version label comes last so that a new version changes nothing above it. The cost is that the images' operating-system packages age between rebuilds; run with `-NoCache` now and then (monthly, or for a security fix).

What it does:

1. Refuses unless the checkout is on `main`, clean, and at the commit `origin` has for `main`, and unless `go` and `sh` are on PATH (the CLI in `cli/` is built with them). On Windows, `sh` comes with Git: put `C:\Program Files\Git\bin` on PATH in the shell that runs the release. Outside a dry run it also checks that `gh` is signed in with `write:packages` and that `podman` answers. When the Podman machine cannot run images for the other platform (linux/arm64 on an amd64 machine, or the reverse), it registers the emulator itself (the registration lasts until the machine restarts), and refuses, naming the command, only if that does not work.
2. Runs the web suite here (`npm ci`, `npm test` in `web/`). Then it copies the commit into the container as a git bundle, clones it into `/tmp/release-<stamp>` with `TMPDIR` set inside that folder, builds `tests/Harness.Tests` and runs the test dll directly. The folder is removed afterwards. Then `go vet` and `go test` in `cli/`. A failure in any suite stops the release. Each suite's per-test results are added to the test history (`scripts/test-history.ps1`).
3. Reads the `v*` tags on origin and takes today's next `yyyy.mm.dd.N`, starting at 1. Today is the local date of the machine it runs on.
4. Makes the annotated tag locally and builds both images from a temporary worktree at that tag, each for linux/amd64 and linux/arm64 as one manifest: `--target control` as `ghcr.io/<owner>/<repo>:<version>` and `--target worker` as `:<version>-worker`. Both get `--build-arg HARNESS_VERSION=<version>` (no `v`) and `--build-arg HARNESS_COMMIT=<full sha of the tag commit>`, the same names `dev-up.ps1` passes. The Containerfile turns those into the `org.opencontainers.image.version` and `.revision` labels; the script adds only `org.opencontainers.image.source`. A failed build deletes the local tag, so nothing has been published yet.
5. Pushes the tag, signs Podman in to ghcr.io with `gh auth token`, and pushes both version tags, then `:latest` and `:latest-worker`, so a failed version push never moves `latest`. The owner and repository come from the `origin` remote, lowercased.
6. Runs `cli/scripts/release.sh <tag>` (with `--prerelease` when asked) from a checkout of the tag, which builds the CLI for every platform with that version, pins the image just pushed, and creates the one GitHub Release for the tag (`--generate-notes`) with the CLI archives and `checksums.txt` attached. The Release is created last so that GitHub's latest Release always has a CLI to download. If this step fails, the images stand and the script names the commands that finish it (see [cli-releases-and-install.md](cli-releases-and-install.md)).

**One version number, one repository.** The images and the CLI are always released together at the same `v<yyyy.mm.dd.N>`: the web app shows that tag beside the logo (the full build version, commit and build time on hover), and `yawble version` prints the same tag.

The `yawble` package on ghcr.io is public, so anyone can pull it. Its visibility is a separate setting from the repository's, under the package's settings on GitHub. For a private fork's package, sign the engine in first:

```powershell
gh auth token | podman login ghcr.io -u <github user> --password-stdin
podman pull ghcr.io/<owner>/<repo>:latest
```

If a step after the tag push fails, the tag is already on origin. Running the script again would cut the next number, so finish that step by hand with the commands the script names, or delete the tag and the Release on GitHub first.

The version arithmetic, the remote parsing and the agent detection are checked offline by `scripts/tests/release-functions.tests.ps1`.
