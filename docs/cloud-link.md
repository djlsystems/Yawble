# Cloud Link

> Cloud Link is a preview.

Cloud Link lets you view and steer Yawble on your computer from a browser somewhere else. You attach the computer
to a cloud site, by default [cloud.yawble.ai](https://cloud.yawble.ai/), and open it there. The browser shows the
app's own screens: Concierge, Teams, Board, Backlog, Documents, Monitor and most of Settings.

The app works fully without it. Nothing is attached until you pair.

## Pairing

1. In the app, open **Settings > Cloud Link**.
2. Check the **Cloud site address**. It is `https://cloud.yawble.ai/` unless you type another. The address must use
   `https`; plain `http` is accepted only for a site on this computer.
3. Press **Pair**. The app asks the site for a pairing code, shows it, and opens the site's pair page in your
   browser with the code filled in.
4. On the site, sign in (or create an account), choose a workgroup and attach the computer. The code works for
   10 minutes.
5. The app shows **Connected**. You can close the dialog.

You never paste anything into the app. If the code expires before it is used, press **Pair again** for a new one.
**Cancel** stops a pairing in progress.

Once attached, the Cloud Link page shows the site, the name the computer has there, its organization and group,
and who is viewing.

## How it connects

- The desktop app makes one outbound WebSocket connection to the site. It opens no listening port and needs no
  inbound firewall rule.
- The site relays each viewer's calls over that connection. The app answers them with the same handlers as the
  desktop window, after the checks below.
- While someone is viewing, the app sends its live updates (terminal output, state changes) up the same connection.
- The computer's id and secret from pairing are kept in the app's data folder. They are never shown.
- When the connection drops, the app reconnects on its own, waiting a little longer each time (up to 30 seconds).
  The status reads **Not connected (retrying)** meanwhile.
- The app's state reads one of: **Not attached**, **Pairing**, **Connecting…**, **Connected**,
  **Not connected (retrying)**.

## What a remote viewer can do

A remote viewer can do what you can do in the window, with the exceptions below. That includes reading and telling
teams, answering their questions, the board and backlog, approving integration requests, answering network
requests (allowing a host an agent was refused, like any other policy change), running the guardrail check, and
changing agents and teams. A network request shows in the remote view's **Needs you** with the same four buttons; if
the computer refuses the answer, the item says why and to answer it in the window. A tool request shows there too,
with **Not now** only: it says to install it in the Yawble window on the computer.

A team's environment variables show their names only. Each value reads **set on the computer** and never leaves it,
for a full viewer and a Visitor alike. A full viewer can add a variable, remove one, or type a new value over a hidden
one; a variable saved untouched keeps its value.

Calls from a viewer run as that viewer. Every remote call that changes something, and every refused call, is
written to `cloud-link-audit.log` in the app's data folder, with the time, the viewer and the method.

## Read-only viewers (Visitors)

The site marks each call with the viewer's access: `full` for an Owner or a workgroup Member, `read` for a workgroup
Visitor. The app checks it on every call. A call with no access, or any value other than exactly `full` or `read`,
is refused and nothing runs. The app's hello on the connection lists `"access"` in its `caps`; the site lets a Visitor
open only a computer that says it.

A read-only viewer sees the same screens and gets the same live updates. The remote view shows a
**Read-only (Visitor)** chip and turns off the buttons that would change something. They can:

- see the overview, teams, members, the Monitor, deliverables, goals and the questions waiting;
- read the board, the backlog, cards and the team's documents;
- see integrations, their requests and calls, host tasks and their runs, network and tool requests;
- read the logs, the backup list, the agents and their versions, settings and policies as shown;
- watch the org dashboard: the computer's summary and its live logs (below);
- browse members' code (when **Show members' code to cloud viewers** is on).

