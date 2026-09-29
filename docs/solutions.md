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
- **Sites** are published as a team publishes one ([sites.md](sites.md)).
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
  "inputs": { "settings": [ … ], "connections": [ … ], "documents": [ … ] }
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

**Unknown top-level keys are kept and ignored**, never refused, so a package written for a later
Host (with a control panel or status lines, say) still checks here. The check lists them as
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

A plugin member takes no `role`, `preset` or `instructions`; an agent member takes no `settings`.

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

These are the trigger settings a person sets in the Triggers dialog. An agent member is never
subscribed to a high-volume event, as the dialog refuses it too.

### Inputs

What only a person provides. The install asks for each; a skipped required document or connection
leaves the team showing it as blocked, naming it, until it is provided. A required setting with no
default cannot be skipped: its plugin member cannot be hired without it.

| Field | Each entry |
|---|---|
| `settings` | `{ "member", "setting", "description", "required" }`: a person-only setting of a plugin member. |
| `connections` | `{ "member", "slot", "description", "required" }`: a connection slot of a plugin member, bound to one of the person's connections ([connections.md](connections.md)). Every slot a plugin requires must be asked for. |
| `documents` | `{ "folder", "description", "required" }`: a folder of the team's documents the person uploads into, such as `Resume`: "your reference resume, .docx or PDF". |

## The check

The check reads a package and answers either **what an install would create** or **everything
wrong with it**. It writes nothing. One validator serves every caller: the route, the CLI and, later,
the install and the board's notice.

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
sentence saying what to change. Every refusal is reported at once, not only the first. It refuses:

- a missing or malformed field in `solution.json`, or a `format` it does not read;
- a trigger naming a member the package does not have;
- a plugin member naming a plugin the package does not ship;
- a plugin manifest the Host would refuse, or one whose `id` is not its folder's name;
- a setting the plugin does not have, a value its manifest refuses, or a person-only setting set by
  the package; an input naming a setting that is not person-only, or a slot the plugin lacks;
- a skill whose name is a built-in's or begins `plugin-`, whose file is missing, or whose front
  matter lacks a matching name, a description, roles or a body;
- an event type the catalog does not know, a filter on a field it does not carry, and a high-volume
  type on an agent member;
- a preset this instance does not have;
- a site that is not a slug, has no folder, or has no `index.html`;
- `{solution}` in an instruction when the package has no `tools/`;
- **any path that escapes**: a folder trigger or document input outside the team's documents, a
  skill or site named by a path, a plugin's executable or skill outside its folder, and any link in
  the package that is absolute or leads out of it.

## The install

**Admin → Plugins → Install from a folder** opens the install wizard when the folder holds
`solution.json` (a plain plugin folder installs as before). The wizard's steps:

1. **Team**: a new team, named after the package by default (editable, and checked as any team name
   is), with a new local repository unless unticked; or **update an existing team** that was
   installed from an earlier version of the same package id.
2. **Review**: every member with its full instructions, every trigger with its kind, schedule or
   event and filter or folder, the member it wakes, its wake setting, its **daily cap** and its
   **full instruction text**, every skill, site, plugin and tool. In an update, what is added and
   changed is marked and what is removed is listed. Agent-written instructions become prompts, so
   this is the safeguard: nothing runs before the person has seen it.
3. **Your part**: the person-only settings, a connection picker per slot, and an upload box per
   requested document folder. Skipping a required document or connection is allowed.
4. **Install**: the steps below; on failure the wizard names the step and the reason.

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
  documents and every site's data;
- rewrites the `team_solutions` row with `solution.updated`.

Only a newer version updates: the same or an older one, or another package's id, is refused with a
sentence.

**Routes** (install, update and what they read are a person's only - `HumansOnly`; an agent never
installs, and person-only settings stay person-only):

| Route | |
|---|---|
| `POST /api/solutions/preview` `{ folder, team? }` | What installing would do, writing nothing: `mode: install` with the team name and why it cannot be used, or `mode: update` with `from`, `to` and the diff. |
| `POST /api/solutions/install` `{ folder, teamName?, agent?, localRepository?, settings?, connections? }` | The install. `{ ok: true, team, missing, steps }`, or `{ ok: false, step, stepNumber, title, reason, undone }`. 409 for a taken name. |
| `POST /api/solutions/update` `{ folder, team, settings?, connections? }` | The update, with `from` and `diff`. |
| `GET /api/solutions/installed` | Every team installed from a package, with its id and version. |
| `GET /api/teams/{team}/solution` | The package a team came from, and what it still waits for. `Read`: the team's own members may read it too. |

**The deep link** `#/solutions/install?folder=<absolute path>` opens the wizard filled in with that
folder. It never installs by itself: the person still reviews and presses Install. A signed-out
visitor signs in first and lands in the wizard. The check it makes carries `from: "link"`, and a
folder outside the instance's documents and every team's folder is refused with a sentence; a link
is a convenience someone hands the person, and the review is the safeguard.

**The CLI**: `yawble solution install <folder> [--team <name>] [--from-instance] [--yes]` asks the
same questions in the terminal - the team name (or, with `--team` naming a team installed from an
earlier version, the update and its diff), each person-only setting, each connection slot, a file
for each document folder - and then installs, printing each step or the step that failed. It stages
the folder under `<dataRoot>/plugins/.solutions/`, which only the Host and root can write, and
hands the Host a request file (`<dataRoot>/plugins/.solution`) that `SolutionRequests` answers with
exactly what the matching route answers. An agent cannot write there, so it cannot install.

**Deleting the team** forgets its `team_solutions` row; the package's plugins stay installed
(deleting a team never removes a plugin), and the delete dialog says so.

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
      "settings": { "keywords": ["engineer", "developer"] }
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
names, and the person-only `sources` setting `inputs.settings` asks for:

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
      "enum": ["sample"],
      "default": [],
      "setBy": "person",
      "description": "The boards this member may read. Empty reads none."
    }
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
  Scan for postings: every 3600 seconds, wakes Scout; wakes the Manager: never; no daily cap
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
