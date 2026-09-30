# Backups, logs and CLI versions

Operations notes for the container described in the root `README.md`: the container `yawble` that `yawble up` runs, its image (`<image>` below; `yawble status` names it), and the volume `yawble-data` mounted at `/data`.

The engine commands below use `podman`. With Docker, `docker` takes the same arguments for `exec`, `stop`, `start` and `run`. `yawble backup` and `yawble restore` work the same on both engines.

## What is backed up, and how

There are two kinds of backup, and they cover different things.

| | Daily database copy | `yawble backup` |
|---|---|---|
| What | `messages.db` only: teams, accounts, the message log, settings, backlog | Everything on `/data`: the database, team folders and git clones, local repositories, documents, keys, agent logins and settings. Caches and reinstallable agent programs are left out unless `--full` |
| Who takes it | The host, by itself | A person, with `yawble backup` |
| Where | `/data/backups` inside the volume | A `.tar.gz` on the computer where `yawble` runs |
| Survives losing the volume | No | Yes |
| Moves to another engine or computer | No | Yes, with `yawble restore` |

### Daily database copy (automatic)

Once a day the host writes `messages-<yyyyMMdd-HHmmss>-daily.db` with SQLite's `VACUUM INTO`. The copy is consistent while the host is running. A plain file copy of `messages.db` is not, because the database is in WAL mode and recent writes are still in `messages.db-wal`.

- The host checks every hour whether the newest daily copy is 24 hours old or missing, and takes one if so. It reads that from the folder, not from memory, so restarting the host does not produce extra copies.
- It keeps the newest **seven** daily copies and deletes older ones. It deletes only files ending in `-daily.db`. Pre-migration backups (`messages-*-before-*.db`) and `--backup` copies (`messages-*-manual.db`) share the folder and are never deleted automatically.
- A failed copy is logged as a warning and tried again at the next hourly check. It does not stop the host.

Settings (in `appsettings.json` or as environment variables, for example `Backups__Keep=14`):

| Setting | Default | Meaning |
|---|---|---|
| `Backups:Enabled` | `true` | `false` turns the daily copy off. |
| `Backups:Directory` | `<data root>/backups`, which is `/data/backups` in the container | Where the copies go. |
| `Backups:Keep` | `7` | How many daily copies to keep. |

Separately, the quiet-team sweep (every minute by default) ends with `PRAGMA wal_checkpoint(TRUNCATE)`. This copies the WAL into the database and cuts `messages.db-wal` back to zero bytes, so the WAL does not stay at its largest size. If a reader holds the database at that moment, the checkpoint reports busy and the next sweep tries again.

To take a database copy by hand while the host runs:

```sh
podman exec yawble dotnet /app/Harness.Host.dll --DataRoot=/data --backup
```

### Restoring a database copy

`--restore` refuses while a host is serving, so stop the container first and run the restore in a one-off container on the same volume:

```sh
podman stop yawble
podman run --rm -v yawble-data:/data --entrypoint dotnet <image> \
    /app/Harness.Host.dll --DataRoot=/data --restore /data/backups/messages-20260923-030000-daily.db
podman start yawble
```

The restore writes its own backup of the database it replaces before overwriting it.

### Whole-instance backup (`yawble backup`)

This is the only backup that survives losing the volume or the computer. It covers everything outside the database too: team repositories, documents, Data Protection keys and agent logins.

**Local repositories are in it.** A local repository ([../local-repositories.md](../local-repositories.md)) is a bare git repository at `<dataRoot>/repos/<name>.git`, which is `/data/repos` on the volume. The whole-instance backup carries `<dataRoot>/repos` like every other folder on the volume, and `yawble restore` puts it back. For a team on a local repository with no hosted remote, this backup is the only copy that survives losing the volume. The daily database copy does not include local repositories.

```sh
yawble backup                          # yawble-backup-<yyyyMMdd-HHmmss>.tar.gz in the current folder
yawble backup --output ~/yawble.tar.gz
yawble backup --full --yes
```

