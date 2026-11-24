---
name: scripts
description: Load before you build, check, test-run, fix or add a script (an unattended workflow run by a script member), or set up the Connections one needs.
roles: [concierge]
---
# Scripts

A script is a JSON document (`yawble-script/1`): settings, the integrations it needs, variables, and a graph of
steps. It is global (every team can use it) and every save keeps a new version. A team runs it as a **script
member**: the core's script engine runs it on that member's triggers. It never runs a program and never calls a
model. You build scripts for the person with your script tools; the person sees the same scripts in the
**Scripts** place.

## Your tools for scripts

- Read: `scripts_list`, `script_get`, `schemas_list`, `schema_get`, `script_runs`, `script_run`.
- Build: `script_check` (checks a document, saves nothing), `script_save` (always keeps a new version; one that fails
  its check is a **draft**, which no member runs), `schema_save`.
- Try: `script_test_run` with `dry_run: true`.
- Need the person's approval in the window: `script_member_add`, a live (not dry) run, turning on a trigger for a
  script member, allowing an integration's write action. Ask with the tool; the window shows the person the
  request. Never ask the person to approve in chat instead.
- If a tool named here is not in your tool list, say so and tell the person where to do that step in the Scripts
  place. Never route it through `tell`.

## The build loop

1. Ask what starts it (every N minutes, a cron, once, a folder change, an event, a `tell`), what it reads, what it
   does and who is told. Ask only what you cannot decide; propose sensible defaults.
2. `scripts_list`: reuse or extend a script that fits. `schemas_list` for the types you can use.
3. `integrations_status`: which Email integrations and APIs exist, which teams they are turned on for, and the
   Connections' status. Set up what is missing first (section Connections).
4. Draft the WHOLE document from the description (the format below), not step by step.
5. `script_check`. For each issue (`stepId`, `code`, `message`) fix the document and check again, until it is clean.
6. `script_save`. Say the version.
7. `script_test_run` with `dry_run: true`, the team, settings, bindings and a sample input. Read every step's
   output and the log; changing calls show as `would ...` and were not made.
8. Show the person the result in plain words: what the run read, what it would have sent and to whom, the log
   lines. Then offer the next step: a live test run, then `script_member_add`, then its trigger. Each of those
   waits for their approval in the window.

## The document

```json
{ "format": "yawble-script/1", "id": "kebab-key", "name": "...", "description": "...",
  "trigger": { "accepts": ["every", "cron", "once"] },
  "settings":  [ { "name": "...", "type": "...", "label": "...", "default": ... } ],
  "requires":  [ { "name": "Support inbox", "kind": "email", "actions": ["list"] } ],
  "variables": [ { "name": "...", "type": "...", "scope": "run" | "saved", "initial": ... } ],
  "outputs":   [ { "name": "...", "type": "..." } ],
  "start": "<first step id>",
  "steps": [ ... ] }
```

- `id` is the library key (letters, digits, `. _ -`). A save of the same `id` is a new version of that script.
- `trigger.accepts`: any of `every cron once folder event tell`. It types `input`: `every`/`cron`/`once` give
  `TriggerFired { triggerId, kind, firedAt }`; `folder` gives `FolderChange { root, added, removed, changed }`;
  `event` gives `EventItem { event, item }`; `tell` gives `{ text }`. Mixed kinds: `input` is untyped, or declare
  `"input": "<type>"`.
- `settings`: set per member when it is added (phones, folder names, thresholds). A setting with no `default`
  must be given.
- An `integration` step's `use` is the integration's name exactly as `integrations_status` (and Settings >
  Integrations) shows it: an Email integration's name or an API's name (`"Support inbox"`, `"Twilio"`). A script never
  names a Connection.
- `requires` lists each integration the steps use, its `kind` and the actions they call (sorted). The save and the
  check derive it from the steps; one that does not match is replaced and reported as a warning, not an issue.
  - kind `email`, actions `list get search thread markRead move` (there is no send action).
  - kind `api`: the operation's id as the API's checklist shows it (e.g. `CreateMessage`), one the team allows or
    asks for.
