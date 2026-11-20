# Features

A short reference to what Yawble does. Yawble is a desktop app that runs teams of AI coding agents, using the agent
CLIs you already installed. It is agent-agnostic: each member picks its own CLI and model, and different agents work
side by side in one team. Each agent run is isolated in its own sandbox (Microsoft's MXC sandbox).

The sidebar has **Concierge**, **Documents**, **Backlog**, **Teams**, **Kanban board**, **Monitor** and **Logs**, and the run-slots line at its bottom. **Settings** is the gear button at the top right, just right of the theme switch (on a phone, at the right of the top bar); **File > Settings** and Ctrl+, open it too.

The window has light and dark themes: the sun or moon button right of the Cloud Link icon in the title bar switches them (also **View > Theme** and **Toggle theme**); until you choose, the theme follows the OS, and **Theme: Use system setting** follows it again. The Monitor is always dark.

## Teams and members

- A team has one **Manager** and any number of **members**. You tell the Manager what you want. It splits the work,
  tells members, and declares the workflow complete.
- **New team** asks for a team name, a repository (a folder on this computer, a new one the app makes, or
  **Clone from an address**) and its agents, each with a role and a CLI. It starts from the defaults in
  **Settings > Teams**: the default Manager and the default members ticked **On new teams**; the others are offered as
  one-click **+ name** buttons. The same defaults make the team when the Concierge or the companion creates one, or a
  backlog item is dispatched to a new team, unless the caller names its own Manager, agent or members.
- A member's name is a job and a first name, for example "Developer Priya". Its id names its folder and branch.
- How members get their first names:
  - A Manager hires with the job word alone ("Developer"). The app picks a random first name from the member-name
    list (Settings > Teams) that no member of any team has, archived teams included; Managers do not count.
  - When no free name is found (the list is empty, or 10 random tries all hit used names), the hire is refused and
    the Manager hires again with a full name: a first name common in the list's country, unused on any team. A full
    name whose first name a member already has is refused the same way.
  - A default member or New Team row whose name is a job word alone gets a first name the same way when the team is
    made; when none is free, the team is not made and the window says so. A full name is used as typed.
  - Names typed in New Team or Add member are used as typed and checked only against their own team.
- The member-name list (Settings > Teams) holds one first name per line. Save drops blank lines, surrounding spaces and
  repeats (ignoring case); a line must be one word of the letters A-Z, 2 to 20 characters, a hyphen inside allowed, and
  other lines are listed and not saved. Under it, how many names a member already uses. **Generate** asks the
  Concierge's default agent once, with no Yawble tools and in a folder deleted afterwards, for 100 popular first names
  of the chosen **Country** (default United States). Its names replace the text, unsaved until **Save**. The country is
  saved with the list.
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
  - **Repositories** shows each repository's detected provider, with a provider override and an optional link pattern
    (see [Repositories and git](#repositories-and-git)).
  - **Environment** holds team environment variables; the next runs get them.
  - On a phone it is laid out like the app's Settings on a phone: a list of its sections (one full-width row each, with a
    chevron); a tap opens that section alone under a Back and its title, and Back returns to the list.
- **Workflows** lists each piece of work with **Nudge** (tell the Manager to pick it up) and **Stop runs**. Every open
  workflow is listed however old, with the newest completed, closed and expired ones; the cloud dashboard, the Concierge and the
  companion list them the same way. Ask the Concierge to close the open ones whose work is done: it checks each one
  and closes only those with nothing left, saying why (on this computer: the remote view's Concierge cannot close
  workflows).
- Each team's tile on **Teams** lists its 50 newest workflows and every open one, last updated first, scrolling inside
  the tile; a row opens that workflow.
- On the team page each member is one framed pane:
  - A title bar: face, name, a role chip and an agent chip, its state and how many instructions wait behind its run,
    and at the right a **☰** menu with every action: Open interactive CLI, Code (when the member has a clone), Settings, Logs,
    Stop and Remove.
  - The agent chip reads agent/model, e.g. `claude/claude-opus-5-5`: the model set on the member, else the agent's
    default model, else (the CLI picks its own) the model the member's last run used most, as the provider reported
    it; before a first run it reads `claude/default`. Its tooltip says which of these it is.
  - A row of tabs, **Report | Activity | Terminal**, with the run's details beside them (how its last run ended, its
    queue position, policy problems).
  - One view, the chosen tab's. **Report**: what the member last said, formatted. **Activity**: the Monitor's lines
    for that member (what it was told, its progress, runs and changed files, newest first). **Terminal**: its runs.
  - The terminal in a pane is never interactive: it is just the screen, always formatted, with no bar of its own and
    no input; an interactive session shows there read-only. **Open interactive CLI** opens the agent's own terminal
    window, where the session starts and takes input. The remote view has no Open interactive CLI.
  - The tab is remembered per member; the Report is the default. Only a terminal that is shown polls; several members'
    terminals can show at once. The Monitor, the palette and the remote view can open a member's terminal: the team
    page shows that pane's Terminal and scrolls it into view.
  - The members are equal tiles in a grid that wraps, like the Teams list: two per row at a 1280 px window by default.
    The gaps right of and below the tiles are width and height handles (drag, or the arrow keys on a focused handle;
    double-click for the default) that resize every tile at once. The size is the team page's own, kept by this
    computer or browser (each remote viewer keeps theirs). The pane fills its tile and its view scrolls inside it.
  - The last tile is **New member**: its **+** opens a small form in the tile: **Job and first name**, the **CLI**, and
    **Add** (or **Cancel**, or Escape). Add is offered once a name is typed; a refused add keeps the form and shows why.
    The remote view has the same grid; a read-only (Visitor) viewer cannot add.
  - On a phone the tiles are one per row, full width, each as tall as its content, with no handles, New member last;
    a pane is stacked the same way, the view at its own height. The Terminal tab opens the terminal full
    screen with one header row; its **⋯** holds the state line and Stop, and **✕** goes back to the Report. The
    transcript gets at least 60% of the screen's height. Every target is at least 44 px.
- Members ask you through the team page. **Approve** answers once; **Always approve for this team…** remembers it.
- One global run limit (default 4) covers every team. Runs and interactive sessions wait in one first-come,
  first-served queue.
- After a model usage limit, the Manager is told to pick the work up again once the limit resets, up to 3 times per
  workflow.
- When a run ends with its workflow still open and nothing else working on it, the Manager gets one reminder.
- **Workflow expiry.** An open workflow with no activity for the team's **workflowExpiryDays** (Team settings, 1 to 365,
  default 3; settable from the cloud like the other team settings) is closed by the app as **expired**, distinct from
  closed by a person and from completed. Activity is any message in it (an instruction, a run's report, a hand-back, a
  status change, an answer) and the person's **Extend**.
  - A workflow never expires while a run works it or waits in the queue for it, while a wake (wake_me) or an automatic
    resume after a usage limit is due in it.
  - A workflow waiting on the person's answer to a question is warned first: after N-1 days of quiet the Concierge
    posts one **Update** and Needs you shows "#<id> will expire tomorrow; it waits on your answer to question <n>" on
    that question. It expires at N days, and never sooner than a day after its warning (an app that was closed at
    warning time warns first).
  - **Extend** (the workflow list, or the Concierge's workflow_extend) starts an open workflow's idle clock again.
  - An open workflow's list row shows when it will expire (expiresAt; none while something works it) and the question it
    waits on the person's answer to (question; null when none). An expired one
    reads **expired** in the Workflows dialog, on the Teams tiles, in the Concierge's team_workflows and on the cloud
    dashboard. Its recorded end (endedAt) is its last real activity, not the expiry; expiredAt is when the app expired it.
  - New activity reopens an expired workflow, as it reopens a completed or closed one.
  - Closing a workflow in any way (completed, closed by a person, expired) ends every card of it not ended yet, with
    the time and how it ended (completed, closed or expired); an expired workflow's cards end at its last activity.
    Closed and expired move every card to Done, a waiting one too; completed leaves a card waiting on a person where
    it is. Telling the card's member something in the workflow makes the card live again.
- **Skills** (Settings > Skills) are instructions agents load through the app, offered to chosen roles and teams.
- **How a team learns** (B100). Agents keep no memory between runs, so a team keeps its lessons in one place: its
  **team_retros** skill, a team-scoped skill (offered to every role) with a short list of lessons, each
  "- <lesson> (<date>, workflow <id>)", under a few headings (Building and testing, Git, Working with the person ...).
  - After a workflow completes, the Manager looks back on it (a retrospective) and keeps at most a few lessons a later run
    should act on, merging them into team_retros with the Manager-only tool **team_retros_write** (the whole new text).
    Only an eventful workflow gets one: a small, uneventful workflow gets no retrospective, and a workflow that was
    closed or expired instead of completed never does (nor does a retrospective's own workflow).
    The tool writes only its own team's team_retros, nothing else; it refuses a text over 4000 characters (saying its
    size), and refuses a write when the text changed since that run read it (the person edited it meanwhile), so the
    Manager reads it again and merges into the person's text: a person's edit is never reset.
  - Live at once, no approval: every run of that team, the Manager's and the members', gets the text in its prompt under
    "Lessons from this team's earlier work". No other team's runs get it, and the Concierge does not.
  - **Settings > Skills**: team_retros is an ordinary team skill there: read it, edit it (your edit is kept by the next
    retro), see who last wrote it ("the retro of workflow <id>" or you) and its versions, newest first. Every write, the
    Manager's or yours, is kept as a version (who, which workflow, when; the last 100); **Restore** brings an older text back
    as a new version.
  - **Team settings > General** shows "Lessons: n, last updated <date> by the retro of workflow <id>" (or "by you"), with
    a link that opens the skill in Settings > Skills; "Lessons: none yet" before the first.

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
- **Network requests**: when a run is refused a host name that is not on its allowlist, the person is asked in
  **Needs you**. There is one request per team, member and host (repeats only count up). The agent can add why with
  its `request_host` tool. One member's waiting requests that share a registrable domain (the eTLD+1, by the bundled
  Public Suffix List) are one item: "crl3.digicert.com, ocsp.digicert.com, crl4.digicert.com (3 hosts)". The items
  show in a **Needs you** strip above every view, and in the Board's **Needs You** lane instead when the Board is open
  (its team and member filters apply). Each item shows the member, team, host or hosts, the agent's reason (or what
  was refused: the method and a short path, without its query), how many times it was refused since when, and what a
  package registry is for (api.nuget.org: NuGet; registry.npmjs.org: npm; pypi.org and files.pythonhosted.org: pip).
  The strip is one scrolling box at most half the window high (40% on a phone), so the view below stays usable.
  - **Answer all**: with two or more items waiting, a bar above them answers every item at once: **Allow all for
    their agents**, **Allow all for their teams**, **Allow all everywhere** or **Deny all**. The first click says what
    it will do; **Confirm** sends it. Each item is answered as its own buttons would with the host choice (never the
    domain allow); an item that does not offer that answer is skipped, and skips and failures are listed.
  - **This host / All of \*.domain**: above the buttons, an item offers what an allow adds: the host (or the item's
    hosts), or the domain allow "All of *.digicert.com", which covers every subdomain of digicert.com but not
    digicert.com itself. The domain choice is offered only when a domain allow may be: never under a shared suffix
    (github.io, herokuapp.com), never over a domain with public suffixes under it (amazonaws.com), never over a
    vendor's or the gateway's domains, and not for the registrable domain itself. On a grouped item that lists the
    registrable domain too, that host is allowed as itself.
  The item has four buttons; on a grouped item each answers every listed host:
  - **Allow for this agent** adds the host (or the domain allow) to the member's loosen block, and closes the member's
    other requests it covers;
  - **Allow for this team** adds it to the team's network allow list, and closes the team's other requests it covers.
    Every run of every member of that team gets the list's hosts, members hired later too, and no other team;
  - **Allow everywhere** adds it to the global allowed hosts, and closes every team's requests it covers;
  - **Deny** closes it; the agent is told (with the note you type above the buttons, if any), and that host is not
    asked again for that member for 24 hours. The 24 hours belong to that member: a member hired later with the same
    name is asked again. Deny on a grouped item denies every listed host and tells the agent once, in one message
    listing all of them.
  Only the host or the domain allow you chose is added, only after your click, through the same checks as a policy
  change; it applies from the member's next run. A later refusal of a host the domain allow covers is not asked about:
  the agent is told the host is allowed from its next run. After an allow the team's Manager is told "<host> is now allowed for <member>; retry what was
  blocked"; allows within 5 s of each other reach it as one message listing every host ("crl3.digicert.com and
  ocsp.digicert.com are now allowed for Priya; ..."), so it runs once. A domain allow is one message too, naming the
  pattern and every member whose requests it closed ("*.digicert.com is now allowed for Priya and Bo; ..."). A member whose own override narrows the host list also gets the host in its loosen block for an everywhere
  answer; a team's list is added after the override, so it reaches that member as it is. The Manager is told only once
  the member's effective hosts include it. If the policy store refuses the change, the item stays and shows why: in a
  remote view, a call the Cloud Link refuses says to answer it in the window, and any other failure shows its own
  error. **Allow for this team** is one write, checked against every member's run first, so a refusal changes nothing.
  - **The team's allow list** (see Sandbox and policy): a member's policy shows each team host as
    `network.allowHosts + <host> (team)`. The team's settings list who added each host, how and when; a **Remove** of a
    host that is no longer on the list says so.
  - **Never asked about:** IP addresses, local and private-network names, the app's own hosts, floor-rule hosts, and names that are not plain ASCII. A plain ASCII name has only letters, digits, hyphens and dots. Internationalized names, including their `xn--` form, a name ending in a dot, and an underscore all fail this test.
  - **Private-resolving names:** the gateway never looks up a refused name, so an agent cannot make the app query a
    domain of its choosing. An allow looks the name up once. A name that resolves only to loopback, private or
    link-local addresses (`localtest.me`, `x.nip.io`) cannot be allowed, since agents can never reach those
    addresses; Deny closes such a request.
  - **Plain http:** the gateway forwards only https. A request raised by a plain-http refusal says so, and the agent is
    told to use the https:// address: plain http is refused even once the host is allowed. Only a refused plain-http
    request to a real outside host carries this note. A gateway virtual host (`*.yawble.internal`), a model vendor or
    sign-in host, and an https refusal (CONNECT or `https://` absolute-form) never do.
  - **Limit:** at most five different hosts wait per member at once, counted by host even when they are one grouped
    item. A repeat of a host already waiting only counts up.
  - **Domain allows in the policy editors:** the global allowed hosts (Settings > Guardrails) and a member's Loosen…
    block take an exact host or one leading `*.` over a registrable domain (`*.digicert.com`), and show the core's own
    reason under an entry it refuses (`*.com`, `*.github.io`, `a.*.b`). The team's allow list shows a domain allow with
    what it covers ("every subdomain of digicert.com").
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
    the same way as installing Git. Only from the window: never over the Cloud Link or from `yawble-companion`.
    Afterwards the app looks for the program again. When it is found, every open request for that tool closes and each
    team's Manager is told "<tool> is installed; retry what was blocked". When it is not, the item stays open with
    winget's message and **Try again**.
  - **Not now** closes it; the agent is told, and the team is not asked about that tool again for 24 hours.
  On Linux the distribution's package manager comes from `/etc/os-release`: dnf on Fedora and RHEL, pacman on Arch,
  apt-get on Debian and Ubuntu.
  - When `pkexec` is installed (`/usr/bin/pkexec` or `/bin/pkexec`, never taken from PATH), a graphical session is
    running (`WAYLAND_DISPLAY` or `DISPLAY`) and the distribution is one of those, the item offers **Install**, says "Your system asks for your password" and shows the exact command:
    `pkexec /usr/bin/dnf install -y <packages>`, `pkexec /usr/bin/apt-get install -y <packages>` or
    `pkexec /usr/bin/pacman -S --needed --noconfirm <packages>`. The package manager is always run from that fixed path,
    never found on PATH, and the packages are always the catalog's. Only from the window, one install at a time, with
    its progress and result in the item and the **Approvals** log, like winget on Windows.
  - Closing the password window (pkexec exit code 126) or not being authorized (127) installs nothing: the item stays
    open and says so in plain words, with **Install** again; it is not reported as a package failure.
  - Afterwards the app looks for the program again where a run's PATH looks (`/usr/local/bin`, `/usr/bin`, `/bin`,
    which every sandbox reads). Found: every open request for that tool closes and each Manager is told
    "<tool> is installed; retry what was blocked". Not found: the item stays open with the package manager's output
    and **Try again**. A program found only under your home folder (such as `~/.dotnet/dotnet`) is not one a run can
    use: the item says the sandbox cannot see it.
  - Without pkexec, a graphical session or a known distribution, the item shows the command to run yourself
    (`sudo apt-get install -y ...` for an unknown distribution) and **Not now**; the app installs nothing.
  - **Check again**, on Linux and macOS, looks for the program again after you installed it yourself: found, the
    requests close and the Managers are told, as after an Install; not found, the item stays open and says so. It
    installs nothing, so a Cloud Link view may use it too.

  On macOS the item shows the command to run yourself (`brew ...`); the app installs nothing. Each request, answer and
  result is in the **Approvals** log; the Concierge's status lists the open ones.
  The items show in the same **Needs you** strip above every view, and in the Board's **Needs You** lane when the Board
  is open (its team and member filters apply). A machine-wide tool says "Windows may ask for permission". While
  winget runs the item says what it is doing (installing, then looking for the program) and its buttons wait; when the
  program is found the item goes and a line says the tool is installed. **Not now** takes an optional note for the
  agent. A remote view has no **Install** and says to install it in the Yawble window; it shows **Not now**, and on
  Linux and macOS **Check again** too.
