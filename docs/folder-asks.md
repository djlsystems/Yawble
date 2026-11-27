# Folder asks

An agent that needs a folder outside its sandbox (a data folder to read, an output folder to write) asks the person
with the bus tool `request_folder`. The Concierge asks for the person with its tool `folder_request`. The person
approves or denies it in Needs you, and says whom an approval covers:

| scope | where the folder is written | who has it |
|---|---|---|
| `member` (the default) | the member's loosen block | that member |
| `team` | the team's folder list | every member of the team, members hired later too |
| `everywhere` | the global policy's `filesystem.readonly` / `filesystem.readwrite` | every agent of every team |

Whatever the scope, the folder is there from the **next** run; a running sandbox never changes. Nothing is granted
without the person's click.

Core: `core/src/Yawble.Core/Integrations/FolderAsks.cs`, `core/src/Yawble.Core/Policy/PolicyStore.cs` (the team's
folder list), migrations `0053_folder_asks.sql` and `0064_team_folders.sql`. TS shapes: `shell/src/shared/api.ts`
(`FolderAsk`, `FolderAskChoice`, `FolderAskScope`, `FolderAnswer`, `TeamFolder`, `TeamFolders`, `Overview.folderAsks`,
`FOLDER_ASKS`, `FOLDER_ANSWER`, `FOLDER_ASKS_CHANGED`; `folder.` is in `UI_METHOD_PREFIXES`).

## The bus tool: request_folder

| argument | required | meaning |
|---|---|---|
| `path` | yes | The folder's full absolute path (`Z:\smokesessions`, `/srv/data`; `~/...` is the person's home). |
| `access` | yes | `read` or `readwrite` (also accepted: `read-only`, `ro`, `read-write`, `rw`). |
| `reason` | yes | One line on what the work needs it for. |
| `member` | no | The Manager only: a member of its own team (`Name` or `Team/Name`). Default: the caller. |

Answers at once, as text:

- `asked (ask N); the person decides; it applies from your next run. ...` - a new ask, or the open one for the same
  member, path and access (a repeat says "it was already waiting"; no second ask is made).
