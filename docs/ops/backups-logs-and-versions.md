# Backups, logs and CLI versions

Operations notes for the container described in the root `README.md`: the container `yawble` that `yawble up` runs, its image (`<image>` below; `yawble status` names it), and the volume `yawble-data` mounted at `/data`.

## What is backed up, and how

There are two kinds of backup, and they cover different things.

| | Daily database copy | Full-volume export |
|---|---|---|
| What | `messages.db` only: teams, accounts, the message log, settings, backlog | Everything on `/data`: the database, team folders and git clones, documents, keys, agent logins, installed CLIs, caches |
| Who takes it | The host, by itself | A person, by hand |
| Where | `/data/backups` inside the volume | A `.tar` wherever you run `podman` |
| Survives losing the volume | No | Yes |

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

### Full-volume export (`podman volume export`)

This is the only backup that survives losing the volume or the machine. It also covers things outside the database, such as team repositories, documents, Data Protection keys and CLI logins. Stop the container first so the tar is not taken while SQLite and git are writing:

```sh
podman stop yawble
podman volume export yawble-data --output yawble-data-$(date +%Y%m%d).tar
podman start yawble
```

From PowerShell, use `--output "yawble-data-$(Get-Date -Format yyyyMMdd).tar"`. The tar is written where the `podman` client runs, so on Windows it lands on the Windows side, outside the Podman machine.

**The tar contains secrets.** It holds the Data Protection keys (`/data/keys`), every agent CLI's saved login (`/data/agent-home`) and any tokens in team clones. Store it the way you store the `.env` file.

To restore into a fresh volume:

```sh
podman rm -f yawble
podman volume rm yawble-data
podman volume create yawble-data
podman volume import yawble-data yawble-data-20260923.tar
yawble up
```

`podman volume import` needs the volume to exist and should be given an empty one. It adds files; it does not delete files that are not in the tar.

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