- **It is consistent, not live.** A running instance is stopped while the archive is written, then started again, and `backup` says so. When agents are running it names them, the same way `up` does before a restart, and asks first. `--yes` answers the question. A stopped instance stays stopped.
- **It works the same on Podman and Docker.** A short-lived container from the instance's own image reads the volume read-only and streams a tar to `yawble`. The image is already on the computer, so nothing is pulled. If the image is missing, `yawble up` pulls it.
- **It is written where `yawble` runs.** On Windows the archive is on the Windows side, not inside the Podman machine or WSL.

**What is left out by default.** Only what the instance reinstalls by itself on its next start:

- the package caches `npm-cache`, `pip-cache`, `go-cache` and `nuget`;
- the headless browser in `ms-playwright`;
- the Claude, Codex and Copilot packages in `npm-global` (other global npm packages are kept);
- Grok's program under `agent-home/.grok/bin` and `agent-home/.grok/downloads`.

These programs are built for one processor type, so leaving them out is also what lets a backup move between an Intel/AMD computer and an Apple silicon Mac. Everything else is kept, including agent logins and settings under `agent-home` (`.claude`, `.codex`, `.copilot`, Grok's `auth.json`), `/data/bin`, `/data/go`, logs and the host's daily database copies. `--full` keeps everything. The first start after a restore reinstalls the left-out programs, which takes a few minutes; `yawble logs` shows it.

**What is in the file.** It is a `.tar.gz`, readable with standard tools (`tar -tzf <file>` lists it). The first entry is `manifest.json`: the format version, the yawble and image versions, the image, the engine and its version, the processor type, when it was made, the database schema steps applied, what was left out, byte and file counts, and the names of the providers whose keys were set. The volume's files follow under `data/`.

**The file contains secrets.** It holds the Data Protection keys (`/data/keys`), every agent CLI's saved login (`/data/agent-home`) and any tokens in team clones. `backup` writes it readable by you only and says so in one line. Keep it that way, and store it the way you store the `.env` file. `yawble backup` does not encrypt it; use your own tools if you want to.

**What is not in the file.** yawble's own settings: the engine and port choices, and the keys set with `yawble secret set`. They belong to the computer, not the instance.

`yawble doctor` shows an `info` line with the newest backup written on this computer and how old it is. It is never a failure.

### Restoring a backup (`yawble restore`)

```sh
yawble restore yawble-backup-20260928-101500.tar.gz
```

`restore` puts the backup into the instance on this computer and engine, whichever made it: Podman to Docker, Windows to macOS, one computer to another. It pulls the image and creates the volume if needed, restores, then starts the instance the way `yawble up` does and waits for it to answer. File ownership is set by the container at every start, so nothing needs fixing afterwards.

- **Into an empty volume, it asks nothing.**
- **Into a volume that already holds data, it refuses** unless you pass `--replace`. With `--replace` it asks you to type the word `replace` at a terminal; from a script, use `--replace --yes`. Before it clears the volume it writes a backup of the current one beside the file being restored, named `yawble-backup-<yyyyMMdd-HHmmss>-before-restore.tar.gz`, and prints the name. Running `yawble up` on a new computer before restoring creates such data (one sign-up is enough), so restore first if you can.
- **A backup from a newer Yawble is refused.** Its database may hold schema steps this image does not know. The message names both versions; run `yawble update`, then restore again. A backup from an older Yawble is fine: the host migrates the database at start and takes its usual pre-migration copy first.

`restore` ends by listing the providers whose keys the backed-up instance had set with `yawble secret set`, never their values.

### Moving an instance to another engine or computer

1. On the old computer: `yawble backup`, then copy the file to the new computer the way you would copy any secret.
2. On the new computer, install `yawble`. Choose the engine and port again: `yawble config set engine podman|docker` and `yawble config set port <port>`, or let `restore` ask the way `up` does.
3. `yawble restore <file>`.
4. Set again the keys `restore` listed, with `yawble secret set NAME` (and `yawble github` for `GH_TOKEN`), then `yawble up`.

To switch engines on the same computer: `yawble backup`, then `yawble uninstall --keep-settings --data`, then `yawble config set engine docker` (or `podman`), then `yawble restore <file>`. The `--keep-settings` flag keeps your keys and port. The uninstall deletes the volume, so check that the backup was written first.

## Logs

The host writes one JSON object per line to the console, with scopes included. Every line written while serving a signed-in request has the caller's principal id at `Scopes[].PrincipalId`, so one person's or one member's requests can be filtered:

```sh
yawble logs 2>&1 | jq -c 'select(any(.Scopes[]?; .PrincipalId? == "<principal id>"))'
```

Only the id is logged, never an email address or a key. Anonymous requests have no `PrincipalId`.

The console is also copied into the size-limited host log under `/data/logs`. The size limit is `HostLog:Budget` if set, otherwise derived from free space. The file keeps what `podman logs` loses when the container is recreated.

## Agent CLI versions per start

At every container start, `scripts/ensure-agent-clis.sh` appends one line to `/data/cli-versions.jsonl`. The line records the start time and the version reported by `claude`, `codex`, `copilot`, `grok`, `agy`, `gh`, `git`, `node`, and the headless Chromium build. A CLI that was not installed is recorded as `null`. The file keeps the newest 200 starts and lives on the volume, so the history survives image rebuilds.

To see it, open **Admin › Diagnostics › Agent CLI versions**. The newest start is first. A version in bold differs from the start before it. The same data is available from `GET /api/diagnostics/cli-versions` (people only). A host started outside the container has no such file and shows an empty history.

## When agent CLIs are updated

Every member launches its agent CLI from one shared install on the volume. A CLI that updated itself would replace that install while other members were being launched from it, and a launch in that moment would find no program. So the CLIs never update themselves. Updates happen in two places only:

- **At every container start**, before the Host runs, `scripts/ensure-agent-clis.sh` brings each installed CLI to its newest version (`npm install -g <package>@latest`, and `grok update`). Nothing can be launching then. Set `HARNESS_UPDATE_AGENTS=0` to keep the installed versions.
- **When a person asks**, with the update button beside a built-in preset on the **Agents** screen (`POST /api/agents/{name}/update`, people only). The Host waits until no run of that CLI is in flight. New launches of it wait until the update is done: they are held, like a run waiting for a free slot, and never failed. The versions before and after are shown, recorded in the tenant log (`agent.updated`), and added to `/data/cli-versions.jsonl` as a line marked `"by":"update"` with the email of the person who asked (`"person"`).

Every launch the Host makes turns the CLI's own updater off: a member, the Concierge's terminal and the sign-in probe. Each preset declares how in its `updates` field:

| CLI | Switch | What it would otherwise do |
| --- | --- | --- |
| `claude` | `DISABLE_AUTOUPDATER=1` | Replace the npm install in the background. |
| `codex` | `-c check_for_update_on_startup=false` | Check at start, and offer to run `npm install -g` in the terminal. |
| `copilot` | `COPILOT_AUTO_UPDATE=false` | Download a newer build into `~/.cache/copilot` and run that instead of the installed one. |
| `grok` | `GROK_DISABLE_AUTOUPDATER=1` | Install itself under `~/.grok/downloads` and repoint `~/.grok/bin/grok`. |

**Which version is installed, and when it last changed.** `yawble doctor` shows an `agent versions` row, `yawble agents` shows it per CLI, and the **Agents** screen shows it on each built-in preset's row, with who brought it (a container start or the person who pressed update). A version the record does not have reads `version not known`. After the row's update button, the row shows the new version, or says the version did not change. Examples: `claude 2.1.285, updated 2026-09-29 20:46 UTC`, or `unchanged since <date>` when the history never saw that version change. The time is the first line in `/data/cli-versions.jsonl` that recorded the version after a different one: either a start or a person's update.
