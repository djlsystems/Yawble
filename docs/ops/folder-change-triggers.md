# Folder-change triggers

A folder-change trigger wakes a member when files appear, disappear or change size in a folder. It works on the team's documents folder with no setup. It can also watch a host folder or a network share, once an operator has made that folder visible to the container and allowed watching on it.

This page covers the container described in the root `README.md`: the container `yawble` that `yawble up` runs, and the volume `yawble-data` mounted at `/data`.

## How it notices a change

The platform **polls**. Every `pollSeconds` it lists the folder and compares the result with the last listing. It does not use file-system notifications. Those do not see changes made on the Windows side of a mounted folder, or by another machine on a share. A polled listing behaves the same everywhere.

When the listing changes, the trigger waits for the **quiet period**. It fires only after the folder has stayed the same for `quietSeconds`. Copying twenty files, or one large file that is still being written, therefore fires once, after the copy finishes.

When it fires, the trigger publishes a `file.changed` event. That event wakes the trigger's member through the normal event path. Team pause, busy-skip, "only when idle", causation and the activity feed all work as they do for any event trigger.

Some changes do not wait for a poll. A file uploaded or deleted through the **Documents** dialog publishes `file.changed` straight away, because the platform made that change itself. That event wakes every enabled folder trigger on the team whose folder and glob cover the file. The next poll does not fire a second time for the same file.

### Touching a file

A change fires only when a file was **added, removed or changed size**. A file whose modification time changed but whose size did not (`touch`, or a sync tool updating timestamps) restarts the quiet period, and is then taken as the new baseline without firing.

As a result, an edit made outside the platform that leaves the file exactly the same size is not seen by polling. Edits made through the **Documents** dialog are always announced, whatever their size.

## Watching the team's documents

Nothing to set up. In the member's **Triggers** dialog, choose **Add trigger**, set **Kind** to **Folder change**, and keep **Root** as **Team documents**. **Path** is a folder inside the documents folder, such as `inbox`. Leave it blank to watch the whole documents folder.

## Watching a host folder or a network share

The container sees only what is passed into it. A network share takes three steps.

### 1. Mount the share on the computer

Make the share available on the computer that runs the container engine: on Windows, map it to a drive letter or mount it at a local folder; on macOS or Linux, mount it at a local folder. Open it to confirm the files are there before continuing.

### 2. Pass it into the container with `podman run -v`

The container needs a `-v <host path>:<container path>` mount for the share, for example `-v S:\scans:/mnt/scans`. **`yawble up` starts the container with the data volume only and has no setting for extra mounts**, so a share cannot be passed in through `yawble` today, and steps 2 onward depend on that mount.

Once the mount is in place, check the files are visible from inside it:

```powershell
podman exec yawble ls /mnt/scans
```

If this lists nothing, or fails, the container cannot see the folder, and a trigger cannot either. Fix the mount before going further. The Podman machine must be able to reach the host path you gave it.

The container path (`/mnt/scans` here) is what the next step uses. The Windows path is not.

### 3. List it under `FileBrowser:Roots` with `allowWatch`

Add the container path as a file-browser root, and set `allowWatch` to `true`. Put it in `src/Harness.Host/appsettings.Production.json`, not in the tracked `appsettings.json`. See the comment at the top of `appsettings.json` for why; its `FileBrowser` comment also describes `allowWatch` beside the other root flags.

```json
{
  "FileBrowser": {
    "Roots": [
      { "name": "Scans", "path": "/mnt/scans", "allowWatch": true }
    ]
  }
}
```

The keys are not case-sensitive: `"Name"`, `"Path"`, `"AllowWatch"` work too. The same root can instead be given as environment variables on the `podman run` command, which is handy when the file lives outside the container:

```powershell
'-e', 'FileBrowser__Roots__0__Name=Scans',
'-e', 'FileBrowser__Roots__0__Path=/mnt/scans',
'-e', 'FileBrowser__Roots__0__AllowWatch=true',
```

Use the next free index (`__1__`, `__2__`, ...) if other roots are already listed that way.

Restart the host. Roots are read once, at startup.

Listing a root has consequences beyond watching. Any listed root can be opened in the file picker, and a team can be placed inside it (the comment in `appsettings.json` explains this). Add only folders you are content for a team to live in. `allowWatch` defaults to `false`: a root without it can be browsed but not watched. The instance's own data folder is never offered for watching.

### 4. Choose it in the trigger dialog, and test it

**Root** now offers **Scans**. Set **Path** to a folder inside it, then press **Test this folder**. The dialog shows:

- how many files the trigger would see,
- the first few of them,
- how long the listing took.

