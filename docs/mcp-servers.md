# MCP servers

Yawble treats MCP servers like connections and plugins: an administrator adds one once, in
**Admin → MCP servers**, and a person gives it to the teams that need it in **Team settings → MCP
servers**. MCP 1 is the list and the assignment; MCP 2 is members using a team's web-address servers through
the `harness` MCP server ([below](#members-using-a-teams-servers-mcp-2)); Yawble starting the ones it runs is MCP 3.

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
an entry is stored and validated only: its health reads "checked when MCP 3 is in place".

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
| `PUT /api/mcp-servers/{id}` | Change; `url` left out keeps the address. Checks a web address again. |
| `POST /api/mcp-servers/{id}/test` | Check a web address again. 400 for an entry Yawble runs. |
| `DELETE /api/mcp-servers/{id}` | Remove from the instance and from every team. |
| `GET`/`POST /api/teams/{team}/mcp-servers` | A team's entries; give one (`{ server }`). |
| `DELETE /api/teams/{team}/mcp-servers/{id}` | Take one off a team. |

Schema step `auth-033` adds `mcp_servers` and `team_mcp_servers` to both stores.

## Members using a team's servers (MCP 2)

A member reaches Yawble through the `harness` MCP server (`/mcp`) with its own member key, so a team's servers
are offered there: every agent CLI gets them at once, and no credential leaves the Host.

- **The list, per run.** At the start of a member's run the Host asks each web-address server given to the
  member's team for its tools (initialize, then `tools/list`, page by page, with the entry's header) and keeps
  that list for the run. `/mcp` stays stateless: its `tools/list` is the platform's own tools, unchanged, plus
  the run's list for the caller. Outside a run, and for a team with no servers, it is the platform's tools alone
  (pinned by `McpContractTests`). A server that cannot be listed in 20 seconds is left out of that run, with a
  warning in the Host log.
- **Names.** `<server>_<tool>`, keeping only `[a-zA-Z0-9_-]`, at most 50 characters so a CLI's
  `mcp__harness__` prefix still fits 64. A longer name is cut to 41 characters and ends in `_` and the first 8
  hex digits of the SHA-256 of the whole `<server>_<tool>`. Descriptions and input schemas (and the rest of the
  tool's listing) pass through as the server gives them.
- **Two tools with one name.** A save or Test whose listed tools would give two tools the same name - two of
  its own, or one of its own and one another web-address entry last listed - fails its check with a sentence
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
  in an owner-only folder of its own that only the agent's group may read, then `mkdir -p` and
  `dd conv=excl`, started through the same `setpriv` prefix as every agent child, make the folders and copy
  the bytes. The file is the member's, and a link anywhere on the way to a place the member may not write (the
  Host's database, keys, logs) is refused by the system: the block is not written, a line gives the system's
  reason and the result is an error. A link to a place the member may write anyway gains it nothing. The
  Host's folder is removed afterwards. Where the Host does not switch users (development, the test suite) it
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