- `<path> is already read-write / readable for you ...` - the member already has it (its own folders, the team's
  documents, the team's folder list, the global policy, an earlier approval). Nothing is asked.
- `refused: <why>. The person is not asked; ...` - no ask row is written. Refused:
  - a relative path;
  - a drive or file system root (`D:\`, `\\server\share`, `/`);
  - the user profile folder, or a folder containing it (`C:\Users`);
  - a folder that keeps secrets (`~/.ssh`, `~/.aws`, `~/.azure`, `~/.gnupg`, `~/.kube`, `~/.docker`, `~/.config/gh`,
    Windows' Credentials / Protect / Crypto folders, `~/Library/Keychains`, ...), or one inside it;
  - Yawble's own data, database, credentials and Grok home, and anything else in Yawble's data folder;
  - another team's folder (its team root and documents, its repositories - the person's checkouts and extras),
    another member's folder;
  - anything on the implicit denied list (the agent CLI's config folders, ...) or under the global policy's
    `filesystem.denied`;
  - system folders: Windows `\Windows`, `System Volume Information`, `$Recycle.Bin`, `Recovery` always;
    `Program Files`, `Program Files (x86)`, `ProgramData` for read-write only (read-only may be asked). Linux and
    macOS: `/proc /sys /dev /boot /etc /System` always; `/usr /bin /sbin /lib /lib64 /opt /var /private /Library
    /Applications` for read-write;
  - anything the policy merger itself refuses for the loosen block (`PolicyStore.PreviewLoosen`).
- `refused: only your Manager may ask ...` / `refused: there is no member named ...` - a member naming someone else, a
  Manager naming another team's member or an unknown one.
- A missing reason or an unknown access is a tool error.

No policy rule changed: the checks only decide whether the person is asked.

## The Concierge's tool: folder_request

| argument | required | meaning |
|---|---|---|
| `team` | yes | The team. |
| `member` | no | The member that needs the folder (an agent or a workflow member). Default: the team's Manager. |
| `path` | yes | The folder's full absolute path. |
| `access` | yes | `read` or `readwrite`. |
| `reason` | yes | One line on what the folder is needed for. |

It raises the same ask as `request_folder`, with `askedBy` = `Concierge`, and answers at once:

- `asked (folder request N): the person is asked in Needs you ...` - a new ask, or the one already waiting.
- `<path> is already read-write / readable for <Team/Member> ... Nothing is asked.`
- `refused: <why>` - the same refusals as above; also an unknown team or member.

The Concierge grants nothing and can never answer an ask: `folder.answer`, `policy.addTeamFolder` and
`policy.removeTeamFolder` refuse it. It runs only in a turn the person started (in the window or over the Cloud Link).
The answer to an ask the Concierge raised tells no agent: no run was blocked on it. `team_status` lists a team's folder
list and its waiting folder requests, and `system_status` every waiting folder request, so the Concierge can say what
is granted and what waits. A program paired through the companion is not offered `folder_request`.

## RPCs (the window and a Cloud Link viewer; person-only answers)

### `folder.asks` `{ all?: boolean, teamId?: number }` -> `FolderAsk[]`

The waiting asks, oldest first (`all: true`: answered ones too).

```ts
interface FolderAsk {
  id: number; at: string; teamId: number; team: string;
  memberId: number; member: string /* "Team/Name": who the access is for */; memberName: string;
  askedBy: string /* "Team/Name": the member, or its Manager; "Concierge": the Concierge asked for the person */;
  path: string /* resolved absolute path */; access: 'read' | 'readwrite'; reason: string;
  exists: boolean /* exists now while pending; when asked, once answered */; existedWhenAsked: boolean;
  state: 'pending' | 'approved' | 'denied' | 'closed'; grantedAccess: 'read' | 'readwrite' | null;
  decidedBy: string | null; decidedVia: string | null; decidedAt: string | null; note: string | null;
  deliverable: number | null;
  choices: FolderAskChoice[] /* pending: ['readwrite','read','deny'] for a read-write ask, ['read','deny'] for a read ask; [] once answered */;
  scopes: FolderAskScope[] /* pending: ['member','team','everywhere']; [] once answered */;
}
type FolderAskChoice = 'readwrite' | 'read' | 'deny';
type FolderAskScope = 'member' | 'team' | 'everywhere';
```

### `folder.answer` `{ id, choice: 'readwrite' | 'read' | 'deny', note?: string, scope?: FolderAskScope }` -> `FolderAnswer`

```ts
interface FolderAnswer { ok: boolean; id: number; choice: FolderAskChoice; path: string; member: string;
  grantedAccess: 'read' | 'readwrite' | null; told: string | null /* "Team/Name" */;
  scope: FolderAskScope | null /* where an approval was written; null for a deny */ }
```

- Only the person: the window (`app`) or a Cloud Link viewer (`cloud`). An agent, the Concierge, a trigger or a paired
  program is refused (`bad_params`).
- Never wider than asked: `readwrite` on a read ask is refused (`bad_params`).
- **Approve** (`readwrite` or `read`), by `scope` (left out: `member`; an unknown one is `bad_params`):
  - `member`: the path goes into the member's loosen block (`filesystem.readwrite` or `filesystem.readonly`; a
    read-write grant takes the same path off the read-only list) through `PolicyStore.PreviewLoosen` and
    `PolicyStore.SaveLoosen` - the same code as `policy.saveLoosen` - confirmed as exactly that one added line, so
    `loosen_summary` and `loosen_approved_at` are set.
  - `team`: the path goes on the team's folder list (`PolicyStore.AddTeamFolder`, below), checked for every current
    member. When one member cannot have it, nothing is written and the ask stays waiting. A read-only approval leaves
    a folder the list holds read-write as it is. No member's loosen block is written.
  - `everywhere`: the path goes into the global policy's `filesystem.readonly` or `filesystem.readwrite`
    (`PolicyStore.SaveGlobal`: its checks and the sandbox dry run).

  The rules above are checked again first. The member's effective policy is checked to have the path with that access;
  otherwise nothing closes (`policy_refused`). Then the ask is `approved` and the team's Manager is told, in the ask's
  deliverable: "`<member> can now read and write <path> (folder request N); retry what was blocked. It applies to runs
  that start from now on.`" (for a team: "Every member of the team can now ..."; for all teams: "Every agent can now
  ..."; a read-only grant of a read-write ask says so).
- **Deny**: the policy is not touched; the ask is `denied` and the asker (the member, or the Manager that asked) is
  told: "`The person denied read-write access to <path> for you (folder request N)[: note]. Do not reach it another
  way or ask the person to edit the policy: ...`". A deny has no scope.
- An ask the Concierge raised tells no agent, approved or denied.
- A second answer to the same ask (two views at once) gets `bad_params` "not waiting" / "being answered already".

Errors: `bad_params` (unknown id, bad choice, bad scope, wider than asked, not the person), `policy_refused` (the
policy store refused the write, or the path is refused now).

## The team's folder list

A team's folder list (`team_folders`: path, access `read` or `readwrite`, who added it, how and when) holds folders
outside the members' own folders that every member of the team has. The policy merger applies it as it applies a
member's loosen block's folders (`PolicyMerger.Merge`, `teamFolders`), for every member of the team at each launch:

- a folder may lie outside the global policy's grantable roots;
- the effective policy lists it under `loosened` as "`filesystem.readonly + <path> (team)`" or
  "`filesystem.readwrite + <path> (team)`", so a member's card says loosened and its Policy tab says the folder comes
  from the team;
- a folder under a denied path (the global policy's, or the member's override's) is dropped for that member; so is one
  that overlaps the implicit denials or Yawble's data folder. A dropped folder is a note in the effective policy,
  never a violation: the team still launches;
- a read-write team folder wins over the same folder granted read-only by the member's own loosen block; a read-only
  entry of the global policy or of an override still narrows it;
- a workflow member's file steps get the list too: a read-write folder to read and write, a read-only one to read.
  The same holds for the folders of the workflow member's own loosen block.

Adding a folder (`PolicyStore.AddTeamFolder`) checks it for **every** current member before one write: the path is
absolute; the refusals of `request_folder` above apply (never another team's folders, secrets, Yawble's own data, a
system folder, the profile root); the member's merged policy with the folder has no violation and really grants it with
that access; the sandbox dry run accepts it. One refusal writes nothing. A member hired or removed while it is checked
is checked again. A member hired later has the list from its first run.

### `policy.teamFolders` `{ teamId }` -> `TeamFolders`

```ts
interface TeamFolder { path: string; access: 'read' | 'readwrite'; addedBy: string; addedVia: string | null; addedAt: string }
interface TeamFolders { teamId: number; folders: TeamFolder[] /* oldest first */ }
```

### `policy.addTeamFolder` `{ teamId, path, access: 'read' | 'readwrite' }` -> `TeamFolders`

Adds the folder, or changes the access of one that is on the list. `policy_refused` lists every reason, one per line.

### `policy.removeTeamFolder` `{ teamId, path }` -> `TeamFolders`

Takes the folder off the list: the team's next runs do not have it. A member that has the folder in its own loosen
block keeps it. `not_found` when it is not on the list.

Adding and removing are the person's alone: the window (`app`) or a Cloud Link viewer with full access (`cloud`). An
agent, the Concierge, a trigger or a paired program is refused (`bad_params`); a Visitor reads the list and is refused
the changes. Both changes are in the audit log. `policy.getMember` carries the member's team's folders as `teamFolders`.

**Team settings > Folders** shows the list (path, read-only or read-write, who added it and when), **Add a folder**
(a path, **Browse…** in the desktop window, read-only or read-write, then a confirm step that says what it grants),
and **Remove** with a confirm. The core's refusal is shown as text.

### Push: `folder.asksChanged`

Sent when an ask is raised (not for a repeat) or answered. Needs you, the You node, the member pane and the Monitor
reload `folder.asks` / `app.overview`.

### `app.overview`

`folderAsks`: the number of waiting folder asks, also added into `needsYou` (as `networkAsks` and `toolAsks`).

## Card text (what the shell shows)

"`<Team> / <Member> asks read-write on <path> - <reason>`" (read-only: "asks read-only on"), whether the folder
exists (`exists`), Approve / Deny; for a read-write ask Approve also offers Read-only. When `askedBy` is not `member`,
the Manager asked for that member ("`Manager asks, for <Member>, ...`"); when it is `Concierge`, "`The Concierge asks,
for <Member>, ...`".

**An approval covers**: "Only `<Member>`" (picked), "Everyone on `<Team>`" or "All teams", from the ask's `scopes`. A
line under it says what an approval grants at that scope, and the buttons read "Approve for the team" / "Approve for
all teams". The card sends `scope` only when it is wider than the member. After approve: "applies from its next run",
or for a wider scope "Approved read-only for every member of `<Team>` on `<path>`: applies from their next runs.".

## Remote (Cloud Link)

Exactly as the network asks: `folder.` is on `RemoteAccess`'s allow list; `folder.asks` is a read (a Visitor may
list), `folder.answer` is a change (a Visitor is refused; an Owner or workgroup Member answers, recorded as `cloud`).
`policy.teamFolders` is a read; `policy.addTeamFolder` and `policy.removeTeamFolder` are changes.
The org summary (`org.summary`) counts them as `needsYou.folderRequests` (per team: count and titles
"`<member>: folder <last path segment>`" - never the full path); yawble-cloud's dashboard does not show that key yet.

## Elsewhere

- The Concierge's (and the companion's) status read lists the waiting folder requests and counts them in "Needs the
  person"; their `team_status` lists the team's folder list and the team's waiting folder requests.
- The audit log has every `folder.answer` (with `member=Team/Name`), `policy.addTeamFolder` and
  `policy.removeTeamFolder`; `folder_ask_events` keeps asked, asked again, approved (what was granted, and for whom),
  denied.
- A member removed from its team has its waiting asks closed (`closed`); a team deleted has its asks and its folder
  list deleted.
- Role texts (member.md, manager.md) and the tool's description: "ask with request_folder; never ask the person to
  edit policy by hand".
- Not done here: the Logs page's Approvals log and `deliverable_digest` do not list folder asks yet.
