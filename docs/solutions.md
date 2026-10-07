# Solution packages

A **solution package** is a whole working team in one folder: who is on it, what wakes them, the
playbook they follow, the page a person uses, the tools their instructions run, and what only a
person can provide. It replaces the list of manual steps a person follows after a team builds a
plugin, a site, a playbook and triggers: merge, install the plugin, hire it, paste a skill, add each
trigger by hand, upload documents.

This page covers the format (`solution.json`, format 1), the package layout, the check, the install
that turns a package into a team, the update to a newer version, and the deep link that opens the
install wizard.

## The layout

```
job-tracker/
  solution.json            the team: members, triggers, skills, sites, inputs
  README.md                for people; optional
  plugins/<id>/            one built version of each plugin the team hires, plugin.json at its root
  skills/<name>.md         each team skill: a SKILL.md, name, description and roles in its front matter
  sites/<name>/            each site's folder, index.html at its root
  tools/                   code the members' instructions run, named {solution} in them
```

- **Plugins** are ordinary plugins ([plugins.md](plugins.md)), one version each, laid out as a
  plugin version folder is: `plugins/<id>/plugin.json`, its executable and anything it reads. The
  folder is named after the manifest's `id`.
- **Skills** are the files a person would otherwise paste. A package's skills are **team skills**:
  offered only to that team's members of their roles.
