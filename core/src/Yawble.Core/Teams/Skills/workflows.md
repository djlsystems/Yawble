---
name: workflows
description: Load before you build, check, test-run, fix or add a workflow (an unattended automation run by a workflow member), or set up the Connections one needs.
roles: [concierge]
---
# Workflows

A workflow is a JSON document (`yawble-workflow/1`): settings, the integrations it needs, variables, and a graph of
steps. It is global (every team can use it) and every save keeps a new version. A team runs it as a **workflow
member**: the core's workflow engine runs it on that member's triggers. It never runs a program and never calls a
model; a `tell` step gives work to the team's agents instead. You build workflows for the person with your workflow tools; the person sees the same workflows in the
**Workflows** place.

## Your tools for workflows

- Read: `workflows_list`, `workflow_get`, `schemas_list`, `schema_get`, `workflow_runs`, `workflow_run`.
- Build: `workflow_check` (checks a document, saves nothing), `workflow_save` (always keeps a new version; one that fails
  its check is a **draft**, which no member runs), `schema_save`.
- Try: `workflow_test_run` with `dry_run: true`.
- Turn on: `workflow_trigger_set` (a trigger for a workflow member), `integration_action_allow` (one API action for a
  team). Both are below (section Turning it on).
- Need the person's yes in the window: `workflow_member_add`, a live (not dry) run, `workflow_trigger_set` that turns a
  trigger on or changes an enabled one's schedule, and every `integration_action_allow`. Ask with the tool; the
  window shows the person the request. Never ask the person to approve in chat instead.
- The tool's answer is the outcome: done, declined or refused (with the reason). Tell the person what it said. Never
  call it again in a loop to get another answer; ask again only when the person asks you to.
- If a tool named here is not in your tool list, say so and tell the person where to do that step in the Workflows
  place. Never route it through `tell`.

## The build loop

1. Ask what starts it (every N minutes, a cron, once, a folder change, an event, a `tell`), what it reads, what it
   does and who is told. Ask only what you cannot decide; propose sensible defaults.
2. `workflows_list`: reuse or extend a workflow that fits. `schemas_list` for the types you can use.
3. `integrations_status`: which Email integrations and APIs exist, which teams they are turned on for, and the
   Connections' status. Set up what is missing first (section Connections).
4. Draft the WHOLE document from the description (the format below), not step by step.
5. `workflow_check`. For each issue (`stepId`, `code`, `message`) fix the document and check again, until it is clean.
6. `workflow_save`. Say the version.
7. `workflow_test_run` with `dry_run: true`, the team, settings, bindings and a sample input. Read every step's
   output and the log; changing calls show as `would ...` and were not made.
8. Show the person the result in plain words: what the run read, what it would have sent and to whom, the log
   lines. Then offer the next step: a live test run, then `workflow_member_add`, then its trigger with
   `workflow_trigger_set`. Each of those waits for their yes in the window.
9. If a step failed with `needs approval: allow <action> for <team>`, offer `integration_action_allow` for exactly
   that action (section Turning it on).

## Turning it on

**The member's trigger.** After `workflow_member_add` succeeds, offer to turn its trigger on with `workflow_trigger_set`
and say in plain words when it would fire ("every 5 minutes, starting now", "weekdays at 08:00 Europe/Paris").

- Fields: `team`, `member` (the workflow member's name), `name` (the trigger's), `schedule`, `enabled` (default
  `true`), `id` (to change an existing trigger), `only_when_idle`.
- `schedule` is a trigger's schedule: `every` (an interval of at least 10 seconds), `cron` (5 or 6 fields, with a
  timezone), `once` (a time), `event` (an integration watch) or `folder` (a folder and file pattern). Pick a kind the
  workflow's `trigger.accepts` lists.