- Files: each member reads and writes only its own clone, home and temp folders. The app's data, the credential
  store, and other members and teams are denied.
- **Settings > Guardrails** is the global policy: Sandbox, Limits (idle and wall-clock timeouts), Files and folders,
  Network (allowed hosts, package restore for .NET, npm and Python) and Desktop.
- A member's **Override** can only tighten the global policy. Widening one agent goes through **Loosen…**: you
  preview the exact list and confirm it.
- A team's **network allow list** holds the hosts allowed for the whole team, each with who added it and when. Each
  member's run gets them as if they were in its loosen block, under the same floor rules; a host the floor refuses is
  never written to the list. The team's settings show the list on their **Network** tab, and each member's settings
  show it on the **Policy** tab next to the member's own policy, each host with who added it, how and when, and **Remove**;
  a removed host is gone from the team's next runs (a member's own loosen block keeps what it has). Adding and removing
  are in the audit log. A host in a member's own loosen block stays there until it is removed from that member's
  policy.
- Floor rules nobody can loosen:
  - model vendors' API and sign-in hosts are never allowed hosts;
  - no IP addresses or wildcards;
  - no grant may reach the app's data folder;
  - timeouts at most 24 hours.

## Concierge

- The Concierge is a chat with an agent that knows every team on this computer and acts for you.
- Conversations are kept. Each reply is one sandboxed run of the conversation's agent CLI.
- A new conversation starts on the default agent from Settings > Concierge while that agent still exists; otherwise
  on the first CLI that is installed and signed in. Existing conversations keep their own agent.
