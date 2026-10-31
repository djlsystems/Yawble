# MCP servers

Yawble treats MCP servers like connections and plugins: an administrator adds one once, in
**Admin → MCP servers**, and a person gives it to the teams that need it in **Team settings → MCP
servers**. This page is MCP 1: the list and the assignment. Members using them is MCP 2; Yawble starting
the ones it runs is MCP 3.

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