- A member runs one run at a time. An `every`, `cron` or `once` fire while its run is going, or waiting on a tell, is
  skipped (a skipped run in its list). A `folder` fire then is queued instead: the trigger's changes are merged, each
  file once, and ONE run starts with them when the member's run ends, however it ends. A file first queued as added
  stays in `trigger.added` even if it changes again (a photo that is still being written); added then removed leaves
  the fire (no run saw it); removed or changed, then added again, is changed; otherwise the latest kind wins. One
  trigger's queued fire holds at most 10,000 files: new files past that are dropped, with one warning in the log. The queue
  is kept over an app restart; a queued fire is dropped if its trigger is deleted or turned off. If the queued run
  cannot start (only drafts, for example) the fire stays queued and is tried again when the member's next run ends,
  the app starts again or a trigger fires.
- Turning a trigger on, or changing the schedule of one that is on, needs the person's yes in the window. Turning one
  off (`enabled: false`) needs none.

**An API action a step needs.** A dry run or a live run can show a step failing with `needs approval: allow
<action> for <team>`: the team's rules for that API ask first, and an unattended run has nobody to ask.

- Explain it: the step calls `<action>` on that API, and the team does not allow it yet.
- Offer `integration_action_allow { team, integration, action }` for exactly that action, the operation id from the
  message. It adds one team allow rule for that one action. Never ask for a whole API, every write action or a
  pattern.
- It always needs the person's yes in the window. It is refused for an unknown API or action, an API not turned on for
  the team, or a sign-in action; "already allowed" means nothing changed. It works only on this computer, not over
  the Cloud Link.
- After a yes, run the dry run again to show the step passes.

## The document

```json
{ "format": "yawble-workflow/1", "id": "kebab-key", "name": "...", "description": "...",
  "trigger": { "accepts": ["every", "cron", "once"] },
  "settings":  [ { "name": "...", "type": "...", "label": "...", "default": ... } ],
  "requires":  [ { "name": "Support inbox", "kind": "email", "actions": ["list"] } ],
  "variables": [ { "name": "...", "type": "...", "scope": "run" | "saved", "initial": ... } ],
  "outputs":   [ { "name": "...", "type": "..." } ],
  "start": "<first step id>",
  "steps": [ ... ] }
```

- `id` is the library key (letters, digits, `. _ -`). A save of the same `id` is a new version of that workflow.
- `trigger.accepts`: any of `every cron once folder event tell`. It types `input`: `every`/`cron`/`once` give
  `TriggerFired { triggerId, kind, firedAt }`; `folder` gives `FolderChange { root, added, removed, changed }`;
  `event` gives `EventItem { event, item }`; `tell` gives `{ text }`. Mixed kinds: `input` is untyped, or declare
  `"input": "<type>"`.
- `settings`: set per member when it is added (phones, folder names, thresholds). A setting with no `default`
  must be given.
- An `integration` step's `use` is the integration's name exactly as `integrations_status` (and Settings >
  Integrations) shows it: an Email integration's name or an API's name (`"Support inbox"`, `"Twilio"`). A workflow never
  names a Connection.
- `requires` lists each integration the steps use, its `kind` and the actions they call (sorted). The save and the
  check derive it from the steps; one that does not match is replaced and reported as a warning, not an issue.
  - kind `email`, actions `list get search thread markRead move` (there is no send action).
  - kind `api`: the operation's id as the API's checklist shows it (e.g. `CreateMessage`), one the team allows or
    asks for.
- A member uses the integrations the steps name; each must be turned on for its team. A member may replace one with
  another of the same kind turned on for its team (its `bindings`: `{ "Support inbox": "Other inbox" }`).
- Older workflows name integrations by a short alias in `requires` (`mail`, `sms`); each of their members binds the
  alias to a real integration. They keep working; write new workflows with the real names.
- `variables`: `run` variables live for one run; `saved` ones are kept for the member and written back only when a
  run succeeds (a failed poll does not move its place).
- Types: `int float string bool date datetime duration any`, `enum(a, b)`, `list<T>`, or a schema's name. Built-in
  schemas: `TriggerFired`, `FolderChange`, `EventItem`, `EmailAddress`, `EmailMessage`, `EmailList`, `ApiResponse`, `TellResult`.
