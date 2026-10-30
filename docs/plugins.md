# Plugin members

A **plugin member** is a team member backed by an installed executable instead of a coding-agent
CLI. It is hired, woken, queued, stopped and shown exactly like an agent member. The same member
runtime hosts both; only the runner behind it differs.

```
Team
 └─ MemberRuntime            (per member: subscription, queue, admission, marks, rows, Stop)
     └─ IMemberRunner         (MemberRunnerRouter picks per invocation from team_members.agent)
         ├─ AgentMemberRunner   "claude-headless", "codex-headless", ...  → agent CLI
         └─ PluginMemberRunner  "plugin:<id>"                             → plugin executable
              both launch through ChildProcess: setsid, the agent user, process-group kill
```

What a plugin member is **not**:

- It is not an agent. It has no system prompt and runs no model. It holds no platform credential,
  so it has no `HARNESS_KEY` and cannot call MCP tools.
- It is not a container. It runs as a child process of the Host's worker, the same place an agent
  member's CLI runs, as the same non-root `agent` user agents use. There is no Docker or Podman sibling container, and root is never needed.
- It is not registered in code. A plugin is a directory with a manifest, read at start and on
  rescan. Nothing is compiled in.

This page covers manifest v1 (`plugin.json`) and the protocol `harness.member/1`, with plugin
skills, published events and hiring by a Manager (see [Skills](#skills), [Events](#events) and
[Hiring](#hiring)). A plugin arrives as a version folder: built by you or a team, or fetched from
**Marketplace → Browse** ([solutions.md](solutions.md#getting-a-package-in)). Plugins are not
signed; a person reviews what they install.

## Install a plugin

There are two install paths. Both end in the Host's one installer (`PluginInstaller`), so both check
the same rules, lay the plugin out the same way, set the same owner and modes, switch `active` to it
and print the Host's verdict:

- **From your computer.** `yawble plugin install <folder>` copies a version you built on the machine
  that runs the instance into it (below).
- **From inside the instance.** A plugin a team built in its worktree, or the Concierge in its
  workspace, is already in the container. `yawble plugin install --from-instance <path>` installs it
  where it is, with no copy out of the container and back
  ([Install from a folder inside the instance](#install-from-a-folder-inside-the-instance)).

Install a built plugin version from your computer with the operator CLI:

```sh
yawble plugin install <folder>
```

`<folder>` is one built version of a plugin: it holds `plugin.json` and everything the manifest names.
That is the whole install. There is no copy, `chown`, `chmod` or rescan to do by hand, and no restart.
The command:

1. **Checks the manifest before anything is copied.** It uses the Host's rules (below) and names the
   field that is wrong. It also checks that the executable and every skill are files inside the
   folder after symlinks are resolved. It does not require an execute bit: the Host sets one. An
   existing `<id>/<version>` is refused here unless you pass `--force`.
2. **Copies the folder into the running instance's staging folder**, `/data/plugins/.incoming/<id>-<version>/`,
   through the instance's engine (Podman or Docker, `podman cp` / `docker cp`), and gives the copy
   `harness:agent` with directories `0750`, files `0640` and the manifest's executable `0750`, so the
   Host can read it. The folder is dot-named, so the catalog passes over it.
3. **Has the running Host install it from there**, through the same request file as
   `--from-instance` ([below](#install-from-a-folder-inside-the-instance)), so every check, the layout
   at `/data/plugins/<id>/<version>/`, the owner and modes (a folder copied from Windows has no
   execute bit, and the Host's installer sets it), `active` and the rescan are the Host's
   `PluginInstaller`'s, and so is the rule below on what an installed solution team needs.
   Earlier version directories are kept, for going back: `yawble plugin install <older folder> --force`.
4. **Prints the Host's verdict**: installed, or refused with its reason (exit 1). The staging folder
   is removed either way.

**A solution team is never left on a plugin older than it needs.** Each team installed from a
solution package needs, for each plugin the package ships, a minimum version: the package's
`pluginMinimums` entry for it, else the version it ships ([solutions.md](solutions.md#solutionjson)).
Making a version active - installing it, Replace (`--force`), or installing a kept older version
again - through the web app, `yawble plugin install` or `--from-instance`, is refused (409) while it
is older than any installed solution team's minimum for that plugin, with one sentence naming each
team, its package and version, and the minimum: "job-board 0.0.9 is older than what Job Tracker 1.0.0 on
team Job Tracker needs (0.1.0 or newer)." Nothing changes. A package that was uninstalled (its team and sites
kept) needs nothing, and a version newer than the active one is never refused by this rule.
Marketplace → Installed lists, under each plugin, the solution teams that use it and the minimum
each needs (`solutions` on `GET /api/plugins`).

`yawble plugin list` shows every version on the volume, whether it is active, and what the Host made
of it. `yawble plugin remove <id> [--version <v>]` removes a plugin or one kept version. It asks first
(`--yes` answers), and it refuses while a member is hired on the plugin, naming the members.

**Remove in the web UI.** Admin → Plugins has **Remove plugin** on each plugin and **Remove version** on
each version of a plugin that has more than one, with the same rules (`DELETE /api/plugins/{id}[?version=<v>]`,
a person's). It asks first. The whole plugin, or its only version, is refused while a member is hired
on it, naming each member. The active version is refused while other versions are kept. A kept version
is removed even while members are hired on the plugin, because no member runs it. A `plugins.removed`
tenant row is written in the same transaction as the removal, and the catalog is rescanned.

**Deleting a team never removes a plugin.** A plugin is the instance's, not a team's. When a team was
installed from a solution package, its delete dialog names the package's plugins, says they stay
installed, and points to Admin → Plugins to remove them.

**How the Host is told, without a person's key.** The CLI writes a nonce to `/data/plugins/.rescan`
through the engine, as root in the container. The Host (`PluginRescanRequests`) looks for that file
once a second. When it finds one, it calls the same `PluginCatalog.Rescan` that `POST /api/plugins/rescan`
calls. It then writes what it now holds, with that nonce, to `/data/plugins/.rescan-report.json`: installed
plugins, refused directories with the reason, and the members hired on each plugin. The CLI reads that
report back. Only the Host user and root can write in `/data/plugins` (`harness:agent 0750`), so an
agent cannot ask for a rescan. The report names members of every team, so the Host writes it `0600`.
When the Host does not answer within 20 seconds the CLI says so; a restart (`yawble down`,
`yawble up`) loads the files it has already put in place.

### Install from a folder inside the instance

A plugin built inside the instance - by a team in its worktree, or by the Concierge in its workspace -
is installed where it is, with no copy out to the operator's computer. Both ways are a person's:

- **The web app:** Admin → Plugins → "Install from a folder…" picks the built version folder and calls
  `POST /api/plugins/install` with `{ "path": "<absolute folder>", "replace": false }` (humans-only).
- **The operator CLI:** `yawble plugin install --from-instance <path>` asks the running Host through a
  request file, the same seam as the rescan. Through `podman exec` (or `docker exec`), which runs as
  root, it writes `{"request": "<nonce>", "path": ..., "replace": ...}` to `/data/plugins/.install`,
  owned `harness:agent` `0640` like `.rescan`: the Host user reads and removes it, and an agent can
  neither write it nor create it. The Host answers in `/data/plugins/.install-report.json` (`0600`,
  the Host user's; the CLI reads it as root) with that nonce, `status` (200, 400 or 409), `id`,
  `version`, `installed`, `replaced` and `reason`. `--force` is `replace`. The CLI prints the verdict
  and exits 1 on a refusal; a Host that does not answer within 60 seconds is reported and the
  request withdrawn.

The route and the request file share one implementation (`PluginInstaller`), so they check, write and answer alike:

1. **Refused before anything is written**, each with a sentence naming why:
   - a path that is not absolute, not a folder, or outside the data root (`/data`);
   - a path through a symlink that leads out of the data root;
   - a symlink inside the folder that leads out of it, or that is absolute (a copy of it would still
     point at the source, which the agent that built it can change);
   - a folder already under `/data/plugins`;
   - a manifest the catalog would refuse, by the catalog's own check: its rules, the executable and
     every skill inside the folder after symlinks, and every runtime it `requires`;
   - an id and version already installed, unless replacing was asked for (409);
   - a version older than an installed solution team needs (409), naming each team, its package and
     version, and the minimum - whether it is new, a Replace or a kept version installed again.
2. **The layout, ownership and modes**: `/data/plugins/<id>/<version>/`, owned by
   the Host user with the `agent` group, directories `0750`, files `0640`, the manifest's executable and
   every `platforms` entry `0750`. The copy is staged beside the target and moved into place.
3. **`active`** names the new version, and the catalog is rescanned.
4. **The Host's verdict** is the answer: installed, or refused with its reason.
5. **A `plugins.installed` row** is appended to `tenant_events`: who, the id and version, the source
   folder, whether it replaced one, and the verdict (`by: "operator"` for the CLI).

An agent cannot install: the route is humans-only, and only root and the Host user can write the
request file.

```sh
yawble plugin install --from-instance /data/teams/<team>/repos/<repo>/<tree>/build/<id>/<version> [--force]
```

The CLI prints the Host's verdict and exits 1 on a refusal (400), an installed version without
`--force` or one older than an installed solution team needs (409), or `installed: false`. If the Host does not answer within 60 seconds, it says so
and withdraws the request, so a later Host never carries it out.

The layout the Host reads, whatever put it there:

```
plugins/<id>/<version>/plugin.json   the manifest
plugins/<id>/<version>/...           its executable, skills, anything it ships
plugins/<id>/active                  one line: the version in use
```

- **Identity is the manifest's `id`, not the path.** The directory must match `id`, and the version
  directory must match `version`.
- **`active` picks the version.** Without an `active` file, a plugin with exactly one version
  directory uses that version.
- **Where the executable may live.** It must resolve inside its version directory after symlinks
  are resolved, and it must be executable.
- **Ownership.** Plugins are owned by the Host user and readable and executable by the `agent` group,
  never writable by `agent`. A member must not be able to replace a plugin's files. The container's
  entrypoint (`scripts/prepare-volume.sh`) creates `/data/plugins` as `harness:agent 0750` and
  re-applies these modes at every start, keeping each file's execute bits.

The Host prints one line per installed plugin at start and at every rescan. It also prints one line
per refused directory, naming the field that is wrong. `GET /api/plugins` lists the same thing. A
person can also rescan with `POST /api/plugins/rescan`. A member already pointing at the plugin runs
the new version on its next wake.

A newer version may drop a `config` field an older one declared. A value a member stored for it is
kept: the run never receives it (a run gets only the fields the active manifest declares), and
saving the member's settings with that value sent back unchanged is accepted. Setting a new value for
a field the active version does not declare is refused, naming the field.

### Plugins to install, and the template to build one

A plugin to install comes from the marketplace, [yawble.ai](https://yawble.ai), not from this
repository: `sample-echo` (the sample plugin: deterministic, it upper-cases or reverses
each instruction's text; a .NET console app its launcher runs with `dotnet`), `sample-echo-go`,
WhoAmI and the rest are packages there, each with its own build and install steps. This repository
holds no plugin to install. Its tests build their own copies as test data under `tests/Fixtures/`,
which is never listed, built into an image or shipped.

A team building a plugin starts from the Go template, `templates/plugin-go` (see
[The Go template](#the-go-template)), and installs the version folder it builds with
`yawble plugin install`. That prints the Host's verdict, here for the .NET `sample-echo` 0.1.0:

```
copied sample-echo 0.1.0 to /data/plugins/sample-echo/0.1.0 and made it the active version
the Host reports sample-echo 0.1.0 installed; hire it as plugin:sample-echo
```

### The Go template

`templates/plugin-go` is how a team builds a plugin, not a package to install: an echo plugin
(`sample-echo-go`) to copy into the plugin's own repository, rename and fill in. It is the template
for a connector (see [Choosing a language](#choosing-a-language)). `build.sh` builds two static binaries
(`CGO_ENABLED=0`, `GOOS=linux`, `GOARCH=amd64` and `arm64`) into `bin/linux-x64/` and
`bin/linux-arm64/`, which the manifest's `platforms` map names; the Host runs the one for its
processor. It needs no runtime from the image, so its manifest has no `requires`.

```sh
# From the copy's folder, on your computer or inside the instance.
./build.sh ~/plugins-build/sample-echo-go/0.1.0
yawble plugin install ~/plugins-build/sample-echo-go/0.1.0

# Built inside the instance, in a team's tree: install it where it is.
yawble plugin install --from-instance /data/teams/<team>/repos/<repo>/<tree>/build/sample-echo-go/0.1.0
```

`PluginGoTemplateEndToEndTests` builds both binaries, checks each is a static ELF for its processor,
installs the folder, hires it and runs it on the Host's processor.

### Hire it into a team

Hire it through the ordinary member route, as a person, naming `plugin:<id>` as the member's Agent:

```http
POST /api/teams/{team}/containers
Content-Type: application/json

{
  "name": "Echo",
  "agent": "plugin:sample-echo",
  "config":       { "mode": "reverse" },
  "secretValues": { "token": "…the value the person typed…" }
}
```

- **`config`** is checked against the manifest. Refused at hire, with the field named: an unknown
  field, a value of the wrong type, outside its `enum` or outside a number's bounds (see
  [Number bounds](#number-bounds)), or a missing `required` field with no default.
- **`secretValues`** is THIS member's own value for each secret field the manifest names (see
  [Configuration and secrets](#configuration-and-secrets)). It is kept for this member only, with the
  member, or the member is not made. A `required` secret with no value and no set key binding is
  refused at hire. An agent's hire (a Manager's, a Concierge's) cannot carry one.
- **`secrets`** (optional) binds a field to a **logical key** instead, a name such as
  `ACME_STORAGE_KEY` whose value the Host holds instance-wide; the API, the operator CLI and solution
  packages use it. A member's own value wins over its binding.
- **What you get.** The member appears on the team's board with `kind: "plugin"`. Work it is told
  through `tell`, the board, a trigger or a schedule reaches it through the same queue any member
  has. Its result is its `agentContainer.completed` row's `output`, and that row wakes the Manager
  like any member's. Any member of its team may `tell` it, without the Tell permit a member needs
  to tell an agent member, so a command that answers the member that asked reaches that member.
- **What is refused.**
  - `config`, `secrets` and `secretValues` on an Agent member.
  - A plugin that is not installed.
  - Repointing a member between an Agent and a plugin. Hire a new member instead: an agent member's
    credential and environment must not survive into a plugin member.

### In the web UI

- **Add member.** The dialog offers each installed plugin (from `GET /api/plugins`) after the Agent
  presets. Choosing one shows every setting of its manifest on one screen, in the manifest's order:
  each `config` field as an input whose placeholder is its default, and each secret field as ONE box
  for its value, labelled by the manifest's `label` (else the field's name in words). A masked field
  is a password box; a `"masked": false` field is a plain text box. No key name is asked for; one
  bound shows only in "As JSON". The typed values go with the hire as `secretValues`, through the
  route above. An empty optional field is left out of the request.
- **Member card.** A `kind: plugin` card shows no live view, no tokens and no spend. It names the
  plugin instead of an Agent and words its failures without provider or spend terms. Its history
  button lists its earlier runs, and each run opens to what it reported.
- **Earlier runs.** `GET /api/teams/{team}/members/{member}/runs` lists every finished run of a plugin
  member, one per run (not one per batched message), newest first. Each run carries `output`: its
  result output, or a failure's launch error. The plugin's own failure sentence is on the card's
  failed mark, not on the run's row. There is no transcript, so `runs/{seq}/transcript` answers 404.
  A run that blocked every item it was given writes no completed or failed row. It is listed from its
  last `blocked` row with `outcome: blocked`, `output` null and `reason` set to that row's reason, and
  the card's history opens it onto `Blocked: <reason>`. While the member is running, an item its
  current run has already blocked is not listed: the run is not over, and it is listed once it is
  (`An_item_blocked_by_a_run_still_working_is_not_listed_as_a_finished_run`). An agent member's
  run that blocked every item is listed the same way; it recorded no transcript, so there is
  nothing to open.
- **Team KPI card.** On a team whose every member is a plugin, the Tokens tile reads `none` with no
  advice about usage capture, and the workflow tile draws no budget line or bar.

## Manifest v1 (`plugin.json`)

```json
{
  "schemaVersion": 1,
  "id": "sample-echo",
  "name": "Sample Echo",
  "description": "Deterministic test plugin: transforms each instruction's text.",
  "version": "0.1.0",
  "protocol": "harness.member/1",
  "executable": { "path": "sample-echo", "args": [] },
  "timeoutSeconds": 60,
  "config":  { "mode": { "type": "string", "enum": ["upper", "reverse"], "default": "upper" } },
  "secrets": { "token": { "description": "A demo credential.", "required": false, "label": "Demo token" } },
  "events":  { "publishes": [ { "type": "done", "summary": "Text was transformed.", "highVolume": false,
                                "fields": [ { "name": "length", "kind": "number", "summary": "Output length." } ] } ] },
  "skills":  ["skills/sample-echo.md"]
}
```

| Field | Status | Rule |
|---|---|---|
| `schemaVersion` | required | Must be `1`. A later number is refused by name rather than misread. |
| `id` | required | `^[a-z0-9][a-z0-9-]{0,47}$`. Stable forever. Members name it as `plugin:<id>`. |
| `name`, `description` | required | Shown in `GET /api/plugins`. |
| `version` | required | A plain version (`0.1.0`). Must equal its directory name. |
| `protocol` | required | `harness.member/1`, the only protocol this Host speaks. |
| `executable.path`, `executable.args` | required / optional | A relative path inside the version directory. `args` is a fixed argv with no substitution. |
| `timeoutSeconds` | optional | An **idle** clock: this long with no `progress` record ends the run. Default 300. |
| `config` | optional | Name → `{type: string, number, bool or list (string when absent); enum; default; required; setBy: "person" or "anyone"; advanced; label; description}`. `label` - a short line of text, 1 to 80 characters - is what a settings screen names the field; anything else refuses the manifest naming the field. A name starts with a letter and holds letters, digits, `_` and `-`, at most 64 characters. `"advanced"` (true or false, nothing else) is accepted and no screen hides a field behind it (see [Every setting on one screen](#every-setting-on-one-screen)); a `number` may add `min`, `max` and `integer` (see [Number bounds](#number-bounds)). A `list` is a list of strings, default `[]`. See [List settings](#list-settings). A `table` is rows of typed columns a person edits; see [Table settings](#table-settings-type-table). |
| `secrets` | optional | Name → `{description, required, label?, masked?, when?}`. A `value` key is refused. `masked` (default `true`; `true` or `false`, anything else refuses the manifest naming the field) says the value is secret: never answered, never shown, redacted from what the plugin writes. `"masked": false` declares a value that is not secret (an account name): the settings screen shows it in a plain text box and the read answers it. `label` is as for `config`. `when` - `{"<config field>": "<value>"}`, one pair - says the secret is needed only while that list or choice setting holds that value (`{"sources": "adzuna"}`); the field must be declared and the value one of its `enum`. A solution install uses it to say a secret for a setting the person left off is not needed. |
| `connections` | optional | Slot name → `{description, providers, scopes, required}`: an OAuth account the plugin acts on, which the Host holds and refreshes. See [Connections](#connections-oauth-accounts). |
| `reads` | optional | A list of `{site, collection}`, at most 32: the collections of the plugin's OWN team's sites it reads, delivered on stdin at each run. See [Reading site data](#reading-site-data-reads). |
| `events.publishes` | optional | `type` is a suffix: lowercase letters, digits, `-` and `.`. The full type is `plugin.<id>.<type>`. `highVolume` (default false) and `fields` (`name`, `kind`: `string`, `number`, `boolean` or `list`, `summary`) are optional; `source` cannot be declared. `monitor` (optional) tells the Monitor what the event did outside the platform; see [Monitor hints](#monitor-hints). `inLedger` is not read in v1: a plugin event declared `highVolume` stays out of the member ledger and is still an ordinary log row, and any other plugin event stays in the ledger. See [Events](#events). |
| `skills` | optional | Files inside the plugin, in the same front-matter format as a skill. Indexed at load and on every rescan. See [Skills](#skills). |
| `requires` | optional | The runtimes the plugin needs from the image: `dotnet`, `node`, `python3`. See [Runtimes](#runtimes-requires). |
| `platforms` | optional | `{"linux-x64": "bin/x64/p", "linux-arm64": "bin/arm64/p"}`, each a relative path inside the version directory. The entry for this machine's processor, when there is one, is run instead of `executable.path`. |
| `actions`, `consumes`, `health`, `signature`, `publisher`, `minHostVersion`, `permits` | reserved | Kept, and not acted on. |

Any other key is ignored and named on the start-up line. This lets a manifest written for a later
Host still load.

### List settings

`"type": "list"` holds a list of strings: an allowlist, a set of scopes. Its default is `[]` when the
manifest gives none, so the plugin always receives an array in the request's `config`. An optional
`enum` limits each item, and a value holding anything else is refused naming the item:

```json
"config": {
  "sendAllowlist": { "type": "list", "setBy": "person", "description": "Addresses it may send to." },
  "scopes":        { "type": "list", "enum": ["read", "send"], "default": ["read"] }
}
```

A `type` other than `string`, `number`, `bool` or `list` refuses the manifest, naming the field.
The web's settings editor shows a list as chips.

### Every setting on one screen

The settings screens (Add member, Member settings, the solution wizard and a solution's panel) show
every `config` field and every secret field of a plugin on one screen, in the manifest's order. A
field's default is its placeholder, and "Reset to default" puts it back. `"advanced": true` on a
`config` field is still accepted - `true` or `false`, anything else refuses the manifest naming the
field - and answered as `advanced` by the settings read and `GET /api/plugins`, but no screen hides a
field behind it.

The plugin's description (`description`, which the settings read answers whole) shows its first
paragraph and a **Documentation** link that opens the
plugin's page on the package catalog in a new tab: `marketplaceUrl`, which the settings read and
`GET /api/plugins` answer as `<origin of the catalog address>/marketplace/<id>` when the catalog's
last read lists the plugin, and null otherwise - then the screen shows the whole description and no
link. The Host names no catalog host: the origin is the one the operator configured
(`Marketplace:Catalog`).

Pinned by `PluginAdvancedSettingsTests`, `PluginFieldLabelsTests`,
`MarketplaceTests.A_plugins_page_is_the_catalog_origin_and_its_id_when_the_catalog_lists_it_and_null_otherwise`
and `PluginMemberSecretsTests.The_settings_read_answers_the_plugins_description_and_no_marketplace_page_when_no_catalog_lists_the_plugin`.

### Number bounds

A `"type": "number"` field may declare `min` and `max` (both inclusive) and `"integer": true` (whole
numbers only). Declare the bounds the plugin can mean: an unbounded number takes a stray keystroke,
and a salary maximum of -2 makes a job board drop every posting that states a salary.

```json
"config": {
  "salaryMax":        { "type": "number", "min": 0, "description": "Highest annual salary to look at." },
  "archiveAfterDays": { "type": "number", "min": 1, "integer": true, "default": 7 }
}
```

- **The manifest is refused**, naming the field, when `min` or `max` is not a number, `integer` is
  not `true` or `false`, a bound is on a field that is not a number, `min`, `max` or the `default` is
  too large to hold (`1e400`), `min` is greater than `max`, or the `default` is outside the bounds
  (or not whole when `integer`).
- **Every writer refuses a value outside them** - Member settings (`PUT .../plugin-settings`), a hire
  (`POST /api/teams/{team}/containers`, Add member and a Manager's `member` tool), and a solution
  install or update (a package's `members[].settings` in the check, a person's `inputs.settings`
  answer at install) - with 400 and one sentence naming the field and the bound, such as
  "`salaryMax` must be at least 0; -2 is below it.", and writes nothing: no setting, no member, no
  `tenant_events` row. One Host check does it (`PluginMemberRunner.SettingsRefusal`, over
  `PluginConfigField.BoundsRefusal`); the web says the bounds earlier, in the field's help, but
  never instead.
- **A value stored before its bounds existed is kept.** It is never rewritten, the member keeps
  running with it, and the settings read names it in `outOfRange` (`{ "<field>": "<sentence>" }`,
  `{}` when none). Saving the settings form with that value unchanged is accepted; changing it to
  another value outside the bounds is refused.
- The settings read's `fields.<field>`, `GET /api/plugins`' `config.<field>` and a solution plan's
  `personSettings[]` carry `min`, `max` (null when not declared) and `integer`.
- A number too large to hold (`1e400`, `-1e400`) is refused by every writer on any number field,
  bounded or not.

Pinned by `PluginNumberBoundsTests` and `SolutionNumberBoundsTests`.

### Connections: OAuth accounts

A plugin that acts on a person's account at an OAuth service (Gmail, Outlook, Google Drive,
Microsoft Graph, anything that speaks OAuth 2.0 with authorization code and PKCE) declares a
**slot** per account it needs. It writes no OAuth code: the Host holds the client and the refresh
token, refreshes and rotates, and hands the plugin a fresh access token on each run. See
[connections.md](connections.md) for connecting an account.

```json
"connections": {
  "mail": {
    "description": "The mailbox to read and send from.",
    "providers": ["google", "microsoft", "custom"],
    "scopes": {
      "google": ["https://mail.google.com/"],
      "microsoft": ["https://outlook.office.com/IMAP.AccessAsUser.All", "https://outlook.office.com/SMTP.Send", "offline_access"]
    },
    "required": true
  }
}
```

| Key | Rule |
|---|---|
| slot name | The key the plugin reads in the request's `connections`. Same form as a config field's name. |
| `description` | One line, shown beside the slot's picker. |
| `providers` | Which providers' connections may be bound: `google`, `microsoft`, `custom` (any custom provider), `custom-<id>` (one custom provider), `imap` for a mailbox signed in with an app password (see [connections.md](connections.md#a-mailbox-with-an-app-password-imap)), `login` for a username and password, or `apikey` for an API key (see [connections.md](connections.md#websites-api-keys-usernames-and-passwords)). At least one. An unknown provider refuses the plugin, with the reason on the Plugins screen. |
| `scopes` | Provider → the scopes the slot needs, or one list that applies to every OAuth provider the slot names. A `custom` entry applies to any custom provider. A connection lacking one of them is refused at binding, with a sentence that offers Reconnect. Scopes do not apply to `imap`, `login` or `apikey`: an entry for one refuses the plugin. |
| `required` | Default false. An unbound required slot blocks the member's runs with a sentence naming the slot, as a missing required secret does. |

- **Without `connections`** a run gets `"connections": {}`.
- **A malformed slot** refuses the plugin with its reason on the Plugins screen, as other manifest
  faults do.
- **The Plugins screen** shows each slot ("needs a Google or Microsoft connection"); a slot whose
  Microsoft scopes span several APIs names each ("needs a Microsoft connection for Azure management,
  Azure SQL and Azure Storage"), and `/api/plugins` lists them per slot as `microsoftApis`
  (`[{ api, name, scopes }]`, null for a slot that does not admit Microsoft).
- **Binding.** A person picks a connection per slot when hiring the member and in Member settings.
  The member stores the connection's id, never a token. The picker lists only the connections a
  person assigned to the member's team (Team settings → Connections). A Manager or Concierge hire may
  bind any connection assigned to its own team, and is refused one that is not. See
  [connections.md](connections.md#which-teams-may-use-a-connection).

#### Several Microsoft APIs from one connection

A Microsoft access token is for one API. A `microsoft` slot's `scopes` may name scopes of several
APIs, and the plugin gets one fresh token per API, from one connection the person signed in to once:

```json
"connections": {
  "azure": {
    "description": "The Azure subscription to report on.",
    "providers": ["microsoft"],
    "scopes": {
      "microsoft": [
        "https://management.azure.com/user_impersonation",
        "https://database.windows.net/user_impersonation",
        "https://storage.azure.com/user_impersonation",
        "offline_access"
      ]
    },
    "required": true
  }
}
```

- **Which API.** A scope's API is the part before its last path segment:
  `https://management.azure.com/user_impersonation` is API `https://management.azure.com/`, and
  `https://database.windows.net//.default` is `https://database.windows.net/`. A bare scope
  (`Mail.Read`, `openid`, `offline_access`) is Microsoft Graph's (`https://graph.microsoft.com/`).
  The sign-in scopes `openid`, `email`, `profile` and `offline_access` go with every API's request,
  so Graph gets a token of its own only when the slot names a Graph scope besides them, or names no
  other API.
- **Names.** The Host names Microsoft Graph, Azure management, Azure SQL, Azure Storage, Key Vault
  and Log Analytics; any other API is named by its address.
- **The sign-in.** The person signs in once. The sign-in asks for every API's scopes, so Microsoft
  shows one consent for all of them. Microsoft's token endpoint answers one API's token per request,
  so the Host redeems the connection's one refresh token for each API with that API's scopes (plus
  `offline_access`). A sign-in with a code whose scopes span several APIs can take two codes:
  Microsoft takes the first code, naming every API, and records the consent to all of them, but
  refuses to finish it for more than one API. The Host then asks at once for a second code naming
  the first API's scopes and the sign-in scopes, for the same sign-in: the Add connection dialog and
  `yawble connect --device` show the new code with a sentence saying Microsoft needs a second code
  and asks no new questions. A refused or expired second code ends the sign-in with nothing stored,
  and there is never a third. Where Microsoft refuses to start a code naming several APIs at all,
  the Host asks for the first API's at once, with one code. Either way the other APIs are redeemed
  from the refresh token, which succeeds when the account or an administrator has consented to
  them for the app, and the connection records every API's scopes it asked for.
- **Tokens.** Each API's token is redeemed when a run needs it and again when it is within 5
  minutes of expiry, so each is refreshed on its own. Refreshes of one connection run one at a time,
  so one at a time per connection and API. A rotated refresh token is stored after each redemption,
  before the next one and before any token is handed out; a rotation the Host cannot store starts
  no run. The refresh token is stored as ciphertext; each API's access token is held in the Host's
  memory only, and a restart redeems it again.
- **A connection with one API** is unchanged: its sign-in's own token, refreshed without naming
  scopes.
- **Failures.** An API the account cannot get a token for (no consent to it, a scope it does not
  know, a sign-in rule for it) blocks the run with a sentence naming the connection, the API and
  Admin → Connections; the connection stays `ok` for its other APIs. A refresh token Microsoft
  refuses marks the connection `needs-reconnect` and blocks the run, as for any connection.
- **Binding** checks every API's scopes; a missing one is named with its API ("`https://database.windows.net/user_impersonation` (Azure SQL)").
- **Needs.** `GET /api/connections/needs?provider=microsoft` answers `microsoftApis` besides
  `scopes`: `[{ api, name, scopes: [{ scope, words, plugins }] }]`, the merged scopes grouped by API
  in first-asked order, each in words where the Host has them (null otherwise). Google's answer has
  no `microsoftApis`.

### Reading site data: `reads`

A plugin reads collections of its own team's sites by declaring them:

```json
"reads": [
  { "site": "board", "collection": "items" },
  { "site": "board", "collection": "settings" }
]
```

- **Each entry** names a `site` and a `collection`, both site names (1-63 lower-case letters, digits
  and hyphens). There is no `team`: an entry that names one is refused, because a plugin reads only
  its own team's sites. A repeated pair, more than 32 entries, or anything that is not a list of such
  objects refuses the plugin, with the field in the sentence. Any other key in an entry is ignored.
- **Without `reads`**, or with `null`, its runs get `"sites": []`.
- **In a solution package** a plugin reads only a site the package ships: the check refuses
  `reads[n].site` naming any other, at install and at update.
- **At run time** a declared site the team does not have (another team's site included) is reported
  as missing in the run's `sites`, with a sentence, and on the card. See the protocol below.

### Runtimes: `requires`

A plugin is self-contained: beyond what the image guarantees, everything it needs lives in its own
folder. `requires` names what it needs from the image, from exactly these: `dotnet`, `node`,
`python3`. A self-contained binary (Go) needs none and omits it.

```json
"requires": ["dotnet"]
```

The catalog looks each one up on the Host's `PATH` when it loads and at every rescan, and refuses a
plugin whose runtime is missing, naming it ("it needs python3, which is not installed on this
instance"), rather than letting its first run fail. A name outside the list is refused naming the
name. The install checks the same before it writes. When control and workers run apart, control's
image carries no runtime, so a runtime is the workers' to have: control does not look it up on its
own `PATH`, and a run on a worker without it fails at launch.

### Named secrets: `namedSecrets`

A plugin that hands secrets on to something it makes - an app it deploys, a service it configures -
cannot know in advance how many there are or what they are for. Fixed numbered fields (`appSecret1`
to `appSecret8`) leave a person telling the team in a message that slot 1 is the Microsoft client
secret, and nothing on screen says what a slot holds. A manifest may instead declare ONE
`namedSecrets` block beside `secrets`: a person gives each secret a NAME they choose and types its
value masked beside it, per member, and the team refers to it by that name.

```json
"namedSecrets": {
  "label": "App secrets",
  "description": "The name is how the team refers to the secret.",
  "max": 20
}
```

- **The block.** `label` follows a field's label rules (1 to 80 characters, one line); `description`
  is text; `max` is a whole number from 1 to 50, 20 when omitted. A key it does not know is ignored
  and named (`namedSecrets.<key>`, in the plugin's `ignored`); anything else malformed refuses the
  manifest naming the field, as the operator CLI's check (`cli/internal/plugin`) does in the same
  words. `GET /api/plugins` answers it as `namedSecrets` (`{ label, description, max }` or null), the
  settings read (`GET .../plugin-settings`) as `namedSecrets` too.
- **A name** is a letter, then letters, digits and underscores, at most 64 characters
  (`^[A-Za-z][A-Za-z0-9_]{0,63}$`), unique per member ignoring case, and never one of the manifest's
  `secrets` field names. **A value** is held to a masked field's rules: not empty, one line, at most
  8192 characters, not padded with white space, at least 4 characters - and never one of the names
  the plugin is told. A refused name or value is 400 with one sentence naming the name, never the
  value, and nothing is stored; one more than `max` is refused naming the limit.
- **Kept** per member in `plugin_member_named_secrets` (schema step `auth-030`) as Data Protection
  ciphertext under `<dataRoot>/keys` (purpose `Harness.PluginMemberNamedSecrets.v1`), with who set it
  and when, exactly as a member's own field values: removing the member or deleting its team removes
  them, a member hired again under the same name holds none, and a clone copies none.
- **The routes**, each `HumansOnly`: `GET /api/teams/{team}/members/{member}/named-secrets` answers
  `{ team, member, label, description, max, namedSecrets: [{ name, setBy, setAt }] }` in name order,
  never a value; `PUT .../named-secrets/{name}` with `{ "value": "..." }` sets or replaces one;
  `DELETE .../named-secrets/{name}` removes one (matched ignoring case; a name not held writes
  nothing). Each answers as `GET` does. A person's hire and the settings save carry
  `namedSecretValues` (name to value; on the save, null removes and a name not sent is left), in one
  transaction with the settings. Each set, replace and removal writes its `member.named-secret-set`,
  `member.named-secret-replaced` or `member.named-secret-removed` tenant row (team, member, plugin
  and name, never the value) in the same transaction, or nothing changes (500 with a sentence). An
  agent's hire carrying `namedSecretValues` is refused 400, as `secretValues` is: a person types
  every value. The settings read answers each name held as `namedSecretStates`.
- **A solution** carries them too. Its plan's `plugins[]` entries carry the block as `namedSecrets`
  (left out for a plugin with none), and `POST /api/solutions/install` and `/api/solutions/update` take
  `namedSecretValues: { <package member>: { <name>: value } }`, beside `secretValues`, under the
  same name and value rules and the same rows, refused to a machine principal or agent as those
  routes are. An update sets them on a member the team already has, checked against the names it
  holds before anything changes, and puts back what each name held if the update is undone.
- **A run** receives them on stdin as `namedSecrets` and `namedSecretNames` (below).
- **The team sees the names.** `GET /api/teams/{team}/named-secret-names` (any principal of the team,
  names only, never a value or who set it) answers each plugin member whose plugin declares the block
  with its names, and the `status` tool lists them, so a Manager can ask a person to add a secret
  named `X` on the member's settings screen and then see that it is there.

Pinned by `PluginNamedSecretsManifestTests` and `cli/internal/plugin/manifest_test.go` (one shared
`tests/Fixtures/PluginNamedSecrets/cases.json`), `PluginNamedSecretsTests` (every route, hire and
save, refusal, transaction, the no-leak scan, the lifecycle and the names `status` reads),
`PluginMemberNamedSecretRowsContract` (both providers), `FreshVolumeTests` and
`SqlServerNamedSecretsStepTests` (the step on an empty and an existing volume), and
`PluginMemberRunnerTests.A_plugin_without_the_named_secrets_block_gets_a_byte_identical_request_and_one_with_it_only_the_two_members`.

### Table settings: `type: table`

A plugin that keeps a LIST OF THINGS a person manages - site logins, accounts, endpoints - declares
one `config` field of `"type": "table"`: rows a person adds, edits, duplicates and deletes on the
member's settings screen, each of typed columns, and secret columns kept as the member's own
secrets.

```json
"config": {
  "entries": {
    "type": "table",
    "label": "Entries",
    "key": "name",
    "maxRows": 100,
    "columns": [
      { "name": "name", "type": "string", "required": true, "label": "Name" },
      { "name": "method", "type": "enum", "enum": ["password", "token", "interactive"], "required": true },
      { "name": "verifyEvery", "type": "number", "min": 1, "integer": true, "default": 24 },
      { "name": "active", "type": "bool", "default": true },
      { "name": "password", "type": "secret" },
      { "name": "session", "type": "secret", "readOnly": true },
      { "name": "status", "type": "string", "readOnly": true }
    ],
    "rowActions": [
      { "action": "sign-in", "label": "Sign in", "when": { "column": "method", "equals": "interactive" } }
    ]
  }
}
```

- **Columns.** `type` is `string` (the default), `number` (with `min`, `max`, `integer`), `bool`,
  `enum` (its `enum` list) or `secret` (masked unless it says `"masked": false`). Each may have
  `label`, `description`, `required` and `default` (not a secret column; a secret column is never
  `required` - whether a row needs one is the plugin's to say). Any column may be `"readOnly": true`:
  a person never writes it - the plugin does, with `row.status`, or for a secret column the platform
  does. A column is never both `required` and `readOnly`.
- **The key** (`key`) names the string column a person writes that names each row: 1 to 64
  letters, digits, `-` and `_`, starting with a letter or digit, unique per member ignoring case. A
  secret cell is stored under its row's key, so a saved row keeps its name (the screen offers
  Duplicate for another name; a row saved under a new name starts with no secrets).
- **The field** has no `default` (it starts as `[]`), `maxRows` from 1 to 500 (100 when omitted),
  and is ALWAYS person-only: `setBy` may be omitted or `"person"`, nothing else. A `secrets` field
  may not take a table's name. `GET /api/plugins` and the settings read answer a table field with
  `table: { key, maxRows, columns, rowActions }`.
- **Rows are checked as a hire is**, by the same rules and in the same sentences, naming the
  field, the row and the column (`` `entries[prod].verifyEvery` ``): each row an object, a usable and
  unique key, only declared columns, every `required` column a person writes, each cell of its
  column's type and inside its bounds, a text cell one line of at most 2048 characters, and NO secret
  cell - a secret column's value is never part of `config`.
- **Only a person fills a table.** An agent's hire (a Manager's or a Concierge's), a package's
  `settings` and a solution's `inputs.settings` may leave it empty and nothing else; the settings
  route (`PUT .../plugin-settings`) is `HumansOnly`, so a Manager, a command or a page's content
  cannot add a row, flip a bool such as Active, or set a secret cell.
- **Saving.** The settings save sends the rows as the field's value; a read-only cell in the body
  is ignored and the stored one kept, so a person's save never forges or loses the plugin's
  status. `tableSecretValues: { <table>: { <row>: { <column>: value } } }` sets or replaces each
  secret cell given and null clears it; a cell not sent keeps its value; a row the save removes
  takes its cells with it. Each change writes its `member.secret-set`, `-replaced` or `-cleared`
  tenant row naming the table, row and column (never the value) in the same transaction as the
  settings. The settings read answers `tableSecretStates: { <table>: { <row>: { <column>: { set,
  setBy, setAt } } } }` - and `value` only for a `masked: false` column. A masked value is never
  answered once saved.
- **Kept** as the member's own secret values (`plugin_member_secrets`, the same Data Protection
  purpose), each under the stored name `cell:` + 40 hex digits of SHA-256 over the table, the row's
  key in lower case and the column - a name no manifest field can have. Removing the member or its
  team removes them, as any member value.
- **A run** receives the rows in `config.<table>` - every cell but a secret one, each column's
  default filled in - and the secret cells under `secrets.<table>.<row>.<column>` (row as the
  settings spell it; `{}` for a table whose cells are all empty), never in `config`. Every masked
  cell joins the run's redaction set.
- **The plugin writes its status** with the `row.status` record (see [Records](#protocol-harnessmember1)).
- **Row actions.** `rowActions` puts a link on each SAVED row whose `when.column` (a string or enum
  column) holds `when.equals`. `action` is one the platform knows - `sign-in`, the interactive
  sign-in page. The screen shows the link once the page is registered in the web app's
  `rowActionLinks` (`web/src/lib/pluginTable.ts`).
- **The platform's own row secret.** A platform feature keeps a value against a member's row (the
  interactive sign-in page's saved session) through `PluginMemberSecretStore`: `CellRefusal(manifest,
  stored, table, row, column, value, byPerson: false, maxLength)` checks it, then
  `SetCellAsync(member, label, plugin, table, row, column, value, actor)` /
  `ClearCellAsync(...)` / `CellValueAsync(member, table, row, column)` keep, clear and read it,
  with the same rows and redaction as a person's cell. Declare such a column `secret` and `readOnly`.
- **The screen** shows the table with Edit, Duplicate and Delete on each row, a bool column a person
  writes as a toggle in the table, a read-only column as text, and a secret column as "Saved" or
  "Not set". Add and Edit open a row editor holding the row to the same rules before Save. Narrow
  (below 40rem), each row stacks as a card. At hire the table is not offered: a person adds rows in
  Member settings once the member exists.

Pinned by `PluginTableManifestTests` (declarations, row rules, merge, run shape, cell names),
`PluginTableSettingsTests` (the request shape, the no-leak scan, keep and clear, refusals, person-only
rows, `row.status`), `cli/internal/plugin/manifest_test.go` and `web/src/lib/__tests__/pluginTable.spec.ts`.

## Protocol `harness.member/1`

**Launch.**
- **Process.** The executable runs through `setsid` as the `agent` user (the same launch agents
  use), in the member's workspace, with the manifest's `args`.
- **Environment.** Only an allowlist is inherited: `PATH`, `HOME`, `LANG`, `LC_ALL`, `TZ`, `TMPDIR`,
  `DOTNET_ROOT`, `DOTNET_CLI_HOME`, plus `HARNESS_CAUSATION`. No provider key, no `HARNESS_KEY`, no
  agent-vendor variable.
- **End of run.** When the run ends, its whole process group is killed.

**stdin**: one JSON document, then EOF.

```json
{
  "protocol": "harness.member/1",
  "plugin": { "id": "sample-echo", "version": "0.1.0" },
  "member": { "id": "Mixed/Echo", "team": "Mixed", "name": "Echo" },
  "causation": 42,
  "correlation": 40,
  "work": [
    { "seq": 42, "type": "agentContainer.instruction.Mixed/Echo", "source": "Mixed/Manager",
      "correlation": 40, "payload": { "instruction": "hello" }, "instruction": "hello" }
  ],
  "config": { "mode": "reverse" },
  "secrets": { "token": "…resolved now…" },
  "secretKeys": { "token": "SAMPLE_ECHO_TOKEN", "backup": "SAMPLE_ECHO_BACKUP" },
  "connections": {
    "mail": { "provider": "google", "account": "person@example.com", "accessToken": "…fresh now…",
              "expiresAt": "2026-09-28T21:05:00Z", "scopes": ["https://mail.google.com/"] },
    "azure": { "provider": "microsoft", "account": "ops@contoso.com", "accessToken": "…management's…",
               "expiresAt": "2026-09-28T21:05:00Z", "scopes": ["openid", "email", "offline_access", "https://management.azure.com/user_impersonation", "https://database.windows.net/user_impersonation"],
               "tokens": {
                 "https://management.azure.com/": { "accessToken": "…fresh now…", "expiresAt": "2026-09-28T21:05:00Z",
                                                    "scopes": ["https://management.azure.com/user_impersonation"] },
                 "https://database.windows.net/": { "accessToken": "…fresh now…", "expiresAt": "2026-09-28T20:50:00Z",
                                                    "scopes": ["https://database.windows.net/user_impersonation"] }
               } }
  },
  "workingDirectory": "/data/teams/Mixed/workspaces/Echo",
  "worktrees": [],
  "siteFiles": [
    { "site": "board", "folder": "/data/documents/Mixed/sites/board/files" }
  ],
  "sites": [
    { "site": "board", "collection": "items",
      "documents": [
        { "id": "t1", "doc": { "title": "First", "status": "archived" },
          "updatedAt": "2026-10-02T09:00:00Z", "updatedBy": "person@example.com" }
      ],
      "total": 1, "cut": null, "missing": null }
  ]
}
```

- **`work`** is the batch, oldest first, all from one workflow. Up to 32 messages can arrive in one
  run.
- **`config`** is the manifest's defaults with this member's own values on top.
- **`secrets`** holds each secret field this member has a value for, resolved at the moment the
  run starts: the member's own value, else the value of the key it binds. This is the only place a
  plugin reads its values: never its environment, which holds none.
- **`secretKeys`** names each secret field: the logical key NAME it is bound to, whether that key is
  set or not, or - for a field set only by the member's own value - the field's own name. `{}` when
  it has none. A field in `secretKeys` and not in `secrets` is a bound key with no value on this
  Host (above, `backup` is bound to `SAMPLE_ECHO_BACKUP`, which is not set). A field with neither a
  value nor a binding is in neither. It is additive: every other member of the request is byte for
  byte what it was before, and a plugin that ignores it is unchanged. A name is not a secret and is
  not redacted. Each name comes from the same field as the value beside it in `secrets`, so a value
  is never beside a name it was not given for; and a value that is itself one of the names is
  refused, at binding and before every run, because it would ride on stdin unredacted. How a plugin words a key that is not set is under
  [Configuration and secrets](#configuration-and-secrets).
- **`namedSecrets`** and **`namedSecretNames`** come only to a plugin whose manifest declares a
  [`namedSecrets` block](#named-secrets-namedsecrets): the member's named secrets, name to value,
  and the names alone, sorted (`{}` and `[]` when it holds none), just before `sites`. Every value
  joins the run's redaction set; a name is not a secret and is not redacted. A value that is one of
  the member's names or a `secretKeys` name is refused before the run (it fails needs-person), since
  it would ride on stdin unredacted. A plugin that declares no block receives neither, and its request
  is byte for byte what it was.
- **`connections`** holds one entry per slot this member has bound: the provider, the account name,
  a fresh access token, when it expires and the scopes it was granted. The Host refreshes the token
  before the run when it would expire within 5 minutes, so a run always starts with at least that
  long. It is `{}` when the manifest declares no slot or the member has none bound; a plugin that ignores it
  is unaffected. The refresh
  token and the client secret never reach a plugin. A **Microsoft** slot also carries `tokens`, its
  last member: API → `{ accessToken, expiresAt, scopes }`, one entry per API the slot's scopes name
  (see [Several Microsoft APIs](#several-microsoft-apis-from-one-connection)), in the slot's order.
  `accessToken` and `expiresAt` are the first API's, and `scopes` is every scope the connection was
  granted; a slot with one API has one entry, holding the same token, and its other members are
  exactly as without `tokens`. Every token in `tokens` joins the run's redaction set. A Google or
  custom slot has no `tokens`. A slot bound to a **mailbox** (`imap`) holds
  instead `{ "kind": "imap", "account", "username", "password", "imap": { "host", "port", "security" },
  "smtp": { "host", "port", "security" } }`, `security` being `TLS` or `STARTTLS`. The app password is
  on stdin only, never in the environment or argv, and is redacted from everything the plugin writes,
  as an access token is. SMTP signs in with `username`, or with `account` when `username` has no @
  (iCloud). A slot bound to a **username and password** (`login`) or an **API key** (`apikey`) holds
  `{ "kind", "account", "preset", "fields": { "<name>": "<value>" } }` - a login's `username`,
  `password` and `url` when set; an API key's preset fields (Stripe `key`; Namecheap `apiUser`,
  `apiKey`; Twilio `accountSid`, `apiKeySid`, `apiKeySecret`) or Other's own - and every masked value
  is redacted from everything the plugin writes.
- **`siteFiles`** names the files folder of every site of the member's OWN team, by name, as an
  absolute path; `[]` when the team has none. A file the plugin makes for a site's page goes under
  that folder, and the plugin stores the path RELATIVE to it in a document with `site.put`; the page
  links to it with `site.files.url(path)` ([sites.md](sites.md#files-a-team-makes-for-a-site)). Every
  site is listed, not only the ones the manifest `reads`, because writing a file needs no read. Paths
  only: never a secret. Never write a secret or a token into a site's files or data.
- **`sites`** holds one entry per collection the manifest [`reads`](#reading-site-data-reads), in
  declaration order; `[]` when it reads none. They are read when the run starts, as the member, from
  the member's OWN team's sites only - the same team-bound lookup `site.put` uses - and reading them
  appends nothing to the tenant log.
  - `documents` are whole, newest `updatedAt` first (`id` breaks a tie): `id`, `doc` (the stored
    JSON), `updatedAt` (UTC) and `updatedBy`, the same shape a page's `site.data.list` returns.
    `total` is how many the collection holds.
  - **The budget.** A run is handed up to 8 MiB of documents, less when the work batch, config or
    secrets are large: what counts is each document's size inside the message that starts the run,
    after escaping. The site limits apply as always (64 KB a document, 10 000 documents a
    collection, 50 MB a site).
  - **A cut is never silent.** Collections fill in order, and the first document that does not fit
    ends delivery for the run, so a plugin always has the newest of each collection it got any of.
    A collection cut gets `cut`, a sentence: "Only 412 of 3000 documents of board/items were
    delivered (newest first): this run's site data is limited to 8 MiB. The rest were cut." When
    the rest of the request left less room, it ends "limited to 8 MiB, less what the rest of the
    request takes; N bytes were left. The rest were cut." The card gets one line for the whole run:
    "Site data for this run was cut: board/items 412 of 3000. This run's site data is limited to
    8 MiB."
  - **A missing site** - one the team does not have, which is how another team's site reads - gets
    `missing: "No such site."`, `documents: []` and `total: 0`, and the card one line naming every
    missing read.
  - What a run reads is never copied into its output, its result, its progress or any file. What it
    prints is redacted as all plugin output is (see [Configuration and secrets](#configuration-and-secrets)).

**stdout**: JSON Lines, one record per line.

| Record | Effect |
|---|---|
| `{"t":"progress","status":"…"}` | A progress row on the card's feed. It resets the idle clock. |
| `{"t":"blocked","reason":"…"}` | Marks the member blocked. With `"item": n` (1-based) only that batch item is blocked. |
| `{"t":"needsDecision","question":"…"}` | Marks the member as waiting for a decision. |
| `{"t":"handback","delivered":"…"}` | Hands the work back and wakes the Manager once. |
| `{"t":"result","ok":true,"output":"…"}` | The run's result. Send exactly one, last. `ok:false` with `"error"` is a failure in those words. Add `"quiet":true` to finish without waking anyone; see [Quiet runs](#quiet-runs). |
| `{"t":"publish","type":"…","payload":{…}}` | Publishes one of the events the manifest declares. See [Events](#events). |
| `{"t":"site.put","site":"…","collection":"…","id":"…","doc":…}` | Writes one document to a site of the plugin's OWN team (see [sites.md](sites.md)). The document is redacted like every other record; a limit, a name, another team or a missing site drops the record with one progress warning per kind per run. A page's Delete or Archive is best kept in site data the plugin reads (for example a `status` on the document, with the collection in [`reads`](#reading-site-data-reads)), rather than in a second store of the plugin's own, so a run never brings back what a person removed. |
| `{"t":"site.delete","site":"…","collection":"…","id":"…"}` | Deletes one document from a site of the plugin's own team. |
| `{"t":"row.status","field":"…","row":"…","values":{…}}` | Writes the READ-ONLY cells of one row of the member's own [table setting](#table-settings-type-table) - the plugin's status columns. The row is matched by its key ignoring case and must already exist: a plugin never adds a row. An unknown table, row or column is dropped, a column a person writes or a secret column is ignored, and a cell not of its column's type is dropped, each with one progress warning per sentence per run; the rest of the record is written. A string cell is redacted like every other record. |

- **Same path as agents.** The first four records go through the same code as an agent's MCP tools
  (`MemberReports`). A plugin's report and an agent's report are therefore the same row, the same
  mark and the same wake.
- **Other lines.** A line that is not a record is kept as output text and is never treated as an
  error.
- **stderr.** It is appended to the output.

### Quiet runs

`{"t":"result","ok":true,"output":"…","quiet":true}` says the run found nothing
anyone needs to be woken for. It is for a plugin that polls on a schedule: without it, every run's
`completed` row wakes the Manager, a paid model run, to learn that nothing happened.

- **The run is still recorded.** Its `agentContainer.completed` row is written as usual, marked
  `"quiet": true`, and the card, the run history and the feed show it.
- **Nobody is woken by that row.** The pump passes over every subscriber on it, the same way it
  passes over a Manager on a `completed` row marked `handedBack`. That includes a person's own
  trigger on the plugin's `completed` rows: a trigger that must see every run should watch the
  plugin's event instead.
- **Never quiet towards a member that is waiting.** Quiet is for work nobody waits on: a schedule's
  poll, a trigger, a person's tell. When the instruction came from another member (a Manager's
  `tell`), the row is written without the quiet mark and wakes as any completion does, so a Manager
  that asked `list is:unread` always gets its answer, even an empty one, and its workflow ends.
- **What quiet does not suppress:**
  - A failure: `ok:false`, a non-zero exit, a timeout or a Stop. A failure is never quiet, whatever
    the record says, and wakes as always.
  - An event the run published. Its triggers fire as usual, so "publish one event per new item,
    finish quiet" is the polling pattern.
  - A hand-back in the run. It wakes the Manager on its own row.
- **The workflow still ends.** A quiet run in a workflow the plugin owns ends that workflow the same
  way a non-quiet one does.
- **Optional.** A plugin that never sends `quiet` is never quiet.

Pinned by `PluginQuietRunTests` and
`PluginHostProbes.A_quiet_answer_to_the_managers_own_instruction_still_wakes_the_manager`.

### Poll with plugins, spend models only when something happened

A plugin watches for free and publishes an event when it finds something; an agent reacts to that
event. A model member on a short schedule is almost always better as a plugin plus an event trigger.

- A **schedule trigger** on the plugin member polls every few minutes. The plugin publishes one
  event per new item and finishes; a run with nothing new costs no tokens.
- An **event trigger** on the agent member, on the plugin's event type, wakes it only when there is
  something to act on.

The trigger's own settings bound what is left (see [triggers.md](triggers.md)):

- **Wake the Manager when a run ends.** By default a trigger wakes the Manager only when its run
  hands back or fails, so a plugin poll that finds nothing wakes nobody, `quiet` or not. Under **Always**,
  `quiet` is how a plugin says "nothing happened". Under
  **Never**, not even a failure wakes the Manager; it still shows on the card and in the feed.
- **Daily token cap.** The billable tokens a trigger's runs, and the Manager runs they woke, may
  spend in a day; past it, fires are skipped until the next day. A plugin run runs no model, so it
  is measured at 0 tokens and never moves the cap - the cap matters on the agent's event trigger.

The Triggers dialog asks for confirmation before a schedule more frequent than every 5 minutes on an
agent member, naming what a run of it has cost and suggesting this shape. A plugin member has no
minimum.

**Outcome.**

| What happened | Result |
|---|---|
| Exit 0 and an `ok` result | Success. |
| No result | Failure: "The plugin exited N without a result record." |
| `ok:false` | Failure, in the plugin's own words. |
| Exit ≠ 0 | Failure: "The plugin exited N." |
| No progress for `timeoutSeconds` | Failure with class `timeout`. |
| A required setting, key or connection is missing | Class `needs-person`; the plugin is not started. See [Hiring](#hiring). |
| The worker running it is lost | Class `worker-lost`. |
| A person's Stop | Class `interrupted`, marked `stoppedByPerson`. |
| The Host shutting down | Class `interrupted`. |

A plugin run runs no model, so its ledger row is measured at 0 tokens and no spend bound is moved by
it. A plugin run takes a WIP slot like any member's.

## Configuration and secrets

- **Ordinary configuration.** Stored per member in `team_member_config` (schema step `auth-008`).
  It is checked against the manifest at hire and delivered in the request's `config`. It is never
  put in the environment.
- **Each plugin member owns its secrets.** Any number of members may use one plugin, on one team
  or several, with the same account or different ones, and none can read, change or clash with
  another's values. A plugin never needs to tell its members apart, and a package author never names
  a key for a person to type. A person types a member's value for each secret field on that member's
  settings screen (Add member, Member settings, the solution wizard); it is kept for that team,
  member and field in `plugin_member_secrets` (schema step `auth-028`) only as Data Protection
  ciphertext under `<dataRoot>/keys` (purpose `Harness.PluginMemberSecrets.v1`), with who set it and
  when, and the member's next run reads it, with no restart. Removing the member or deleting its team
  removes its values; a member hired again under the same name starts with none, and cloning a team
  carries the member's configuration and key bindings, never a value.
- **Which value a run gets.** At each run a field resolves to the member's own value; else, when the
  member's settings bind the field to a logical key, that key - its value stored from the screen
  (`PUT /api/secrets/{key}`), then the Host's environment, where the operator CLI's
  `yawble secret set NAME` and `yawble up` put it. Clearing a member's value falls back to its
  binding; clearing a stored key falls back to the environment. Key bindings remain for the API, the
  operator CLI and solution packages; a person on the screen never sees one.
- **Moving to member values.** Once, at the first start of a Host with `auth-028`, every member
  bound to a key with a STORED value gets that value as its own (`member.secrets-copied`, counts
  only). The binding and the stored key stay, so the CLI and the environment still work. A key the
  Host holds only in its environment is never copied: the Host never moves an environment value into
  its database.
- **The member routes**, each `HumansOnly` (a machine principal is refused 403):
  - `GET /api/teams/{team}/members/{member}/secrets` answers `{ team, member, secrets: [...] }`, one
    entry per manifest secret field in the manifest's order: `field`, `label`, `masked`, `required`,
    `needed`, `set`, `setBy`, `setAt`, `source` (`member`, `key`, `environment` or null) and `key` (the
    bound key's name or null) - and `value` only for a `"masked": false` field. The settings read
    carries the same list as `secretStates`.
  - `PUT .../secrets/{field}` with `{ "value": "..." }` sets or replaces the member's value, with a
    `member.secret-set` or `member.secret-replaced` tenant row; `DELETE .../secrets/{field}` clears it,
    with a `member.secret-cleared` row, and clearing nothing writes nothing. Each answers the field's
    entry. The row names the team, member, plugin and field, never the value, and lands in the same
    transaction as the write: when it cannot be written nothing changes, and the answer is 500 with
    `error`.
  - A field the manifest does not declare is refused 400, and so is a value that is empty, holds a
    line break or a NUL, or is over 8192 characters - and, for a masked field, one that begins or ends
    with white space or is under 4 characters. The sentence names the field and never repeats the
    value.
  - A person's hire carries values as `secretValues`; the settings save as `secretValues` too (a
    string sets, null clears, a field not named is left), each in one transaction with the settings.
  - No answer, tenant row, log or file carries a masked value.
- **Setting a key from the screen** (the binding's value, instance-wide). `plugin_secrets` (schema
  step `auth-025`) keeps a logical key's value as Data Protection ciphertext (purpose
  `Harness.PluginSecrets.v1`); every member binding the key reads it.
  - **The routes**, each `HumansOnly`:
    - `GET /api/secrets/{key}` answers `{ key, set, refusal, setWith, setBy, setAt, source }`.
      `source` is `stored` (with `setBy`, the person's email, and `setAt`), `environment` (with
      `setBy` and `setAt` null) or null when the key is not set. `setWith` names the screen first
      and the operator CLI second.
    - `PUT /api/secrets/{key}` with `{ "value": "..." }` sets or replaces the stored key, with a
      `secrets.set` or `secrets.replaced` tenant row; `DELETE /api/secrets/{key}` clears it, with a
      `secrets.cleared` row, and clearing a key that is not stored writes nothing. Both answer as GET
      does, in the same transaction as their row, or nothing changes (500 with `error`).
    - A key a plugin may not be pointed at is refused 400 with `error` and `refusal`. So is a value
      that is empty, holds a line break or a NUL, begins or ends with white space, or is under 4 or
      over 8192 characters; the sentence never repeats the value.
  - Keys starting `HARNESS_` and model-provider keys are refused, from both sources, by the same
    name rules (`EnvironmentSecretStore.Refusal`).
  - The value reaches the plugin only on stdin, never in argv or the environment. A plugin reads its
    values from stdin and never from its environment.
  - **Redaction is a net, not a guarantee.** Every text the plugin writes - its result output and
    error, its stderr, the text of each `progress`, `blocked`, `needsDecision` and `handback`
    record, and every string in a published event's payload - has each masked value it was handed replaced with
    `[redacted]` before it is stored or reported, and then the platform's diagnostic redaction runs
    over it (named values such as `password=...`, and credential-shaped strings). The rules, each
    pinned by a test in `PluginMemberRunnerTests`:
    - **Longest value first**, so a value that is a prefix of another cannot leave the other's tail
      behind (`Overlapping_secrets_are_redacted_longest_first`).
    - **JSON-escaped forms too.** A line that is not a record is kept as the plugin wrote it, so a
      value serialised with JSON escapes (the default encoder writes `+` as `\u002B`) is also
      matched in its escaped forms (`A_secret_json_escaped_in_a_raw_line_is_redacted`), including
      `/` written as `\/`, as PHP and some Java encoders do
      (`A_secret_with_an_escaped_solidus_in_a_raw_line_is_redacted`).
    - **In any case.** `DEMO-TOKEN-2026` is caught for the bound value `demo-token-2026`
      (`A_secret_is_redacted_whatever_its_case`).
    - **In every record** (`A_bound_secret_in_a_report_record_is_redacted_before_it_is_reported`)
      and in a published payload (`A_declared_publish_is_appended_and_an_undeclared_one_warned_once`).
    - **A payload's keys and numbers refuse the publish.** A published payload whose property name,
      at any depth, would be changed by redaction, or whose number (or `true`/`false`/`null`) has
      JSON text that would be, is **dropped**, not rewritten, with one `progress` warning row that
      names neither. Renaming a key or turning a number into `[redacted]` would silently change the
      shape a trigger or `{event.*}` token reads (`A_secret_as_a_publish_key_or_number_refuses_the_publish`,
      `A_number_matching_a_secret_refuses_the_publish`). For a secret that is itself a number, a
      payload number is also compared by VALUE, so `2.0261234e7` for `20261234` is refused the same
      way (`A_number_equal_in_value_to_a_secret_refuses_the_publish`).

    A plugin that writes a secret otherwise TRANSFORMED - reversed, base64, cut in pieces - defeats
    all of it, and the result reaches the log. Do not write secrets out at all.
  - A bound value shorter than 4 characters cannot be redacted without mangling the text around
    it, so it is refused: at hire if it is already set, and on every run
    (`A_secret_too_short_to_redact_is_refused_before_launch`).
- **Naming a value that is missing.** A plugin that needs a value it was not handed says which, by
  the name in `secretKeys` (a bound key, or the field), and how to set it: the member's settings
  screen first, the operator CLI second for a bound key - "Set `SHOP_ACCOUNT_SID` in this member's
  settings, or with `yawble secret set SHOP_ACCOUNT_SID` and `yawble up`." It names what it was given,
  never a guess, and never writes a value beside it. (A `required` secret with no value never reaches
  the plugin: the Host refuses the run, naming the field or the key.) Pinned by
  `PluginMemberRunnerTests.A_run_is_told_each_bound_secret_fields_key_name_and_an_unset_keys_name_with_no_value`,
  `PluginMemberRunnerTests.The_request_is_otherwise_byte_identical_to_the_one_before_secret_keys`,
  `PluginMemberRunnerTests.No_secret_value_appears_beside_a_key_name_it_was_not_bound_to` and
  `PluginMemberRunnerTests.A_key_whose_value_is_a_bound_key_name_is_refused_at_binding_and_before_launch`.
- **OAuth is a connection, never a secret.** A plugin that needs a person's account at an OAuth
  service declares a `connections` slot (see [Connections](#connections-oauth-accounts)) and reads
  `connections.<slot>.accessToken` on stdin. It never asks the person for a token, never takes a
  refresh token as a secret, and never ships an "authorize" command of its own.
  - The access token, and every token in a Microsoft slot's `tokens`, is added to the run's
    redaction set with the bound secrets, and every rule above applies to it.
  - It is never put in argv or the environment, and the plugin must not write it to its workspace:
    the next run gets a new one.
  - **Long runs.** A token lives about an hour. A plugin whose run could outlast it (a long sync)
    should do a bounded amount of work, keep its place in its workspace, finish, and let the next
    run pick up with a new token. A run never refreshes a token itself. On a 401 it fails in its
    own words; if the provider has revoked the grant, the Host marks the connection `needs
    reconnect` at the next run and blocks it with a sentence saying to reconnect it from
    Admin, Connections.
  - A refresh the provider refuses blocks the run before it starts, naming the connection.
- **Worked example.** A storage plugin declares `config.account`, `config.container` and
  `secrets.accountKey` (`"label": "Account key"`). Two members of it on one team, for two accounts,
  each have the person type their own account key in their own settings; neither sees or changes the
  other's. An operator who would rather hold the value instance-wide binds
  `{"secrets": {"accountKey": "ACME_STORAGE_KEY"}}` and runs `yawble secret set ACME_STORAGE_KEY` and
  `yawble up`; a member's own value still wins.
- **The store.** Only the resolver knows where values live: `PluginMemberSecretStore` for a member's
  own, `ISecretStore` (which is `PluginSecretStore`) for a key. A plugin reads values on stdin only.

Pinned by `PluginMemberSecretsTests` (every case: two members of one plugin each receive their own
value; a value wins over its key, then the environment; the scan of every response, row and file
under the data root; the rows in one transaction; a machine refused every route; removal, team
deletion, a same-name successor and a clone), `MemberSecretsCopyTests`, `PluginNeedsPersonTests`,
`PluginSecretsTests` and `FreshVolumeTests.The_plugin_member_secrets_step_applies_to_an_empty_and_an_existing_volume`.

### Changing a plugin member's settings after hire

A person changes a plugin member's configuration, its secret values and its connection bindings without hiring a new member:
**Member settings → Settings**, the same form Add member shows, filled with what is stored. It is
generated from the manifest: every setting on one screen in the manifest's order
([Every setting on one screen](#every-setting-on-one-screen)) - one typed input per `config` field
(text, number box, toggle, dropdown for an `enum`, chips for a `list`), its default as its
placeholder, "Reset to default" per field; one value box per secret field; and a read-only view of
the JSON that will be stored, the only place a bound key name shows. It saves only the fields that
differ from their default. A plugin form's fields carry no info icon and no "Default" or "Set by"
line; what a person must see now - an error, Required, "not set", the saved line - stays printed.

Each secret field is one box, labelled by the manifest's `label`. A masked field is a password box:
it starts empty, says when a value is saved and by whom ("Set by <person> on <time>", or "Set in
this Host's environment" for a bound key the CLI set), typing replaces it, and **Clear** removes the
member's own value. A `"masked": false` field is a plain text box showing the saved value. The typed
values are sent with the form's own button (Add member, Save, Install or Update) as `secretValues`.
A refusal names the field and stops the save.

Each connection slot is a picker listing the connections of the providers the slot allows; a
connection lacking a scope the slot asks for is refused with a sentence that offers Reconnect. A
required slot left unbound says, in grey, that the member's runs are blocked until a connection is
chosen there; it is not a refusal, because the picker and Connect are right beside it.

- `GET /api/teams/{team}/members/{member}/plugin-settings` answers `plugin`, `version`, the plugin's
  `description` (the manifest's, whole; null when it has none), the stored
  `config`, `secrets` (bound logical keys, never a value), `secretStates` (each secret field as
  `GET .../secrets` answers it), the manifest's `fields` and `secretFields` (each with `label`, a
  secret with `masked`), `connections` (slot → bound connection id), `connectionFields` (each slot
  as `/api/plugins` describes it) and `marketplaceUrl`.
- `PUT` the same route with `{ "config": {...}, "secretValues": {...}, "secrets": {...}, "connections": {...} }`
  replaces the configuration. `secretValues` sets (a string) or clears (null) the member's own value
  of each field it names. `secrets` and `connections` given replace the bindings (`{}` unbinds every
  key or slot); **omitted, the stored bindings are kept**, so a form that shows no key name and
  knows nothing of connections unbinds nothing. It is validated
  exactly as a hire is - field and secret names, types, `enum` (per item for a list), required fields,
  the key's form, a reserved or provider key, a required secret with no value and no set key, a value
  too short to redact - and refused with a 400 naming the field, with nothing written. Two stored values are
  kept when they are sent back unchanged: a number outside bounds added later (named in
  `outOfRange`), and a value for a field the active version no longer declares (the run never
  receives it).
- The save, its `member.plugin-settings-changed` row in `tenant_events` (the plugin and the NAMES of
  the fields and secret bindings that changed, never a value) and each value with its own
  `member.secret-*` row are one transaction.
- It takes effect on the member's next run: the runner reads the settings at every run.
- Both routes are humans-only. A Manager cannot change a member's settings after hire, as it cannot
  change a member's instructions.

### Signing in to a site: the interactive sign-in page

An `interactive` row of a plugin member's table has a **Sign in** link to
`#/teams/<team>/members/<member>/sign-in/<field>/<row>`. The page shows a Google Chrome the platform
runs for that row, live: its screen as a screencast, the person's mouse, keys and paste sent to it.
It opens at the entry's Site URL. The person signs in (OAuth, two-step codes and CAPTCHAs included)
and presses **Done**: the session (cookies and local storage, with the browser's identity: see
"The saved session" below) is kept as the row's masked secret cell, and the browser closes. **Cancel**, or
closing the tab, closes it and keeps nothing. The plugin receives the session on its next run like any
other secret cell.

- **Which rows.** The link is the table's `sign-in` row action (`rowActionLinks['sign-in']` in
  `web/src/lib/pluginTable.ts`). The page (`PluginSignInRows`) takes a saved row as an entry when it
  holds the action's `when` value (`method` = `interactive`), a bool `active` column is not off, its
  `siteUrl` cell is an `http(s)` URL, and the table has a masked `readOnly` secret column to keep the
  session in: `session`, or the table's only such column. A `hosts` string cell lists the entry's
  other hosts, by name or URL, separated by commas or spaces.
- **Kept** through `PluginMemberSecretStore` (see Table settings above): `CellRefusal(..., byPerson:
  false, maxLength: 1 MiB)`, then `SetCellAsync`, under the table write lock, so a settings save that
  removes the row cannot cross it. A person's save can only clear the cell.

- **Where it may go.** Top-level navigation reaches the entry's own hosts, and other hosts only as a
  sign-in flow that STARTS ON THE ENTRY passes through them: an OAuth-, OpenID-, SAML- or
  WS-Federation-shaped step from an entry host (a link, a form or the entry server's redirect), then
  that provider's server redirects in the same chain. Each such host is recorded as a provider. A link
  or a redirect the person sets off from a provider's page to a host not yet admitted is refused like
  any other host; a provider that hops between hosts of its own that way needs them in the row's
  `hosts`. A refused page is not opened: the browser stays on the page it was on, alive, and the page
  says why. What is saved is pruned to the entry's hosts plus the providers the person actually passed
  through: their cookies, and the local storage of each such origin the person visited, not only the
  one open at Done.
- **Redacted part by part.** Besides the whole value, each WHOLE cookie value and each WHOLE
  local-storage value joins the run's redaction set on its own, with its URL-decoded form, so a plugin
  that prints one cookie, a short code or a PIN does not leak it:
  - **8 characters or more:** redacted wherever it appears, in any case, inside other words too. So is
    each string of 8 characters or more inside a local-storage value that is JSON; shorter strings
    inside JSON are left out.
  - **4-7 characters:** redacted only as a WHOLE TOKEN, in any case: where no letter or digit is right
    before or after it. `flag=code123;`, `mid k9x4z` and `"k9x4z"` are redacted; `trueness` and
    `code1234x` are left as they are. An escape right before it (`\u0022` as JSON writes a quote,
    `\n`, `%3D`) counts as no letter, so an escaped copy is still caught.
  - **1-3 characters** (`1`, `ok`): left out, so they do not mangle the run's output.
  - **Never redacted:** a value (or a string inside JSON) that is one of `true`, `false`, `null`,
    `none`, `undefined`, `yes`, `no`, `on`, `off`, `dark`, `light`, `auto`, `default`, compared
    ignoring case. These are settings, not secrets.

  A plugin that redacts its own output should use the same rule.
- **Limits.** 10 minutes without input closes the browser with nothing saved. One live sign-in per
  person: opening another **replaces** the first, whose page says it was closed. The page's socket
  (`/api/teams/{team}/members/{member}/sign-in/{field}/{row}`) is humans-only, like every route that
  sets a member's secret; an agent's, a Manager's or the Concierge's key is refused before anything
  starts. A row that is there but cannot be signed in to (inactive, not `interactive`, no `http(s)`
  Site URL), a row, sign-in table or member that is not there, or a member that no longer runs a
  plugin this Host has (a stale link), starts no browser: the socket sends
  `{"kind":"ended","cause":"refused","reason":...}` saying which ("This member's settings have no
  row '...'", "... have no sign-in table '...'", "Team '...' has no member '...'", "'...' runs ...,
  not a plugin; only a plugin member has sign-in settings", "'...' runs a plugin this Host can't run
  now, ..." with the catalog's reason), and closes, and the page shows that sentence with "No browser
  was started". A request that is not a WebSocket keeps its status: 404 for a missing row, table or
  member, 400 for a member that runs no plugin, 409 for a plugin that is not installed or was
  refused. The table shows no Sign in link for
  such a row. The page is told why a browser closed
  (replaced, idle, saved) before its socket closes. Above the browser the page shows one note at a
  time (the latest refusal or provider, which the person can dismiss), so the browser stays on a
  phone's screen.
- **Never shown.** The saved state crosses from the worker to control only sealed, is never sent to
  the page (a save answers the hosts kept and the providers among them) and never logged. Page
  addresses are reported by origin and path only.
- **Shown on a canvas.** Each JPEG frame is decoded and drawn on a `<canvas>`, so the page's
  Content-Security-Policy (`img-src 'self'`) needs no `blob:` exception.
- **The browser is a person's Chrome, as the site sees it.** Google Chrome stable (not a test or
  headless build), run headed on a virtual screen of its own on a worker, with its own sandbox on:
  nothing says Headless, `navigator.webdriver` is not true, there is no automation bar, the user
  agent, client hints and `Sec-CH-UA*` headers agree (Google Chrome on Linux x86_64, as the fonts and
  WebGL say), and the screen is 1920x1080 with a real window around a 1280x800 page. The person's
  page sends its `navigator.languages` and time zone when it opens (`?lang=` and `?tz=` on the
  socket), and the browser takes them: `navigator.languages`, `Accept-Language` and the time zone are
  the person's. Which build, and how it stays current: `decisions.md`.
- **One profile per row.** Each row has its own browser profile on the worker's volume
  (`sign-in-profiles`, the worker user's alone, named by hashes), reused on every sign-in for that row,
  so the site sees the same browser come back. One browser uses it at a time: while one person signs
  in to a row, another is refused. Deleting the row (a settings save without it), the member or its
  team removes the profile.

#### The saved session

The row's `session` cell holds this envelope (version 2), and a plugin can rely on it:

```json
{
  "v": 2,
  "storageState": { "cookies": [ ... ], "origins": [ { "origin": "...", "localStorage": [ { "name": "...", "value": "..." } ] } ] },
  "identity": {
    "browser": "chrome",
    "version": "156.0.8078.73",
    "userAgent": "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36",
    "userAgentMetadata": {
      "brands": [ { "brand": "Google Chrome", "version": "156" }, ... ],
      "fullVersionList": [ { "brand": "Google Chrome", "version": "156.0.8078.73" }, ... ],
      "platform": "Linux", "platformVersion": "", "architecture": "x86", "model": "", "mobile": false, "bitness": "64"
    },
    "locale": "de-DE",
    "languages": [ "de-DE", "de", "en-US", "en" ],
    "timezoneId": "Europe/Berlin",
    "screen": { "width": 1920, "height": 1080 },
    "viewport": { "width": 1280, "height": 800 },
    "deviceScaleFactor": 1
  }
}
```

- `storageState` is Playwright's `storageState`, pruned as above: pass it to `browser.newContext({ storageState })`.
- `identity` is the browser the site saw, read from Chrome itself when the person pressed Done. To
  present the same browser, use Chrome (Playwright's `channel: 'chrome'` when `browser` is `chrome`),
  and give the context `userAgent`, `locale`, `timezoneId`, `viewport`, `screen` and
  `deviceScaleFactor`; set the client hints with CDP's `Emulation.setUserAgentOverride`
  (`userAgent`, `acceptLanguage` from `languages`, `userAgentMetadata` as it is here).
- A value without `"v"` is a bare storage state saved before the envelope (version 1, no identity);
  read both. A `"v"` other than 2 is a newer format: refuse it rather than guess. (`SignInSessionValue`
  in `Harness.Contracts` reads and writes it.)

## Skills

A plugin's `skills` files tell a Manager how to use it. They are indexed whenever the plugins are
loaded or rescanned, however the rescan is started (`PluginCatalog.Attach`).

- **Kind `plugin`, locked.** Stored with the plugin id as `source` (schema step `skill-003`). A
  person cannot edit, rename or delete one (403), and a custom skill may not take a name beginning
  `plugin-` (409). Pinned by `PluginSkillsTests.Plugin_skills_are_replaced_per_source_and_locked`
  and `PluginSkillDiscoveryTests.A_hired_plugins_skill_is_found_named_listed_absent_and_locked`.
- **Names are forced.** A file naming the plugin itself (`<id>` or `plugin-<id>`) becomes
  `plugin-<id>`; any other becomes `plugin-<id>-<name>`. A plugin cannot shadow `manager` or any
  other skill. Pinned by `PluginSkillsTests.A_plugin_skills_name_is_forced_into_the_plugins_namespace`.
- **Roles `manager member`** unless the file's `roles` narrows them; `any` means both, and
  `concierge` is never offered. A file that cannot be read refuses the plugin, by name. Pinned by
  `PluginSkillsTests.A_skill_file_narrows_its_roles_but_never_widens_them` and
  `A_skill_file_that_cannot_be_read_refuses_the_plugin_by_name`.
- **Replaced per plugin, in one transaction** (`ReplacePluginSkillsAsync`); an uninstalled plugin's
  skills are removed on the next rescan. Pinned by
  `PluginSkillDiscoveryTests.A_rescan_indexes_a_newly_installed_plugins_skill_and_drops_an_uninstalled_ones`.
- **Never in a prompt's "Available skills"**, so a prompt does not grow with every install. A
  Manager finds a plugin's skill three ways: its **roster** names each plugin member on its team, as
  `Echo (plugin sample-echo: <description> - skill plugin-sample-echo)`; **`skills_search`** finds
  every installed plugin's skill; and the **`hiring`** tool lists every installed plugin with its id,
  one line and skill. Pinned by `PluginSkillDiscoveryTests.A_hired_plugins_skill_is_found_named_listed_absent_and_locked`.

## Events

A plugin publishes with a `publish` record:
`{"t":"publish","type":"done","payload":{"length":4}}`.

- **Only a declared suffix.** The type is always `plugin.<id>.<suffix>`, and `<suffix>` must be one
  the manifest's `events.publishes` declares. The record may name the bare suffix or the full type.
  Anything else - an undeclared suffix, another plugin's type, a platform type such as
  `agentContainer.completed` - is dropped, with one `progress` warning row per type per run. A
  platform type is refused a second time where every publish is appended (`MemberReports.PublishAsync`).
  Pinned by `PluginMemberEndToEndTests.C_An_undeclared_or_forged_publish_is_dropped_with_one_warning_row`.
- **Joins the workflow.** The row's source is the member's qualified id, so its team is the
  member's, and its causation is the message the run is handling. Its payload is a JSON object no
  larger than the member's `ExcerptChars` (4,000 by default), every string redacted; a key or a
  number that holds a secret drops the publish (see Secrets above). Pinned by
  `PluginMemberRunnerTests.A_declared_publish_is_appended_and_an_undeclared_one_warned_once` and
  `A_publish_over_the_members_artifact_limit_is_dropped`.
- **Everything reads the union.** `EventCatalog.For(type)` answers the platform's types and the
  installed plugins' (`IPluginEventRegistry`, the plugin catalog, read on each call so a rescan is
  seen at once). Triggers and their filters, `{event.<field>}` tokens, `GET /api/events` and the
  high-volume rule all go through it, so a plugin event declared `highVolume: true` is refused as a
  language-model member's subscription or trigger by the existing checks. Pinned by
  `PluginEventsTests` and
  `PluginMemberEndToEndTests.C_A_plugins_publish_reaches_a_trigger_on_its_team_in_the_same_workflow`.
- **sample-echo** declares `done` (field `length`) and publishes it on every run that completes.
  `publish:<type>` in an instruction makes it publish that type too, for trying the refusals.

### Monitor hints

An event in `events.publishes` may carry a `monitor` object, so the Monitor draws what the plugin
did to the outside system as a resource beside the plugin member, with its label, for as long as
the Monitor's settings say.

```json
{ "type": "file.written", "summary": "A file was written outside the platform.", "highVolume": true,
  "fields": [ { "name": "name", "kind": "string", "summary": "The file's name." } ],
  "monitor": { "icon": "draft", "action": "created", "label": "Wrote {name}" } }
```

When to declare one:

- Declare a monitor hint for each event that reports something the plugin created, changed or
  deleted outside the platform. Never for a read, a list, a check or a refusal.
- Choose an icon for the resource (a database, a mail, a DNS record, a container). Use `iconBy`
  when one event covers several kinds.
- Publish one event per resource when a command touches several. Declare `highVolume: true` when
  the event exists to be shown rather than to be acted on.
- Keep the label short. Never put a secret, a body, or a message's text in it.

| Key | Status | Rule |
|---|---|---|
| `icon` | required | A Material Symbols Outlined name (`database`, `mail`, `dns`, `deployed_code`), as the `@quasar/extras` list spells it. It is drawn when `iconBy` has no icon for the publish's value. |
| `iconBy` | optional | `{ "field": "<declared string field>", "icons": { "<value>": "<icon>" } }`: the field's value in each publish picks its icon from `icons`, each a Material Symbols name. |
| `action` | one of these two | `created`, `changed` or `deleted`: what every publish of the event did. |
| `actionField` | one of these two | A declared `string` field of the event holding `created`, `changed` or `deleted`. |
| `label` | optional | Text with `{field}` placeholders over declared `string` or `number` fields, at most 120 characters as written. |

An unknown key inside `monitor` is ignored and named `events.publishes.<type>.monitor.<key>`. The
Host refuses a bad icon (in `icon` or in `iconBy.icons`), an `iconBy` that is not such an object, both or neither of `action` and `actionField`, a bad action, a field that is
not a declared string, a placeholder that is not a declared string or number, and a label over the
limit, each sentence naming the event type and the field. A Host that does not read `monitor`
ignores the key, so a manifest that carries one still loads there.

A plugin event declared `highVolume` stays out of the member ledger and is still an ordinary log
row. Any other plugin event stays in the ledger. Pinned by `PluginMonitorHintTests`, `MaterialSymbolNamesTests`,
`PluginHighVolumeLedgerTests` and the CLI manifest tests in `cli/internal/plugin`.

**sample-echo-go** declares `file.written` with a hint (icon `draft`, action `created`, label
`Wrote {name}`) and `highVolume: true`; `publish:file.written` in an instruction publishes it.

## Hiring

A person hires a plugin through `POST /api/teams/{team}/containers` with `agent: "plugin:<id>"`.
**A Manager may hire any installed plugin onto its own team** with the `member` tool:
`plugin` (the id the `hiring` tool lists), `config`, and `secrets`.

- It is validated against the manifest exactly as a person's hire is. It is not bound by the team's
  agent allowlist, which chooses the Agents a hire may run; a plugin runs no model.
- **Secrets are logical keys a person has already set and bound on this team.** A Manager never
  handles a value, and it may bind only a key that a person's hire has already bound on a member
  of its own team, and that is still set on the Host. Anything else, optional or not, is refused
  with a reason that names the key and no value. That includes `PATH`, `HOSTNAME` or any other
  variable the Host happens to carry, a key set on the Host that no person has bound here, and one
  bound only on another team. So the first member that uses a new key is hired by a person. Being
  "set on the Host" is not enough on its own: the secret store falls back to the Host's whole
  environment, and the Host cannot see which names `secret set` wrote, because that list lives in
  the operator's env file outside the container. A person's binding is a deliberate choice for this
  team. A Manager cannot grow the set, and firing the last member that bound a key removes the key
  from it. Pinned by `PluginManagerHiringTests.A_manager_binds_only_keys_a_person_has_bound_on_its_team`.
- **Connections** are assigned to a team by a person (Team settings → Connections): an agent's hire
  may bind any connection assigned to its own team, and no agent route or tool assigns one.
- **Person-only settings.** A `config` field marked `"setBy": "person"` may be left at its default
  by an agent's hire and nothing else (see [the house rule](#plugins-that-act-outward-the-house-rule)).
- Its own team only: the route's `{team}` is checked against the Manager's credential.
- **A member an agent could not finish setting up waits for a person.** An agent's hire (a
  Manager's or a Concierge's) may leave a required secret or a required connection slot unbound.
  The member is made, and the hire's answer carries `waitingForPerson` (each thing to set, in
  words) and `waitingNotice`. A person's own hire is still refused an unbound required key. Until a
  person sets what is missing, each run of the member fails with the class `needs-person` without
  starting the plugin: it is never resumed automatically and never counted as the plugin's fault.
  When a person's save leaves nothing missing (the member's settings, or a key's value from the
  screen), the Manager is told once, in every open workflow whose run of that member last failed
  that way, to send the instruction again. Pinned by `PluginNeedsPersonTests`.

Pinned by `PluginManagerHiringTests`.

## A workflow a person starts by telling a plugin

A workflow is its owner's to declare: the member its root instruction addressed. When a person
tells a plugin member directly, the plugin owns the workflow, and a plugin holds no permit to call
`workflow-complete`. So **the platform declares a workflow whose owner cannot declare** (holds no
`Progress` permit) at the first run end in it, by any member, that succeeded and leaves nothing
working it: nobody running, nothing pending or undelivered, not paused, and no unfinished card. The
`workflow.completed` row is the owner's, with `declaredByPlatform: true`. A failed or stopped run
leaves the workflow open for a person, and so does a member whose last run in it failed
`needs-person`: it waits for a person's save, which tells the Manager in that workflow to send the
instruction again, and only an open workflow is told. An agent owner is unchanged: it declares for
itself (`UndeclarableWorkflows`). Pinned by
`PluginMemberEndToEndTests.E1_A_workflow_a_person_starts_by_telling_a_plugin_ends_completed` and
`PluginNeedsPersonTests.A_run_waiting_for_a_person_fails_needs_person_and_a_persons_save_tells_the_manager_in_that_workflow_once`.

## Plugins that act outward: the house rule

A plugin **acts outward** when it sends, posts, pays, deletes, or does anything else a person cannot
take back in the outside system. Every such plugin follows this rule; a spec for one names each
setting below.

- **The default mode never acts outward.** A mail plugin creates a draft for the person to send;
  any other plugin does a dry run and reports what it would have done. A member hired with no mode
  set is in this mode.
- **Acting for real needs an allowlist.** The real mode is a separate `config` value, and it needs a
  non-empty allowlist setting in the member's `config` (for mail: the recipient addresses and whole
  domains it may send to).
  - A member run in the real mode with an empty allowlist is **refused**, with a sentence naming
    the allowlist setting. A manifest cannot express "required when", so the plugin checks it on
    every run and fails the run with `ok:false` in those words.
  - An action whose target is outside the allowlist is **refused and reported, never sent**. The
    run reports it (`blocked` for that item, or in its result), and nothing reaches the outside
    system.
  - There is no "act for anyone" mode.
- **Only a person widens it.** The mode, the allowlist and the scope limits are `config` a person
  sets when hiring. No command, instruction or incoming content can widen them: a command line
  cannot name a recipient outside the list or switch the mode, and a plugin never reads settings
  out of what it fetched. Incoming content (an email body, a web page) is untrusted, and it reaches
  a Manager's context, so a Manager told by that content to send somewhere else must be unable to.
  **The platform enforces this:** mark those fields `"setBy": "person"` in the manifest. An
  agent's hire (a Manager's `member` tool, or a Concierge) may leave such a field at its default or
  omit it, and is refused any other value with a sentence naming the field; only a person's hire
  sets it. For mail: `sendMode` (default `draft`) and `sendAllowlist` are both `"setBy": "person"`.
- **Scope limits are settings too.** Which mailbox rule a watcher follows, which folders a member
  may move mail to, which bucket it may delete from: each is a `config` value, not a choice made
  per command.

The built-in skill `authoring-plugins` (offered to the Concierge and the Manager) carries this rule
in plain words, with the checklist for writing a plugin's spec as a backlog item.

## Choosing a language

The protocol is JSON on stdin and stdout, so a plugin may be written in any language whose program
runs in the image. Every run is a new process, so start-up is paid on every run.

- **Go is the default** for connectors to REST APIs, clouds, databases, queues and mail: one small
  static binary per processor, no runtime, a start measured in milliseconds. Template:
  `templates/plugin-go`.
- **.NET** when the best or only SDK for the target system is .NET: SharePoint, Dynamics, Exchange
  on-premises, SAP, heavy Office documents. There is no .NET template in this repository; the .NET
  `sample-echo` on the marketplace is the example to start from.
- **Python** when the library the plugin needs exists only in Python.

A plugin's spec says which language and why: find which language has the official SDK for the
target system, and record the choice and the reason. The built-in `authoring-plugins` skill carries
these rules.

### Self-contained plugins

The image guarantees the .NET runtime, Node and Python 3, and nothing else. Everything a plugin needs
beyond that lives in its own folder:

- **Go:** libraries are compiled into the binary; build with `CGO_ENABLED=0` so it links nothing
  from the image.
- **.NET:** `dotnet publish` puts every NuGet package into the plugin's folder beside its build.
- **Python:** packages go in a virtual environment inside the plugin's folder, and the launcher runs
  that environment's interpreter.

Nothing a plugin needs is ever added to the image, and a plugin must not rely on a system package
someone happened to install. The runtimes it does need from the image are named in the manifest's
`requires` (`"dotnet"`, `"node"`, `"python3"`); a .NET plugin declares `["dotnet"]`, and a Go
plugin declares none.

## Where it is pinned

- **Agents are unchanged.** `MemberGoldenTests` pins an agent member's rows, prompt, context,
  environment, persisted row and snapshot on recorded goldens. Re-record them
  only with `HARNESS_UPDATE_GOLDENS=1`, and only for a change that is meant to be visible.
- **The end-to-end tests.** `PluginMemberEndToEndTests` installs and hires sample-echo on
  the real Host. It also asserts that the pump (`Harness.Containers`) has no code naming plugins.
  `PluginGoTemplateEndToEndTests` does the same for sample-echo-go, built for both processors.
- **The pump stays generic, structurally.** `PumpArchitectureTests` reads the pump's compiled IL:
  `MemberRef` is used only by `KindOf` in `MemberRuntime.Snapshot`, and a member's implementation -
  `ContainerDefinition.Agent`, `ContainerSnapshot.Agent` and `Kind`, `MemberRef.KindOf` - is
  followed through the stack, locals, fields and return values and may only be PASSED ON (to the
  invocation the router receives, the snapshot, the definition, a delegate the Host handed in, or
  logging), never transformed or compared. Its self-tests prove a comparison, `ToLowerInvariant().StartsWith`,
  `Split(':')[0] ==` and a comparison on `Snapshot().Kind` are all caught. The whole
  `ContainerDefinition` carries the Agent too, so it is followed the same way, but checked against a
  deny-list. It may not reach `ToString`, `Equals` (the instance method or `object.Equals`),
  `GetHashCode`, `PrintMembers`, `Deconstruct`, the equality operators, string formatting or
  interpolation, `System.Text.Json`, reflection, or logging. Each of those has a self-test in
  `The_scan_catches_a_violation`, and `PrintMembers` has `The_definition_rules_forbid_PrintMembers`.
  A read of another field, such as `Environment.Count`, is a capability, and it is allowed.
- **Plugins run as `agent`.** `PluginLaunchUserTests` launches a plugin through a real user switch
  and checks it runs as `agent` and reads its `0750` directory. Where the process cannot switch
  users it skips, naming why; the release suite runs it as root in the product image.
- **The web UI and the runs route.** `PluginMemberRunsTests` (runs listed with their output, one per
  run), and the web mount specs `add-member-plugin`, `container-card-plugin` and
  `team-kpi-plugin-only`.
- **The pieces.** `PluginMemberRunnerTests` (protocol), `PluginCatalogTests` (manifest v1),
  `PluginMemberRegistryTests` (hire, restore, repoint), `ChildProcessTests` (the shared launcher)
  and `MemberReportsTests` (the shared report path).
- **The install.** `cli/internal/cli/plugin_test.go` pins each `yawble plugin` command and each
  refusal against a scripted engine, including an install from a folder whose launcher has no execute
  bit, copied to the staging folder and installed by the Host's request file
  (`TestPluginInstallCopiesToTheStageAndTheHostInstallsItAndPrintsItsVerdict`), and the Host's
  refusal of a version older than a solution team needs (`TestPluginInstallPrintsTheHostsRefusalAndExitsOne`).
  It also runs the container scripts under a real `sh`: the stage's modes under a setgid parent, an
  installed version refused without `--force`, and the layout `plugin list` reads. `plugin install --from-instance` is pinned against a faked request/report exchange:
  the request it writes (`replace` only with `--force`), no copy or rescan of its own, the Host's
  verdict for a good folder, a refused manifest, an installed version without and with `--force`,
  and a folder outside the data root, and a request the Host never answers being withdrawn; its
  request, report and withdraw scripts run under `sh`. `PluginInstallRouteTests` pins the Host's side,
  a copy staged under `.incoming` included (`A_copy_the_operator_cli_staged_under_incoming_installs_through_the_installer`).
- **The minimum a solution team needs.** `PluginMinimumTests.A_plugin_version_older_than_an_installed_solution_team_needs_is_refused_naming_the_team_and_a_newer_one_is_not`
  (a new install, a Replace and a kept version through the route and the request file, each refused
  with nothing changed; a newer version accepted; accepted again once the package is uninstalled) and
  `PluginMinimumTests.Without_plugin_minimums_the_team_needs_the_shipped_version_and_an_update_rewrites_what_it_needs`.
  `cli/internal/plugin` pins the manifest rules against the Go template and the test plugins under
  `tests/Fixtures/Packages`, the `connections` slot rules included.
- **A site's files folder.** `PluginSiteFilesTests` pins `siteFiles`: own team only, every site, with or without `reads`,
  absolute and existing, `[]` without sites, paths only; `SiteFilesEndToEndTests` pins a file a plugin
  writes there downloading from the page by its stored path, and nothing secret in anything served.
- **Reading site data.** `PluginSiteReadsTests` pins `reads` and its refusals, the `sites` block,
  newest first, the cut and its sentences, own team only, the tenant log and redaction;
  `WorkerFrameTests` pins that a cut run still fits the worker frame, including when the rest of the
  request is large; `PluginSiteReadsEndToEndTests` pins a page's write, archive and delete reaching
  the next run on the real Host; `SolutionCheckTests` and `SolutionInstallTests` pin the package
  check.
- **Connections, the web and the CLI.** The mount specs `connections-dialog`, `connection-picker`
  (the binding picker at hire and in Member settings) and `plugins-dialog` (each slot on the Plugins
  screen). `cli/internal/cli/connect_test.go` runs `yawble connect` against a scripted Host and a real
  loopback listener: the start request, the browser's round trip, a redirect with another state
  ignored, the provider's refusal, reconnect by name, the code never on a command line, `list`,
  `remove` refused naming the members, and a request no Host answers withdrawn.
  `PluginRescanRequestTests` shows a plugin installed after start is listed with no restart, and pins
  the report's refusals, its hired members and its `0600` mode. `PrepareVolumeTests` pins
  `/data/plugins` as `harness:agent 0750`, created when missing.
- **Install from inside the instance, settings after hire.** `PluginInstallRouteTests` installs a built
  folder from inside the data root through the route (modes, `active`, the tenant row) and through the
  `.install` request file, and pins each refusal: outside the data root, a symlink out of it, a symlink
  inside leaving the folder, an existing version without replace, and a manifest the catalog refuses;
  a Manager is refused. It pins the settings route: validated like a hire, never a secret value, the
  tenant row, and a Manager refused. `PluginAdvancedSettingsTests` pins `advanced` and a stored
  value for a field a newer version dropped. `PluginManifestListAndRequiresTests` pins list settings and
  `requires`; `PluginMemberRunnerTests.A_list_setting_is_delivered_in_the_requests_config_and_defaults_to_empty`
  pins a list reaching the plugin.