- It can read teams, status, activity, workflows, documents, the backlog, the board, git state, integrations,
  usage and the doctor.
- It can tell a Manager, answer a team's question, create a team, pause or resume a team, nudge, close or extend a
  workflow, and add, dispatch, move or comment on backlog items and cards.
- Git and team actions: delete, prune, push or sync branches, open or merge a pull request (GitHub with your `gh`,
  GitLab with your `glab`, Azure DevOps with your `az repos`; see [Repositories and git](#repositories-and-git)),
  archive or delete a team, set environment variables, change an agent.
- Anything destructive first shows the exact list. It runs only after you say yes in a later message in the same
  conversation.
- It keeps notes across conversations, and can watch teams and review their updates. A review it starts on its own
  can read and recommend, never act.
- You can attach images to a message.

## Board, Backlog and Documents

- **Kanban board**: four lanes, **To Do**, **In Progress**, **Needs You** and **Done**. Cards come from the teams' work and
  move with its status. You can move a card, comment, or ask the Manager about it. **Needs You** also lists the
  Connections to reconnect, the network requests and the tool requests waiting for you (see Sandbox and policy), with
  their buttons. The card a member is working on says **Heavy work** while that member holds the heavy-work lease, and
  **Waiting for heavy work** (with its place in the queue) while it waits; the tooltip says what for.
- **Discuss with the Concierge** sits on a member's question, a network request, a tool request, an open Kanban card,
  the team page's branch sync notice, the Git notice and the not-ready-to-run-agents notice:
  it starts a Concierge chat about that team with the request written out (the agent's own words quoted), so you can
  talk it through first. The Concierge advises; you still answer the request yourself.
  - On a team with a tester, verify and test cards go to the tester. The tester tests the developer's branch as the
    developer left it, and reports what ran and the result on the card and in its hand-back.
  - A developer's report never closes a verify card. A tester's card is a card the tester holds now or was ever
    assigned. If anyone other than that tester (the Manager or a developer) moves it to Done before the tester has
    tested it, the move needs a reason (for example "no tester on this team" or "the person asked to skip testing").
    Without one the app refuses the move. With one, the reason shows on the card as a comment. Your own moves never
    need a reason.
  - If someone else's hand-back or finished run would put a tester's card in Done (for example after it was given
    back to a developer), the card stays In Progress. A comment says it is waiting for the tester.
  - When the workflow is declared complete, a tester's card the tester has not tested stays out of Done. A comment
    says why.
  - The tester has tested a card only when it hands back, or one of its runs finishes having handed back, after the
    card was last assigned or given new work. A run that failed, a run that ended without a hand-back (for example one
    that was stopped or cut off), a run from before that assignment, and its progress notes or comments do not count.
  - How a tester is recognised: a member other than the Manager whose name starts with "Tester" (Tester Wren), or
    whose job title contains the word test, tester, testers, testing or QA, in any case. The job title is the job
    text before its first colon ("Tester: builds and tests"), or its first line if it has no colon. Only whole words
    count, so "latest" or "contest" do not make a tester. "tests" and "tested" do not count either, so
    "Developer: writes the code and its unit tests" is not a tester.
  - Teams with no tester are not asked for a reason.
- **Time accounting**: every card and every workflow has one overall time, from when it was created to when it was
  last done, and a breakdown of where that time went. The core computes both; the board, the Workflows list, the
  Concierge (team_workflows, board_card), the phone and the cloud show the same numbers.
  - A card's overall time runs from when it was planned (created) to the last time it entered Done. A workflow's runs
    from its first message to when it was completed, closed or expired; an expired workflow ends at its last activity.
    While open, the time runs to now. A reopened card or workflow moves its end later, and the time it spent done
    before the reopen counts as **Was done**.
  - A card records its planned time and its started time: the first time it has a member and is queued or told.
    Every change of a card's state is recorded with its time and category.
  - Every moment is in exactly one category, so the categories add up to the overall time: **Agent working** (a run
    is active on it), **Stalled** (a run is active but shows no sign of work for 10 minutes or more: no tool call, no
    progress and no model request going out or answer streaming back; from its last sign of activity to its next
    one, or to the run's end), **Waiting on you** (an open question to you),
    **Waiting for a slot** (queued for a run slot, or waiting for the heavy-work lease), **Waiting on a teammate**
    (handed back, or its run ended, and it waits for the Manager or a tester), **Blocked** (blocked, or its run failed),
    **Scheduled** (parked until the wake it asked for), **Not started** (planned, no member yet), **Idle** (open,
    nobody working it, nothing scheduled) and **Was done**.
  - A workflow's cards overlap, so they are not added up: each moment takes the highest category among its cards
    not in Done (and the Manager's own runs), in the order Agent working, Waiting on you, Stalled, Waiting for a slot,
    Waiting on a teammate, Blocked, Scheduled, Not started, Idle, Was done. Its categories add up to its wall-clock
    time.
  - A card's or a workflow's timeline (timeline.get) gives one row of category bands per card, the workflow's summary
    row, and markers for planned, started, reopen, hand-back, question asked and answered, a run ended by the idle
    watchdog, and expired. A Visitor can read it.
  - Cards from before time accounting have their timeline rebuilt from their history (planned, assigned, told,
    progress, handed back, done, moved, run ended, failed), marked "rebuilt from history"; a run the idle watchdog
    ended is Stalled from the line before it to its end.
  - The overall time shows as one quiet line (like 1h 12m 05s) on a card's bottom row, under each workflow in the
    Workflows dialog and on the workflow rows of the Teams-list tiles. An open card or workflow counts on each second.
  - Clicking or tapping the overall time (or Enter on it) opens the **Timeline** dialog; it does not open the card.
    The dialog shows:
    - the overall time, created → ended (or open, still counting) and how it ended;
    - the planned and started line (for a workflow: first message and first card started), with how long it waited
      before it started;
    - the breakdown table: each category with time, its share (the shares add up to 100%) and the total;
    - "rebuilt from history" when the timeline was rebuilt from an older card's history;
    - the timeline: for a workflow an **All cards** summary row on top, then one row per card; for a card one row.
      Each row is drawn as category bands on one time axis, with the start time on the left and the end time (or
      now) on the right.
  - Pointing at, tapping or tabbing to a band or a marker says it in the readout under the timeline (its category,
    from and to, and its length; a marker's kind and time). **Events** lists the markers as text.
  - Markers sit on a row under the bands: planned, started, reopen, hand-back, question asked and answered, a run
    ended by the idle watchdog, and expired. The legend lists the categories that appear.
  - **Stalled** is drawn in Agent working's colour, hatched, in the bands, the legend and the table.
  - A long quiet stretch (2 hours or more and over a quarter of the whole, with nothing changing) is drawn shorter
    behind a break mark in each row. The first row shows its real length on the mark (2h 37m), and so does the time
    axis (≈ 2h 37m); that label never covers the start and end times. A band that runs across a break says in the
    readout how much of it is drawn shorter.
  - On a phone the axis scrolls sideways inside the dialog; the page does not. Bands and markers keep their size.
  - An open item's timeline is asked again every 30 seconds while the dialog is open.
  - A Visitor sees the overall times and the same Timeline dialog.
  - Time only: no tokens or cost.
- **Backlog**: work you want done, most important first.
  - Add an item, read it and mark it ready, then dispatch it to a team's Manager or to **A new team…**.
  - When the team declares the work complete, only you mark it implemented.
  - Items can be archived and restored. **Dispatches** shows each dispatch with its token counts.
  - An opened item lists each team it went to on one line: **Open team**, **Its board**, and **workflow #N** (that
    team's latest workflow for the item), which opens the Workflows page with that workflow selected.
- **Workflows**: every team's workflows in one table, in the sidebar directly below Backlog (its badge counts the
  open ones).
  - **Ways in**: the sidebar; the board's **Workflows** button next to Backlog (filtered to the board's team when it
    shows one, else all teams); **All workflows** in a team's Workflows dialog and on its tile in the Teams list; a
    Backlog item's **workflow #N**. A link of the form `#workflows?t=3&s=open&q=…&sel=12` opens it filtered.
  - **Filters**: a team (or all teams), free text over the id, title, item label, team and member names (filters as
    you type, the matches highlighted), and the status chips **Open**, **Needs you**, **Done**, **Closed/Expired** and
    **All**, each with its count. A summary line ("5 open · 1 needs you · 12 completed today") with a tiny sparkline of
    today's starts and completions per hour. The filters are remembered and kept in the page's address.
  - **Columns**: **Status** (an icon and a word: Running, Needs you, Waiting (for a slot, the lease or a wake-up),
    Idle (its tooltip counts down to its expiry), Stopped, Completed, Closed, Expired; a workflow someone is working
    breathes gently, and holds still when the system asks for reduced motion); **Workflow** (#id and the clean title: a dispatched item's own
    title); **Item** (the backlog label, a link to the Backlog with that item selected, an archived one in the
    archive; "—" when it was not dispatched from the backlog); **Team**; **Who** (the faces of the members working it
    now, then its owner); **Elapsed** (the overall time, ticking while open; it and the timeline icon beside it open
    the Timeline dialog; under it a thin bar of where the time went, in the Timeline's colours, named on hover);
    **Last activity**.
  - **Order**: what needs you first, then running, then the other open ones, then the ended ones by when they ended.
    Click a header to sort by that column; again to reverse.
  - **The pane** (click a row, or Enter; Esc closes it; up and down arrows move the selection): the status, #id,
    title, item link, team (Open team, Its board); the item's spec (a few lines, **Show all**); the cards by lane, each
    with its member, status and own time (a card opens in the board's card panel); the last messages newest first
    (commit IDs are links); the overall time with its breakdown and a **Timeline** button.
  - **Actions** in the pane, each asked in the app's confirm dialog: **Nudge** (nobody is working it), **Extend** (it
    will expire), **Stop** (what runs in it) and **Close**. A Visitor sees none of them.
  - A new workflow's row eases in; a workflow that completes turns its mark into a check. With nothing open, a short
    note and a button to the Backlog.
  - On a phone the list comes first and an opened workflow takes the page (**← Workflows** goes back). Through the
    Cloud Link a Visitor sees the page read-only; the item link and the spec show only when the viewer may read the
    backlog.
  - The Workflows dialog and a team tile's workflows show each workflow's item label as the same link, and a
    workflow node's panel on the Monitor names its backlog item.
  - Time only: no tokens or cost.
- **Documents**: each team has a shared documents folder for reports, specs and plans. The list shows each entry's
  size and when it was last modified.
  - Every member reads and writes it as plain files.
  - You can browse, read, edit, upload (up to 25 MB), rename and delete there.

## Monitor

- A live wall of every team. **Nodes**: each team is a hub in its own colour; its members orbit it as discs with
  their face and a state ring (running turns, waiting creeps round, blocked, needs you and failed throb, idle is a
  thin line). The Manager wears an outer ring with ticks; queued instructions orbit a member as small dots.
- **Workflow nodes**: each open workflow is a small node tied by dashed lines to the members working it (to its team
  while nobody is on it). It flashes green and drifts away when it completes, fades with a cross when it is closed,
  and shows a "!" while it is blocked. Under it, its number and title ("#3848 B081 Remote UI: xterm's…"), cut to
  30 characters, with a backlog workflow's "Backlog item " left out; hover it for the whole title, click it to open the
  workflow. The label is never smaller than 8 px on screen and dims with its team. When the window is phone-wide
  (under 760 px) and the wall is zoomed out, it shows only "#3848". A desktop window always shows the title, even
  when the sidebar and Activity leave the wall narrow. Labels never cover each other, a member's disc or name, a
  team's name pill, or any team's code map (its folders, folder names and files). A workflow square drawn on its
  team's map stays on top and takes the hover and the click.
  Where a label has no room, it moves above its node or off to one side. With no room there either, it shrinks to
  "#3848", or it is left out and the hover still gives the title. Blocked and working workflows go first, then the
  newest. There is no Workflows panel on the wall.
- **Rhythm**: each member shows its recent tool steps rising off it and what it is doing now; a busier member pulses
  faster, a command or test run shows a ring filling while it runs, then a green or red burst by its exit code when
  that is known, and thinking shimmers softly.
- **Cards**: a Manager's instruction flies to the member with its card number (#373). The hand-back returns along the
  same line with a ✓, or a ✗ when the run failed or was blocked.
- **You node**: at the edge of the wall. Questions, approvals and network or tool asks that wait for you fly to it,
  and it glows with their count until they are answered. Click it to open the Kanban board (every team), whose **Needs You** lane lists them.
- **Focus a team**: click a team's hub and the wall zooms to it and dims the rest; a chip under the counts names it.
  Press **Esc**, click the empty wall or the chip's **×** to see every team again. Not remembered.
- **Density** beside the camera controls: **Calm** moves only the big moments (handbacks, failures, needs you,
  commits, merges), **Normal** (the default) what the teams do without the smallest steps, **Lively** every step and
  message. Remembered. Calm also shows fewer code-map files (72 in all) and at most 3 resting commits per team.
  Reduced motion is respected at every density.
- **Sound** (the speaker beside the density): off unless you turn it on, and remembered. Then a short, quiet tone
  plays for needs you, a failure or block, a finished workflow, a hand-back, a commit, a merge, a landing on main and
  a push, each kind at most once every 1.5 s. Steps and messages make no sound, nor does a replay.
- On a phone or in the remote view the camera, the cog and the legend share one row; the density and the sound sit
  at the top of the cog's panel, so nothing scrolls sideways at 375 px.
- **Code map**: each team has one tree round its hub, past its members: the repository, then its folders, then their
  files. It builds up over the life of a workflow.
  - A new file pops in. An edit lights a file bright, a read lights it faint. A folder glows warmer the busier its
    files are.
  - A line runs from each member to every file it touched. A file two members touched wears a double ring. A file a
    commit confirmed wears a ring; hover a file for its path, who touched it and its commit.
  - A file leaves when every workflow that touched it has ended, or after it has been idle for 2 minutes. It fades
    out over the last 20 seconds.
  - A large map collapses to its busiest folders, each with **+N** for the rest, and a **+N folders** node. All the
    maps together show at most 240 files, and nodes off the screen are not drawn.
- **Code events** on the wall, also listed in **Activity** and marked on the timeline:
  - **commit**: the member's changed files gather into a dot that travels along the member's spoke to the team's hub
    and rests there for a while. Hover it for the message and the short sha.
  - **merge into the team branch**: a burst along the hub's spokes.
  - **landed on main**: a larger burst.
  - **push**: a pulse that runs off the edge of the wall.
- A focused team's code map, commits and merges stay bright; the other teams' maps dim with them.
- With reduced motion nothing travels, pops or pulses: the map just updates, a commit's dot is simply at the hub, a
  comet is a still line with its label, and states are steady marks.
- Counts across the top and an **Activity** panel of messages.
- A timeline replays any moment: the map, the commits and the merges play at their times. **● Back to live** returns.
- The camera is **Auto fit**, **Fit** once, or **Pinned**. The cog beside them opens **Network forces**: link distance,
  link strength, repulsion, gravity, friction and **Team spacing** (how far apart the teams sit), each a multiplier
  (0.25× to 3×) on the wall's own layout. They apply as you drag and are remembered; **Reset** goes back to the
  defaults: distance 0.3×, strength 2.75×, repulsion 0.35×, gravity 2.4×, friction 0.45×, team spacing 1×.
- The teams sit on an oval sized so that, at team spacing 1×, the two nearest teams' clusters (members, workflow
  nodes and their labels, and the code maps actually drawn) are close but do not overlap. A code map fans out away from
  the other teams, so only the part of it that wraps back toward a neighbour adds to the room between them.
- Under Auto fit at team spacing 1×, once the wall comes to rest it measures the clusters as drawn and slides them so
  the nearest two are about 40 px apart, the same every time the wall opens. Each team's hub stays at its place on
  the oval. Other spacings scale what was measured at 1×. Pinned, focused, or after you move the wall, nothing slides.

**Code map sources.** The app records what really reaches the team repository: each new commit a member's run brings
back (its files and whether each was added, modified, deleted or renamed), each merge into the team branch, each Merge
to main and each push to origin, with the workflow it came from. It reads them in the team repository, never in a
member's clone. Commit diffs are the truth for a file's existence and status. Transcript steps are only the immediate,
provisional signal, and the only source for reads.

## Logs

**Logs** (in the sidebar under Monitor) is a page that shows every log the app keeps, one at a time, as a table. **Logs** in
the ☰ menu of a member's pane opens the same table as a dialog, at that member's activity.

- **Logs:**
  - **All activity**: everything that happened, by members, teams and the app, gathered from every log below, with
    every Audit entry. Its Member column is the member an entry is about; else the team, as `<Team>` (a team created,
    deleted or changed, a backlog item for it); else `app` (settings, triggers without a member, the Cloud Link,
    backups...). Every member's activity is auditable here. Audit entries keep the team and member names they were
    written with, so an entry about a team or member deleted since still says who it was about.
  - **Accountability**: **Audit** (every change made from the window, a Cloud Link viewer, the Concierge or a paired
    program; reads are not recorded), **Cloud Link**, **Tool calls**, **Approvals**, **Agent changes**, **Host tasks**.
  - **Security**: **Network** and **Runs** (how each sandboxed run ended). Network is what each run's gateway
    decided: host connections allowed and refused, errors and requests answered with anything other than 2xx,
    credential problems, rate limits, and each run's first successful model request per model host (`model`, `grok`,
    `xai`, `copilot`, `codex` `.yawble.internal`) and the first after each credential problem or rate limit on that
    host. The run's other successful model requests are not rows here: they are counted in **Model usage**. Rows
    written one per model request before this are removed when the app updates by the same rule, so an old run still
    shows the success that followed a problem.
    A plain-http request to a host that is not allowed (`host-denied`) and a refused HTTPS connection
    (`connect-denied`), refused again in the same run, are both counted on one row: that run's first row for the host
    (for `connect-denied`, the host and port it asked for). A host's plain-http and HTTPS refusals stay two rows. Its
    **Times** column says how many times, and in **All activity** the row's detail ends in ` (Nx)` when it counts
    more than one (` (5x)` for five). Refused paths are not folded into one row: the first few of each in a minute are
    written, and the rest are counted in one more row written at the next such refusal after the minute. Every other
    row's Times is 1.
  - **Usage**: **Model usage**, tokens per model request (`usage_events`).
  - **Agent output**: **Transcripts**.
  - **Work history**: **Team messages** and **Board history**.
  - **Diagnostics**: what the app itself reported (`<data>/logs/`).
- Filters: team, member, each log's own columns, a date range, and free text across every column. The Member filter
  picks a member (that member's entries only), a team as a whole (its members' entries and the team's own), or, in All
  activity, `app` (the app's own entries only); picking one clears the others. Every column sorts either way. Pages load as the table scrolls; a row opens to show every field.
- **Prune…** deletes a log's entries older than a chosen age, or all of them. It first says how many entries and files
  go, their date range, and what is lost, and deletes only after **Delete**. Approvals still waiting for an answer are
  never pruned, and an answered one is kept by when it was answered. Pruning Approvals also removes answered host tool
  requests (installed, or Not now) with their history. A tool request still waiting or installing, a tool's Not now
  from the last 24 hours (for the team and that tool under any of its names, such as dotnet-sdk-10 and dotnet-sdk-lts),
  and a host denial from the last 24 hours are kept even when pruning all of it, so the 24-hour quiet holds. Team
  messages and Board history are never pruned: the team pages, workflows and the Monitor's replay
  are built from them.
- **Retention…** sets how long each log is kept (or forever). Once a day the app deletes what is older. Defaults: a
  year for accountability, security and usage, 30 days for transcripts, 14 days for diagnostics.
- Rows are redacted when they are read: known secrets and token-shaped text show as `[redacted]`. The Logs page, `logs.query` over the Cloud Link and the live tail all use the
  same redaction, so a row reads the same everywhere.
- Over the Cloud Link the logs can be read, never pruned, and retention cannot be changed.
- A Cloud Link viewer can tail the logs live (`logs.tail`): new entries are pushed as they are written, filtered on
  this computer by level (warnings, errors and workflow events by default, or everything), team and member, and
  redacted as above. It stops when the viewer stops it or disconnects.

## Repositories and git

- Each team has a team repository on this computer: a folder you pick, a clone of an address, or one the app makes.
- A team can have several repositories, each with a base branch and an optional name members see.
- Each member works in its own local clone on its own branch, `members/<team>/<name>`, for example
  `members/yawble-engineering-team/developerines`. The Manager's branch is `team/<team>`. `<team>` is the same team
  slug in both, so two teams on one repository with same-named members never push to the same branch.
- Claude Code members have AI attribution turned off in their settings. For every agent, the app refuses to fetch back (or merge) commits that carry an AI attribution trailer (a line starting with `Co-Authored-By:` naming an AI vendor, model or tool, `Claude-Session:`, or "Generated with" an AI tool), tells the member to amend its own unpushed commits, and never rewrites them.
- A member whose clone is still on an older `members/<name>` branch moves to `members/<team>/<name>` at its next
  start, with its commits: the app creates the new branch at the old one's commit and pushes it. The old branch on
  origin is left for you to delete in the Git dialog, where it is listed as old.
- Git cannot hold a branch `members/<x>` beside branches `members/<x>/...`. So an old `members/<x>` branch whose
  name is a team's slug (for example another team's member `members/docs` and a team named Docs) blocks that team's
  `members/<slug>/<member>` branches. Before a member branch is made or pushed, the app looks for such a branch in the
  team's repositories, on origin (as of the last fetch), in the shared view and in the member's clone. The app changes
  nothing then: the member's start fails with a plain message naming the branch, the repository it is in, and what to
  do. Nothing is deleted for you, and another team's branches are never touched. An old-style member that cannot move
  because of such a branch stays on its old branch and keeps working there. At a run's end, a branch that such a branch
  on origin blocks is not pushed, and the push report says why instead of git's error.
- When a member is still on the blocking branch (its old `members/<name>`, of this team or of another team on the same
  repository), the message says whose it is: that member's next start moves it to `members/<its team>/<name>`, which
  frees the name, so start that member first. Such a branch is never deleted from the Git dialog, which is also true
  for any branch another team's member is on.
- A member named like its own team's slug (member Docs of team Docs on `members/docs`) moves through its old branch at
  its next start: the branch is renamed to `members/docs/docs` here and in its clone (the old one cannot be kept beside
  it), and at that run's end origin's old `members/docs` is replaced by the new branch when the new one holds all its
  commits. If origin refuses that, the push report says so.
- The Git dialog lists a blocking branch in its own section, with the repository and whether it is here or on origin.
  **Delete…** goes through the same plan-then-Delete as any other branch; for a branch a member is still on there is no
  Delete, and the section says which member's next start moves it.
- Clones have no remote and no credential helper. After each run the app cleans the clone's git config and fetches
  the member's branch into the team repository. It never runs git inside a clone an agent used.
- When the team repository has an origin, branches are pushed with your own git setup at each run's end. Pushes are
  never forced. The origin can be on any git host, or there can be none: a team repository with no origin keeps
  every branch on this computer.
- Everything above works with any git remote, or none: branches, fetch, sync, merge, push, delete and Tidy are plain
  git. What depends on the host is only the web links and pull requests below.
- Each repository has a **provider**, detected from its origin: GitHub, GitLab (gitlab.com, or a host with "gitlab" in
  its name), Bitbucket, Azure DevOps (dev.azure.com, `<org>.visualstudio.com`, ssh.dev.azure.com), Gitea (also Forgejo
  and codeberg.org), or none (plain git: any other host, a local folder, or no origin). https, ssh and `git@host:`
  addresses all work.
- **Team settings > Repositories** shows each repository's detected provider. You can override it with **Provider**, and
  set a **Link pattern** for a self-hosted or unknown host, then **Save**. The pattern is one or more address templates
  separated by spaces. Each template is used for the link whose placeholders it holds exactly:

  | Link | Placeholders |
  | --- | --- |
  | commit | `{commit}` |
  | branch | `{branch}` |
  | file at a commit | `{commit}` and `{file}` |
  | compare | `{from}` and `{to}` |
  | pull request | `{pr}` |

  `{base}` (the repository's web address) and `{path}` (its owner/name path) may appear in any template. For example
  `https://git.example.com/{path}/commit/{commit} https://git.example.com/{path}/src/{branch}` gives commit and branch
  links. With no provider (none: detected or chosen), the other kinds get none. On a provider's host (detected or
  chosen) a pattern replaces only the kinds it covers and the other kinds keep the provider's own links: with a GitHub
  origin and a commit-only pattern, a pull request still links to GitHub's `/pull/<n>`. **As detected** and a blank
  pattern go back to detection.
- **Open on GitHub** (or GitLab, Bitbucket, Azure DevOps, Gitea; **Open in the browser** for a link pattern): the Git
  dialog links the base and team branches, the member branches, their commits (the short commit is the link), the
  conflicting files at the team branch's commit, and **Compare with** the base. A link shows only when the provider
  (or a pattern) can build it and the commit or branch is on origin. A commit not pushed yet has no link, and a
  repository with no provider and no pattern has none. Links open in your browser.
- Pull requests (merge requests on GitLab) are a hosting service's feature, not git's. The app opens, lists and merges
  them for GitHub through your `gh`, GitLab through your `glab` and Azure DevOps through your `az repos`, each installed
  and signed in on this computer. Bitbucket, Gitea and plain git have none from here. The Git dialog's **Pull
  requests** line says whether they work for the repository, and if not, why: the repository has no origin, its
  provider has no pull requests from here, or the provider's CLI is not installed on this computer. The pull request is
  then left to the host's own site. Whether the CLI is signed in is not checked there: a CLI that is not signed in shows
  only when a pull request call is refused (the tool or action says so).
- git 2.38 or later is required. When it is missing, too old or not working, a banner at the top of the window says
  so, with **Download Git** and **Check again**; on Windows with winget, **Install Git** (or **Update Git**) installs
  Git for Windows for everyone on the computer, so Windows asks for permission. The app finds a git installed while it
  runs without a restart.
- **Git** on the team page: **Fetch**, **Sync now**, **Merge** (the team branch into the base branch, then pushed
  to origin), and per-member branch state. Old-style `members/<name>` branches are listed beside the member branches,
  marked old. **Delete…** on a branch first shows exactly what will be deleted, here and on origin; nothing is deleted
  until you press **Delete**. A branch whose commits are not merged is deleted only when you tick that you accept
  losing them. If the branches changed since the plan, the dialog says so and shows the new plan. The app never
  forces, rebases or stashes.
- A banner shows when branches could not be brought up to date on their own.
- Each member's working copy has a read-only code browser.
  - It never follows a link and never shows `.git` or likely secrets.
  - After a file or folder is opened, it checks again what was actually opened, on Windows, macOS and Linux (x64 and
    arm64). The real path must still be inside the working copy, not in `.git` and not hidden. A file must have only
    one name, so a hard link is never read. A folder must be the same folder before and after it is listed.
    Otherwise it is not shown.
  - Windows does these checks with `GetFinalPathNameByHandleW` and `GetFileInformationByHandle`.
  - macOS and Linux open with `O_NOFOLLOW` and check with `fstat` (link count, device and inode). The real path comes
    from `F_GETPATH` on macOS and from `/proc/self/fd` on Linux.

## Integrations

Your CLIs (such as `gh`, `git` and `az`), local and remote MCP servers, web APIs described by OpenAPI, and websites
can be offered to teams' agents as tools. Every call runs on this computer, under rules: allowed, ask first, or off.
Calls that ask first wait for your approval. Sign-ins are Connections kept in the OS credential store, which agents
never see. Host tasks run named programs for a team, under a standing approval.
Host tasks can take environment variables from Connections (such as `GH_TOKEN` for a release script); the value is
masked in the log and never reaches an agent. See
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

`yawble-companion` is a small program installed with the app. It lets a program on this computer, such as Claude Code, see and
work with your teams, as an MCP server (`yawble-companion mcp`) or a CLI.

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
- A Cloud Link viewer is told an update is available; it is installed on the computer itself. macOS gets a link to the
  release page.
- On Linux the release's file is `yawble-<version>-linux-<arch>.tar.gz`, listed in the release's own
  `SHA256SUMS-linux`. Linux builds are not signed: the tarball's SHA-256 must match that list, and it must hold one
  app folder with nothing inside that leads outside it. **Restart to update** quits the app, a copy of the install's
  `apply-update.sh` waits until nothing runs from the install, runs the new `install.sh` over the same folder and starts
  Yawble again; it writes what it did to `<data>/logs/update.log`. This works for an `install.sh` install that you
  may change (the default `~/.local/opt/yawble`). A system-wide install in `/opt`, or a run from source, gets a link
  to the release page.

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

## Cloud org dashboard

- The cloud site's org dashboard shows each attached computer live: the site stores nothing and asks the computers
  while someone is watching. See [Cloud Link](cloud-link.md#the-org-dashboard).
- `org.summary` answers one snapshot of this computer (`summaryVersion` 1): the computer (name, label, version,
  platform, uptime, run slots, queued runs), each team's agents (running, queued, idle, errored, and what each running
  agent is doing), the open workflows and those ended in the last 24 hours (state, including expired; backlog item,
  cards done of total, owner, endedAt and expiredAt), what waits for the person per team (decisions, tool-call approvals, network and tool requests), and the
  last 20 warnings and errors of the last hour.
- While a viewer watches (`org.summary.watch`), the app pushes `org.summaryChanged` with the changed sections, at most
  once every 2 seconds. Nothing is built or pushed while nobody watches; unwatching or disconnecting stops it.
- The summary holds facts only: no token counts, cost, usage or price. Titles and log lines are redacted.
- Visitors (read-only viewers) can use the dashboard too. The hello lists `summary` and `logsTail` in its caps.

## Settings

| Tab | What it has |
|---|---|
| General | **Keep running when the window is closed** (Yawble stays in the tray), **Start Yawble when I sign in to this computer**, Backups |
| Agents | Detected CLIs and custom agents |
| Concierge | **Default agent** for new Concierge conversations: an agent, or **First available on this computer** (the first CLI that is installed and signed in) |
| Teams | **Default agents** for new teams (the Manager's name and agent, and optional members, each ticked **On new teams** or offered as a one-click add), and **Member names**: the list new members' first names come from, with **Generate** for a country |
| Skills | Skills and which roles and teams they are offered to |
| Guardrails | The global policy and the global run limit |
| Integrations | CLIs, APIs, MCP servers, Connections, host tasks |
| Cloud Link | Pairing and remote access; see [Cloud Link](cloud-link.md) |
| Health | The doctor |
| Companion | `yawble-companion`: the line to add it to Claude Code, paired programs |
| System | Version, build, platform, sandbox backend and capabilities, About |

- A notification says when Yawble needs you: questions, approvals, network requests, tool requests and Connections to reconnect.
- Every call that changes something is recorded in an audit log, with who made it and how. Keys, tokens and
  messages are never recorded.
- Each run's output is kept in an owner-only transcript file. The terminal shows it as **Formatted** or **Raw**.
- A member's terminal is in its pane on the team page (the **Terminal** side of its toggle). An agent's **Terminal**
  and **Test** sessions from Settings > Agents open in the window's terminal pane.
