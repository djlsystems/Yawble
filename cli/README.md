# yawble

The operator CLI for [Yawble](https://github.com/djlsystems/Yawble): install the container engine, pull the image, start and update the instance, diagnose it, and put it on the internet. It is for the person who runs an instance. Agents never touch it; they call the platform's MCP tools.

## Install

```sh
curl -fsSL https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.sh | sh    # Linux, macOS
```

```powershell
irm https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.ps1 | iex          # Windows
```

These install the newest **regular** release. To take the newest release, a pre-release included:

```sh
curl -fsSL https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.sh | sh -s -- --prerelease
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.ps1))) -Prerelease
```

While Yawble publishes pre-releases only, the plain form says no release is available and prints
the `--prerelease` form to run instead.

No GitHub account or token is needed. (Installing from a private fork instead is covered in
[docs/ops/cli-releases-and-install.md](../docs/ops/cli-releases-and-install.md).)

Each script places the binary (`~/.local/bin`, or `%LOCALAPPDATA%\Programs\yawble` and your user Path on Windows), verifies its checksum, and stops. Then:

```
yawble up
```

`up` installs no container engine. It uses what is installed: Podman or Docker, asking which when both are (Podman is recommended; the answer is saved as `engine` in yawble's config, and `yawble config set engine` changes it; `--yes` takes Podman). Before it creates anything it checks the port: when another program holds it (8080 by default), `up` offers the next free one and saves your answer as `port`. With neither, it stops and says where to get one: Podman Desktop (recommended, free for everyone) or Docker Desktop on macOS and Windows, Podman or Docker Engine on Linux, then run `yawble up` again; the install scripts end with the same advice. Podman on Windows needs WSL, and `up` says how to install it (at the computer, then restart) before creating the machine. macOS is Apple silicon only for now (Intel Macs are not tested yet); on a Mac, `install.sh` adds `~/.local/bin` to `~/.zprofile`. On macOS and Windows it creates the Podman machine (rootless) if there is none, starts it if it is stopped, and offers to make a rootful one rootless, because a rootful machine on Windows never answers on localhost: on Windows a no stops `up` before anything is created, on macOS it carries on with a note. `--yes` answers every question; without a terminal and without `--yes`, a question is a refusal that names the flag. On Windows, if the WSL virtual machine has less memory than the container limit, `up` says what to put in `.wslconfig` and continues. First installs have been run end to end on Linux (Ubuntu 24.04), on Windows 11 with Podman, and on macOS with both Podman and Docker. A new Mac machine is created with half the Mac's RAM, capped at 12 GB.

### Control and workers

An instance is one **control** container, `yawble`, which serves the board on the published port and keeps the database, and one or more **worker** containers, `yawble-worker-1` … `yawble-worker-N`, which run the agents. All of them mount the same data volume. On Podman they share one pod; on Docker each worker joins control's network namespace (`--network container:yawble`), so control is `127.0.0.1:8080` in every container. Control runs the release's image (`…/yawble:<version>`); workers run its worker image (`…/yawble:<version>-worker`), or `workerImage` in yawble's config. A key that control and the workers share is made once by the first `up`, kept owner-only in `worker.env` beside yawble's config, handed to the containers only as an env file, and removed by `uninstall`.

`yawble workers <n>` sets the number of workers and applies it at once, touching only worker containers: a new worker is started and waited for until it connects to control. A worker that is removed (the highest first) is told to take no new run and stopped once its runs end; `--now` stops it at once, naming the runs that then fail as worker-lost (the Manager re-sends each once), after a question `--yes` answers. `yawble workers` with no number prints each worker's state. `status` and `doctor` show each worker with control's record of it, and `yawble logs worker-2` (or `logs 2`) reads a worker's log.

An instance from before control and workers is moved by `yawble update` (or `up`): its container becomes control and worker 1 joins it, on the same volume, with no new sign-in and no change to its data or settings.

### What the first `up` asks: each worker's memory and CPUs

`memory` and `cpus` are each worker's. Control has its own fixed allowance beside them (1536 MB and 2 CPUs), so a worker may have at most the engine's memory less control's. `up` and `yawble workers <n>` refuse two or more workers whose memory with control's is more than the engine has, naming the three figures; with one worker (and on an upgrade) that is a warning, never a refusal. CPUs over the engine are only warned about: a CPU limit is a ceiling the containers share.

A worker's size comes from what the engine has, asked of the engine itself: `docker info` (`MemTotal`, `NCPU`) under Docker on every OS, which is Docker Desktop's VM on macOS and Windows; the Podman machine on macOS and Windows; this computer under Podman on Linux. One rule everywhere: memory is half of the engine's, capped at 12 GB and at what is left beside control, and CPUs are the engine's, capped at 8. When the engine cannot answer, nothing is estimated: `up` says the memory and CPUs were not measured and uses 8192m and 4 CPUs until you choose (`yawble config set memory|cpus`), and saves nothing.

When yawble's config holds neither `memory` nor `cpus`, the first `up` at a terminal shows one short screen and asks for each, Enter accepting the proposal:

```
How much of this computer should Yawble's worker get? (asked once)
  available        12288 MB memory and 10 CPUs: Docker Desktop's share of this computer, not all of it
                   To give Docker Desktop more: Docker Desktop's Settings > Resources
  control takes    1536 MB memory, 2 CPUs (fixed), so a worker may have up to 10752 MB
  proposed         6144 MB memory, 8 CPUs (half the memory available, up to 12 GB; the CPUs, up to 8)
Up to 3 agent runs can work at once, because each run is given 2048 MB of memory and the worker has 6144 MB.
Press Enter to accept a value, or type another.
Memory in MB (4096 to 10752) [6144]:
CPUs (1 to 10) [8]:
saved memory 6144m and cpus 8 in yawble's config (yawble config set memory <size> and yawble config set cpus <n> change them, then yawble up)
Up to 3 agent runs can work at once, because each run is given 2048 MB of memory and the worker has 6144 MB.
```

Memory takes megabytes (`8192`) or a size (`8g`). A value above what the engine has beside control is refused with the maximum named, and so is memory below the 4 GB floor (the Host's own share beside one run) and fewer than 1 CPU. An engine under 8 GB proposes less than 4 GB, and then the floor is that proposal, so the default in brackets is always inside the range the prompt states; then it asks again. A refusal for being above the engine's figure also says how to give that engine more:

- Podman machine (macOS, Windows): `To give the Podman machine more: podman machine stop, then podman machine set --memory <MB> --cpus <n>, then podman machine start`
- Docker Desktop: `To give Docker Desktop more: Docker Desktop's Settings > Resources`
- Linux, no VM (Docker or Podman): `On Linux there is no VM: this computer's own RAM and CPUs are the limit, and there is nothing to enlarge`

(Docker in some other VM, such as Colima, is told to change it in the tool that runs that VM.) `yawble doctor`'s `capacity` warning and a later `up`'s warning give the same hint when a saved value is more than the engine has. The figures are named as whose share of this computer they are (the Podman machine's, Docker Desktop's, or on Linux all of it), with where to give it more on the line below - on the screen, with `--yes`, and in a refusal. How many agent runs can work at once is the only figure yawble derives itself: the Host's own rule at its default allowance of 2048 MB a run, said in one sentence with its reason, and the started line repeats that number. It is not asked; `yawble config set maxRunning` overrides it, and `yawble doctor --details` reports the limit the Host actually applies.

With `--yes`, or without a terminal, `up` takes the proposal without asking and prints what it chose and how to change it. Either way the answer is saved, so later `up`s do not ask; `yawble config set memory <size>` and `yawble config set cpus <n>`, then `yawble up`, change it. A saved value above what the engine now has (a smaller Docker Desktop VM, say) is warned about on every `up`, with the largest value that fits and how to give the engine more. `yawble restore` does not ask; it uses what is saved, or the proposal.

To build from source instead:

```sh
go build ./cmd/yawble
```

A source build says `no image pinned` in `yawble version`: a release pins the exact Yawble image it was tested with, and a dev build refuses to guess one. Point it at an image with `yawble config set image <reference>`.

## Commands

```
yawble up                 check the engine, machine and port, pull the images, create the volume, start control and the workers. Idempotent.
yawble down               stop the workers and control and free the port. The volume is kept.
yawble status             running or not, URL, versions, tunnel URL, one line per worker with control's record of it
yawble workers [<n>] [--now] [--yes]   the number of worker containers; with a number, set it and apply it
yawble doctor [--fix]     every check, pass or fail with the fix spelled out. Exit 1 on any failure.
yawble update             a newer yawble when there is one, then the instance onto its image. --cli / --instance do one half.
yawble logs [control|worker-<n>|<n>] [-f]   control's log, or a worker's
yawble backup [--output <file>] [--full] [--yes]   the instance's data in one .tar.gz on this computer, to restore here or elsewhere
yawble restore <file> [--replace] [--yes]         a backup into this computer's instance, on either engine, then start it
yawble agents [--details]  per agent: installed, signed in, launches, and what to do if not; --details adds its source and the launch's own words
yawble agents credential set <preset|command> [--api-key|--token] | clear <preset|command>   the issued credential of a CLI command; see below
yawble agents source <preset> home|issued   whether a preset signs in through the shared home or its command's issued credential
yawble remote enable <cloudflare|tailscale|ngrok> | disable | status
yawble config get|set     port, engine, memory and cpus (each worker's), running limit, image, workers, workerImage
yawble secret set|list|unset   GH_TOKEN and provider API keys for the instance; values are never shown
yawble plugin install <folder> | --from-instance <path> [--force] | list | remove <id> [--version <v>]   plugins members can be hired on; see below
yawble solution check <folder> | --from-instance <path> [--json]   what installing a solution package would create, or its problems; see below
yawble solution install <folder> [--team <name>] [--from-instance] [--yes]   install a solution package as a team, or update one from an earlier version; see below
yawble connect <provider> [--scopes …] [--name …] [--port …] | list | remove <name>   connect an OAuth account (Google, Microsoft, custom) for plugins; see below
yawble repo list | clone <name> [folder]   the instance's local repositories; clone one onto this computer (no pushing back)
yawble github             guided GitHub token setup: the gh login or a pasted fine-grained token, checked with GitHub
yawble uninstall          removes the instance and yawble's settings. The volume only with --data and a typed confirmation.
yawble version
```

Tab completion for a shell is there but not listed in `yawble --help`: `yawble completion powershell | Out-String | Invoke-Expression` turns it on in PowerShell (add that line to `$PROFILE` to keep it), and `yawble completion --help` shows bash, zsh and fish.

The engine is the one configured, else the one installed (asked when both are); `config set engine podman|docker` sets it.

## Connections

A connection is an account at an OAuth service that plugin members act on; the Host keeps its
refresh token and hands a plugin only a fresh access token per run (`docs/connections.md` at the
repository root). The web UI connects one from Admin → Connections; `connect` does it from this
computer, for an instance the provider will not redirect to (a tunnel, a private address):

```
yawble connect google --scopes https://mail.google.com/ --name "Work mail"
yawble connect list
yawble connect remove "Work mail"
```

`connect` asks the Host to start the flow, listens on `http://127.0.0.1:<free port>` (`--port` to
choose it; Microsoft is sent it as `http://localhost:<port>/`, the loopback form Entra takes), opens the browser at the provider's consent page, catches the code and hands it to the
Host, which does the exchange: the client secret never leaves the Host and no token reaches this
computer. `--name` of an existing connection of that provider reconnects it. Like
`plugin install --from-instance`, it reaches the Host through the container engine with a request
file the Host answers (`/data/connections/.connect`, harness-only), so nothing is signed in. The
provider's client is set up first in Admin → Connections.

## Plugins

A plugin is a program a team member runs instead of an agent CLI (`docs/plugins.md` at the repository root). Build one version into a folder outside the repository, then:

```
yawble plugin install ~/plugins-build/sample-echo/0.1.0
```

That is the whole install. The manifest is checked with the Host's rules before anything is copied, and a bad field is named. The folder is copied to `/data/plugins/<id>/<version>/` through the engine, `/data/plugins` is created if it is missing, and ownership and modes are set: `harness:agent`, directories `0750`, files `0640`, and the manifest's executable `0750`. The executable bit is set even when the folder came from Windows without one. `active` is pointed at the version, and earlier versions are kept. The Host then rescans with no restart and no API key, and the command prints its verdict: installed, or refused with the reason. An existing version is refused unless `--force` is given.

**A plugin built inside the instance** (by a team in its worktree, or by the Concierge in its workspace) is installed where it is, with no copy to this computer and back:

```
yawble plugin install --from-instance /data/teams/acme/repos/Tools/main/build/sample-echo-go/0.1.0
```

`<path>` is the folder's absolute path inside the container. The CLI does not install it itself: it writes the request to `/data/plugins/.install` through the engine, and the running Host installs the folder with the same installer as Admin → Plugins → "Install from a folder…", then answers in `/data/plugins/.install-report.json`. So the checks and their sentences are the Host's: it refuses, before anything is written, a folder outside the data root (`/data`), one reached through a symlink out of it, one holding a symlink that leaves the folder, and a manifest it would refuse; an installed version is refused unless `--force` is given. Otherwise the folder is laid out with the modes above and made active, and the command prints the Host's verdict (exit 1 on a refusal). A Host that does not answer within 60 seconds (an image from before this option) is reported, and the request is withdrawn.

**Language, and what a plugin may rely on.** Go is the default for connectors to REST APIs, clouds, databases, queues and mail (template: `samples/plugins/sample-echo-go`, one static binary per processor). Use .NET when the best SDK for the target system is .NET (template: `samples/plugins/sample-echo`), and Python when the library exists only in Python. A plugin is self-contained: the image guarantees the .NET runtime, Node and Python 3, and everything else the plugin needs is in its own folder (Go libraries compiled in, NuGet packages published beside the build, Python packages in a virtual environment in the folder). Nothing is ever added to the image for a plugin. The manifest's `requires` names the runtimes it needs from the image. `docs/plugins.md` has the details.

`yawble plugin list` shows each version, active or not, and installed or refused (`--json` too). `yawble plugin remove <id>` asks first (`--yes` answers) and refuses while a member is hired on the plugin, naming the members. `--version <v>` removes one kept version; the active one cannot be removed while others are kept.

## Solution packages

A solution package is a whole working team in one folder: `solution.json` beside its plugins, skills, sites and tools (`docs/solutions.md` at the repository root). Before installing one, check it:

```
yawble solution check samples/solutions/job-tracker
```

The folder is copied into a temporary folder in the container, the running Host checks it with the rules `POST /api/solutions/check` applies (through its own `--solution-check` switch, the way `doctor` reads `--doctor`), and the copy is removed. Nothing is installed and nothing is written. A package that passes is printed as its install would create it: the team, each member, each trigger with its schedule or event, the member it wakes, whether it wakes the Manager, its daily cap and its **whole instruction**, the team skills, the sites, the tools and what you will be asked for. One that does not is printed as its problems, each naming the file and the field (`solution.json triggers[1].member: ...`), and the command exits 1. `--json` prints the Host's answer as it is.

A package already inside the instance (a team wrote it to its documents) is checked where it is, with nothing copied: `yawble solution check --from-instance /data/documents/<team>/job-tracker-1.0.0`.

Then install it:

```
yawble solution install samples/solutions/job-tracker
yawble solution install ./job-tracker-1.1.0 --team "Job Tracker"
```

The folder is copied to `/data/plugins/.solutions/<nonce>/<folder name>/` (`harness:agent`, directories `0750`, files `0640`), and the CLI asks the running Host through a request file, `/data/plugins/.solution`, answered in `/data/plugins/.solution-report.json` with what the matching route (`POST /api/solutions/preview`, `install`, `update`) answers. First the plan is printed as `solution check` prints it. Then the questions the web wizard asks: the team's name (`Team name [Job Tracker]:`, asked again while the Host refuses it), each setting only a person provides (its description, type and default; blank keeps the default; a list is comma-separated, a yes/no is `y` or `n`), a connection for each connection slot chosen by number from the instance's connections, and one file from this computer for each document input. Blank skips; a required input skipped leaves the team blocked until it is provided, and the command says so. After `Install <name> <version> as team <name>? [y/N]` (`--yes` answers it) the chosen files are copied to `/data/plugins/.solutions/<nonce>/.documents/<input folder>/`, the Host installs step by step and each step is printed done. A step that fails is printed as `Failed at step <n> (<title>): <reason>. Nothing was left behind.`, and the command exits 1. What is still missing is printed next, as the team board's `Blocked: waiting for ...` lines. Under `Schedules:` each schedule's first run is named: `Scan for postings ran now.` for one with `runAtInstall` (the install fired it once, then it runs on its schedule), or `Morning summary first runs at 8:00 AM.`; a first run at install that did not happen says why and when it first runs instead, and does not fail the install. The plan says such a schedule `runs once now, then every ...`. The secrets the package's plugin members bind are printed before the confirmation and again in the result, each by key name - never a value - as `set on this Host`, `not set - its source fails until it is set. Set it with: yawble secret set <KEY> (it prompts for the value), then yawble up to restart the Host`, or `not needed` for a source left off; the result ends with the keys still unset. An unset key does not stop the install. The staged copy is always removed.

`--team` naming a team installed from an earlier version of the same package updates it: the changes (members, plugins, triggers, skills, sites, tools added, changed and removed) are printed with the versions from and to, and the team keeps its settings, bindings, documents and site data. `--from-instance <path>` installs a package already inside the instance where it is. An image that does not answer (from before this command) is reported and the request withdrawn: run `yawble update`.


A local repository is a git repository that lives only on the instance, as a bare repository at `/data/repos/<name>.git` on the volume, with no hosting service. A team works on it as `local:<name>` ([docs/local-repositories.md](../docs/local-repositories.md)). People create, attach and delete them in the web app. The CLI gets the code out:

```
yawble repo list [--json]
yawble repo clone my-plugin [~/code/my-plugin]
```

`list` shows each one: name, `local:<name>`, default branch, last commit and size. `clone` streams `/data/repos/<name>.git` out of the running instance through the engine (`exec ... tar`, Podman or Docker) and clones it with this computer's git into the folder (default `./<name>`). Every branch becomes a local branch and the default branch is checked out. The clone has no remote, because pushing back is not supported. A folder that already exists is refused and left alone. An illegal name (the Host's rule: 1 to 100 letters, digits, `.`, `_` or `-`, starting with a letter or digit, not ending in `.git` or `.lock`, with no `..`) or one the instance does not have is refused and named. Size is the total length of the repository's files, as Admin → Repositories shows it.

## Backup and restore

`yawble backup` writes the instance's data to one `.tar.gz` on this computer: by default `yawble-backup-<yyyyMMdd-HHmmss>.tar.gz` in the current folder, or the file named with `--output`. A short-lived container from the instance's own image reads the volume read-only, so it works the same on Podman and Docker and pulls nothing. A running instance is stopped while the archive is written and started again, and `backup` says so. When agents are running it names them, as `up` does, and asks first (`--yes` answers). A stopped instance stays stopped.

By default it leaves out only what the instance reinstalls at its next start: the package caches (`npm-cache`, `pip-cache`, `go-cache`, `nuget`), the headless browser (`ms-playwright`), and the Claude, Codex, Copilot and Grok programs. Agent logins and settings are kept. `--full` keeps everything. The archive starts with `manifest.json`: versions, image, engine, processor type, creation time, database schema steps and byte counts. The file holds secrets (sign-in keys, agent logins, tokens in clones), so it is written readable by you only, and `backup` says so.

`yawble restore <file>` puts a backup into the instance on this computer and engine, whichever made it: Podman to Docker, or one computer to another. It restores into an empty volume without asking. If the volume already holds data, it refuses unless `--replace` is given. `--replace` asks you to type the word `replace` (or use `--replace --yes` from a script), and first writes a backup of the current volume beside the file, naming it. A backup from a newer Yawble is refused with both versions named: run `yawble update` first. After restoring, it starts the instance as `up` does and waits for it to answer. It ends by listing the providers whose keys the backed-up instance used, never their values.

yawble's own settings are not in a backup. On a new computer, choose the engine and port again and set those keys again with `yawble secret set`. `yawble doctor`'s one `backups` line tells the Host's daily copies in the data volume, which are lost if the volume is lost, from the newest backup written on this computer and its age; with no backup on this computer it warns and names `yawble backup`. More in [docs/ops/backups-logs-and-versions.md](../docs/ops/backups-logs-and-versions.md).

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

## Issued agent credentials

Besides signing in through the shared home, a preset can sign in with a credential issued to it. The credential is stored once per CLI command (`claude`, `codex`, `grok`, `copilot`) and shared by every preset that runs that command; whether a preset uses it is the preset's own source, `home` (the default) or `issued`:

```sh
yawble agents credential set claude-headless               # asks, typing hidden; stored for claude
printf '%s\n' "$CODEX_KEY" | yawble agents credential set codex   # the first line of stdin
yawble agents credential set claude --token                # claude takes an API key or a token
yawble agents source claude-headless issued                # this preset now uses it; claude is unchanged
yawble agents credential clear claude                      # every preset that runs claude loses it
```

The value is never taken from the command line: a second argument is refused and nothing is sent. A name must be plain - letters, digits, `-`, `_` and `.`, at most 64 characters - for `credential set`, `credential clear` and `source`; anything else may be a value pasted in the wrong place, so it is refused before a value is read or anything is sent, and the refusal does not repeat it. The value travels to the instance on stdin, into a request file only the Host can read, which the Host deletes before acting; yawble waits 60 seconds for the answer and withdraws the request if none comes. When the Host cannot answer a request safely it deletes it unanswered. If its folder is still its own it also says why, and yawble prints that reason and exits 1. If not, it writes nothing: at the deadline the request is already gone, and yawble says the Host took it and deleted it without acting, that nothing was stored, and that the Host's log says why (exit 1). Only a request still there to withdraw is reported as unanswered. A preset or command the Host does not know is answered with its sentence, "No agent preset or command by that name." Nothing prints the value: the answer is the command, and who set it and when (`operator` for this CLI). A CLI that takes one kind needs no flag: codex and grok take an API key, Copilot a token (`COPILOT_GITHUB_TOKEN`). Claude takes either, so name it: `--api-key` (`ANTHROPIC_API_KEY`) or `--token` (`CLAUDE_CODE_OAUTH_TOKEN`). The Host refuses a kind the CLI does not declare, and yawble shows its sentence as it is. `yawble agents --details` and `yawble doctor --details` show each preset's source, `home` or `issued (set / NOT set)` (an `issued` source with nothing set shows without `--details` too, since it is why the row warns); under `issued` with nothing set, the hint is `yawble agents credential set <preset>`, and that preset's member runs do not start until it is set. Copilot refuses to start when `COPILOT_GITHUB_TOKEN`, `GH_TOKEN` or `GITHUB_TOKEN` holds a classic `ghp_` token - the team's git `GH_TOKEN` included - so the Host refuses a classic token for Copilot; use a fine-grained one, for git too.

You are responsible for your provider's terms when one credential is used by many runs.

## Updating, remote access, uninstalling

`yawble update` updates both halves. It looks for a newer yawble release; when there is one it downloads it, verifies its checksum against the release's `checksums.txt`, replaces the binary (on Windows the old file is moved aside and swept on the next run), and runs the new yawble to move the instance, because each yawble pins the Yawble image it was released with. Moving the instance pulls that image and recreates the container on the same volume; the Host migrates its database on start, so nothing is lost. When this yawble is already the latest, or the release cannot be read, `update` moves the instance to the image this build pins (or the one in config). If the new image's Host refuses the volume as written by a newer build, `update` says so in the Host's words with the two ways out. `yawble update --cli` replaces the binary only; `yawble update --instance` moves the instance only and never looks for a release.

`yawble remote enable <cloudflare|tailscale|ngrok>` runs a tunnel as a second container beside the Host. It makes only outbound connections, starts and stops with the instance, and nothing is installed on this machine or opened on your router.

- **cloudflare**: with no token, a quick tunnel and a random `trycloudflare.com` name that changes on every start; with `--token` from a tunnel you created in the Cloudflare dashboard, a stable hostname configured there. Free.
- **tailscale**: `--token tskey-auth-...`; the instance joins your tailnet as `yawble` and is reachable over HTTPS from devices signed into it. `--funnel` opens it to the public internet.
- **ngrok**: `--token <authtoken>`, optionally `--domain <static domain>`. The free tier shows visitors an interstitial page.

`remote status` prints the provider, the sidecar's state and the URL; `doctor` has a `remote` row; `remote disable` removes the sidecar and keeps the credential, `--forget` removes both. The credential lives in `remote.toml` beside the config, owner-only, and reaches the sidecar as an environment variable, never as a command argument. The Cloudflare quick tunnel has been run for real; Tailscale and ngrok are pinned by tests of their command lines.

`yawble uninstall` asks first, then removes the tunnel, the container, the pod (or network), the image and yawble's settings from every engine installed, Podman and Docker, naming the engine on each line and saying when one holds nothing: the engine and port choices, the API keys and tokens saved with `secret set`, and remote-access credentials. `--keep-settings` keeps the settings, for a reinstall or a switch of engine; `--yes` answers the question for you. The data volume, with every team, account, document and saved agent login, goes only with `--data` after you type the word `delete` at a terminal, or with `--data --yes` from a script. At the end it prints the command that removes the `yawble` program itself.

`up` opens the board in your browser when run from a terminal; `--no-browser` does not.

Conventions: `--json` on `status`, `doctor`, `agents`, `config get` and `version`; exit 0 on success, 1 when the thing failed, 2 when the invocation was wrong; `YAWBLE_*` environment variables override the config file; no prompts when stdin is not a terminal; no colour, except the Yawble mark that `yawble`, `yawble version` and `yawble up` draw in orange for a terminal (`NO_COLOR` turns the colour off; a pipe or `--json` gets no mark at all).

`doctor` prints one line per check. By default it lists whether the instance runs, whether an agent can work in it and whether it is backed up, and every check that warns or fails, then counts the rest; `--details` lists every check and, under them, what each agent's tools would load. The verdict words are `ok`, `warn`, `FAIL`, `skip` and `info`; `skip` means the check could not be measured (a stopped instance has no health to check) and is not a failure, and `info` (a figure such as `capacity`) only informs. With one agent signed in and launching, `agents` is ok and the others are listed as not in use; one that is signed in but fails to start is named with "see yawble agents", and the row warns only when no agent can run. Exit 1 when anything FAILs. The in-container half runs the Host's own `--doctor` switch through `podman exec` (or `docker exec`), so those checks are computed by the platform and only rendered here. `--fix` starts a stopped Podman machine (with Podman as the engine), creates a missing data volume and starts a stopped container; it never removes anything. After starting a machine it checks again and stops there, so a second `--fix` may be needed for the container.

```
$ yawble doctor --details
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
ok    backups        1 daily copy in the data volume (newest 2026-09-28 03:00 UTC), lost if the volume is lost; newest copy on this computer: C:\Users\me\yawble-backup-20260927-181200.tar.gz, 17 hours old
ok    agents         claude signed in, launch ok · codex not in use (not signed in) · copilot not measured · grok signed in, launch FAILED, exit 134, see yawble agents · agy not in use (not installed)
info  capacity       engine has 15688 MB, 10 CPUs (podman machine); the container got 8192 MB, 8 CPUs (the Host's cgroup reading)
ok    running limit  4, from the memory bound (the Host's answer: ...)
info  run memory     rlimit, 1792 MB per run: runs.memoryLimitMb is set
```

`capacity` puts what the engine has (asked of it, as `up` does) beside what the container got, as the Host inside reads its cgroup; it warns when yawble's config asks for more than the engine has. The running limit and run memory are the Host's own answers; when the Host cannot be asked they read "not known", never an estimate. The first four rows appear only with Podman on macOS and Windows, where it runs in a machine; on Linux, and with Docker, the list starts at `engine`.

## Where things live

- Settings: `~/.config/yawble/config.toml` on Linux, `~/Library/Application Support/yawble` on macOS, `%APPDATA%\yawble` on Windows. `yawble config` reads and writes it.
- Provider keys the instance should receive: a file named `env` beside the config, one `NAME=value` per line, passed to the container at `up`; `yawble secret set <KEY>` writes it.
- Everything else is state the container engine holds: pod `yawble` (a network of that name with Docker), volume `yawble-data` mounted at `/data`, container `yawble`.

## Releasing

`docs/ops/cli-releases-and-install.md` at the repository root. Image and CLI carry one version number, `v<yyyy.mm.dd.N>`, and one tag: the root's `scripts/release.ps1` pushes the image, then runs `cli/scripts/release.sh` at the same tag, which cross-compiles, checksums and publishes the one GitHub Release, pinned to that image.
