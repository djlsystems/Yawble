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
- It is not a container. It runs as a child process of the Host, as the same non-root `agent` user
  agents use. There is no Docker or Podman sibling container, and root is never needed.
- It is not registered in code. A plugin is a directory with a manifest, read at start and on
  rescan. Nothing is compiled in.

Status: this is the local proof of concept (manifest v1, protocol `harness.member/1`). Plugin
skills, event publishing, UI kind-awareness and manager-driven plugin hiring are reserved in the
formats below but not implemented yet. The marketplace, downloading, signing and updating are out of
scope.

## Install a plugin

Plugins live under `<dataRoot>/plugins`. In the container image the data root is `/data`.

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
- **Ownership.** Install plugins owned by the Host user and readable and executable by the `agent`
  group, and never writable by `agent`. A member must not be able to replace a plugin's files.

The Host prints one line per installed plugin at start. It also prints one line per refused
directory, naming the field that is wrong. `GET /api/plugins` lists the same thing. After you
install or upgrade a plugin, a person runs `POST /api/plugins/rescan` or restarts the Host. A member
already pointing at the plugin runs the new version on its next wake.

### Example: sample-echo

`samples/plugins/sample-echo` is the proof-of-concept plugin. It is deterministic: it upper-cases or
reverses each instruction's text. It is a .NET console app with no dependencies, and its launcher
runs it with `dotnet`, which the image guarantees.

Run the build block **from the repository root**. The sample's project takes its target framework
from the repository's build properties, so a copy of `samples/` built anywhere else fails with
`NETSDK1013`.

```sh
# From the repository root: build and lay out version 0.1.0.
V=out/sample-echo/0.1.0
dotnet publish samples/plugins/sample-echo/src -c Release -o "$V/lib"
cp samples/plugins/sample-echo/plugin.json samples/plugins/sample-echo/sample-echo "$V/"
cp -r samples/plugins/sample-echo/skills "$V/"
echo 0.1.0 > out/sample-echo/active

# Put it on the volume, owned by the Host user and readable (not writable) by `agent`.
# NOT YET VERIFIED: these two lines have not been run against a real container.
podman cp out/sample-echo yawble:/data/plugins/
podman exec -u 0 yawble sh -c 'chown -R harness:agent /data/plugins && chmod -R u=rwX,g=rX,o= /data/plugins'
```

The two `podman` lines are the intended install onto a running container, but nobody has run them
yet. The layout they produce, `/data/plugins/sample-echo/0.1.0/...` owned `harness:agent` with mode
`u=rwX,g=rX,o=`, is what the Host expects, whatever way it gets there.

Then register it without a restart: a person calls `POST /api/plugins/rescan`, or the Host is
restarted.

### Hire it into a team

Hire it through the ordinary member route, as a person, naming `plugin:<id>` as the member's Agent:

```http
POST /api/teams/{team}/containers
Content-Type: application/json

{
  "name": "Echo",
  "agent": "plugin:sample-echo",
  "config":  { "mode": "reverse" },
  "secrets": { "token": "SAMPLE_ECHO_TOKEN" }
}
```

- **`config`** is checked against the manifest. Refused at hire, with the field named: an unknown
  field, a value of the wrong type or outside its `enum`, or a missing `required` field with no
  default.
