# MCP servers

Yawble treats MCP servers like connections and plugins: an administrator adds one once, in
**Admin → MCP servers**, and a person gives it to the teams that need it in **Team settings → MCP
servers**. MCP 1 is the list and the assignment; MCP 2 is members using a team's web-address servers through
the `harness` MCP server ([below](#members-using-a-teams-servers-mcp-2)); Yawble starting the ones it runs, on a worker
as a user of their own, is MCP 3 ([below](#servers-yawble-runs-mcp-3)).

## The list

An entry has a **name**, unique on the instance whatever its case: 1 to 20 letters, digits, `-` or `_`.
It later prefixes the server's tools' names. An entry is one of two kinds:

| Kind | What it holds |
|---|---|
| **Web address** (`url`) | The server's URL. Optionally an API key **connection**, the **field** of it to send (by default its one secret field), a **header name** (by default `Authorization`) and a **value template** holding `{value}` (by default `Bearer {value}`). |
| **Yawble runs it** (`runs`) | The command (`npx` or `uvx`), the package and **one exact version**, fixed arguments, and environment variables each filled from a named field of a connection. |

A "Yawble runs it" entry whose version is missing, is `latest` (`pkg@latest`), or is a range (`^1.2.0`,
`1.x`, `1`, `>=0.6`, `~=0.6`) is refused with a sentence saying a version must be pinned. For `npx` a
version is `major.minor.patch` (with an optional pre-release or build); for `uvx` it is a Python release
(`0.6.2`, `1.0rc1`). A version written into the package (`@scope/pkg@1.2.3`, `pkg==0.6.2`) counts. Such
an entry is checked as a web address is: [below](#servers-yawble-runs-mcp-3).

## Checking a web address

**Saving** a web-address entry checks it, and **Test** checks it again: the Host's worker connects over
streamable HTTP, initializes, says it is initialized and lists the server's tools, page by page, with the
header the template makes on every request. The dialog shows the tools' names and how many there are.
The check runs on a worker, as Test connection does, and a check no worker ran records nothing.

Its health follows Test connection and a run's connection failures:

- **ok** - the tools are listed and kept; an earlier Needs you on the entry is cleared.
- **refused** (HTTP 401 or 403) - the entry reads **Needs you** with the sentence, and so does its
  connection (`needs-reconnect`, with a `connections.needs-reconnect` row naming the server), so every
  member using that connection is held up as for any refused credential. Fix the key in Admin →
  Connections; a Test that passes clears the entry, and Test connection or an edit clears the connection.
- **failed** (not reached, timed out, a redirect, not answering as an MCP server) - the check fails with a
  sentence and marks neither the entry nor its connection.

## Giving entries to teams

**Team settings → MCP servers**, beside Connections, lists what the team was given and adds from the
instance's list. Assignments are stored per team, as team connections are. A team with none works exactly
as before. **Removing an entry** in Admin → MCP servers removes it from every team that had it, in one
transaction, and each of those teams gets one `team.mcp-server-removed` row naming it. A deleted team's
assignments go with it.

## Records and secrets

Every write lands with its tenant row in the same transaction: `mcp.server-added`, `mcp.server-edited`
(naming the settings that changed), `mcp.server-removed`, `mcp.server-checked` (outcome, sentence, how many
tools), `team.mcp-server-assigned` and `team.mcp-server-removed` (with `reason`: removed from the team, or
from the instance). Each reads in words in the Log.

No answer, row or log line holds a connection's value or the URL's query string: the URL is stored whole
only as Data Protection ciphertext (`mcp_servers.url_protected`), and every answer and row shows it
without its query. An edit that leaves the address empty keeps the stored one.

## Routes (people only)

| Route | What it does |
|---|---|
| `GET /api/mcp-servers` | The list, with each entry's health, tools and teams. |
| `POST /api/mcp-servers` | Add; answers `{ server, check }`. 409 for a name taken. |
| `PUT /api/mcp-servers/{id}` | Change; `url` left out keeps the address. Checks the entry again; one Yawble runs is stopped on every worker first. |
| `POST /api/mcp-servers/{id}/test` | Check the entry again: a web address, or one Yawble runs (started on a worker, listed, stopped). |
| `DELETE /api/mcp-servers/{id}` | Remove from the instance and from every team; one Yawble runs is stopped on every worker. |
| `GET`/`POST /api/teams/{team}/mcp-servers` | A team's entries; give one (`{ server }`). |
| `DELETE /api/teams/{team}/mcp-servers/{id}` | Take one off a team. |

Schema step `auth-033` adds `mcp_servers` and `team_mcp_servers` to both stores.

## Members using a team's servers (MCP 2)

A member reaches Yawble through the `harness` MCP server (`/mcp`) with its own member key, so a team's servers
are offered there: every agent CLI gets them at once, and no credential leaves the Host.

- **The list, per run.** At the start of a member's run the Host asks each server given to the
  member's team for its tools (initialize, then `tools/list`, page by page, with the entry's header; one Yawble runs through the member's worker) and keeps
  that list for the run. `/mcp` stays stateless: its `tools/list` is the platform's own tools, unchanged, plus
  the run's list for the caller. Outside a run, and for a team with no servers, it is the platform's tools alone
  (pinned by `McpContractTests`). A server that cannot be listed in 20 seconds is left out of that run, with a
  warning in the Host log.
- **Names.** `<server>_<tool>`, keeping only `[a-zA-Z0-9_-]`, at most 50 characters so a CLI's
  `mcp__harness__` prefix still fits 64. A longer name is cut to 41 characters and ends in `_` and the first 8
  hex digits of the SHA-256 of the whole `<server>_<tool>`. Descriptions and input schemas (and the rest of the
  tool's listing) pass through as the server gives them.
- **Two tools with one name.** A save or Test whose listed tools would give two tools the same name - two of
  its own, or one of its own and one another entry last listed - fails its check with a sentence
  naming both (``Tool `get.page` of fake and tool `getpage` of fake would both be named `fake_getpage` …``) and
  keeps nothing it listed. Should a server start listing such a pair later, both are left out of the run.
  The platform's own tools count too: an entry `skills` whose server offers `get` would be `skills_get`, so the
  save fails (``Tool `get` of skills and the platform's own tool `skills_get` would both be named …``) and a run
  leaves that tool out. A platform tool is never shadowed or listed twice.
- **A call.** The Host finds the server and tool from the name (the run's list first, then what every entry
  last listed), checks the caller's team holds the server at that moment, and forwards `tools/call` with the
  credential added. Not held - another team's tool, or one taken off the team during the run - answers
  "`<server>` is not one of `<team>`'s MCP servers." A name nobody lists is the protocol's unknown-tool error.
  When the server answers an error and no longer lists the tool, the answer is "`<server>` no longer offers
  `<tool>`."
- **Limits.** A call with no answer in 120 seconds fails with "`<server>` did not answer." The run goes on.
  The Host reads at most 64 MB of one answer; a bigger one is dropped whole and the result says so, with the
  20 MB rule. codex is launched with `mcp_servers.harness.tool_timeout_sec=180`, so its own client waits past the
  Host's 120 s.
- **Binary results.** Image, audio and embedded-resource (`blob`) bytes are written into the caller's working
  copy - the card's tree when it has one, else the member's working folder - under
  `.harness/mcp/<server>/<UTC yyyyMMdd-HHmmss>-<n>.<ext>`, the extension from the MIME type (`bin` when
  unknown). An `outputPath` (or `output_path`) argument naming a relative path inside the working copy is used
  instead (`name-2.ext` and on for a second block); a path outside it, absolute, or through a link that leads
  out is refused before the call is sent. **The file is written as the member.** When the Host switches users
  (the image: Host `harness`, members `agent`), it writes nothing in the working copy itself: it puts the bytes
  in a request to a worker (`WriteAsAgent`, as it asks the agent's pass of a removal), and the worker puts them
  in an owner-only folder of its own that only the agent's group may read, then `mkdir -p` and
  `dd conv=excl`, started through the same `setpriv` prefix as every agent child, make the folders and copy
  the bytes. The file is the member's, and a link anywhere on the way to a place the member may not write (the
  Host's database, keys, logs) is refused by the system: the block is not written, a line gives the system's
  reason and the result is an error. A link to a place the member may write anyway gains it nothing. The
  worker's folder is removed afterwards; no worker answering within 2 minutes is a line saying so. The bytes
  cross to a remote worker as base64 in one frame: that one kind of frame may be up to 32 MiB, room for 20 MB,
  while every other worker frame stays within 16 MiB (a worker built before this reads at most 16 MiB, so give
  remote workers the Host's version). A request that cannot be sent at all, or any other failure of the write,
  is a line saying so, never an error out of the call. Where the
  Host does not switch users (development, the test suite) it
  writes itself, as the member's own user, and still never through a link: it opens each folder on the way
  relative to the one before with `O_DIRECTORY|O_NOFOLLOW` and creates the file with
  `O_CREAT|O_EXCL|O_NOFOLLOW`. Either way nothing is
  overwritten: a default name already taken moves on to the next `<n>`, and an `outputPath` already taken is
  refused with a line saying it exists. Binary results are written only when the Host runs on Linux; elsewhere
  the block is refused with a line saying so. In the result, each block becomes a text block holding
  `{"path", "mimeType", "bytes"}`, the path absolute. At most 20 MB is written per call; a block past that is
  dropped and a line says which and how big.
- **Text** passes through, cut at 100,000 characters across the result's text blocks, with a line saying the
  rest was cut. A `structuredContent` over that is dropped, with a line saying so.
- **Redaction.** Every string in the result is redacted of each Connection value the call used (the field's
  value, the header sent, the URL's query and its parts) by the same redaction plugin output goes through:
  every case, JSON-escaped forms, longest first, then the named-value and credential-shape rules. The Host adds
  no run step and no row: the member's transcript shows the call once.

The folder is the platform's own dot-folder, as `.harness-team` is, so the product's name stays out of a path
members rely on. Add `.harness/` to a repository's `.gitignore` if its members should not commit results.

## Servers Yawble runs (MCP 3)

Many useful MCP servers are local programs (`npx ...`, `uvx ...`). A worker runs them as a user of their own, `mcp`,
so no agent can read their keys, environment or files, and they read none of a member's.

- **The user.** The image has `mcp` (uid and gid 10004) beside `harness`, `agent` and `worker`, with no login
  shell, in no group but its own, and nobody in its group. Its home, and npm's and uv's caches in it, are under
  `/data/mcp`, which the entrypoint makes `mcp:mcp` mode 0700 on every start (the folder itself only: what is in it
  is `mcp`'s own writing, and root does not walk it). Node and uv are in the worker image, so `npx` and `uvx` are in
  `/usr/local/bin`.
- **The volume.** For `mcp` to reach its folder, the data root is now `harness:agent` mode **0751**: "other" may pass
  through and nothing more. In exchange no other top-level entry keeps an "other" bit: the entrypoint makes each of
  agent's top-level folders and files `o=` (teams, documents, agent-home, tmp, the tool caches and the rest; a
  file-browser root outside the volume too), so `mcp`, the one user that is "other" to all of them, reaches none of
  them, whatever modes are left deeper down. Host, plugin, repository and browser entries already had no "other" bit.
- **Starting.** On the first list or call for an entry on a worker, that worker starts it: `setsid`, then the same
  `setpriv --reuid=10004 --regid=10004 --init-groups --inh-caps=-all --ambient-caps=-all` switch agent children use
  (so it holds no capability), then `sh -c` as `mcp`, which makes its home and its working folder
  (`/data/mcp/work/<worker>/<entry id>`) with umask 077, enters the folder and execs `npx -y <package>@<version>
  <args>` or `uvx <package>==<version> <args>`. Its environment is the variables the entry names, each filled from
  its connection's field on the Host, plus `PATH=/usr/local/bin:/usr/bin:/bin` and `HOME=/data/mcp/home`, and
  nothing else of the worker's (no key, no `HARNESS_*`; an entry naming `PATH` or `HOME` does not replace them). The
  values cross the worker connection sealed, as a terminal's environment does, and no answer, row or log line holds
  one. The values never reach `setsid` or `setpriv` (which hold the worker's capabilities) under their own names:
  they cross them as numbered `HARNESS_MCP_ENV_<n>` copies, and the shell switched to `mcp` gives each its name and
  drops the copy; no value is on any command line. An entry may not name `LD_*`, `MALLOC_*`, `GCONV_PATH`, `LOCPATH`
  or `NLSPATH`. A package's name must start with a letter or digit (after an optional `@scope/`), so it is never read
  as an `npx` or `uvx` option. **Never put a secret in the arguments:** a server's command line (package, version
  and arguments) is readable in `/proc/<pid>/cmdline` by any user on the worker; give it a variable from a
  connection instead. A worker that should switch to `mcp` and cannot (no CAP_SETUID) starts nothing and says why; where there is no
  `mcp` user (development, the test suite) the server runs as the worker's own user under its temp folder.
- **One process per server per worker,** shared by every caller on that worker. A member's list and calls go to the
  worker its run is placed on; a check goes to any worker. The protocol is MCP over stdio: initialize, the
  initialized notification, then the requests, matched by id; a `ping` from the server is answered, any other request
  from it is refused. **The Host reaches it only through the worker's control connection**: the pipes are the
  worker's, and `agent` cannot open them (`/proc/<pid>/fd`, like `/proc/<pid>/environ`, belongs to `mcp`).
- **Limits.** The first start installs the package and may take a minute: initialize may take up to 5 minutes. The
  120-second call limit counts from the moment the process answered initialize. A run's list at its start still
  waits at most 20 seconds; a first start that takes longer goes on on the worker, and the next run lists it. An
  answer may be up to 32 MiB on the worker connection (room for 20 MB of binary as base64, as a write as the member
  has); a bigger one is dropped whole with a sentence. The limit covers writing the request too: a server that stops
  reading its input is killed when a call's limit ends (that call fails as unanswered, calls queued behind it as
  stopped), and the next call starts it again. A line from the server that cannot be read never ends the process; a
  call it was answering fails with a sentence. Results reach the member exactly as a web address server's
  do (MCP 2): binary written as the member, text cut, every string redacted of the values the entry was given.
- **Stopping.** After 10 minutes without a call the process is stopped (its whole process group: SIGTERM, then
  SIGKILL after two seconds), and the next call starts it. A process that stops by itself is started again by the
  next call; a call it was answering fails with "`<server>` stopped; the next call starts it again." Editing the entry
  stops it on every worker, and so does editing a connection it reads (any change Connections reports, through the
  same hook that holds members up); the next call starts it with the new values. A call carrying values the running
  process was not started with restarts it anyway (each start carries a fingerprint keyed by the Host), so a worker
  that missed the stop still never serves an old value. Removing the entry stops it on every worker. A worker that
  exits leaves its servers' stdin closed, which ends a well-behaved stdio server.
- **The check** (save and **Test**) starts the server on a worker in a process of its own, lists its tools page by
  page and stops it. It reads as a web address's: `ok` with the tools (kept, and their names count in the same-name
  check against every other entry), or `failed` with a sentence (did not start in time, stopped before initialize
  with its exit code and the end of its stderr, redacted). A check no worker ran records nothing. A check's process
  runs beside the shared one, if there is one, in the same working folder and package cache.

## The Concierge using its active team's servers (MCP 4)

The Concierge reaches the same `harness` server with its own key, so it is offered the same central servers as
the team the person has open, without sending work to a team.

- **The list, per session.** When a Concierge session starts, the Host reads the person's current team (the
  "active team" its header shows) and lists that team's web-address servers' tools exactly as at the start of a
  member's run, and keeps that list for the session. With no current team it keeps none: `tools/list` is the
  platform's tools alone.
- **Servers Yawble runs (MCP 3) are a member's only.** Such an entry runs on the worker a member's run is placed
  on, and the Concierge is on no worker, so its list leaves them out and a call to one is refused as a server its
  team does not hold.
- **A call** is checked against the person's current team **at that moment**, not the one the session started
  on. A server that team does not hold answers "`<server>` is not one of `<team>`'s MCP servers. Reload the
  Concierge to get `<team>`'s tools.", naming the team by its label. With no team open it answers
  "`<server>` is not available: no team is open. Open a team and reload the Concierge to get its tools."
- **Switching team never restarts the Concierge.** Its list stays the start's; after a switch the panel's
  header says "MCP tools still `<team>`'s until Reload" (or "no MCP tools until Reload" when it started with no
  team), from `GET /api/concierge/mcp-tools` (`{ started, team, name }`, the caller's own session). Reload ends
  the session and starts a new one, which takes the team current then. The list is dropped when the session
  ends, however it ends. A Host restart forgets it too: a session that outlives one lists no MCP server tools
  until Reload, and its calls are still checked against the current team.
- **Binary results** go to the Concierge's own workspace, under `.harness/mcp/<server>/`, and an `outputPath`
  must be inside that workspace. They are written the same way as a member's: as the agent, through a worker
  where the Host switches users, never through a link and never over a file. Text, limits and redaction are
  as for a member.
- **Its home configuration keeps working as today.** Servers the Concierge's agent CLI has configured in its
  own home are loaded beside `harness` as before. **Remove from that home configuration any server the
  instance also offers** (Admin → MCP servers, given to the team): the home configuration is a file in the
  agent user's home, which every member's agent shares, so a key written there is readable by any of them; the
  instance's entry keeps its key in a Connection that never leaves the Host. With both, the CLI lists the server twice, once under its
  home name and once as `harness`'s `<server>_<tool>`.