- A member uses the integrations the steps name; each must be turned on for its team. A member may replace one with
  another of the same kind turned on for its team (its `bindings`: `{ "Support inbox": "Other inbox" }`).
- Older scripts name integrations by a short alias in `requires` (`mail`, `sms`); each of their members binds the
  alias to a real integration. They keep working; write new scripts with the real names.
- `variables`: `run` variables live for one run; `saved` ones are kept for the member and written back only when a
  run succeeds (a failed poll does not move its place).
- Types: `int float string bool date datetime duration any`, `enum(a, b)`, `list<T>`, or a schema's name. Built-in
  schemas: `TriggerFired`, `FolderChange`, `EventItem`, `EmailAddress`, `EmailMessage`, `EmailList`, `ApiResponse`.

## Steps

Every step has `id` and `type` and names what comes next. `null` ends the enclosing loop body (or the run at the
top level). Every step must be reachable from `start`; a loop body must end with `null`, never jump back out.

| type | fields | does |
|---|---|---|
| `set` | `var`, `value`, `next` | sets a declared variable (the value is converted to its type) |
| `if` | `when` (a condition), `then`, `else` | branches |
| `switch` | `on`, `cases` `{ "value": "stepId" }`, `default` | branches on a value |
| `forEach` | `items`, `as`, `body`, `then` | runs `body` once per item, the item named by `as`; `then` after the last |
| `integration` | `use`, `action`, `args`, `into`, `next` | calls one action of an integration (by its name); the result goes `into` a variable |
| `file.writeAllText`, `file.appendLine` | `path`, `text`, `next` | writes a text file |
| `file.readAllText`, `file.readLines`, `file.exists` | `path`, `into`, `next` | reads (string, `list<string>`, bool) |
| `file.delete` | `path`, `next` | deletes a file |
| `log` | `text`, `next` | a line in the run's log |
| `end` | `outputs` (optional) | ends the run, setting declared outputs |

- File paths: relative ones are in the member's data folder; an absolute path must be in a folder the team granted
  (writing needs a writable grant).
- `email` results: `list` gives `EmailList { messages: list<EmailMessage>, cursorReset }`; `get`, `markRead`, `move`
  give `EmailMessage`; `search`, `thread` give `list<EmailMessage>`. `EmailMessage` has `id threadId messageId folder
  from to cc subject receivedAt snippet body hasAttachments unread`; `from` is `EmailAddress { name, address }`.
- `api` results: `ApiResponse { status, body }`. A non-2xx answer or a refused call fails the step. Path parameters
  the Connection fills (an account id) are left out of `args`; a form body's fields go in `args` by name.
- Limits: a run timeout (10 minutes by default), a step budget, a cap on a loop's items. One run at a time per
  member.

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
  `matches`), and leave only the rest to judgment. Agent steps come later; until then a script has none.
- **One item at a time for anything an agent writes that is sent.** Loop over the items with `forEach` and send each
  on its own, so one customer's text never ends up in another's message. Recipients come from settings or the
  data, never from what an agent wrote.
- **Dry run before live.** Always `script_test_run` with `dry_run: true` first and show the person the `would ...`
  lines. A live run, the member and its trigger come only after they have seen it.
- **Templates for alerts.** Build the alert text in one template from fields (`"New email: {m.subject}
  ({m.from.address})"`). Keep it short; no message bodies in texts.
- **Polling mail:** `list { folder, after: vars.lastId, since: <first-run time>, max }`. If `messages` is not empty,
  save the last message's `id` in a saved `string` variable; when empty, keep the one you have. The id is opaque:
  never build or compare one. Make the first run's `max` large enough to take everything after `since` (at most
  200).

## Connections

Scripts reach a mailbox or an API only through the team's integrations, and those sign in with Connections. You
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
  "format": "yawble-script/1",
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

## What to tell the person

- What you built or changed: the script's name, key and version, and whether it checked clean or is a draft (with
  the issues left).
- What the dry run did, step by step in plain words, and what it would have sent.
- What waits for them: Connections to paste and Test, approvals in the window, and the one next step you recommend.