- `TellResult` is what a `tell` step gives back: `outcome` (`enum(sent, done, failed, blocked, timedOut, dryRun)`),
  `deliverable` (int, 0 in a dry run), `reply` (string, the agent's closing summary) and `data`, which takes the type of
  the tell's `reply` schema (null without one or when the work did not finish).

## Steps

Every step has `id` and `type` and names what comes next. `null` ends the enclosing loop body (or the run at the
top level). Every step must be reachable from `start`; a loop body must end with `null`, never jump back out.

| type | fields | does |
|---|---|---|
| `set` | `var`, `value`, `next` | sets a declared variable (the value is converted to its type) |
| `if` | `when` (a condition), `then`, `else` | branches |
| `switch` | `on`, `cases` `{ "value": "stepId" }`, `default` | branches on a value |
| `forEach` | `items`, `as`, `body`, `then` | runs `body` once per item, the item named by `as`; `then` after the last |
| `while` | `when` (a condition), `body`, `then`, `max` | runs `body` again while `when` is true; `then` when it is false (section Loop) |
| `code` | `inputs`, `code`, `returns`, `into`, `next` | runs a small JavaScript function on named inputs; its result goes `into` a variable (section Code) |
| `integration` | `use`, `action`, `args`, `into`, `next` | calls one action of an integration (by its name); the result goes `into` a variable |
| `file.writeAllText`, `file.appendLine` | `path`, `text`, `next` | writes a text file |
| `file.readAllText`, `file.readLines`, `file.exists` | `path`, `into`, `next` | reads (string, `list<string>`, bool) |
| `file.delete` | `path`, `next` | deletes a file |
| `file.move`, `file.copy` | `path`, `to`, `overwrite`, `into`, `next` | moves or copies a file, any kind (bytes as they are); `into` gets the final path (string) |
| `file.list` | `path`, `pattern`, `recursive`, `into`, `next` | the files in a folder: `list<{ path, name, size, modifiedAt }>` sorted by path |
| `log` | `text`, `next` | a line in the run's log |
| `tell` | `member`, `team`, `text`, `attachments`, `wait`, `timeout`, `reply`, `into`, `next` | gives work to an agent member of a team (section Tell) |
| `end` | `outputs` (optional) | ends the run, setting declared outputs |

- File paths: relative ones are in the member's data folder; an absolute path must be in a folder the team granted
  (writing needs a writable grant). Links and junctions are not followed: a path (in the data folder, a granted folder
  or `docs:/`, at either end of a move or copy, a free name, a tell's attachment) that goes through one fails its step:
  `'docs:/j/x.jpg' goes through a link, which is not followed`.
- `docs:/` paths are in the team's Documents folder (the one the person reads in Documents, and agents see as
  `YAWBLE_TEAM_DOCS`): `docs:/smoke/smokesessions.json` is `smoke\smokesessions.json` there. The team's workflow
  members can read and write it with every `file.*` step, at either end of a move or copy, and `file.list` can list
  `docs:/` or a folder in it. After `docs:/` comes a path inside the folder: no `..`, no drive or absolute path; one that
  leaves the folder fails its step. `workflow_check` reports a written-out path that breaks these rules (`docs:`
  without `/`, `docs:/../x`, `docs:/C:/x`, `docs:/` alone where a file is needed) as `bad-docs-path`. A dry run writes
  the dry run's own copy, and its log names the real file: `would write <Documents>\smoke\smokesessions.json (dry run: ...)`.
- `file.move` and `file.copy` need a writable folder at both ends. A `to` that ends in `/` or names a folder that exists
  means the same name inside it (the folder is made when missing). When the target is there and `overwrite` is not
  `true`, the file takes the first free name: `IMG_0001 (1).jpg`, `IMG_0001 (2).jpg`... A move to another drive copies,
  then deletes the source. A move that changes only the letter case (`a.JPG` to `a.jpg`) renames the file.
- `file.list`: `pattern` is one or more names with `*` and `?`, split by `;` (`*.jpg;*.jpeg;*.heic;*.png`, any case;
  default `*`); `recursive: true` takes the subfolders too. Links, junctions, hidden and system files are not listed,
  and a recursive list does not go into a linked folder. `size` is in bytes, `modifiedAt` a UTC datetime. A folder
  that does not exist fails the step. Declare its variable as `list<any>` (or a schema with those fields).
- A dry run changes no real file: writes, moves and copies go to the dry run's own copies, which later steps see, and
  the log says `would move <from> -> <to>` / `would copy <from> -> <to>` with the real paths.
- `email` results: `list` gives `EmailList { messages: list<EmailMessage>, cursorReset }`; `get`, `markRead`, `move`
  give `EmailMessage`; `search`, `thread` give `list<EmailMessage>`. `EmailMessage` has `id threadId messageId folder
  from to cc subject receivedAt snippet body hasAttachments unread`; `from` is `EmailAddress { name, address }`.
- `api` results: `ApiResponse { status, body }`. A non-2xx answer or a refused call fails the step. Path parameters
  the Connection fills (an account id) are left out of `args`; a form body's fields go in `args` by name.
- Limits: a run timeout (10 minutes by default), a step budget, a cap on a loop's items. One run at a time per
  member.

## Loop

A `while` step repeats its body while a condition holds.

```json
{ "id": "page", "type": "while", "when": { "left": "{vars.cursor}", "op": "exists" },
  "body": "fetch", "then": "done", "max": 50 }
```

- `when`: a condition, as `if` has. It is checked before each pass; when it is false the run goes to `then`.
- `body` and `then` follow the `forEach` rules: the body's last step has `null` as its successor, which ends that pass.
  The body changes what `when` reads (a `set` of a variable), or the loop runs until `max`.
- `max`: the most passes, default 100, at most 10000. A loop that passes `max` fails the run. The run's step budget
  and timeout still apply.

## Code

A `code` step runs a small function in JavaScript (strict mode) for what expressions cannot do: arithmetic over
several fields, reshaping a list, parsing a text.

```json
{ "id": "score", "type": "code",
  "inputs": { "msg": "{m}", "max": "{settings.maxAge}" },
  "code": "const age = (Date.now() - Date.parse(inputs.msg.receivedAt)) / 36e5;\nreturn { stale: age > inputs.max, age };",
  "returns": "Staleness", "into": "staleness", "next": "check" }
```

- `inputs`: named values (expressions). In the code they are `inputs.<name>`, as plain JSON.
- `code`: the body of a function; `return` gives the result. `log(text)` writes a line to the run's log. The code has
  only `inputs` and `log`: no `require` or `import`, no files, network, timers or host objects.
- `returns`: a type (native or a schema's name), default `any`. A returned value that does not match it fails the
  step. `into` takes the value, typed.
- Limits per step: 2 seconds, 64 MB, a statement count and a recursion depth. Hitting one fails the step.
- The check parses the code: a syntax error is a problem on the step, with its line and column. The input types
  come from their expressions; the code itself is not type-checked.
- A dry run runs the code (it changes nothing).

## Tell

A workflow never runs an agent. A `tell` step gives work to an agent member of a team, as the person's tell does.

```json
{ "id": "triage", "type": "tell", "member": "Manager", "team": "Support",
  "text": "A customer wrote about {m.subject}. Read it and decide if it is a refund request.",
  "wait": true, "timeout": "4h", "reply": "TriageResult", "into": "triage", "next": "route" }
```

- `member`: an agent member's name, default `Manager`. Telling the Manager starts a deliverable it plans. Telling another
  agent member gives that member one card directly, in a deliverable the Manager sees but does not plan. Workflow members
  and the person cannot be told; a name that is not an agent member of the team when the step runs fails the step.
- `team`: default the workflow member's own team. Another team is named here. Settings > Workflows **Workflows may tell
  other teams** (on by default) turned off fails such a step with that reason.
- `text`: the instruction, a template. The deliverable says which workflow, member and run it came from.
- `attachments` (optional): files the agent should open, as a list of paths: `["{trigger.added}"]`,
  `["docs:/smoke/report.pdf", "{vars.photo}"]`. Each item is a path or an expression that gives a path, a list of paths
  or `file.list`'s items. Each file must exist and be one the workflow member may read (its data folder, a granted
  folder, `docs:/`), at most 20 files of 100 MB each; otherwise the step fails with the reason and tells nothing.
  The files are copied (the originals stay) into the told team's Documents, `attachments\<time>-<name>`, and the
  instruction ends with the copies' paths, so the agent can open them without access to the source folder. The log
  says `attached <file> as <copy>`. If one file cannot be copied, the copies already made are deleted and the step fails
  with nothing sent; so are they when the tell itself is refused. HEIC/HEIF photos (`.heic`, `.heif`, `.hif`) are copied as they are, not converted to JPEG:
  the log and the instruction say so, and an agent's image tools may not open them. `workflow_check` reports an
  item that is not text, an empty path or a malformed `docs:` path; whether an absolute path is in a granted folder is
  known only when the step runs.
- `wait`: `false` (default) goes on at once. `true` parks the run (state `waiting`) until the deliverable is done, fails
  or is blocked, or `timeout` passes: `30m`, `4h`, `2d`; default `24h`, at most `7d`. A waiting run holds no run slot,
  its run timeout stops, and it resumes after an app restart. It is still the member's run: an `every`, `cron` or
  `once` trigger that fires meanwhile is skipped (a poller that tells should not wait), and a `folder` trigger's fire
  is queued and runs once the wait ends. Cancelling the run leaves the deliverable alone.
- `reply` (only with `wait`): a schema's name. The instruction asks the agent to end its answer with JSON of that
  schema; a reply that does not match fails the step.
- `into`: a variable of type `TellResult { outcome, deliverable, reply, data }`. `outcome` is one of `sent` (`wait`
  false), `done`, `failed`, `blocked`, `timedOut` or `dryRun`; `reply` is the agent's closing summary; `data` is the
  reply's JSON (of the `reply` schema), or null.
- A run tells at most 5 times (a member setting, at most 50); the next tell fails the run.
- A dry run or a test run never sends a tell: the log says `would tell <member> of <team>: ...`, then
  `would attach <file>` for each attachment (nothing is copied), and `into` gets outcome `dryRun`. The Workflows place's Test run takes a made-up reply for each tell that waits.

## Values and expressions

- Any string is a template: `"New email: {m.subject}"`. A string that is exactly one `{...}` keeps the value's type
  (`"{vars.sent + 1}"` is an int). Write `{{` and `}}` for literal braces.
- Roots: `input`, `settings.<name>`, `vars.<name>`, `steps.<id>` (a step's result), and a loop's `as` name.
  Fields with `.name` or `['name']`, list items with `[0]`.
- Operators: `+ - * /`, `== != < <= > >=`, parentheses, unary `-`; literals `'text'`, `"text"`, numbers, `true`,
  `false`, `null`. `+` with text joins text. Dates: datetime `+`/`-` duration, datetime `-` datetime.
- Functions (no others exist): `now()`, `today()`, `count(list)`, `first(list)`, `last(list)`, `max(list)` or
  `max(list, 'field')`, `min(list)` or `min(list, 'field')`, `addHours(t, n)`, `addDays(t, n)`, `hoursSince(t)`,
  `formatDuration(d)`, `lower(s)`, `upper(s)`, `contains(s or list, x)`, `startsWith(s, x)`, `matches(s, regex)`,
  `join(list, sep)`, `coalesce(a, b, ...)` (2 to 8 arguments; the first that is not null).
- A function's result takes no `.field`: `{last(vars.res.messages).id}` is an error. Set a variable first
  (`set last = {last(vars.res.messages)}`), then use `{vars.last.id}`.
- Conditions (`when`): `{ "left": ..., "op": ..., "right": ... }` with op `eq ne gt ge lt le contains startsWith
  matches`, or `exists` / `empty` with no `right`; combine with `{ "all": [...] }`, `{ "any": [...] }`,
  `{ "not": ... }`. A template string or `true`/`false` also works. `gt ge lt le` with a null side is false.

## Patterns

- **Fixed rules before agent steps.** Sort what you can with conditions (a known sender, a subject that
  `matches`), and `tell` an agent only about the rest. Each tell starts paid agent work.
- **A poller tells without waiting.** A workflow on a short `every` trigger tells with `wait: false`; a waiting run
  skips the member's triggers until it resumes.
- **One item at a time for anything an agent writes that is sent.** Loop over the items with `forEach` and send each
  on its own, so one customer's text never ends up in another's message. Recipients come from settings or the
  data, never from what an agent wrote.
- **Dry run before live.** Always `workflow_test_run` with `dry_run: true` first and show the person the `would ...`
  lines. A live run, the member and its trigger come only after they have seen it.
- **Templates for alerts.** Build the alert text in one template from fields (`"New email: {m.subject}
  ({m.from.address})"`). Keep it short; no message bodies in texts.
- **Polling mail:** `list { folder, after: vars.lastId, since: <first-run time>, max }`. If `messages` is not empty,
  save the last message's `id` in a saved `string` variable; when empty, keep the one you have. The id is opaque:
  never build or compare one. Make the first run's `max` large enough to take everything after `since` (at most
  200).

## Connections

Workflows reach a mailbox or an API only through the team's integrations, and those sign in with Connections. You
never see, ask for or repeat a secret: the person pastes it into the app, never into the chat. If they paste one
in the chat, tell them to change it at the provider (it is now in the conversation) and to paste the new one in
the app.

1. `integrations_status`: is there the integration, is it turned on for the team, is its Connection **works**?
2. If not, tell the person exactly where and what to paste:
   - Mail: **Settings > Integrations > Connections**, **+ Add connection**, **Email account**: the address, the
     IMAP and SMTP servers and ports (Gmail fills them in), the password. Gmail needs a Google app password (2-Step
     Verification on). Then an Email integration on that account, turned on for the team.
   - An API (an SMS service, say): **+ Add API** with its address and documentation, then its Connection: for a
     user name and password sign-in, the account id as the user name and the token as the secret.
3. Ask them to press **Test** on the Connection, then read `integrations_status` again: **works** means go on;
   **needs reconnect** says what failed. Say it plainly and what to change.

## Example: texts the subject of every new email

```json
{
  "format": "yawble-workflow/1",
  "id": "inbox-sms-alert",
  "name": "Inbox SMS alert",
  "description": "Texts the subject and sender of every new email to a list of phones.",
  "trigger": { "accepts": ["every", "cron", "once"] },
  "settings": [
    { "name": "recipients", "type": "list<string>", "label": "Phones to text (E.164, e.g. +15551234567)" },
    { "name": "fromNumber", "type": "string", "label": "The SMS number that sends" },
    { "name": "folder", "type": "string", "label": "Mail folder", "default": "INBOX" },
    { "name": "since", "type": "datetime", "label": "First run: mail received after", "default": "2026-11-20T00:00:00Z" }
  ],
  "requires": [
    { "name": "Support inbox", "kind": "email", "actions": ["list"] },
    { "name": "Twilio", "kind": "api", "actions": ["CreateMessage"] }
  ],
  "variables": [
    { "name": "lastId", "type": "string", "scope": "saved" },
    { "name": "res", "type": "EmailList", "scope": "run" },
    { "name": "last", "type": "EmailMessage", "scope": "run" },
    { "name": "sent", "type": "int", "scope": "run", "initial": 0 }
  ],
  "start": "list",
  "steps": [
    { "id": "list", "type": "integration", "use": "Support inbox", "action": "list",
      "args": { "folder": "{settings.folder}", "after": "{vars.lastId}", "since": "{settings.since}", "max": 200 },
      "into": "res", "next": "any" },
    { "id": "any", "type": "if", "when": { "left": "{count(vars.res.messages)}", "op": "gt", "right": 0 },
      "then": "each", "else": "done" },
    { "id": "each", "type": "forEach", "items": "{vars.res.messages}", "as": "m", "body": "phones", "then": "keep" },
    { "id": "phones", "type": "forEach", "items": "{settings.recipients}", "as": "to", "body": "text", "then": null },
    { "id": "text", "type": "integration", "use": "Twilio", "action": "CreateMessage",
      "args": { "To": "{to}", "From": "{settings.fromNumber}", "Body": "New email: {m.subject} ({m.from.address})" },
      "next": "count" },
    { "id": "count", "type": "set", "var": "sent", "value": "{vars.sent + 1}", "next": null },
    { "id": "keep", "type": "set", "var": "last", "value": "{last(vars.res.messages)}", "next": "mark" },
    { "id": "mark", "type": "set", "var": "lastId", "value": "{vars.last.id}", "next": "log" },
    { "id": "log", "type": "file.appendLine", "path": "run.log",
      "text": "{now()} {count(vars.res.messages)} new, {vars.sent} texts", "next": "done" },
    { "id": "done", "type": "end" }
  ]
}
```

Write the names `integrations_status` gives in place of `Support inbox` and `Twilio`. Its member uses those
integrations (turned on for its team) and sets `recipients` and `fromNumber`; a 5-minute `every` trigger runs it.

## Example: moves new photos into a subfolder

```json
{
  "format": "yawble-workflow/1",
  "id": "sort-photos",
  "name": "Sort photos",
  "description": "Moves the photos in a folder into its SmokeSessions subfolder.",
  "trigger": { "accepts": ["every", "once"] },
  "settings": [
    { "name": "folder", "type": "string", "label": "Photo folder (a writable folder grant)" }
  ],
  "variables": [
    { "name": "photos", "type": "list<any>", "scope": "run" },
    { "name": "moved", "type": "int", "scope": "run", "initial": 0 }
  ],
  "start": "list",
  "steps": [
    { "id": "list", "type": "file.list", "path": "{settings.folder}", "pattern": "*.jpg;*.jpeg;*.heic;*.png",
      "into": "photos", "next": "each" },
    { "id": "each", "type": "forEach", "items": "{vars.photos}", "as": "p", "body": "move", "then": "log" },
    { "id": "move", "type": "file.move", "path": "{p.path}", "to": "{settings.folder}/SmokeSessions/", "next": "count" },
    { "id": "count", "type": "set", "var": "moved", "value": "{vars.moved + 1}", "next": null },
    { "id": "log", "type": "log", "text": "{vars.moved} photos moved into SmokeSessions", "next": "done" },
    { "id": "done", "type": "end" }
  ]
}
```

The list is not recursive, so the photos already in `SmokeSessions` are not listed again. A photo whose name is already
there keeps both: the new one becomes `name (1).jpg`. The team grants the folder writable; a dry run lists the folder,
logs each `would move` and moves nothing.

## Example: keeps a list in the team's Documents

Steps that, after the photos are listed, write their names to the team's Documents, where the person and the agents
read them:

```json
    { "id": "names", "type": "code", "inputs": { "photos": "{vars.photos}" },
      "code": "return JSON.stringify(inputs.photos.map(p => ({ name: p.name, size: p.size })), null, 2);",
      "returns": "string", "into": "json", "next": "save" },
    { "id": "save", "type": "file.writeAllText", "path": "docs:/smoke/smokesessions.json", "text": "{vars.json}", "next": "done" }
```

The file is `smoke\smokesessions.json` in Documents (the folder `smoke` is made when missing); no grant is needed.

## What to tell the person

- What you built or changed: the workflow's name, key and version, and whether it checked clean or is a draft (with
  the issues left).
- What the dry run did, step by step in plain words, and what it would have sent.
- What waits for them: Connections to paste and Test, approvals in the window, and the one next step you recommend.
