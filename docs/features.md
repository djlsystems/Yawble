# Features

A short reference to what Yawble does. Yawble is a desktop app that runs teams of AI coding agents, using the agent
CLIs you already installed. It is agent-agnostic: each member picks its own CLI and model, and different agents work
side by side in one team. Each agent run is isolated in its own sandbox (Microsoft's MXC sandbox).

The sidebar has **Concierge**, **Documents**, **Backlog**, **Teams**, **Board**, **Monitor** and **Logs**, plus Settings.

## Teams and members

- A team has one **Manager** and any number of **members**. You tell the Manager what you want. It splits the work,
  tells members, and declares the workflow complete.
- **New team** asks for a team name, a repository (a folder on this computer, a new one the app makes, or
  **Clone from an address**) and its agents, each with a role and a CLI.
- A member's name is a job and a first name, for example "Developer Priya". Its id names its folder and branch.
- **Member settings** has General (CLI, model, credential, instructions) and Policy (see
  [Sandbox and policy](#sandbox-and-policy)).
- Team **Settings** has the tabs General, Members, Repositories, Environment and Integrations:
  - **Pause** starts no new runs; a running one finishes; queued work and triggers wait. **Resume** queues it again,
    oldest first.
  - **Archive** keeps a quiet team whole and paused. **Unarchive** brings it back paused.
  - **Delete** removes a quiet team's members, queue, triggers, skills and Board cards. A repository folder you
    picked is never touched.
  - **Reset** clears, per member, waiting instructions, the home folder (the CLI's own history), the clone, and the
    team's documents, as you choose.
  - **Environment** holds team environment variables; the next runs get them.
- **Workflows** lists each piece of work with **Nudge** (tell the Manager to pick it up) and **Stop runs**.
- Members ask you through the team page. **Approve** answers once; **Always approve for this team…** remembers it.
- One global run limit (default 4) covers every team. Runs and interactive sessions wait in one first-come,
  first-served queue.
- After a model usage limit, the Manager is told to pick the work up again once the limit resets, up to 3 times per
  workflow.
- When a run ends with its workflow still open and nothing else working on it, the Manager gets one reminder.
- **Skills** (Settings > Skills) are instructions agents load through the app, offered to chosen roles and teams.

## Agent CLIs

- Yawble is agent-agnostic. Supported: **Claude Code**, **Grok (xAI)**, **Codex (OpenAI)**, **GitHub Copilot**,
  **Antigravity (Google)**. Each member's CLI and model are its own, so one team can mix them, and an agent can be
  changed later (Member settings, or the Concierge when asked).
- Yawble does not ship or update them. It detects your own install (PATH, then the vendor's install locations) at
  every launch and runs it by absolute path.
- Each run gets the install folders read-only. The CLI's config and credential folders are blocked. Self-update is
  off in the sandbox.
- **Install** (on a CLI that is not found) and **Update** open a terminal that runs the maker's own install or update
  command: PowerShell install scripts for Claude Code and Grok, npm for Codex, winget for GitHub Copilot. Antigravity
  installs from its site. You sign in to each CLI in that terminal, your own way, then press **Detect again**.
- A member's **Credential** setting picks the sign-in its runs use:
  - Claude Code: the app's switch, a Claude subscription, or an Anthropic API key.
  - Grok: the app's switch, your Grok login, or an xAI API key.
  - The other CLIs: their own sign-in on this computer.
- Credentials are kept in the OS credential store and used by the run's gateway, never inside the sandbox.
- **Settings > Agents** lists detected CLIs (**Detect again**) and custom agents. A custom agent copies a built-in
  with its own name, default model and command-line arguments.
- **Terminal** opens an agent's own interface in the window. **Test** runs it with a diagnostic prompt that checks
  its version, folders and access.

## Sandbox and policy

- Every agent run, Concierge turn and guardrail check runs in its own sandbox.
- No model credential enters a sandbox. Each run has its own gateway on this computer. It is the run's only network
  path, and it adds the real credential to model requests outside the sandbox.
- The run gets a random run token. It works only for that run and stops when the run ends.
- Network: model and team traffic go through the gateway. Other hosts are reachable only if allowed; every refusal
  is logged.
- **Network requests**: when a run is refused a host name that is not on its allowlist, you get one **Needs you** item
  per team, member and host (repeats only count up). The agent can add why with its `request_host` tool. The items
  show in a **Needs you** strip above every view, and in the Board's **Needs You** lane instead when the Board is open
  (its team and member filters apply). Each item shows the member, team, host, the agent's reason (or what was
  refused: the method and a short path, without its query), how many times it was refused since when, and what a
  package registry is for (api.nuget.org: NuGet; registry.npmjs.org: npm; pypi.org and files.pythonhosted.org: pip).
  It has four buttons:
  - **Allow for this agent** adds the host to the member's loosen block;
  - **Allow for this team** adds it to the team's network allow list, and closes the team's other requests for that
    host. Every run of every member of that team gets the list's hosts, members hired later too, and no other team;
  - **Allow everywhere** adds it to the global allowed hosts, and closes every team's requests for that host;
  - **Deny** closes it; the agent is told (with the note you type above the buttons, if any), and that host is not
    asked again for that member for 24 hours. The 24 hours belong to that member: a member hired later with the same
    name is asked again.
  Only the exact host is allowed, only after your click, through the same checks as a policy change; it applies from
  the member's next run. After an allow the team's Manager is told "<host> is now allowed for <member>; retry what was
  blocked". A member whose own override narrows the host list also gets the host in its loosen block for an everywhere
  answer; a team's list is added after the override, so it reaches that member as it is. The Manager is told only once
  the member's effective hosts include it. If the policy store refuses the change, the item stays and shows why: in a
  remote view, a call the Cloud Link refuses says to answer it in the window, and any other failure shows its own
  error. **Allow for this team** is one write, checked against every member's run first, so a refusal changes nothing.
  - **The team's allow list** (see Sandbox and policy): a member's policy shows each team host as
    `network.allowHosts + <host> (team)`. The team's settings list who added each host, how and when; a **Remove** of a
    host that is no longer on the list says so.
  - **Never asked about:** IP addresses, local and private-network names, the app's own hosts, floor-rule hosts, and names that are not plain ASCII. A plain ASCII name has only letters, digits, hyphens and dots. Internationalized names, including their `xn--` form, a name ending in a dot, and an underscore all fail this test.
  - **Private-resolving names:** the gateway resolves a refused name first. A name that resolves only to loopback,
    private or link-local addresses (`localtest.me`, `x.nip.io`) is a plain refusal and raises no request.
  - **Plain http:** the gateway forwards only https. A request raised by a plain-http refusal says so, and the agent is
    told to use the https:// address: plain http is refused even once the host is allowed.
  - **Limit:** at most five different hosts wait per member at once. A repeat of a host already waiting only counts up.
  - **Answering:** one request takes one answer at a time; a second answer given meanwhile is refused.
  - **A member who leaves:** removing a member closes its waiting requests (state closed, in the Approvals log).

  Each request and its answer
  is in the **Approvals** log; the Concierge's status lists the open ones.
- **Tool requests**: an agent missing a developer tool asks with its `request_tool` tool, naming one from the app's
  fixed catalog: **.NET SDK 10**, **.NET SDK (current LTS)**, **Node.js LTS** or **Python 3**. Agents never give a
  package id or command. An unknown name gets the catalog's names back; a tool already installed is said to be there
  (the next run has it on PATH). Otherwise you get one **Needs you** item per team and tool (repeats only count up;
  names that install the same package, such as .NET SDK 10 and the current LTS, are one tool, also for **Not now**),
  with the member, the reason, the publisher, whether it installs for you or for the whole computer, and the exact
  command. The choices:
  - **Install** runs exactly `winget install --id <id> --exact --source winget --silent --accept-package-agreements
    --accept-source-agreements --disable-interactivity` (plus `--scope user` for Python), in the app on this computer,
    the same way as installing Git. Only from the window: never over the Cloud Link or from the `yawble` companion.
    Afterwards the app looks for the program again. When it is found, every open request for that tool closes and each
    team's Manager is told "<tool> is installed; retry what was blocked". When it is not, the item stays open with
    winget's message and **Try again**.
  - **Not now** closes it; the agent is told, and the team is not asked about that tool again for 24 hours.
  On Linux and macOS the item shows the command to run yourself; the app installs nothing. Each request, answer and
  result is in the **Approvals** log; the Concierge's status lists the open ones.
  The items show in the same **Needs you** strip above every view, and in the Board's **Needs You** lane when the Board
  is open (its team and member filters apply). A machine-wide tool says "Windows may ask for permission". While
  winget runs the item says what it is doing (installing, then looking for the program) and its buttons wait; when the
  program is found the item goes and a line says the tool is installed. **Not now** takes an optional note for the
  agent. A remote view shows the item with **Not now** only and says to install it in the Yawble window.
- Files: each member reads and writes only its own clone, home and temp folders. The app's data, the credential
  store, and other members and teams are denied.
- **Settings > Guardrails** is the global policy: Sandbox, Limits (idle and wall-clock timeouts), Files and folders,
  Network (allowed hosts, package restore for .NET, npm and Python) and Desktop.
- A member's **Override** can only tighten the global policy. Widening one agent goes through **Loosen…**: you
  preview the exact list and confirm it.
- A team's **network allow list** holds the hosts allowed for the whole team, each with who added it and when. Each
  member's run gets them as if they were in its loosen block, under the same floor rules; a host the floor refuses is
  never written to the list. The team's settings show the list next to the member policy, each host with **Remove**;
  a removed host is gone from the team's next runs (a member's own loosen block keeps what it has). Adding and removing
  are in the audit log. Hosts that **Allow for this team** wrote into members' loosen blocks before the team list
  existed stay in those blocks.
- Floor rules nobody can loosen:
  - model vendors' API and sign-in hosts are never allowed hosts;
  - no IP addresses or wildcards;
  - no grant may reach the app's data folder;
  - timeouts at most 24 hours.

## Concierge

- The Concierge is a chat with an agent that knows every team on this computer and acts for you.
- Conversations are kept. Each reply is one sandboxed run of the conversation's agent CLI.
- It can read teams, status, activity, workflows, documents, the backlog, the board, git state, integrations,
  usage and the doctor.
- It can tell a Manager, answer a team's question, create a team, pause or resume a team, nudge or close a
  workflow, and add, dispatch, move or comment on backlog items and cards.
- Git and team actions: delete, prune, push or sync branches, open or merge a pull request (GitHub repositories, with
  your `gh`),
  archive or delete a team, set environment variables, change an agent.
- Anything destructive first shows the exact list. It runs only after you say yes in a later message in the same
  conversation.
- It keeps notes across conversations, and can watch teams and review their updates. A review it starts on its own
  can read and recommend, never act.
- You can attach images to a message.

## Board, Backlog and Documents

- **Board**: four lanes, **To Do**, **In Progress**, **Needs You** and **Done**. Cards come from the teams' work and
  move with its status. You can move a card, comment, or ask the Manager about it. **Needs You** also lists the
  Connections to reconnect, the network requests and the tool requests waiting for you (see Sandbox and policy), with
  their buttons.
- **Backlog**: work you want done, most important first.
  - Add an item, read it and mark it ready, then dispatch it to a team's Manager or to **A new team…**.
  - When the team declares the work complete, only you mark it implemented.
  - Items can be archived and restored. **Dispatches** shows each dispatch with its token counts.
- **Documents**: each team has a shared documents folder for reports, specs and plans.
  - Every member reads and writes it as plain files.
  - You can browse, read, edit, upload (up to 25 MB), rename and delete there.

## Monitor

- A live wall of every team. Members orbit their team as discs with a state ring: running, waiting, needs you,
  idle.
- Each member shows its recent tool steps, changed files and what it is doing now.
- Counts across the top, an **Activity** panel of messages, and a **Workflows** panel per team.
- A timeline replays any moment. **● Back to live** returns.

## Logs

**Logs** (in the sidebar under Monitor) shows every log the app keeps, one at a time, as a table. The **log** icon on
a member's card opens it at that member's activity.

- **Logs:**
  - **All activity**: everything a member did or was asked to do, gathered from every log below. Every member's
    activity is auditable here.
  - **Accountability**: **Audit** (every change made from the window, a Cloud Link viewer, the Concierge or a paired
    program; reads are not recorded), **Cloud Link**, **Tool calls**, **Approvals**, **Agent changes**, **Host tasks**.
  - **Security**: **Network** (each run's gateway: model requests, connections allowed and refused) and **Runs** (how
    each sandboxed run ended).
  - **Usage**: **Model usage**, tokens per model request.
  - **Agent output**: **Transcripts**.
  - **Work history**: **Team messages** and **Board history**.
  - **Diagnostics**: what the app itself reported (`<data>/logs/`).
- Filters: team, member, each log's own columns, a date range, and free text across every column. Every column sorts
  either way. Pages load as the table scrolls; a row opens to show every field.
- **Prune…** deletes a log's entries older than a chosen age, or all of them. It first says how many entries and files
  go, their date range, and what is lost, and deletes only after **Delete**. Approvals still waiting for an answer are
  never pruned, and an answered one is kept by when it was answered. Host tool rows in Approvals are not pruned. Team messages and Board history are never pruned: the team pages, workflows and the Monitor's replay
  are built from them.
- **Retention…** sets how long each log is kept (or forever). Once a day the app deletes what is older. Defaults: a
  year for accountability, security and usage, 30 days for transcripts, 14 days for diagnostics.
- Over the Cloud Link the logs can be read, never pruned, and retention cannot be changed.

## Repositories and git

- Each team has a team repository on this computer: a folder you pick, a clone of an address, or one the app makes.
- A team can have several repositories, each with a base branch and an optional name members see.
- Each member works in its own local clone on its own branch, `members/<name>`. The Manager's branch is
  `team/<team>`.
- Clones have no remote and no credential helper. After each run the app cleans the clone's git config and fetches
  the member's branch into the team repository. It never runs git inside a clone an agent used.
- When the team repository has an origin, branches are pushed with your own git setup at each run's end. Pushes are
  never forced. The origin can be on any git host, or there can be none: a team repository with no origin keeps
  every branch on this computer.
- Pull requests are a hosting service's feature, not git's. The app reads, opens and merges them only for a
  repository whose origin is on GitHub, through your `gh`. For any other host it says so and leaves the pull request
  (or merge request) to that host's own site.
- git 2.38 or later is required. When it is missing, too old or not working, a banner at the top of the window says
  so, with **Download Git** and **Check again**; on Windows with winget, **Install Git** (or **Update Git**) installs
  Git for Windows for everyone on the computer, so Windows asks for permission. The app finds a git installed while it
  runs without a restart.
- **Git** on the team page: **Fetch**, **Sync now**, **Merge** (the team branch into the base branch, then pushed
  to origin), and per-member branch state. The app never forces, rebases or stashes.
- A banner shows when branches could not be brought up to date on their own.
- Each member's working copy has a read-only code browser.

## Integrations

Your CLIs (such as `gh`, `git` and `az`), local and remote MCP servers, web APIs described by OpenAPI, and websites
can be offered to teams' agents as tools. Every call runs on this computer, under rules: allowed, ask first, or off.
Calls that ask first wait for your approval. Sign-ins are Connections kept in the OS credential store, which agents
never see. Host tasks run named programs for a team, under a standing approval. See
[Connections and integrations](connections-and-integrations.md).

## Triggers

- **⏰ Triggers** on a team's page sends an instruction to a chosen agent as a new piece of work when the trigger
  fires.
- Kinds:
  - **Every**: an interval of at least 10 seconds.
  - **Cron**: 5 or 6 fields, with a timezone.
  - **Once**: at a set time.
  - **Folder**: when files in the team's documents or a folder on this computer change and settle. It can filter by
    file pattern.
  - **Event**: when an integration watch reports a new item.
- Options: **Only when the agent is idle**, **Enabled**, **Run now**.
- **Wake the Manager when a run ends**: only on a hand-back or failure (the default), every time, or never.

## Companion

`yawble` is a small program installed with the app. It lets a program on this computer, such as Claude Code, see and
work with your teams, as an MCP server (`yawble mcp`) or a CLI.

- It pairs once per program, with a code you approve in the window.
- Access levels: `read`, `act` and `destructive`. Destructive calls wait for your approval in the window.
- It talks only to the running app on this computer. Nothing of it is reachable over Cloud Link.
- Paired programs are listed and revoked in Settings > Companion.
- Setup is in the [README](../README.md).

## Updates

- Yawble checks GitHub Releases a minute after it starts and every 6 hours (**Settings > General > Updates**:
  **Check for updates automatically**, on by default), or when asked (**Check now**, or **Help > Check for updates**).
- A newer release's installer for this computer is downloaded to `<data>/updates/` and checked before it can run: its
  SHA-256 must match the release's `SHA256SUMS`, and it must carry a valid signature by DJL Systems, Inc. A file that
  fails either check is deleted.
- A line at the top of the window then offers **Restart to update**: the app quits, the installer runs silently over
  the installed app (your data stays), and the app starts again. While agents are working it asks first. **Later**
  hides the line until a newer version.
- With automatic checks off, a newer release is offered with **Download** instead.
- A Cloud Link viewer is told an update is available; it is installed on the computer itself. macOS and Linux get a
  link to the release page.

## Backups and doctor

- **Backups** (Settings > General) copy the app's database:
  - daily, before an update, and on **Back up now**;
  - 7 daily backups are kept by default, plus 5 before-update and 5 manual ones.
- Passwords and sign-ins are not copied. A backup records which Connections and secret fields existed, never their
  values.
- **Restore…** returns teams, cards and messages to that moment. It is applied at the next start.
- Old log entries are deleted daily, by each log's retention (see [Logs](#logs)).
- **Settings > Health** runs the doctor. Its checks are Database, Disk space, Sandbox, Git, Agent CLIs, Cloud Link,
  Connections, Teams and Logs.
- Fixes it offers: open a Connection to reconnect it, remove unused credentials, sync a team that is behind, archive
  a team that is quiet, install or update Git (Windows). Fixes that change something ask first.
- A banner at start-up says when this computer is not ready to run agents, and how to fix it.

## Guardrail check

- The check proves the fence from inside it. It runs a probe in a sandbox under a member's effective policy, or the
  global policy alone, with no model credential.
- The probe tries to:
  - write outside its folders;
  - read the app's data, the credential store and your home's secrets;
  - reach the network directly or through hosts the gateway does not allow;
  - reach host processes.
- The report shows PASSED or what failed. For each probe it gives the verdict, the evidence, and whether the gateway
  logged the refusal.
- Run it from a member's settings or **Settings > Guardrails** (**Run check**).
- It also runs as a command:
  `yawble-core guardrail-check --data-dir <app root>/data [--team T --member M | --cli claude|grok] [--json]`. The
  exit code is 0 for passed, 1 for failed and 2 for a usage error.

## Settings

| Tab | What it has |
|---|---|
| General | **Keep running when the window is closed** (Yawble stays in the tray), **Start Yawble when I sign in to this computer**, Backups |
| Agents | Detected CLIs and custom agents |
| Skills | Skills and which roles and teams they are offered to |
| Guardrails | The global policy and the global run limit |
| Integrations | CLIs, APIs, MCP servers, Connections, host tasks |
| Cloud Link | Pairing and remote access; see [Cloud Link](cloud-link.md) |
| Health | The doctor |
| Companion | The `yawble` companion: the line to add it to Claude Code, paired programs |
| System | Version, build, platform, sandbox backend and capabilities, About |

- A notification says when Yawble needs you: questions, approvals, network requests, tool requests and Connections to reconnect.
- Every call that changes something is recorded in an audit log, with who made it and how. Keys, tokens and
  messages are never recorded.
- Each run's output is kept in an owner-only transcript file. The terminal shows it as **Formatted** or **Raw**.
