# Connections and integrations

An **integration** is a tool on your computer that you offer to your teams' agents. Agents run in a sandbox with
no sign-ins, and their only network path is the run's gateway, which reaches only the hosts the policy allows (see
[Network requests](#network-requests-are-not-integrations)). Integrations let them use your services anyway, through
the app, under rules you set. The app runs each call on your computer, with your sign-in, and the agent gets only
the result.

A **Connection** is a sign-in (an API key, a token, an OAuth sign-in or a website login) that integrations and
host tasks share. A host task can take an environment variable from a Connection (see
[Environment from a Connection](#environment-from-a-connection)). Connections are kept on this computer; agents
never see them.

Everything here is in **Settings > Integrations**, which has four tabs: **CLIs**, **APIs**, **MCP servers** and
**Connections**. Host tasks are listed below the tabs.

## Kinds of integration

| Kind | What it is | How it runs | What agents get |
|---|---|---|---|
| CLI | A command-line program you installed, such as `gh`, `git` or `az` | On this computer, in your checkout of the team's repository, with your own sign-in | A tool named after the CLI, plus `cli_help` and `cli_commands` |
| MCP server, local | A server program you run, defined by a command, arguments and environment variables | Started on this computer | One tool per server tool, named `server__tool` |
| MCP server, remote | A server at an `https` address | Called from this computer | One tool per server tool, named `server__tool` |
| API | A web service described by an OpenAPI spec | Called from this computer, with a Connection's secret | One tool per API, named `api_<name>` |
| Website | A site agents use in a browser window of this app | In the app's window on this computer | One tool per site, named `web_<name>` |

### CLIs

- The app knows `gh` (GitHub CLI), `git` and `az` (Azure CLI) well. Each comes with default rules: read-only
  commands allowed, changes asking first, a few destructive commands off.
- Any other CLI can be added by name with **+ Add CLI**. It starts with help and version allowed and everything else
  asking first.
- System and destructive programs can never be added: `shutdown`, `format`, `diskpart`, `reg`, `del`, `rm`,
  `taskkill`, `cmd`, `powershell`, `pwsh`, `msiexec`, `schtasks`, `icacls` and similar.
- The app finds the program, its version, and whether it is signed in (using the CLI's own status command).
- **Login via terminal** opens a terminal window on this computer where you run the CLI's own sign-in (Windows).
  Agents never run a sign-in.
- Calls never go through a shell. Arguments are passed as a list, stdin is closed, there is no prompt, a call is
  stopped after 2 minutes, and output is cut at 64 KB.
- `git` as an integration only reads your checkout (log, status, diff, show, blame and similar). Every command that
  changes it is off. Agents have git in their own clones for their own work.
- With `gh` enabled, agents also get `pr_open` (open a pull request from the team branch; asks first by default) and
  `pr_status` (the team branch's pull requests).

### MCP servers

- **+ Add MCP server** offers **On this computer** (a command) or **Remote (URL)**. You can fill the form or
  **Paste config** from a server's install instructions.
- **Import from this computer** reads servers already set up for Claude Code, Claude Desktop, Cursor and VS Code.
  Their secrets go into a Connection.
- A local server's environment variables can be marked secret. A secret value is kept in a Connection, not with
  the server.
- A remote server signs in with **No sign-in**, **A token** (sent as `Authorization: Bearer`), an API key in a named
  header, or **The server signs me in** (MCP OAuth).
- **Check** starts the server and lists its tools. **Discover** lists and classifies them for the checklist.

### APIs

- **+ Add API** takes the service's address and, optionally, its documentation address.
- Discover finds the OpenAPI spec: the documentation address itself, spec links on that page, then the usual
  places on the service address. It sends no sign-in while looking.
- Each operation becomes an action, named by its method and route (for example `GET /pets/{petId}`).
- The sign-in comes from the spec: a token, an API key in a header or in the address, a user name and password, or
  OAuth.
- Calls go only to the service's own address. The sign-in headers, cookies and framing are the app's; an agent
  cannot set them.

### Websites

- **+ Add website** takes the site's address and a website login, or **None (public site)**.
- Agents get six actions: `open`, `read` and `screenshot` (allowed by default), and `click`, `fill` and `submit`
  (off by default).
- Agents stay on the site. Pages outside it are never opened.

### Watches

A watch runs one of a team's allowed, read-only integration commands every few minutes (for example
`gh pr list --json number,title,url`). Each new item in its JSON output becomes an event for the team. No model
runs for a watch. Add one with **+ New watch** in the team's integration settings.

### Host tasks

A host task is a named program you define for a team, such as a build or a deploy script. It runs on this
computer, on the base branch of your checkout, and only when that checkout is clean. An agent can only name the
task and give a reason. Each request asks you first: **Run once**, or **Always for this team**, which stores a
standing approval pinned to the files shown. When those files change, the task asks again and shows what changed.
The result reaches the agent when the run ends.

#### Environment from a Connection

A task's environment holds only a few basic variables and the variable names you list to pass on from the app's
own environment. A **binding** adds one more variable whose value comes from a Connection: a variable name, a
Connection, and one of that Connection's secret fields. A release script that needs `GH_TOKEN`, for example, takes
it from a Connection that holds a GitHub token.

- **How to add one.** In the task's form, under **Environment from a Connection**, press
  **+ Variable from a Connection**, type the variable name, pick the Connection, then pick the field. A Connection
  with one secret field fills the field in. **Remove** takes a row out. The name cannot be one the task already
  passes on from your environment, nor one the app sets itself (`PATH`, `HOME`, `TERM`, `CI`, `NO_COLOR` and the
  other basic variables).
- **What is shown.** The task and its approval request show a binding as
  `GH_TOKEN ← Connection 'GitHub release' (token)`: the variable, the Connection's name and the field, never the value.
- **What is pinned.** The binding is part of the task's definition. Adding, removing or re-pointing a binding asks
  you again, like any other change to the task. The secret value is not pinned: replacing an expired token in the
  Connection asks nothing.
- **At run time.** The value is read from the Connection when the task starts and set in the task's environment
  only. It is masked in the task's log, and it never reaches an agent: not in the request, the result, the log, the
  call record or the board. The agent still only names the task and gives a reason.
- **When it cannot be read.** If the Connection is gone, its field is empty, or it needs reconnecting, the task does
  not start. The agent is told which binding is missing, by variable and Connection name, and the run record notes
  it. Fix the Connection and ask again.
- **Over Cloud Link** the bindings show by name only and cannot be added, removed or re-pointed, even when host
  terminals are allowed for the Cloud Link.
- **A bound value must be at least 8 characters**, so the log can mask it; a shorter one stops the run as above. A
  value over several lines (a key file) is masked line by line.

#### Example: a release job that uploads to GitHub

A team publishes a build to each new GitHub release of a repository.

1. On GitHub, make a fine-grained personal access token with **Contents: write** on that one repository only, and
   nothing else.
2. In **Settings > Integrations > Connections**, add an **API key or token** Connection named, for example,
   `GitHub release (Yawble)`, and paste the token into its `token` field.
3. Below the tabs, add a host task for the team, for example `release`, that runs the release script in the
   repository (for example `bash` with the argument `packaging/release.sh`). Under **Environment from a Connection**,
   add `GH_TOKEN` ← `GitHub release (Yawble)` (`token`). Save it.
4. Offer `gh` to the team and give the team a watch on `gh release list --json tagName`. Add an **Event** trigger on
   that watch that tells the team's Manager when a new release appears.
5. When a new release appears, the Manager asks for the task `release` by name, with a reason. The request shows on
   the team's page with the command, the pinned files and the binding. Approve it once with **Always for this team**;
   later releases run without asking, until something pinned changes.

Why this shape is safe:

- The token can reach only that one repository, and only its contents.
- Agents never see the token. They name the task; the app reads the token and starts the script on this computer.
- A change to the script, its command line or the binding asks you again before the next run.
- Removing the Connection stops the job: the task refuses to start without it.

## Who gets an integration

Each integration has **Offer to**: **Every team** or the teams you pick. Only offered teams' agents see its tools.
Team settings also have an **Integrations** tab.

## Rules

Every call is decided before it runs. There are three outcomes:

- **allowed**: it runs at once.
- **ask first**: it waits for you. The agent must give a one-line reason, which you see.
- **off**: it never runs. The agent is told it is refused.

Rules are checked in this order:

1. **Floors.** Commands that reveal or change a sign-in are refused for every CLI, known or not, before any rule is
   read. The check is by word: `token`, `secret`, `password`, `credential`, `apikey`, `oauth`, `cookie`, `kubeconfig`,
   `api key`, `list keys`, `get-access-token` and more. Known CLIs add their own (`gh auth token`, `gh secret`,
   `az keyvault secret show`, `git config` and others). Floors are never loosened.
2. **The team's rules.**
3. **The rules for every team.**
4. **The CLI's defaults.**
5. Nothing matches: **ask first**.

Within a layer the most specific pattern wins. On a tie, off beats ask first, which beats allowed. A CLI pattern is
matched against the arguments joined by spaces, case-insensitively; `*` matches anything. For example, `pr merge*`
matches `pr merge 12 --squash`. API actions and MCP tools are matched by their exact name.

### The checklist

**Discover** builds a checklist of what an integration can do:

- For a CLI it walks the CLI's help. Each command is classified by the floors, the CLI's defaults and its verb.
  Commands it still cannot place can be classified by the team's model, which sees only the command's path and its
  one-line summary.
- For an MCP server it lists the tools. For an API it reads the spec.

You tick what is **allowed** or **ask first** and leave the rest **off**. **tick all read-only** is a shortcut. A
command that reveals a sign-in is locked and cannot be ticked. Discovering again keeps your choices.

## Approving a request

A call that asks first shows on the team's page with the agent's reason and the exact command. You choose:

| Button | Effect |
|---|---|
| **Approve once** | Runs this call only. |
| **Always allow** | Runs it and allows this command from now on. |
| **Keep asking** | Runs it and keeps asking each time. |
| **Decline** | Does not run it. You can add a note for the agent. |
| **Never** | Does not run it and turns this command off. |

**Always allow**, **Keep asking** and **Never** apply to **this team** or **every team**, as you choose. They save an
ordinary rule, which you can edit or remove later. An approval is remembered for the command you saw, never for a
whole family of commands. Pass-through commands (`gh api`, `az rest`, anything named `api`, `rest` or `graphql`) are
remembered only as the exact line. A tool that reveals a sign-in cannot be always allowed.

The agent does not wait. It is woken in the same workflow with the result.

Every call is logged with its decision, the rule that decided it, the team and member, and who started the work.

## Network requests are not integrations

A host an agent reaches itself (a package registry for `dotnet restore` or `npm ci`, say) is network policy, not an
integration: no sign-in is involved and the agent's own program makes the call, through the run's gateway. When the
gateway refuses a host that is not allowed, or the agent asks for one with its `request_host` tool, a network request
shows in **Needs you** with four buttons: **Allow for this agent**, **Allow for this team**, **Allow everywhere** and
**Deny**. An allow changes the network policy (the member's allowed hosts, every team member's, or the global allowed
hosts in **Settings > Guardrails**) and applies from the member's next run. See [Features](features.md#sandbox-and-policy).

## Connections

The **Connections** tab lists every Connection, its status (**untested**, **works** or **needs reconnect**) and what
uses it. **Test** checks a Connection.

A Connection that a host task takes a variable from also says which tasks use it. You can still remove it: the
removal warns you, and those tasks then refuse to start until they are bound to another Connection.

### How secrets are kept

- Secret values are kept only in the operating system's credential store: Windows Credential Manager, the macOS
  Keychain, or the Secret Service on Linux. On Linux without a Secret Service, they go in a file only your account
  can read, and the app warns you once.
- The database holds a Connection's name, kind, non-secret fields and status. It never holds a secret.
- Screens show a secret masked: the first and last four characters of a long value, at most the last two of a
  shorter one, and dots otherwise. A value is never shown in full again after you save it. To change it, paste a
  new one.
- Before any text reaches an agent, a log or a call record, every known secret is removed and replaced with
  `[redacted]`. This covers each value as written and in its JSON-escaped and URL-encoded forms, and the
  `Authorization: Basic` form of a user name and password. CLI output is also scrubbed of anything shaped like a token.
- Saving the same secret twice reuses the existing Connection.
- Backups list which secrets exist, never their values.

### Sign-in kinds

| Kind | Used by | How you sign in |
|---|---|---|
| **API key or token** | APIs, remote MCP servers, local MCP servers' secret variables | Paste the value. It is sent as a bearer token, in a named header, as a query parameter, or as a user name and password, as the API needs. |
| **OAuth sign-in** | APIs | Your own OAuth app (its client ID, and a secret if it has one). The app finds the provider's sign-in and token addresses from its metadata or the API's spec, asks for the scopes the allowed actions need, and opens your browser. Register the redirect address `http://127.0.0.1/callback` (any port) in your app. |
| **MCP OAuth** | Remote MCP servers ("The server signs me in") | The app finds the server's sign-in service, registers itself there, and opens your browser. |
| **Website login** | Websites | You sign in once in a window of this app, then press **Done**. The session stays in that window on this computer. Agents use the site, never your password. |

OAuth sign-ins use PKCE and a random state, accept only `https` addresses (or `http` to this computer), and follow
no redirects. Tokens are refreshed on this computer shortly before they expire. When a refresh is refused, the
Connection is marked **needs reconnect** and you sign in again. Changing an OAuth app's client ID or addresses
drops its old tokens.

## What agents see

- The tools of the integrations offered to their team, with their descriptions.
- `cli_commands`, which lists the discovered commands and what each does for that team: run, ask,
  refused, or never.
- The result of each call: exit code, output (cut at 64 KB) and any error, with secrets removed. A website
  `screenshot` returns an image.
- For an asked call: that it waits for your approval, and later the outcome.

Agents never see a Connection, a secret, a session, a rule they could change, or your sign-in to any CLI.

## Over Cloud Link

A remote viewer through [Cloud Link](cloud-link.md) can see integrations, approve or decline requests, answer network
requests, and run
Discover and save rules for CLIs the app knows or that are already set up on this computer. These are refused
remotely and happen only on the computer itself:

- seeing or changing Connections, and defining or changing a host task's environment from a Connection;
- every sign-in: OAuth, website logins, MCP server sign-ins, and opening a terminal to sign a CLI in;
- adding, removing or discovering an API;
- importing MCP servers, and adding, changing or reading a remote MCP server;
- while host terminals are not allowed for Cloud Link: adding an MCP server, defining a host task, or adding a CLI
  the app does not know.

See also: [Features](features.md).
