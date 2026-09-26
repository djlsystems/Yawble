# yawble

The operator CLI for [Yawble](https://github.com/djlsystems/Yawble): install the container engine, pull the image, start and update the instance, diagnose it, and put it on the internet. It is for the person who runs an instance. Agents never touch it; they call the platform's MCP tools.

## Install

```sh
curl -fsSL https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.sh | sh    # Linux, macOS
```

```powershell
irm https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.ps1 | iex          # Windows
```

No GitHub account or token is needed. (Installing from a private fork instead is covered in
[docs/ops/cli-releases-and-install.md](../docs/ops/cli-releases-and-install.md).)

Each script places the binary (`~/.local/bin`, or `%LOCALAPPDATA%\Programs\yawble` and your user Path on Windows), verifies its checksum, and stops. Then:

```
yawble up
```

`up` installs no container engine. It uses what is installed: Podman or Docker, asking which when both are (Podman is recommended; the answer is saved as `engine` in yawble's config, and `yawble config set engine` changes it; `--yes` takes Podman). Before it creates anything it checks the port: when another program holds it (8080 by default), `up` offers the next free one and saves your answer as `port`. With neither, it stops and says where to get one: Podman Desktop (recommended, free for everyone) or Docker Desktop on macOS and Windows, Podman or Docker Engine on Linux, then run `yawble up` again; the install scripts end with the same advice. Podman on Windows needs WSL, and `up` says how to install it (at the computer, then restart) before creating the machine. macOS is Apple silicon only; on a Mac, `install.sh` adds `~/.local/bin` to `~/.zprofile`. With Docker, the limits come from `docker info`. On macOS and Windows it creates the Podman machine (rootless) if there is none, starts it if it is stopped, and offers to make a rootful one rootless, because a rootful machine on Windows never answers on localhost. `--yes` answers every question; without a terminal and without `--yes`, a question is a refusal that names the flag. On Windows, if the WSL virtual machine has less memory than the container limit, `up` says what to put in `.wslconfig` and continues. First installs have been run end to end on Linux (Ubuntu 24.04), on Windows 11 with Podman, and on macOS with both Podman and Docker. On macOS and Windows the container's default memory is half the Podman machine's, capped at 12 GB, and its CPUs the machine's, capped at 8; a new Mac machine is created with half the Mac's RAM, capped at 12 GB.

To build from source instead:

```sh
go build ./cmd/yawble
```

A source build says `no image pinned` in `yawble version`: a release pins the exact Yawble image it was tested with, and a dev build refuses to guess one. Point it at an image with `yawble config set image <reference>`.

## Commands

```
yawble up                 check the engine, machine and port, pull the image, create the volume, start. Idempotent.
yawble down               stop the instance and free its port. The volume is kept.
yawble status             running or not, URL, versions, tunnel URL, one line per agent
yawble doctor [--fix]     every check, pass or fail with the fix spelled out. Exit 1 on any failure.
yawble update             a newer yawble when there is one, then the instance onto its image. --cli / --instance do one half.
yawble logs [-f]          the container log
yawble agents             per agent: installed, signed in, and how to sign in if not
yawble remote enable <cloudflare|tailscale|ngrok> | disable | status
yawble config get|set     port, engine, memory, cpus, running limit, image
yawble secret set|list|unset   GH_TOKEN and provider API keys for the instance; values are never shown
yawble github             guided GitHub token setup: the gh login or a pasted fine-grained token, checked with GitHub
yawble uninstall          removes the instance and yawble's settings. The volume only with --data and a typed confirmation.
yawble version
```

Tab completion for a shell is there but not listed in `yawble --help`: `yawble completion powershell | Out-String | Invoke-Expression` turns it on in PowerShell (add that line to `$PROFILE` to keep it), and `yawble completion --help` shows bash, zsh and fish.

The engine is the one configured, else the one installed (asked when both are); `config set engine podman|docker` sets it.

## Secrets: GitHub and API keys

The first `yawble up` at a terminal asks whether your teams will use GitHub and, if so, sets up `GH_TOKEN`: your GitHub CLI login (`gh auth token`) when `gh` is installed and you choose it, or a fine-grained token you create (the page and the permissions to pick are shown) and paste at a hidden prompt. The token is checked with GitHub before it is saved. It asks once; `yawble github` runs the same setup any time. Only teams that clone private repos, push, or open pull requests need it.

When `up` restarts a running instance to apply a change (a new secret, image or limit), it does not ask: running `up` is the instruction. It names what the restart stops, each team member's run as `Team/Member (cli)` and each open Concierge session, before it replaces the container.


The instance reads its credentials from its environment: `GH_TOKEN` for GitHub (cloning, pushing, pull requests), and `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `XAI_API_KEY` for agents that sign in with a key rather than a saved login. Set them with `yawble secret`, then run `yawble up`, which hands them to the container and recreates it when they change (the data volume is kept):

```sh
gh auth token | yawble secret set GH_TOKEN      # piped: never on screen, never in shell history
yawble secret set ANTHROPIC_API_KEY             # asks, typing hidden
yawble secret list                              # names only, never values
yawble secret unset XAI_API_KEY
yawble up
```

`NAME=value` on the command line is accepted with a warning, because the shell keeps it in its history. Names starting with `HARNESS_` are refused (the platform's own credentials never come from this file). The values live in `env` in yawble's config folder (`%AppData%\yawble` on Windows, `~/Library/Application Support/yawble` on macOS, `~/.config/yawble` on Linux), readable only by you. For `GH_TOKEN` a fine-grained token with read and write on Contents and Pull requests for the repositories your teams use is safer than a `gh` login token, which ends when you log out of `gh`. `yawble doctor` asks GitHub whether `GH_TOKEN` still works and says so when GitHub rejects it. A sign-in done inside the product (an agent's own login in a Concierge terminal) is kept on the data volume and needs no secret here.

## Updating, remote access, uninstalling

`yawble update` updates both halves. It looks for a newer yawble release; when there is one it downloads it, verifies its checksum against the release's `checksums.txt`, replaces the binary (on Windows the old file is moved aside and swept on the next run), and runs the new yawble to move the instance, because each yawble pins the Yawble image it was released with. Moving the instance pulls that image and recreates the container on the same volume; the Host migrates its database on start, so nothing is lost. When this yawble is already the latest, or the release cannot be read, `update` moves the instance to the image this build pins (or the one in config). If the new image's Host refuses the volume as written by a newer build, `update` says so in the Host's words with the two ways out. `yawble update --cli` replaces the binary only; `yawble update --instance` moves the instance only and never looks for a release.

`yawble remote enable <cloudflare|tailscale|ngrok>` runs a tunnel as a second container beside the Host. It makes only outbound connections, starts and stops with the instance, and nothing is installed on this machine or opened on your router.

- **cloudflare**: with no token, a quick tunnel and a random `trycloudflare.com` name that changes on every start; with `--token` from a tunnel you created in the Cloudflare dashboard, a stable hostname configured there. Free.
- **tailscale**: `--token tskey-auth-...`; the instance joins your tailnet as `yawble` and is reachable over HTTPS from devices signed into it. `--funnel` opens it to the public internet.
- **ngrok**: `--token <authtoken>`, optionally `--domain <static domain>`. The free tier shows visitors an interstitial page.

`remote status` prints the provider, the sidecar's state and the URL; `doctor` has a `remote` row; `remote disable` removes the sidecar and keeps the credential, `--forget` removes both. The credential lives in `remote.toml` beside the config, owner-only, and reaches the sidecar as an environment variable, never as a command argument. The Cloudflare quick tunnel has been run for real; Tailscale and ngrok are pinned by tests of their command lines.

`yawble uninstall` asks first, then removes the tunnel, the container, the pod (or network), the image and yawble's settings: the engine and port choices, the API keys and tokens saved with `secret set`, and remote-access credentials. `--keep-settings` keeps the settings, for a reinstall or a switch of engine; `--yes` answers the question for you. The data volume, with every team, account, document and saved agent login, goes only with `--data` after you type the word `delete` at a terminal, or with `--data --yes` from a script. At the end it prints the command that removes the `yawble` program itself.

`up` opens the board in your browser when run from a terminal; `--no-browser` does not.

Conventions: `--json` on `status`, `doctor`, `agents`, `config get` and `version`; exit 0 on success, 1 when the thing failed, 2 when the invocation was wrong; `YAWBLE_*` environment variables override the config file; no prompts when stdin is not a terminal; no colour.

`doctor` prints one line per check. The verdict words are `ok`, `warn`, `FAIL` and `skip`; `skip` means the check could not be measured (a stopped instance has no health to check) and is not a failure. Exit 1 when anything FAILs. The in-container half runs the Host's own `--doctor` switch through `podman exec` (or `docker exec`), so those checks are computed by the platform and only rendered here. `--fix` starts a stopped Podman machine (with Podman as the engine), creates a missing data volume and starts a stopped container; it never removes anything. After starting a machine it checks again and stops there, so a second `--fix` may be needed for the container.

```
ok    machine        podman-machine-default, 10 CPUs, 15688 MB
ok    rootless       the machine is rootless
ok    machine memory 15688 MB, above the container limit of 8192 MB
ok    wsl            WSL answers
ok    engine         podman 6.0.2
ok    image          localhost/yawble:doctor present
ok    volume         yawble-data
ok    container      running
ok    health         http://127.0.0.1:18080/healthz answers
skip  port           measured only while the instance is stopped
warn  path           C:\src\yawble is not on PATH
                     fix: [Environment]::SetEnvironmentVariable("Path", "$env:Path;C:\src\yawble", "User")   (PowerShell; then open a new terminal)
skip  release        newer yawble releases are not checked in this build
ok    data root      /data, 915 GB free, writable
ok    database       schema accepted (9 steps)
warn  backups        no daily backup yet (the Host writes one a day into /data/backups)
warn  agents         claude signed in · codex NOT signed in · copilot not measured · grok signed in · agy not installed
                     fix: yawble agents
```

The first four rows appear only with Podman on macOS and Windows, where it runs in a machine; on Linux, and with Docker, the list starts at `engine`.

## Where things live

- Settings: `~/.config/yawble/config.toml` on Linux, `~/Library/Application Support/yawble` on macOS, `%APPDATA%\yawble` on Windows. `yawble config` reads and writes it.
- Provider keys the instance should receive: a file named `env` beside the config, one `NAME=value` per line, passed to the container at `up`.
- Everything else is state the container engine holds: pod `yawble` (a network of that name with Docker), volume `yawble-data` mounted at `/data`, container `yawble`.

## Releasing

`docs/ops/cli-releases-and-install.md` at the repository root. Image and CLI carry one version number, `v<yyyy.mm.dd.N>`, and one tag: the root's `scripts/release.ps1` pushes the image, then runs `cli/scripts/release.sh` at the same tag, which cross-compiles, checksums and publishes the one GitHub Release, pinned to that image.