They cannot change anything. Telling a manager, answering a question, approving or declining a request, answering a
network or tool request, creating, editing, moving or deleting anything (a goal too, or a deliverable's goal), running the guardrail check, saving a
policy or a setting, git actions, and terminals (opening, attaching to - even just to watch - typing into, resizing
or stopping a member's terminal or a host terminal: `terminal.attach` is refused like the others, so there is no
read-only terminal view) are all refused with:

> You have read-only access to this computer (Visitor). Ask a workgroup Member or an Owner for more.

Each refusal is written to `cloud-link-audit.log` like other refusals. Every method the Cloud Link allows is listed in
`RemoteAccess` as a read or a change; a method that is not listed counts as a change, so a new one is refused for
read-only viewers until it is listed as a read.

The remote view learns its access from `core.getInfo`: over the Cloud Link the answer carries
`"remoteAccess": "full"` or `"remoteAccess": "read"`. In the window on the computer the field is absent.

## Embedded in the cloud page

The cloud opens the remote view in a frame as `/i/<id>/ui/remote.html?embed=1&theme=light|dark`. With `embed=1`
the cloud draws one left pane (its own items, then this computer's sidebar items) and one theme switch for the whole
page; the remote view publishes its sidebar to the cloud and does what the cloud asks, over the contract below.
`remote.html` without `embed=1` is the plain remote view, with its own sidebar and theme switch. In embed mode the
viewer's own stored remote theme is neither read nor written: the theme comes from the `theme` query parameter at
load, then from `yawble.theme` messages.

The contract (the cloud has the same text; the TypeScript types are in `shell/src/shared/embedContract.ts`, the
desktop's side in `shell/src/renderer/src/embed.ts`):

```text
CONTRACT (window.postMessage, both sides check event.origin === location.origin and event.source; anything else is ignored)
Desktop to cloud: {type:'yawble.nav', version:1, nav} each time anything in it changes, and once on load. nav =
  { places: [{id, label, icon, badge|null, active, disabled}],   // New chat, Concierge, Teams, Goals, Deliverables, Backlog, Kanban board, Workflows, Documents, Monitor, Logs: the same ids/order as Sidebar.vue
    teams:  [{id, name, dot:'idle'|'running'|'waiting'|'queued'|'paused'|'attention', badge|null, active}],
    canNewTeam: bool,
    chats:  [{id, title, active, busy:'deleting'|null, actions:[{id, label, danger}]}],
    runSlots: string|null,           // the line under the sidebar today
    readOnly: bool }                 // a Visitor's view
  icon is a short key: newChat, concierge, teams, goals, deliverables, backlog, kanban, workflows, documents, monitor, logs; the cloud draws its own icon for a key it knows.
Cloud to desktop: {type:'yawble.navigate', to:{kind:'place', id} | {kind:'team', id} | {kind:'chat', id} | {kind:'newTeam'} | {kind:'chatAction', id, action}} does exactly what the same click in the desktop sidebar does (including the confirm dialogs for a chat's actions, drawn inside the frame).
Cloud to desktop: {type:'yawble.theme', theme}.
Desktop to cloud: {type:'yawble.ready', version:1} once it listens.
```

The values the desktop sends:

- **Places**, in this order, as `id` / `icon`: `newChat` / `newChat`, `concierge` / `concierge`, `teams` / `teams`,
  `backlog` / `backlog`, `deliverables` / `deliverables`, `board` / `kanban` (label Kanban board), `documents` /
  `documents`, `monitor` / `monitor`, `workflows` / `workflows` (the Workflows dialog; a Visitor reads the workflows, schemas and runs, and is not offered save, import, delete, schema save or test run), `logs` / `logs`. There is no Terminal and no Help place: an agent's terminal is in
  its pane on the team page, and the version and help links are in Settings, which the cloud's own settings button
  opens. Settings itself is not a place. `badge` is a number: Teams has the
  number of teams, Deliverables the number of open deliverables; otherwise, or when it is 0, `null`. `active` is the screen
  shown now; New chat and Logs are never active. `disabled` is `false`.
- **Teams**: `id` is the team's number. `dot` is `running`, `queued`, `paused` or `idle`, as the sidebar's dot; the
  cloud draws a value it does not know as idle. `badge` is how many of its agents are running, or `null`. `active`
  is the team whose page is open.
- **Chats**: `actions` are `{id:'rename', label:'Rename', danger:false}` and `{id:'delete', label:'Delete', danger:true}`,
  and none for a read-only viewer. `busy` is `'deleting'` while the chat is being deleted.
- **runSlots** is the line under the sidebar, such as `Run slots: 1 / 4 in use · 0 queued`, or `null` before the
  computer's first answer. **canNewTeam** is `false` for a read-only viewer.

What the desktop does with the cloud's messages:

- `{kind:'place', id}` takes an `id` from the places above; `{kind:'team', id}` a team's number; `{kind:'chat', id}`
  and `{kind:'chatAction', id, action}` a chat's id, with `action` `rename` or `delete`.
- Delete asks **Delete this chat?** in the frame first; Rename asks for the new title in the frame.
- A message is ignored, as a missing or turned-off button would be, when the team or chat is not in the nav, when a
  read-only viewer asks for New team or a chat's action, or when a Delete is asked while a chat is being deleted. A
  message with another `type`, an unknown `kind`, place, action or theme, or the wrong origin or source is ignored.
- The desktop posts to its own origin only (`location.origin`) and takes messages only from its parent window.
  Opened outside a frame, it posts nothing and listens to nothing.

## Capabilities in the hello

When it connects, the app sends a hello: `{"type": "hello", "name", "os", "version", "caps": [...]}`. `caps` says
what this computer understands:

| Cap | Meaning |
|---|---|
| `access` | The app checks each call's `access` and refuses a read-only viewer's changes (above). |
| `summary` | The app answers `org.summary`, `org.summary.watch` and `org.summary.unwatch`, and pushes `org.summaryChanged` while watched (below). |
| `logsTail` | The app answers `logs.tail` and `logs.untail`, and pushes `logs.tailEntry` while tailed (below). |

The site shows a computer whose hello lacks `summary` as "update Yawble to appear here" on the org dashboard.

## The org dashboard

The site's org dashboard shows what every attached computer is running, live. The site stores none of it: it asks each
computer while someone is watching. The app sends only facts it already has. It never sends token counts, cost,
usage, price or "value" figures, though its run records hold token counts. The summary's field names never contain
token, cost, usage or price, and the model usage log is never read for it.

All five methods below are reads: a Visitor may call them, and anything a viewer cannot read today stays hidden. Free
text (titles, questions, log lines) is redacted the way the other reads redact it (known secrets and token-shaped text
become `[redacted]`) and cut to one short line.

A dashboard row links into the remote view: `/i/{id}/ui/remote.html#deliverable={wf}` opens that deliverable selected in
its team's Deliverables (the team is found from the deliverable id; `&team={id}` is an optional hint), `#team={id}` opens that
team, and an unknown or malformed id opens the usual start page. The deliverable is looked for among each team's newest
200 (the most `bus.deliverables` returns); one older than that opens a short "Deliverable #N was not found" note instead
(in its `&team=` team's Deliverables when that hint is given). A Visitor can open both. `#terminal/{memberId}` is
still an agent's terminal window.

### `org.summary` (contract version 1)

`org.summary` (no parameters) answers one snapshot of this computer:

```json
{
  "summaryVersion": 2,
  "at": "2026-11-16T12:00:00Z",
  "computer": {
    "name": "DESK-1", "label": "Ada's desk", "yawbleVersion": "2026.11.15.1", "platform": "Microsoft Windows 10.0.26200",
    "startedAt": "2026-11-16T09:00:00Z", "uptimeSeconds": 10800,
    "runSlots": { "used": 2, "total": 4 }, "queuedRuns": 1
  },
  "teams": [
    {
      "name": "web", "paused": false, "archived": false,
      "agents": { "total": 5, "running": 1, "queued": 1, "waitsForRun": 1, "held": 0, "pending": 0, "idle": 1, "errored": 1, "stopped": 0, "waitingOnYou": 1 },
      "running": [ { "member": "Manager", "role": "Manager", "doing": "card 7: plan the login page", "since": "2026-11-16T11:55:00Z" } ],
      "waiting": [
        { "member": "Ines", "state": "queued", "waitsFor": null },
        { "member": "Wren", "state": "waits-for-run", "waitsFor": "Ines" }
      ]
    }
  ],
  "deliverables": [
    {
      "id": 10, "team": "web", "title": "Build the login page",
      "item": { "id": "B048", "title": "Live org summary" },
      "state": "running", "cards": { "done": 2, "total": 5 },
      "startedAt": "2026-11-16T10:00:00Z", "lastActivityAt": "2026-11-16T11:59:00Z", "owner": "Manager"
    }
  ],
  "needsYou": {
    "total": 4, "decisions": 1, "toolCalls": 1, "networkRequests": 1, "toolRequests": 1,
    "teams": [
      {
        "team": "web", "total": 2,
        "decisions": { "count": 1, "titles": ["Which login provider?"] },
        "toolCalls": { "count": 1, "titles": ["Ines: gh pr create"] },
        "networkRequests": { "count": 0, "titles": [] },
        "toolRequests": { "count": 0, "titles": [] }
      }
    ]
  },
  "problems": [
    { "source": "Runs", "team": "web", "member": "Ines", "at": "2026-11-16T11:59:00Z", "level": "error", "message": "run failed: claude exit 1" }
  ]
}
```

- **computer**: the computer's name, `label` (its name on the cloud site; null when not attached), the Yawble version,
  the OS, when the core started and its uptime, the run slots in use and in total, and the runs waiting for a slot.
- **teams**: every team, by name, paused or archived. `agents` counts its agents: `running`; work not started, by why it waits
  (`queued` only in the run queue, for a run slot; `waitsForRun` a hand-back held until the run that sent it ends; `held`
  the team is paused or archived; `pending` anything else); `idle`, and
  `errored` (idle, and its last run failed; not also counted as idle), and `stopped` (idle agents whose last run the
  person stopped: also counted in `idle`, never in `errored`). `waitingOnYou` counts the agents with an
  open `needs_decision` for the person, whatever their state (older cores leave it out: read absent as 0). `running`
  lists each running agent: member
  name, role (`Manager` or `Member`), what it is doing (its open card on the board, else its last status line) and
  since when. `waiting` lists each agent with work not started: `member`, `state` (`queued` | `waits-for-run` |
  `held` | `pending`, the agent card's state) and `waitsFor` (the member whose run must end first; only with
  `waits-for-run`, else null).
- **deliverables**: every open deliverable, and those that ended in the last 24 hours, newest activity first. `item` is the
  backlog item it came from (null when none). `cards` counts its cards on the board, done and in total. `owner` is
  the agent that owns it (its Manager). `state` is one of:

  | State | Meaning |
  |---|---|
  | `running` | An agent is working it (running, queued, or about to be woken). |
  | `waitingOnPerson` | A question in it waits for the person. |
  | `blocked` | An agent reported it is blocked, and nothing is working it. |
  | `nobodyWorking` | Open, and nothing is running, queued or waiting to be woken. |
  | `failed` | Open with nobody working it, and its owner's last run failed. |
  | `completed` | Declared done. |
  | `closed` | Closed by a person. |

  `completed` and `closed` are the ended states. Every other state, `failed` included, is open, so the deliverable is
  listed however long ago its last activity was.

  `time` is the deliverable's overall time and where it went, the same object as on the `bus.deliverables` item:
  `createdAt`, `startedAt`, `endedAt` and `endedHow` (null while open), `open`, `overallMs`, `rebuilt` (from history,
  for deliverables from before time accounting) and `breakdown` (`[{ "category", "ms" }]`, whose ms add up to `overallMs`).
  It is null when the app has no board. The timeline itself is `timeline.get { "deliverable": id }` (or `{ "card": id }`),
  which a Visitor may call.

- **needsYou**: what waits for the person: decisions (questions), tool-call approvals, network requests and tool
  requests. The totals across every team, then per team the count and the first five short titles of each kind.
- **problems**: the last 20 warning and error entries of the last hour, newest first: failed or cut-short runs,
  refused network calls, and the app's own diagnostics lines that report an error or a warning. Each has its source,
  team and member (null when the entry names none), time, `level` (`warning` or `error`) and a one-line message.

Times are UTC, ISO 8601. New fields may be added; `summaryVersion` changes only when a field changes meaning
or goes away.

### `org.summary.watch`, `org.summary.unwatch` and `org.summaryChanged`

- `org.summary.watch` (no parameters) starts the live updates for this viewer and answers the full snapshot, as
  `org.summary` does.
- While at least one viewer watches, the app rebuilds the snapshot every 2 seconds. When a section changed, it pushes
  the event `org.summaryChanged` once, however much changed in those 2 seconds. The uptime ticking alone is not a
  change, nor is an open deliverable's `time` growing (its `overallMs` and breakdown ms; a new category is a change):
  the deliverables section then carries the current ms with its next real change, so a dashboard counts an open
  deliverable's time on from `overallMs` itself between pushes.
- `org.summary.unwatch` stops it for this viewer and answers `{"watching": false}`.
- A viewer is a sign-in: each viewer's watch is its own. A viewer who leaves (the site's viewer list no longer names
  them) or a dropped connection counts as an unwatch.
- When nobody watches, the app builds nothing and pushes nothing.

The event carries the sections that changed, each in full:

```json
{
  "event": "org.summaryChanged",
  "data": {
    "summaryVersion": 2, "at": "2026-11-16T12:00:02Z", "full": false,
    "changed": ["computer", "needsYou"],
    "sections": { "computer": { ... }, "needsYou": { ... } }
  }
}
```

`full` is true when every section is in it. The site replaces each named section of its copy with the one sent.

### `logs.tail`, `logs.untail` and `logs.tailEntry`

- `logs.tail {level: "default" | "all" | "debug", team?: string, member?: string, app?: boolean}` starts pushing this computer's
  new log entries, as they are written, to this viewer. `default` sends warning and error entries and deliverable events;
  `all` sends every level but debug; `debug` sends debug entries (the Diagnostics log's routine lines) too. `team` and `member` narrow it to one team or one member; `app: true` to the entries about
  no team and no member.
- The activity log carries every audit entry. An entry about a member has its `team` and `member`; one about a
  whole team has `member: "<Team>"` (the team's name in angle brackets) and its `team`; one about the app itself
  (backlog, settings, the link, backups...) has `team: null, member: "app"`. A member filter takes that member's own
  entries only; a team filter takes its members' entries and its `<Team>` entries. `logs.query {source: "activity"}`
  takes the same `team`, `member` and `app: true`, and `logs.sources` says which source offers `app`. Its rows carry
  the `level` the tail gives the same entry, and `logs.query` takes `level` (`default`: every level but debug, `all`,
  or one of `error`, `warning`, `info`, `debug`) for each source whose `logs.sources` entry says `levels: true`
  (All activity and Diagnostics).
- Each entry that passes the filter is pushed as the event `logs.tailEntry`, carrying one entry. `logs.query` rows
  and `logs.tailEntry` entries are redacted the same way, by the same code as the window's Logs page: known secrets
  and token-shaped text become `[redacted]`.
- The filtering happens on the computer. Nothing is kept for the cloud beyond the logs the app already keeps, and with
  no viewer tailing nothing is done.
- `logs.untail` stops it. A viewer who disconnects stops it too.

## What only the person at the computer can do

These are refused over Cloud Link. The viewer sees the reason.

| Area | Refused remotely |
|---|---|
| Cloud Link | Changing its settings, detaching, pairing. |
| Credentials | Entering or removing model credentials. Their status and mode can be read. |
| Connections | Seeing, adding, changing or removing any Connection. |
| Sign-ins | OAuth sign-ins, website logins, MCP server sign-ins, finding a provider's sign-in addresses, opening a terminal to sign a CLI in. |
| APIs | Adding, removing or discovering an API (each of these makes the computer fetch addresses). |
| MCP servers | Importing servers from config files on the computer; adding or changing a remote server, its address, transport, sign-in or secrets; reading a remote server's tools. |
| Agent terminals | An agent's **Terminal** / **Test** session. It is started, typed into and stopped in the window only, and its output is not sent up. |
| Backups | **Back up now** and **Restore**. The list of backups can be read. |
| Host tools | **Install** (and **Try again**) for a tool an agent asked for: winget runs on the computer, so it is clicked there. The requests can be listed and answered **Not now**. |
| Companion | Pairing, approving, listing or revoking `yawble-companion` clients. Nothing of the companion is sent up. |

### Host terminals

A host terminal is a shell on your computer, outside any sandbox. They are off for Cloud Link by default. While they
are off, a viewer also cannot:

- open or use a host terminal;
- add or change an MCP server (that defines a program the computer runs);
- define a host task;
- detect, enable, discover or save rules for a CLI the app does not know and that is not already an integration on
  this computer.

Turn on **Allow host terminals from the site** in Settings > Cloud Link to lift these. It is meant for signing in to
agent CLIs remotely.

### Members' code

**Show members' code to cloud viewers** is on by default. It lets viewers use the read-only code browser of each
member's working copy. Files that may hold secrets are never shown. Turn it off to refuse every code browser call
from the site.

### Concierge over Cloud Link

A Concierge message sent from the site starts a turn that counts as remote. In such a turn the Concierge cannot:

- create a team, or add or remove a team repository;
- close a deliverable;
- delete, prune, push or sync branches, or open or merge a pull request;
- archive or delete a team, or set team environment variables;
- apply doctor fixes.

It also cannot take a "yes" sent from the site as confirmation for an action that needs one. It keeps its read
tools and the backlog and board actions.

## Turning it off

- **Detach this computer** in Settings > Cloud Link closes the link, tells the site, and deletes the computer's id
  and secret.
- Detaching the computer on the site has the same effect: the app notices, forgets the link and shows
  **Not attached**.
- To attach again, pair again.

See also: [Features](features.md), [Connections and integrations](connections-and-integrations.md).