- **Sites** are published as a team publishes one ([sites.md](sites.md)). Files the installed team
  generates for a site - a document, a spreadsheet, an image the page offers for download - go in
  that site's files folder, `<team documents>/sites/<site>/files/`; the members store the path
  relative to it in the site's data, and the page links with `site.files.url(path)` or a static
  `<a href="_api/files/<path>">` ([sites.md](sites.md#files-a-team-makes-for-a-site)). A package ships
  no files folder: the install creates one with each site. The check accepts a page that links to it,
  and a `panel.outputs` entry may name `sites/<site>/files`.
- **Tools** are copied to `<teams root>/<team>/solution/` on install, read-only to agents like other
  platform-made folders. An instruction names that folder with the token `{solution}`.
- **Nothing is fetched.** A package holds everything it needs; the check reads only the folder.

## `solution.json`

```json
{
  "format": 1,
  "id": "job-tracker",
  "name": "Job Tracker",
  "version": "1.0.0",
  "description": "…",
  "team": { "name": "Job Tracker", "instructions": "…" },
  "members": [ … ],
  "triggers": [ … ],
  "skills": [ "job-search-playbook" ],
  "sites": [ "tracker" ],
  "inputs": { "settings": [ … ], "connections": [ … ], "documents": [ … ] },
  "panel": { "primarySite": "tracker", "outputs": [ "Drafts" ], "settings": [ … ], "status": "…" }
}
```

| Field | Required | What it is |
|---|---|---|
| `format` | yes | `1`. The hard gate: a format this Host does not read is refused and nothing else is checked. |
| `id` | yes | A slug: lowercase letters, digits and hyphens, at most 48 characters. The same package id across versions is how an update finds the team it came from. |
| `name` | yes | What a person calls it, at most 60 characters. |
| `version` | yes | A plain version such as `1.0.0`. |
| `description` | yes | One or two sentences, shown on the review. |
| `team` | no | `name`: the team's default name (the package's `name` when absent; a person may change it at install). `instructions`: the team's additional instructions, added to every member's prompt. |
| `members` | no | The team, below. |
| `triggers` | no | What wakes whom, below. |
| `skills` | no | Names of team skills; each is `skills/<name>.md`. |
| `sites` | no | Names of sites; each is published from `sites/<name>/`. |
| `inputs` | no | What only a person provides, below. |
| `panel` | no | What the solution's launcher tile and control panel show, below. |

**Unknown top-level keys are kept and ignored**, never refused, so a package written for a later
Host still checks here. The check lists them as
`ignored`. Unknown keys inside an entry are ignored too.

### Members

Each member has a `name`: the package's own name for it, which triggers and inputs use. Names are
matched case-insensitively and must be distinct once made into member names (letters, digits, `-`
and `_` kept).

An **agent** member:

| Field | What it is |
|---|---|
| `name` | Required. |
| `role` | `manager` (the team's Manager; at most one) or `member` (the default). |
| `preset` | The Agent preset it runs, as Admin → Agents names it. Omit it and the team chooses, as a hire does. |
| `instructions` | Its own instructions, added after the built-in prompt for its role. May use `{solution}`. |

A **plugin** member (a `pluginId` makes one; `"kind": "plugin"` may say so too):

| Field | What it is |
|---|---|
| `name` | Required. |
| `pluginId` | Required: a plugin the package ships under `plugins/<id>/`. |
| `settings` | Its configuration, checked against the plugin's manifest. A **person-only** setting (`"setBy": "person"`) may only be left at its default here: ask a person for it under `inputs.settings`. |
| `secrets` | Its secret bindings: `{ "<manifest secret field>": "<LOGICAL_KEY_NAME>" }`, each a field the plugin's manifest declares under `secrets`, bound to a **key name, never a value**. See [Secrets](#secrets). |

A plugin member takes no `role`, `preset` or `instructions`; an agent member takes no `settings`
or `secrets`.

#### Secrets

A package binds its plugin members' secrets by **logical key name**, exactly as a person binds them
in the member's settings ([plugins.md](plugins.md#configuration-and-secrets)). The value never travels with a package:
the operator sets it on the Host with `yawble secret set <KEY>` (it prompts for the value) and
restarts the Host with `yawble up`, and the plugin receives it on stdin at each run.

```json
{
  "name": "Scout",
  "pluginId": "job-board",
  "secrets": {
    "adzunaAppId": "ADZUNA_APP_ID",
    "adzunaAppKey": "ADZUNA_APP_KEY"
  }
}
```

- **The check refuses**, each naming `solution.json` and the field (`members[1].secrets.adzunaAppId`),
  all at once: a field the plugin's manifest does not declare; a key that is not a legal environment
  variable name (upper-case letters, digits and underscores, not starting with a digit) or one the
  platform keeps for itself (`HARNESS_*`, model providers' credentials); and an entry that **looks
  like a value** - a known credential prefix (`sk-`, `ghp_`, `AKIA`, `eyJ`, a PEM block ...), a long
  run of letters and digits with no underscore, or anything that is not a string. A refusal never
  repeats the entry: what was written there may be the credential itself.
- **The install binds** each named field to its key in the same step as the member is hired, through
  the same check a person's binding runs. The install is a person's action, so it may bind keys no
  one has bound on the team yet (a Manager's or the Concierge's hire still may not).
- **A key the Host has not set is not a refusal**: the install goes ahead and the secret's source
  fails until the key is set. The wizard and the CLI say so per key, with the exact way to set it.
- **Needed for one setting only.** A plugin's manifest may say a secret is needed only while a
  setting holds a value - `"when": { "sources": "adzuna" }` ([plugins.md](plugins.md#configuration-and-secrets)). Where
  the person leaves that setting off (a source not ticked), the secret is shown as not needed.
- **An update keeps a binding a person changed**: a field still bound as the installed version bound
  it takes the new version's key; a field the person rebound or unbound stays as they left it. A
  change is saved as a person's settings change is, with its `member.plugin-settings-changed` row
  naming the fields (never a value).

### Triggers

| Field | What it is |
|---|---|
| `name` | Required, distinct. |
| `kind` | Required: `schedule`, `event` or `folder`. |
| `schedule` | For `schedule`: `{ "cron": "0 0 8 * * 1-5", "timezone": "Europe/London" }` (six fields, seconds first; UTC when no timezone) or `{ "everySeconds": 3600 }` (at least 10). |
| `event` | For `event`: `{ "type": "site.action", "filter": "siteAction eq tracker/apply" }`. The type must be one the event catalog knows: the platform's, an installed plugin's, or `plugin.<id>.<type>` for a type a plugin in the package declares. The filter is `field op value` (`eq` or `contains`) on a field that type carries. |
| `folder` | For `folder`: `{ "path": "Resume", "glob": "*.pdf" }`, a folder inside the team's documents. |
| `member` | Required: the package's name for the member it wakes. |
| `instruction` | Required: what the member is told, verbatim. `{event.<field>}` is filled from the event at each fire, and `{solution}` names the installed tools folder. |
| `wakeManager` | `always`, `onHandbackOrFailure` (the default) or `never`: see [triggers.md](triggers.md#wake-the-manager-when-a-run-ends). |
| `dailyTokenCap` | The most billable tokens its runs may spend in a day, at least 1; absent or `null` for none. See [triggers.md](triggers.md#daily-token-cap). |
| `idleOnly` | Skip a fire while the member is busy; `true` by default. |
| `runAtInstall` | For `schedule` only: `true` fires it once right after the install's last step succeeds, then it runs on its schedule as usual. The check refuses it on an `event` or `folder` trigger, naming the file and field. |

These are the trigger settings a person sets in the Triggers dialog. An agent member is never
subscribed to a high-volume event, as the dialog refuses it too.

**A first run at install.** A package whose point is a schedule can say `"runAtInstall": true`, so
the person does not look at an empty page until its first due time. The install fires it once, after
the last step has succeeded and the documents are copied in. It is the same fire the schedule makes:
source `schedule:<id>`, its instruction with its `wakeManager`, skipped with its row for a paused team,
a busy idle-only member or a reached daily cap. Its next due time then counts on from that fire. A
failed step makes no fire, since nothing is left behind. If the first run itself fails, or is
skipped, that is the run's outcome and not the install's: the install is still done, and the result
says the schedule did not run now and when it first runs. The plan's schedule reads "runs once now,
then every …". An update does not fire it.

### Inputs

What only a person provides. The install asks for each; a skipped required document or connection
leaves the team showing it as blocked, naming it, until it is provided. A required setting with no
default cannot be skipped: its plugin member cannot be hired without it.

| Field | Each entry |
|---|---|
| `settings` | `{ "member", "setting", "description", "required" }`: a person-only setting of a plugin member. |
| `connections` | `{ "member", "slot", "description", "required" }`: a connection slot of a plugin member, bound to one of the person's connections ([connections.md](connections.md)). Every slot a plugin requires must be asked for. |
| `documents` | `{ "folder", "description", "required" }`: a folder of the team's documents the person uploads into, such as `Resume`: "your reference resume, .docx or PDF". |

### Panel

Every installed solution has a tile in the **Solutions** launcher and a **control panel** the
platform builds (below, [The launcher and the panel](#the-launcher-and-the-panel)). The package
writes no UI for either; `panel` says what they show. Every key is optional.

```json
"panel": {
  "primarySite": "tracker",
  "outputs": ["Drafts"],
  "settings": [ { "member": "Scout", "setting": "keywords" }, { "member": "Scout", "setting": "sources" } ],
  "status": "{data.jobs.count status=new} new jobs · last checked {lastRun.at}"
}
```

| Field | What it is |
|---|---|
| `primarySite` | The site the tile's **Open** opens in a new tab: one of the package's `sites`. Without it the tile has no Open. |
| `outputs` | Documents folders the team writes results into, listed on the panel newest first with download links. Relative folder names: no `..`, no leading `/`, no hidden (`.`) part, no wildcard; `Applications/Sent` is fine. |
| `settings` | Plugin settings shown first on the panel, each `{ "member", "setting" }`: a plugin member of the package and a setting its plugin's manifest declares. "All settings" reaches the rest, person-only settings included. |
| `status` | The tile's status line: a template of at most 200 characters, below. Without it the line is the last run and the state. |

**The status line** is text with placeholders the platform fills, and only these exist:

| Placeholder | Filled with |
|---|---|
| `{data.<collection>.count}` | How many documents that collection of the primary site holds. |
| `{data.<collection>.count <field>=<value>}` | How many of them have that top-level field equal to the value: a string by its characters, a number, `true`, `false` or `null` as JSON writes it. |
| `{lastRun.at}` | When the latest run of any of the package's members ended, UTC (`2026-09-30T08:00:05Z`); `never` before any. |
| `{lastRun.outcome}` | How it ended: `completed`, `handedBack`, `blocked` or `failed`; `none` before any. |

The template is text, never code: there are no expressions, and a brace that is not one of these
placeholders is refused by the check. Data that is missing - no such collection, no documents -
counts as 0. The filled line is plain text; the platform renders it, and a site document's value
that looks like HTML, as the characters it is. A `{data...}` placeholder needs `primarySite`.

## The check

The check reads a package and answers either **what an install would create** or **everything
wrong with it**. It writes nothing. One validator serves every caller: the route, the CLI, the board's notice and, later,
the install.

- **The route**: `POST /api/solutions/check` with `{ "folder": "<absolute folder inside the data root>" }`,
  for a person and the Concierge; a member is refused. It answers `{ ok: true, plan }` or
  `{ ok: false, refusals }`. A folder outside the data root, or reached through a link that leaves it,
  is refused with 400.
- **The CLI**: `yawble solution check <folder>` copies a folder from your computer into the instance
  and asks the Host; `--from-instance <path>` checks one already there. See
  [cli/README.md](../cli/README.md#solution-packages).

**The plan** lists the package; the team; each member with its role, preset, instructions or plugin
and settings; the plugins installed and the events they publish; each trigger with its kind, its
schedule, event and filter or folder, the member it wakes, its wake setting, its daily cap and its
**full instruction text**; each skill with its roles and body; each site and its files; the tools
and where they go; and what the person will be asked for. Agent-written instructions become prompts,
so the review shows them whole.

**Each refusal names the file and the field** - `solution.json triggers[1].member`,
`plugins/job-board/plugin.json executable.path`, `skills/job-search-playbook.md roles` - with a
sentence saying what to change. Every refusal is reported at once, not only the first: a refusal in
`solution.json` leaves out the item it names, and the plugins, skills, sites, event types and inputs
are still checked. Only a `solution.json` that cannot be read past its `format` stops the check. It
refuses:

- a missing or malformed field in `solution.json`, or a `format` it does not read;
- a trigger naming a member the package does not have;
- a plugin member naming a plugin the package does not ship;
- a plugin manifest the Host would refuse, or one whose `id` is not its folder's name;
- a setting the plugin does not have, a value its manifest refuses, or a person-only setting set by
  the package; an input naming a setting that is not person-only, or a slot the plugin lacks;
- a secret field the plugin's manifest does not declare, a key that is not a legal environment
  variable name, and a value-looking entry ([Secrets](#secrets));
- a skill whose name is a built-in's or begins `plugin-`, whose file is missing, or whose front
  matter lacks a matching name, a description, roles or a body;
- an event type the catalog does not know, a filter on a field it does not carry, and a high-volume
  type on an agent member;
- a preset this instance does not have;
- a site that is not a slug, has no folder, or has no `index.html`;
- a `panel.primarySite` that is not one of the package's sites, a `panel.outputs` folder that is not
  a safe relative folder name, a `panel.settings` entry on a member that is not a plugin member or
  naming a setting its plugin does not declare, and a `panel.status` with a placeholder this Host
  does not fill (or `{data...}` with no primary site);
- `{solution}` in an instruction when the package has no `tools/`;
- **any path that escapes**: a folder trigger or document input outside the team's documents, a
  skill or site named by a path, a plugin's executable or skill outside its folder, and any link in
  the package that is absolute or leads out of it.

## Getting a package in

A package is installed from a folder inside the instance, so it goes into Documents first:

1. Open **Documents**, pick your team's folder, and upload the package: **Upload a folder** for the
   whole folder (its subfolders are kept), or **Upload a .zip**, which is unpacked into a folder of
   the zip's name.
2. Open **Solutions → Install from a folder** (or **Admin → Plugins → Install from a folder**: the
   same dialog). **Browse…** opens in the teams' Documents; open the package's folder (the one
   holding `solution.json`) and press **Choose this folder**, then **Install**. The folder can also
   be typed: any folder inside the instance's data root.

**Shortcut: Upload a package (.zip).** In that same install dialog, **Upload a package (.zip)** takes
a `.zip` from your computer, puts it at the top of a team's folder in Documents - your active team's,
or the one you pick next to the button; the line under it says where - unpacks it into a folder of
the zip's name, and fills **Folder** with that folder, so the next press is **Install**. If a folder
of that name is already there you are asked Keep both, Replace or Skip, as any Documents upload asks
(Skip fills **Folder** with the one already there). A zip holding a link, an absolute path or a `..`
entry is refused whole, with the reason shown, and nothing is written.

The picker shows only Documents and what is under it - not the data root's own folders, and no
dot-files such as `.harness-team` - and "Choose this folder" stays disabled until a folder below
Documents is open. A package a team built in its own folder arrives with a **Review and install**
link instead (see the deep link below), and the CLI's `yawble solution check` copies a folder from
your computer.

## The install

**Solutions → Install from a folder** opens the install wizard on the chosen folder. **Admin →
Plugins → Install from a folder**, the same dialog with a **Replace** option for a plugin version
already installed, opens the wizard when the folder holds `solution.json` (a plain plugin folder
installs as before). The wizard's steps:

1. **Team**: a new team, named after the package by default (editable, and checked as any team name
   is), with a new local repository unless unticked; or **update an existing team** that was
   installed from an earlier version of the same package id.
2. **Review**: every member with its full instructions, every trigger with its kind, schedule or
   event and filter or folder, the member it wakes, its wake setting, its **daily cap** and its
   **full instruction text**, every skill, site, plugin and tool. In an update, what is added and
   changed is marked and what is removed is listed. Agent-written instructions become prompts, so
   this is the safeguard: nothing runs before the person has seen it.
3. **Your part**: the person-only settings, a connection picker per slot, and an upload box per
   requested document folder. Skipping a required document or connection is allowed. In an update,
   a kept member's settings and bindings are shown with their current values rather than asked
   (the update never changes them), and a document folder names the files already in it; only an
   empty required folder or an unbound required slot warns that the team will be blocked. The
   preview's `kept` carries these, and `yawble solution install` shows them the same way.
   **Secrets** are listed here too, each by key name with whether the Host has it set (by name
   only, never the value), what the manifest says it is for, and either "set on this Host", "not
   set: its source fails until it is set" with the exact way to set it (`yawble secret set <KEY>`,
   then `yawble up`), or "not needed" when it belongs to a setting the person left off. Unset is not
   a refusal. The preview's `secrets` carries these (in an update, as the update will leave them).
4. **Install**: the steps below; on failure the wizard names the step and the reason. The result
   names each schedule's first run ("Scan for postings ran now", "Morning summary first runs at
   8:00 AM"), and lists the keys still unset, each with the way to set it. The answer's `firstRuns`
   carries these (`trigger`, `member`, `runAtInstall`, `ranNow`, `outcome`, `at`, `next`), and
   `yawble solution install` prints the same lines under "Schedules:".

The install runs, in order, through the stores a person's own clicks use, each step appending its
usual tenant row:

| # | Step | What it makes |
|---|---|---|
| 1 | `plugins` | Each of the package's plugins, installed as `yawble plugin install` would. One already active at that version or newer is left alone: a plugin is the instance's, and an install never downgrades what other teams run. |
| 2 | `team` | The team, with the package's team instructions and a new local repository (B001F). |
| 3 | `members` | The Manager takes the package manager's name and instructions; every other member is hired. An agent member with no preset runs the Agent the wizard names (the instance's first installed model preset by default). |
| 4 | `skills` | Each skill, registered as a **team skill** (`TeamSkills.RegisterAsync`). |
| 5 | `tools` | `tools/` copied to `<team folder>/solution/`, read-only to agents (the Host's files with the agent's group: folders 0750, files 0640, executables 0750). Every agent member's prompt names the folder, and `{solution}` resolves to it. |
| 6 | `sites` | Each site created and published (`site.created`, `site.published`). |
| 7 | `triggers` | Each document input's folder, then each trigger, through the Triggers dialog's own path (`TriggerCreation`) with every check it makes. |
| 8 | `record` | The package id and version in `team_solutions` (schema step `auth-016`), with the `solution.installed` row in the same transaction. |

**When a step fails**, everything made so far is undone in reverse order - the triggers, the
sites, the tools, the skills, the members, the team with its local repository and its empty
documents folder, and each plugin version it installed (the previously active version made active
again) - and the answer names the step, its number and the reason; `solution.failed` records what
was undone.

**Skipped required inputs.** `GET /api/teams/{team}/solution` answers which package and version the
team came from and `missing`: each required document folder with no file in it and each required
connection slot with nothing bound. The team board shows a blocked notice naming each one, with an
upload box for a document, until it is provided. Nothing needs re-running: the notice is read live.

**Update.** Installing a newer version of the same package id onto a team shows the diff first -
members, triggers, skills, sites, tools and plugins, each added, changed or removed - and then:

- adds what is new and rewrites what changed (a changed agent member's instructions and preset, a
  changed skill, a changed site published as a new version, the tools replaced, a changed trigger
  made again with its new text and cap);
- removes what the package no longer has, last, once everything that can fail has: a member, a
  trigger, a skill; a site is **unpublished, never deleted**, so its data stays the person's;
- keeps the person's settings and connection bindings on the members it keeps, the uploaded
  documents and every site's data; a kept member's secret bindings take the new version's keys
  except where the person changed them ([Secrets](#secrets));
- rewrites the `team_solutions` row with `solution.updated`.

Only a newer version updates: the same or an older one, or another package's id, is refused with a
sentence.

**Routes** (install, update and what they read are a person's only - `HumansOnly`; an agent never
installs, and person-only settings stay person-only):

| Route | |
|---|---|
| `POST /api/solutions/preview` `{ folder, team? }` | What installing would do, writing nothing: `mode: install` with the team name and why it cannot be used, or `mode: update` with `from`, `to` and the diff. Both carry `secrets`: `{ member, field, key, description, required, when, set, needed, setWith }` per binding - `needed` is null while it waits on the person's answer to `when`'s setting, and `setWith` names the operator CLI's `secret set <KEY>` and `up` (the wizard and the CLI print the full commands). |
| `POST /api/solutions/install` `{ folder, teamName?, agent?, localRepository?, settings?, connections? }` | The install. `{ ok: true, team, missing, steps, secrets, unset }` (`unset`: the keys bound, needed and not set on the Host), or `{ ok: false, step, stepNumber, title, reason, undone }`. 409 for a taken name. |
| `POST /api/solutions/update` `{ folder, team, settings?, connections? }` | The update, with `from` and `diff`. |
| `GET /api/solutions/installed` | Every team installed from a package, with its id and version. |
| `GET /api/teams/{team}/solution` | The package a team came from, and what it still waits for. `Read`: the team's own members may read it too. |

**The deep link** `#/solutions/install?folder=<absolute path>` opens the wizard filled in with that
folder. It never installs by itself: the person still reviews and presses Install. A signed-out
visitor signs in first and lands in the wizard. The check it makes carries `from: "link"`, and a
folder outside the instance's documents and every team's folder is refused with a sentence; a link
is a convenience someone hands the person, and the review is the safeguard. Following a second link while the
wizard is open reopens it on the new folder.

**The CLI**: `yawble solution install <folder> [--team <name>] [--from-instance] [--yes]` asks the
same questions in the terminal - the team name (or, with `--team` naming a team installed from an
earlier version, the update and its diff), each person-only setting, each connection slot, a file
for each document folder - prints the secrets list as "Your part" shows it, and then installs,
printing each step or the step that failed, the secrets list again and the keys still unset. It stages
the folder under `<dataRoot>/plugins/.solutions/`, which only the Host and root can write, and
hands the Host a request file (`<dataRoot>/plugins/.solution`) that `SolutionRequests` answers with
exactly what the matching route answers. An agent cannot write there, so it cannot install.

**Deleting the team** forgets its `team_solutions` row; the package's plugins stay installed
(deleting a team never removes a plugin), and the delete dialog says so.

## The launcher and the panel

**Solutions**, a button near the start of the ribbon, opens the launcher: one tile per installed
solution, with its name, version and team, its status line, a state badge, **Open** (the primary
site, when it has one) and **Manage** (the panel). The badge is the first of: **paused** (the team
is paused); **blocked**, naming what the team waits for ("Upload a file to Resume/"); **running**
(a member is running now); **capped** (a trigger's measured spend today reached its daily cap);
**idle**. The launcher reads `GET /api/solutions/installed`. A filter above the tiles narrows them
by words of the name, team or status line, by team and by state - what is shown only. **Details**
on a tile lists the package, version, team, folder, who installed it, its plugins, site and status.
With nothing installed it says where a solution comes from: a package a team built, whose
**Review and install** link appears in that team's Activity feed and on its backlog item - or
**Install from a folder**.

**The control panel** (`GET /api/teams/{team}/solution/panel`) is the platform's own screen, not a
site: its controls act with the person's authority, which a site is deliberately without. The
read also answers `sites`: each site the package publishes, in the package's order, with its
address and whether it is published now - a read, with no control of its own. Its sections:

- **Status**: each member's state and last run; each trigger with its next fire; each of the
  package's sites with **Open** (disabled while unpublished); anything blocked,
  with the fix inline (an upload box for a missing document, a picker for a missing connection);
  today's spend against each trigger's daily cap - **measured only**: the Triggers dialog's own
  `spentToday`, with runs that reported no usage counted as unmeasured, never estimated.
- **Controls**: pause and resume the team, **Run now** for each schedule, each trigger's on/off and
  daily cap, the `panel.settings` first with "All settings" for the rest, and connection bindings.
  A trigger's tile shows the first line of its instruction; its **Details** shows the whole
  instruction and what fires it.

Members, triggers and sites are tiles, laid out as the Plugins and Agents tiles are; the settings,
connections and maintenance stay forms.
- **Results**: each `panel.outputs` folder newest first with download links, and recent runs with
  their output.
- **Maintenance**: the version and the folder it was installed from, **Update from a folder** (the
  wizard's update path) and **Uninstall**.

**Every control is an existing route**, with its own permit marker and tenant row - the panel adds
none:

| Control | Route |
|---|---|
| Pause, resume | `POST /api/teams/{team}/pause`, `POST /api/teams/{team}/resume` |
| Run now | `POST /api/teams/{team}/triggers/{id}/run` |
| A trigger's on/off and daily cap | `PATCH /api/teams/{team}/triggers/{id}` |
| Plugin settings, connection bindings | `PUT /api/teams/{team}/members/{member}/plugin-settings` |
| Upload a missing document | `POST /api/teams/{team}/documents/upload` |
| Download a result | `GET /api/teams/{team}/documents/content` |
| A run's transcript | `GET /api/teams/{team}/members/{member}/runs/{seq}/transcript` |
| Update from a folder | `POST /api/solutions/preview`, `POST /api/solutions/update` |

**Uninstall** (`POST /api/teams/{team}/solution/uninstall`, `{ removePlugins? }`, a person only)
asks first - the asking is the panel's - and then removes the team's triggers, its members (the
Manager stays, as every team has one, with the package's instructions cleared), its team skills
and its tools folder, with a `solution.uninstalled` row. **It takes the package's sites offline and
keeps them, with every version and all their data**, as an update keeps a site its package drops,
and keeps the record that the team held the package, with the person's answers. **It keeps the team
and its documents.** Installing the same package id onto that team again - the wizard offers it, and
`POST /api/solutions/install` takes it as `teamName` - installs over it: the kept sites are published
again with their data, the members and triggers are made again, and the person's earlier answers
that still apply are filled in and asked again (`previous` in the preview). A team that never had the
package is refused by name, as any taken name is. Deleting the team removes the kept sites, their
data and the record. With `removePlugins: true` it
removes each of the package's plugins that no other team has a member on, and names the ones it
kept and the teams using them. Something that cannot be removed is named in `failures` and the rest
still goes. Deleting the team itself is still the team delete.

## The board notice

When a member declares a workflow complete, the platform looks in the team's documents folder for
package folders - a folder directly inside it holding `solution.json` - with a file written since
the workflow began, and checks each one exactly as the route does. For each it appends a
`solution.checked` event inside the workflow, on the declaring member's card:

- **It passes**: "Job Tracker 1.0.0 is ready. **Review and install**", and the link
  `#/solutions/install?folder=<absolute path>` opens the install wizard on that folder. The link
  never installs; the review is where the person decides.
- **It fails**: the notice names each problem by file and field, and offers no install.

The backlog item the workflow was dispatched from shows the same notice under that dispatch. A
package written in an earlier workflow is not checked again, and a passing notice is not repeated:
when two workflows were open while the package was written, only the first declared shows it, and a
later one shows it again only if the folder's files changed since (each notice carries
`contentHash`, a digest of the folder). So a team delivering a package writes it
to `<team documents>/<id>-<version>/`; the built-in skill `packaging-solutions` tells the Concierge
and the Manager so, and the Concierge hands the person the link on the address their browser uses
(`HARNESS_PUBLIC_URL`).

## The full example: Job Tracker

[`samples/solutions/job-tracker`](../samples/solutions/job-tracker) is a team that finds job
postings, tracks them on a page and drafts a cover letter when the person presses **Apply**. It is
the package the solution tests check and install.

- **Coordinator** is the Manager. Each weekday at 08:00 London time it summarises the tracker.
- **Scout** is the `job-board` plugin (a stand-in board that reads postings bundled with it, never the
  network). Every hour it writes new matches to the tracker site and publishes
  `plugin.job-board.posting-found`, finishing quiet when nothing is new.
- **Writer** is an agent. A new posting wakes it to note how well the resume fits; **Apply** on the
  page (a `site.action` narrowed to `tracker/apply`) wakes it to draft a letter, starting from
  `{solution}/make-cover-letter.py`; a new resume in `Resume/` wakes it to refresh its notes.
- The person provides their **resume** (required) and ticks which **sources** the Scout may read
  (a person-only setting: until they do, it reads none).
- The Scout binds five secrets by key name - `ADZUNA_APP_ID`, `ADZUNA_APP_KEY`, `USAJOBS_API_KEY`,
  `USAJOBS_USER_AGENT` and `THEMUSE_API_KEY` - each needed only when its source is ticked. The
  stand-in board calls none of them; it says which ticked board has no keys set.

```json
{
  "format": 1,
  "id": "job-tracker",
  "name": "Job Tracker",
  "version": "1.0.0",
  "description": "Finds job postings that match your keywords, tracks them on a page, and drafts a cover letter from your resume when you press Apply.",

  "team": {
    "name": "Job Tracker",
    "instructions": "This team runs a job search for one person. The postings live on the tracker site, collection `jobs`. The person's reference resume is in the Resume folder of the team's documents. Never apply anywhere yourself: drafts go to the Drafts folder of the team's documents for the person to send."
  },

  "members": [
    {
      "name": "Coordinator",
      "role": "manager",
      "instructions": "You coordinate the job search. Each weekday morning you read the tracker and tell the person, in one short note, what is new and what is waiting on them."
    },
    {
      "name": "Scout",
      "pluginId": "job-board",
      "settings": { "keywords": ["engineer", "developer"] },
      "secrets": {
        "adzunaAppId": "ADZUNA_APP_ID",
        "adzunaAppKey": "ADZUNA_APP_KEY",
        "usajobsApiKey": "USAJOBS_API_KEY",
        "usajobsUserAgent": "USAJOBS_USER_AGENT",
        "themuseApiKey": "THEMUSE_API_KEY"
      }
    },
    {
      "name": "Writer",
      "role": "member",
      "instructions": "You write cover letters. Read the person's resume from the Resume folder, read the posting from the tracker, and start from the draft {solution}/make-cover-letter.py makes. Keep it to one page."
    }
  ],

  "triggers": [
    {
      "name": "Scan for postings",
      "kind": "schedule",
      "schedule": { "everySeconds": 3600 },
      "runAtInstall": true,
      "member": "Scout",
      "instruction": "scan",
      "wakeManager": "never",
      "idleOnly": true
    },
    {
      "name": "New posting",
      "kind": "event",
      "event": { "type": "plugin.job-board.posting-found" },
      "member": "Writer",
      "instruction": "A new posting matched: {event.title} at {event.company} ({event.url}). Read it from the tracker site (collection jobs, id {event.id}) and add a two-line `fit` note to that document saying how well the resume fits. Do not write a letter yet.",
      "wakeManager": "never",
      "dailyTokenCap": 200000
    },
    {
      "name": "Apply pressed",
      "kind": "event",
      "event": { "type": "site.action", "filter": "siteAction eq tracker/apply" },
      "member": "Writer",
      "instruction": "{event.by} pressed Apply on {event.payload}. Run `python3 {solution}/make-cover-letter.py` with that posting's JSON and the resume's path to get a first draft, then finish the letter and save it as Drafts/<job id>.md in the team's documents. Set the posting's status to `drafted` on the tracker and hand back naming the file.",
      "wakeManager": "onHandbackOrFailure",
      "dailyTokenCap": 400000
    },
    {
      "name": "Resume changed",
      "kind": "folder",
      "folder": { "path": "Resume", "glob": "*" },
      "member": "Writer",
      "instruction": "The resume in Resume/ changed ({event.changed}). Re-read it and refresh the `fit` note on every open posting in the tracker.",
      "wakeManager": "onHandbackOrFailure",
      "dailyTokenCap": 300000
    },
    {
      "name": "Morning summary",
      "kind": "schedule",
      "schedule": { "cron": "0 0 8 * * 1-5", "timezone": "Europe/London" },
      "member": "Coordinator",
      "instruction": "Read the tracker site's jobs collection. Tell the person, in five lines at most, which postings are new since yesterday and which drafts are waiting to be sent.",
      "wakeManager": "never",
      "dailyTokenCap": 100000
    }
  ],

  "skills": ["job-search-playbook"],

  "sites": ["tracker"],

  "panel": {
    "primarySite": "tracker",
    "outputs": ["Drafts"],
    "settings": [
      { "member": "Scout", "setting": "keywords" },
      { "member": "Scout", "setting": "sources" }
    ],
    "status": "{data.jobs.count status=new} new jobs · last checked {lastRun.at}"
  },

  "inputs": {
    "settings": [
      {
        "member": "Scout",
        "setting": "sources",
        "description": "Which job boards the Scout may read. It reads only the boards you tick.",
        "required": false
      }
    ],
    "documents": [
      {
        "folder": "Resume",
        "description": "Your reference resume, .docx or PDF. The Writer starts every letter from it.",
        "required": true
      }
    ]
  }
}
```

The plugin's manifest, `plugins/job-board/plugin.json`, declares the event the "New posting" trigger
names, the person-only `sources` setting `inputs.settings` asks for, and the secrets the Scout
binds, each needed only for its source:

```json
{
  "schemaVersion": 1,
  "id": "job-board",
  "name": "Job Board (sample)",
  "description": "A stand-in job board: reads the postings bundled with it, never the network, and reports the ones that match.",
  "version": "0.1.0",
  "protocol": "harness.member/1",
  "executable": { "path": "job-board", "args": [] },
  "timeoutSeconds": 60,
  "requires": ["python3"],
  "config": {
    "keywords": {
      "type": "list",
      "description": "A posting matches when its title holds one of these words. Empty matches every posting.",
      "default": []
    },
    "sources": {
      "type": "list",
      "enum": ["sample", "adzuna", "usajobs", "themuse"],
      "default": [],
      "setBy": "person",
      "description": "The boards this member may read. Empty reads none. adzuna, usajobs and themuse each need their keys set on the Host."
    }
  },
  "secrets": {
    "adzunaAppId": { "description": "Your Adzuna application id, from developer.adzuna.com. Needed to read Adzuna.", "when": { "sources": "adzuna" } },
    "adzunaAppKey": { "description": "Your Adzuna application key, from developer.adzuna.com. Needed to read Adzuna.", "when": { "sources": "adzuna" } },
    "usajobsApiKey": { "description": "Your USAJOBS API key, from developer.usajobs.gov. Needed to read USAJOBS.", "when": { "sources": "usajobs" } },
    "usajobsUserAgent": { "description": "The email address you registered the USAJOBS key with; USAJOBS asks for it on every request.", "when": { "sources": "usajobs" } },
    "themuseApiKey": { "description": "Your The Muse API key, from themuse.com/developers. Needed to read The Muse.", "when": { "sources": "themuse" } }
  },
  "events": {
    "publishes": [
      {
        "type": "posting-found",
        "summary": "A posting matched the keywords for the first time. Once per new posting.",
        "fields": [
          { "name": "id", "kind": "string", "summary": "The posting's id, and its document id in the tracker's jobs collection." },
          { "name": "title", "kind": "string", "summary": "The job title." },
          { "name": "company", "kind": "string", "summary": "The company." },
          { "name": "url", "kind": "string", "summary": "Where the posting lives." }
        ]
      }
    ]
  }
}
```

Check it:

```
$ yawble solution check samples/solutions/job-tracker
Job Tracker 1.0.0 (job-tracker) passes its check. Installing samples/solutions/job-tracker would create:

Team: Job Tracker
  Instructions: This team runs a job search for one person. …

Members (3):
  Coordinator: agent, manager, preset chosen by the team
    Instructions: You coordinate the job search. …
  Scout: plugin job-board 0.1.0
    setting keywords = ["engineer","developer"]
  Writer: agent, member, preset chosen by the team
    Instructions: You write cover letters. …

Plugins installed (1):
  job-board 0.1.0 (Job Board (sample)), publishes plugin.job-board.posting-found

Triggers (5):
  Scan for postings: runs once now, then every 3600 seconds, wakes Scout; wakes the Manager: never; no daily cap
    Instruction: scan
  New posting: on plugin.job-board.posting-found, wakes Writer; wakes the Manager: never; daily cap 200,000 tokens
    Instruction: A new posting matched: {event.title} at {event.company} ({event.url}). …
  Apply pressed: on site.action where siteAction eq tracker/apply, wakes Writer; wakes the Manager: onHandbackOrFailure; daily cap 400,000 tokens
    Instruction: {event.by} pressed Apply on {event.payload}. …
  Resume changed: when files change in documents/Resume matching *, wakes Writer; wakes the Manager: onHandbackOrFailure; daily cap 300,000 tokens
    Instruction: The resume in Resume/ changed ({event.changed}). …
  Morning summary: cron 0 0 8 * * 1-5 (Europe/London), wakes Coordinator; wakes the Manager: never; daily cap 100,000 tokens
    Instruction: Read the tracker site's jobs collection. …

Team skills (1):
  job-search-playbook (for manager, member): Use when working the Job Tracker team's search - …

Sites published (1):
  tracker (3 files)

Tools: tools/ copied to the team's solution/ folder, named {solution} in instructions (1 file): make-cover-letter.py

You will be asked for:
  documents in Resume/ (required): Your reference resume, .docx or PDF. The Writer starts every letter from it.
  Scout's setting sources (optional): Which job boards the Scout may read. It reads only the boards you tick.
```

(The CLI prints every instruction whole; they are shortened here.) Break it, and the check says
where:

```
$ yawble solution check ./job-tracker
./job-tracker does not pass its check (2 problems):
  solution.json triggers[1].member: `triggers[1].member` 'Recruiter' names no member of this package; its members are Coordinator, Scout, Writer.
  solution.json triggers[2].dailyTokenCap: `triggers[2].dailyTokenCap` must be a whole number of tokens, at least 1, or null for no cap.
```

### Version 1.1.0

[`samples/solutions/job-tracker-1.1.0.overlay`](../samples/solutions/job-tracker-1.1.0.overlay)
holds only what 1.1.0 changes. Copy `job-tracker`, then copy the overlay over the copy:

```
cp -r samples/solutions/job-tracker /tmp/job-tracker-1.1.0
cp -r samples/solutions/job-tracker-1.1.0.overlay/. /tmp/job-tracker-1.1.0/
```

Against 1.0.0 it adds the member Reviewer and its Weekly review trigger, changes New posting (one
more instruction sentence, a cap of 250,000), removes the Resume changed trigger, adds the team
skill `interview-prep` and ships `job-board` 0.2.0. The tests make it the same way
(`SolutionSamples.JobTracker("1.1.0")`).

## Where it is pinned

- A page linking to its site's files folder passes the check, and the install creates each site's
  files folder, whose file the page's static link downloads:
  `SolutionCheckRouteTests.A_package_whose_page_links_to_its_sites_files_passes_the_check`,
  `SolutionInstallTests.An_installed_packages_sites_each_get_a_files_folder`.
- Every refusal case, each naming its file and field: `SolutionCheckTests` (the five named ones as
  their own tests - `A_trigger_naming_a_member_the_package_does_not_have_is_refused`,
  `A_plugin_member_naming_a_plugin_the_package_does_not_ship_is_refused`,
  `A_skill_named_like_a_built_in_is_refused`, `An_event_type_the_catalog_does_not_know_is_refused`,
  and the escapes `A_folder_trigger_path_leaving_the_documents_is_refused`,
  `A_document_input_folder_leaving_the_documents_is_refused`,
  `A_link_leaving_the_package_is_refused_naming_the_link`,
  `A_plugin_executable_path_leaving_its_folder_is_refused`,
  `A_skill_or_site_named_by_a_path_is_refused` - and one row per malformed or missing field in
  `A_malformed_or_missing_field_is_refused_naming_its_file_and_field`).
- The sample passes and its plan is complete:
  `SolutionCheckTests.The_job_tracker_sample_passes_and_its_plan_is_what_an_install_would_create`;
  1.1.0 differs as its overlay says: `The_1_1_0_variant_passes_and_differs_from_1_0_0_as_its_overlay_says`;
  unknown keys: `Unknown_top_level_keys_are_kept_and_ignored`; nothing written: `The_check_writes_nothing`.
- The route: a person and the Concierge are answered, a member is refused whatever it holds, a folder
  outside the data root or through a leaving link is refused, and nothing is created:
  `SolutionCheckRouteTests`. Its one marker is `RequirePermit(CreateTeam)`, the Concierge's and never
  a team's, and the handler refuses a member besides (`RouteMarkerTests` holds the one-marker rule).
- The board notice: `SolutionNoticeTests` (a pass with its link on the board and the backlog item, a
  fail naming file and field, and no notice for a package not written during the workflow); on the
  web, `activity-feed-solution-notice.mount.spec.ts` and `backlog-dialog-solution-notice.mount.spec.ts`.
- The CLI against Podman and Docker scripted engines: `cli/internal/cli/solution_test.go`, answered
  with what the Host really prints, which `SolutionCheckCliFixtureTests` keeps equal to the Host.
- The install, step by step: `SolutionInstallTests.A_person_installs_the_package_and_every_step_makes_what_it_says`
  (the plugin, the team and its local repository, the Manager's name and instructions, the members,
  the team skill, the read-only tools named in the prompt, the live site, every trigger with its
  cap, wake setting and full instruction, the record and its row, and `ITeamSolutions.For`).
- A failure at each step leaves nothing behind:
  `SolutionInstallTests.A_failure_at_any_step_leaves_nothing_behind` (one row per step: no plugin,
  team, member, skill, tools, site, trigger, record, folder or repository), and a step the platform
  itself refuses is named with its sentence:
  `SolutionInstallTests.A_step_the_platform_refuses_is_named_with_its_own_sentence_and_everything_before_it_undone`.
- The update from 1.0.0 to 1.1.0: `SolutionInstallTests.Updating_from_1_0_0_to_1_1_0_shows_the_diff_keeps_the_persons_part_and_applies_it`.
- A skipped required document blocks the team by name until provided:
  `SolutionInstallTests.A_skipped_required_document_blocks_the_team_naming_it_until_it_is_provided`.
- A person's only: `SolutionInstallTests.Install_update_preview_and_the_list_are_a_persons_only_and_an_agent_never_installs`.
- The deep link's folder rule: `SolutionInstallTests.A_link_opens_only_a_folder_in_the_documents_or_a_teams_folder`.
- The team skill offered to its team's Manager only: `SolutionInstallTests.Its_team_skill_is_offered_to_its_manager_and_refused_to_another_teams_manager`.
- The operator's request file: `SolutionInstallTests.The_operator_clis_request_file_is_answered_with_the_routes_body_and_its_documents_are_copied_in`;
  a team's deletion forgets its package: `SolutionInstallTests.Deleting_the_team_forgets_its_package_and_keeps_its_plugins`.
- `auth-016`: `FreshVolumeTests.The_team_solutions_step_applies_to_an_empty_volume_and_round_trips`
  and `ShippedSchemaStepsTests`.
- The web wizard, one spec per step (`solution-wizard-team`, `-review`, `-inputs`, `-install`), the
  Plugins dialog opening it (`plugins-dialog-solution`), the deep link through sign-in
  (`web/src/pages/__tests__/solution-deep-link.mount.spec.ts`) and the blocked notice
  (`solution-blocked-banner`), all `*.mount.spec.ts`.
- `yawble solution install` against Podman and Docker scripted engines:
  `cli/internal/cli/solution_install_test.go`.
- Secrets: the check - `SolutionCheckTests.A_valid_secrets_block_passes_and_the_plan_names_each_key_and_what_it_is_for`,
  `A_secret_field_the_plugin_manifest_does_not_declare_is_refused_naming_file_and_field`,
  `A_secret_key_that_is_not_a_legal_environment_variable_name_is_refused_naming_file_and_field`,
  `A_value_looking_secret_entry_is_refused_naming_file_and_field_and_never_repeated`,
  `Every_secrets_refusal_is_named_at_once`, `An_agent_member_naming_secrets_is_refused` and
  `A_manifest_secret_needed_when_an_unknown_setting_or_value_is_refused`; the install -
  `SolutionSecretsTests.Install_binds_the_job_tracker_plugin_member_to_its_five_keys_and_names_each_keys_set_state_never_its_value`
  (with the no-value scan of every response and every file the Host wrote); the update -
  `SolutionSecretsTests.An_update_keeps_a_binding_the_person_changed_and_takes_the_packages_new_key_where_they_did_not`
  and `Merged_secrets_keep_what_the_person_changed_or_unbound`; the wizard -
  `solution-wizard-secrets.mount.spec.ts`; the CLI -
  `TestSolutionInstallPrintsEachSecretByKeyAndTheKeysStillUnset`.
- A first run at install: the check - `SolutionCheckTests.RunAtInstall_on_a_schedule_passes_and_the_plan_says_it_runs_once_now_then_on_its_clock`,
  `Without_runAtInstall_the_plan_says_only_the_clock` and the `runAtInstall` rows of
  `A_malformed_or_missing_field_is_refused_naming_its_file_and_field` (on an event trigger, on a
  folder trigger, not a flag); the install -
  `SolutionFirstRunTests.RunAtInstall_fires_once_at_install_through_the_schedules_own_fire_and_next_on_its_interval`
  and `Without_runAtInstall_nothing_fires_at_install_and_the_result_names_the_first_due_time`; no
  fire from a failed step - `SolutionInstallTests.A_failure_at_any_step_leaves_nothing_behind`; the
  wizard - `solution-wizard-first-runs.mount.spec.ts`; the CLI -
  `TestSolutionInstallNamesEachSchedulesFirstRun`.
- The panel keys: `SolutionCheckTests` (`A_primary_site_the_package_does_not_have_is_refused`,
  `An_output_folder_that_is_not_a_safe_relative_folder_name_is_refused`,
  `A_panel_setting_the_plugin_does_not_have_is_refused`,
  `A_status_line_with_a_placeholder_this_host_does_not_fill_is_refused` and the rest of that section).
- The status line: `SolutionStatusTemplateTests` (every placeholder, missing data, a value that looks
  like HTML, the refused forms, the default line).
- The launcher and the panel: `SolutionPanelTests` (the tile's filled status line, state and Open; the
  panel's status, controls and results with measured spend only; a cap changed through its existing
  route showing at once; Uninstall removing what the package made and keeping the documents and the sites offline with
  their data; a reinstall onto the kept team bringing them back; a
  plugin another team uses kept; a person's only).
