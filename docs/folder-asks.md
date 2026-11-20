# Folder asks (B111)

An agent that needs a folder outside its sandbox (a data folder to read, an output folder to write) asks the person
with the bus tool `request_folder`. The person approves or denies it in Needs you. An approval writes the path into the
member's loosen block, so the member's **next** run has it; a running sandbox never changes.

Core: `core/src/Yawble.Core/Integrations/FolderAsks.cs`, migration `0053_folder_asks.sql`. TS shapes:
`shell/src/shared/api.ts` (`FolderAsk`, `FolderAskChoice`, `FolderAnswer`, `Overview.folderAsks`, `FOLDER_ASKS`,
`FOLDER_ANSWER`, `FOLDER_ASKS_CHANGED`; `folder.` is in `UI_METHOD_PREFIXES`).

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
  documents, an earlier approval). Nothing is asked.
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

## RPCs (the window and a Cloud Link viewer; person-only answers)

### `folder.asks` `{ all?: boolean, teamId?: number }` -> `FolderAsk[]`

The waiting asks, oldest first (`all: true`: answered ones too).

```ts
interface FolderAsk {
  id: number; at: string; teamId: number; team: string;
  memberId: number; member: string /* "Team/Name": who the access is for */; memberName: string;
  askedBy: string /* "Team/Name": the member, or its Manager */;
  path: string /* resolved absolute path */; access: 'read' | 'readwrite'; reason: string;
  exists: boolean /* exists now while pending; when asked, once answered */; existedWhenAsked: boolean;
  state: 'pending' | 'approved' | 'denied' | 'closed'; grantedAccess: 'read' | 'readwrite' | null;
  decidedBy: string | null; decidedVia: string | null; decidedAt: string | null; note: string | null;
  workflow: number | null;
  choices: FolderAskChoice[] /* pending: ['readwrite','read','deny'] for a read-write ask, ['read','deny'] for a read ask; [] once answered */;
}
type FolderAskChoice = 'readwrite' | 'read' | 'deny';
```

### `folder.answer` `{ id, choice: 'readwrite' | 'read' | 'deny', note?: string }` -> `FolderAnswer`

```ts
interface FolderAnswer { ok: boolean; id: number; choice: FolderAskChoice; path: string; member: string;
  grantedAccess: 'read' | 'readwrite' | null; told: string | null /* "Team/Name" */ }
```

- Only the person: the window (`app`) or a Cloud Link viewer (`cloud`). An agent, the Concierge, a trigger or a paired
  program is refused (`bad_params`).
- Never wider than asked: `readwrite` on a read ask is refused (`bad_params`).
- **Approve** (`readwrite` or `read`): the path goes into the member's loosen block (`filesystem.readwrite` or
  `filesystem.readonly`; a read-write grant takes the same path off the read-only list) through
  `PolicyStore.PreviewLoosen` and `PolicyStore.SaveLoosen` - the same code as `policy.saveLoosen` - confirmed as exactly
  that one added line, so `loosen_summary` and `loosen_approved_at` are set. The rules above are checked again first.
  The member's effective policy is checked to have the path with that access; otherwise nothing closes
  (`policy_refused`). Then the ask is `approved` and the team's Manager is told, in the ask's workflow:
  "`<member> can now read and write <path> (folder request N); retry what was blocked. It applies to runs that start
  from now on.`" (a read-only grant of a read-write ask says so).
- **Deny**: the policy is not touched; the ask is `denied` and the asker (the member, or the Manager that asked) is
  told: "`The person denied read-write access to <path> for you (folder request N)[: note]. Do not reach it another
  way or ask the person to edit the policy: ...`".
- A second answer to the same ask (two views at once) gets `bad_params` "not waiting" / "being answered already".

Errors: `bad_params` (unknown id, bad choice, wider than asked, not the person), `policy_refused` (the policy store
refused the write, or the path is refused now).

### Push: `folder.asksChanged`

Sent when an ask is raised (not for a repeat) or answered. Needs you, the You node, the member pane and the Monitor
reload `folder.asks` / `app.overview`.

### `app.overview`

`folderAsks`: the number of waiting folder asks, also added into `needsYou` (as `networkAsks` and `toolAsks`).

## Card text (for the shell, card 497)

"`<Team> / <Member> asks read-write on <path> - <reason>`" (read-only: "asks read-only on"), whether the folder
exists (`exists`), Approve / Deny; for a read-write ask Approve also offers Read-only. When `askedBy` is not `member`,
the Manager asked for that member. After approve: "applies from its next run".

## Remote (Cloud Link)

Exactly as the network asks: `folder.` is on `RemoteAccess`'s allow list; `folder.asks` is a read (a Visitor may
list), `folder.answer` is a change (a Visitor is refused; an Owner or workgroup Member answers, recorded as `cloud`).
The org summary (`org.summary`) counts them as `needsYou.folderRequests` (per team: count and titles
"`<member>: folder <last path segment>`" - never the full path); yawble-cloud's dashboard does not show that key yet.

## Elsewhere

- The Concierge's (and the companion's) status read lists the waiting folder requests and counts them in "Needs the
  person".
- The audit log has every `folder.answer` (with `member=Team/Name`); `folder_ask_events` keeps asked, asked again,
  approved (what was granted), denied.
- A member removed from its team has its waiting asks closed (`closed`); a team deleted has its asks deleted.
- Role texts (member.md, manager.md) and the tool's description: "ask with request_folder; never ask the person to
  edit policy by hand".
- Not done here: the Logs dialog's Approvals log and `workflow_digest` do not list folder asks yet.