If the folder cannot be watched, the dialog shows the reason instead (see [Refusals](#refusals)).

The listing time matters for a share. A share that takes several seconds to list should not be polled every 15 seconds. Increase **Poll every** instead.

## The fields

| Field in the dialog | Wire name | Default | Rule | Meaning |
|---|---|---|---|---|
| Root | `watchRoot` | Team documents (`documents`) | `documents`, or `root:<name>` for a root with `allowWatch` | Which folder tree the path is inside. |
| Path | `watchPath` | blank | Relative: no leading `/`, no drive letter, no `..` | The folder to watch, inside the root. Blank means the root itself. |
| Glob (optional) | `watchGlob` | blank (every file) | `*` and `?`; `**` for any depth | Without a `/` it matches file **names** (`*.pdf`). With a `/` it matches the path under the watched folder (`in/**/*.csv`). |
| Poll every (s) | `pollSeconds` | 60 | 15 or more | How often the folder is listed. |
| Quiet period (s) | `quietSeconds` | 30 | 0 or more | How long a change must stay unchanged before the trigger fires. |
| Minimum interval (s) | `minIntervalSeconds` | 60 | 0 or more | The shortest time between two fires of this trigger. |
| Only when the member is idle | `idleOnly` | on | | As for every trigger. For a folder it is also the loop guard; see below. |

The instruction can use the `file.changed` fields as tokens: `{event.path}` (the watched folder), `{event.changed}` (the changed files, at most 100), and `{event.count}` (how many changed in total). The event carries paths and counts. It never carries file contents.

## What is ignored

These never count as a change, wherever they appear in the watched folder:

- `.harness-team`, `.git`, `.worktrees`, `node_modules`
- editor temporary files: `*~`, `.#*`, `*.tmp`, `*.swp`

Only files are counted, not folders. A change means a file added, removed or resized.

## Stopping a member from waking itself

A member woken by a folder often writes into that same folder. Three things prevent it from waking itself in a loop:

- **Changes made while the team is working are not fired on.** If any member of the same team is running when the poll looks, or a member ran at any point since the previous poll, the new listing is taken as the baseline and nothing is published. A member writing its answer into the folder it watches therefore does not wake itself.
- **Only when the member is idle** is on by default for this kind. While the trigger's member is busy, the change is held, not published. It is published on a later poll, once the member is idle, unless a member of the team has run meanwhile; see below.
- **Minimum interval** is the shortest gap between two `file.changed` events from one trigger. A change inside that gap waits until the gap has passed. It is not lost, unless a member of the team runs meanwhile; see below.

The first rule has a cost. A file that a person drops into the folder while one of the team's members happens to be running is folded into the baseline too, and does not fire. So is a change held by the other two rules if a member of the team runs before it is published. If that matters, drop files through the **Documents** dialog, which always announces them, or give the watching member its own team.

## What the trigger row shows

In the **Triggers** dialog, a folder trigger's row reads, for example:

> when files change in Scans/incoming matching \*.pdf
> last poll 2 minutes ago, took 2.4 s, 12 files • last fired 3 hours ago

- **last poll** is when the folder was last listed, how long it took, and how many files it saw.
- **last fired** is when the trigger last published `file.changed`.
- The coloured chip beside the row reports whether that event actually woke the member. If the member was busy, it did not.

If the last poll could not list the folder, the reason appears in red under the row. For example, the share has gone, or the folder is too large.

## Limits

- A watched folder may hold at most **10,000** entries. Watch a smaller folder instead.
- An instance may have at most **50** folder triggers (`FolderWatch:MaxWatches`).
- A path that resolves to a symbolic link is refused.
- A path may not leave its root, and the platform's own data folder cannot be watched.
- Only people can create or edit folder triggers. An agent cannot.

## Refusals

The server refuses a folder it will not watch. The sentence appears in the trigger dialog when you save, and under **Test this folder** when you test. Each one says what to change:

| Sentence | What to do |
|---|---|
| `No file-browser root is named "<name>".` | The root was removed from `FileBrowser:Roots`, or the host was not restarted after adding it. |
| `The file-browser root "<name>" does not allow watching. Set "allowWatch": true on it under FileBrowser:Roots.` | Step 3 above. |
| `That path leaves the team's documents folder.` / `That path leaves the file-browser root "<name>".` | Use a path inside the root. |
| `The platform's own data folder cannot be watched.` | Choose another folder. |
| `"<path>" is a symbolic link, and a watched folder may not be one.` | Watch the real folder, through a root that contains it. |
| `There is no folder at "<path>" in the team's documents.` / `There is no folder at "<path>" in the root "<name>".` | Check the path. For a share, check `podman exec yawble ls <container path>`. |
| `The folder holds more than 10,000 entries. Watch a smaller folder.` | Narrow the path. |
| `This instance already watches <N> folders, the most it allows.` | Delete an unused folder trigger, or raise `FolderWatch:MaxWatches`. |
| `A folder trigger needs a root: "documents" or "root:<name>".` | Choose a **Root**. |
| `pollSeconds must be at least 15.` | Raise **Poll every**. |
| `quietSeconds must be 0 or more.` / `minIntervalSeconds must be 0 or more.` | Use 0 or a positive number. |

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `FileBrowser:Roots[].allowWatch` | `false` | Whether folder triggers may watch inside that root. |
| `FolderWatch:MaxWatches` | `50` | The most folder triggers the instance allows. Counted when a trigger is created or becomes a folder trigger; read at startup. |