- **`secrets`** binds each secret the manifest names to a **logical key**, never to a value (see
  [Configuration and secrets](#configuration-and-secrets)). A `required` secret whose key is not set
  is refused at hire.
- **What you get.** The member appears on the team's board with `kind: "plugin"`. Work it is told
  through `tell`, the board, a trigger or a schedule reaches it through the same queue any member
  has. Its result is its `agentContainer.completed` row's `output`, and that row wakes the Manager
  like any member's.
- **What is refused.**
  - `config` and `secrets` on an Agent member.
  - A plugin that is not installed.
  - Repointing a member between an Agent and a plugin. Hire a new member instead: an agent member's
    credential and environment must not survive into a plugin member.

### In the web UI

- **Add member.** The dialog offers each installed plugin (from `GET /api/plugins`) after the Agent
  presets. Choosing one shows its manifest's `config` fields as inputs, prefilled with their
  defaults, and each of its `secrets` by name: what a person types there is the logical key, never a
  value. The hire goes through the route above. An empty optional field or an unnamed optional
  secret is left out of the request.
- **Member card.** A `kind: plugin` card shows no live view, no tokens and no spend. It names the
  plugin instead of an Agent and words its failures without provider or spend terms. Its history
  button lists its earlier runs, and each run opens to what it reported.
- **Earlier runs.** `GET /api/teams/{team}/members/{member}/runs` lists every finished run of a plugin
  member, one per run (not one per batched message), newest first. Each run carries `output`: its
  result output, or a failure's launch error. The plugin's own failure sentence is on the card's
  failed mark, not on the run's row. There is no transcript, so `runs/{seq}/transcript` answers 404.
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
  "secrets": { "token": { "description": "A demo credential.", "required": false } },
  "events":  { "publishes": [ { "type": "done", "summary": "Text was transformed." } ] },
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
| `config` | optional | Name → `{type: string, number or bool; enum; default; required; description}`. Flat in v1. |
| `secrets` | optional | Name → `{description, required}`. A `value` key is refused. |
| `events.publishes` | optional, declared only | `type` is a suffix. The full type is `plugin.<id>.<type>`. Publishing is reserved. |
| `skills` | optional, declared only | Files inside the plugin, in the same front-matter format as a skill. Loading them is reserved. |
| `platforms` | reserved, validated | `{"linux-x64": "bin/x64/p", "linux-arm64": "bin/arm64/p"}`. When present it takes precedence over `executable.path` for this machine. |
| `actions`, `consumes`, `health`, `signature`, `publisher`, `minHostVersion`, `permits` | reserved | Kept, and not acted on. |

Any other key is ignored and named on the start-up line. This lets a manifest written for a later
Host still load.

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
  "workingDirectory": "/data/teams/Mixed/workspaces/Echo",
  "worktrees": []
}
```

- **`work`** is the batch, oldest first, all from one workflow. Up to 16 messages can arrive in one
  run.
- **`config`** is the manifest's defaults with this member's own values on top.
- **`secrets`** holds only the secrets this member binds, resolved at the moment the run starts.

**stdout**: JSON Lines, one record per line.

| Record | Effect |
|---|---|
| `{"t":"progress","status":"…"}` | A progress row on the card's feed. It resets the idle clock. |
| `{"t":"blocked","reason":"…"}` | Marks the member blocked. With `"item": n` (1-based) only that batch item is blocked. |
| `{"t":"needsDecision","question":"…"}` | Marks the member as waiting for a decision. |
| `{"t":"handback","delivered":"…"}` | Hands the work back and wakes the Manager once. |
| `{"t":"result","ok":true,"output":"…"}` | The run's result. Send exactly one, last. `ok:false` with `"error"` is a failure in those words. |
| `{"t":"publish","type":"…","payload":{…}}` | Reserved. It is understood and not delivered, and a note says so. |

- **Same path as agents.** The first four records go through the same code as an agent's MCP tools
  (`MemberReports`). A plugin's report and an agent's report are therefore the same row, the same
  mark and the same wake.
- **Other lines.** A line that is not a record is kept as output text and is never treated as an
  error.
- **stderr.** It is appended to the output.

**Outcome.**

| What happened | Result |
|---|---|
| Exit 0 and an `ok` result | Success. |
| No result | Failure: "The plugin exited N without a result record." |
| `ok:false` | Failure, in the plugin's own words. |
| Exit ≠ 0 | Failure: "The plugin exited N." |
| No progress for `timeoutSeconds` | Failure with class `timeout`. |
| A person's Stop | Class `interrupted`, marked `stoppedByPerson`. |
| The Host shutting down | Class `interrupted`. |

Plugin runs report no token usage, so the spend bound never counts them. A plugin run takes a WIP
slot like any member's.

## Configuration and secrets

- **Ordinary configuration.** Stored per member in `team_member_config` (schema step `auth-008`).
  It is checked against the manifest at hire and delivered in the request's `config`. It is never
  put in the environment.
- **Secrets are logical keys.** A member stores `{"token": "ACME_STORAGE_KEY"}`, the key only. The
  Host never writes the value to the database, the manifest or the plugin directory. Cloning a team
  carries the member's configuration and its keys, never a value.
- **Setting a secret.** The value is resolved at each run from the Host's own environment, which is
  where `yawble secret set NAME` values already arrive: set it, then restart the Host.
  - Keys starting `HARNESS_` and model-provider keys are refused.
  - The value reaches the plugin only on stdin, never in argv or the environment.
  - **Redaction is a net, not a guarantee.** Every text the plugin writes - its result output and
    error, its stderr, and the text of each `progress`, `blocked`, `needsDecision` and `handback`
    record - has each bound value replaced with `[redacted]` before it is stored or reported, and
    then the platform's diagnostic redaction runs over it (named values such as `password=...`, and
    credential-shaped strings). A plugin that writes a secret TRANSFORMED - reversed, encoded, cut
    in pieces - defeats both, and the result reaches the log. Do not write secrets out at all.
  - A bound value shorter than 4 characters cannot be redacted without mangling the text around
    it, so it is refused: at hire if it is already set, and on every run.
- **Worked example.** An Azure Storage plugin would declare `config.account`, `config.container`
  and `secrets.accountKey`. Its member would store
  `{"config": {"account": "acme", "container": "invoices"}, "secrets": {"accountKey": "ACME_STORAGE_KEY"}}`.
  The operator runs `yawble secret set ACME_STORAGE_KEY`.
- **Swapping the store.** Only the resolver (`ISecretStore`) knows where values live. An encrypted
  store can replace it later, and no plugin or binding changes.

## Where it is pinned

- **Agents are unchanged.** `MemberGoldenTests` pins an agent member's rows, prompt, context,
  environment, persisted row and snapshot on goldens recorded before the refactor. Re-record them
  only with `HARNESS_UPDATE_GOLDENS=1`, and only for a change that is meant to be visible.
- **The end-to-end proof of concept.** `PluginMemberEndToEndTests` installs and hires sample-echo on
  the real Host. It also asserts that the pump (`Harness.Containers`) has no code naming plugins.
- **The pump stays generic, structurally.** `PumpArchitectureTests` reads the pump's compiled IL:
  `MemberRef` is used only by `KindOf` in `MemberRuntime.Snapshot`, and a member's `Agent` is never
  compared there.
- **The web UI and the runs route.** `PluginMemberRunsTests` (runs listed with their output, one per
  run), and the web mount specs `add-member-plugin`, `container-card-plugin` and
  `team-kpi-plugin-only`.
- **The pieces.** `PluginMemberRunnerTests` (protocol), `PluginCatalogTests` (manifest v1),
  `PluginMemberRegistryTests` (hire, restore, repoint), `ChildProcessTests` (the shared launcher)
  and `MemberReportsTests` (the shared report path).
