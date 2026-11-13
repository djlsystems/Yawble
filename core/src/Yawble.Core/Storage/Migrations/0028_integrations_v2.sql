-- 0028 integrations: where a checklist row came from, the key an ask is remembered under, and the MCP tools' checklist.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- integration_commands.origin: 'discovered' (a walk of the help found it; every row before this step), 'found' (seen in use or in a log, not by a walk),
-- 'builtin' (shipped with the app). A row that is not discovered is never marked gone; a walk that finds it makes it discovered.
-- integration_asks.action: the row path the ask is remembered under (NULL = none, or the exact line).
-- mcp_servers.discover_runs: how many times the tools of the server were merged (the first run marks nothing new).
-- mcp_commands / mcp_choices: the same checklist for the tools of an MCP server; a choice owns the mcp_rules row it generated.

ALTER TABLE integration_commands ADD COLUMN origin TEXT NOT NULL DEFAULT 'discovered' CHECK (origin IN ('discovered', 'found', 'builtin'));
ALTER TABLE integration_asks ADD COLUMN action TEXT NULL;
ALTER TABLE mcp_servers ADD COLUMN discover_runs INTEGER NOT NULL DEFAULT 0;

CREATE TABLE mcp_commands (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    server_id     INTEGER NOT NULL REFERENCES mcp_servers(id) ON DELETE CASCADE,
    path          TEXT    NOT NULL,
    summary       TEXT    NULL,
    class         TEXT    NOT NULL CHECK (class IN ('readonly', 'change', 'destructive', 'credential', 'unknown')),
    class_source  TEXT    NOT NULL CHECK (class_source IN ('floor', 'annotation', 'verb', 'none')),
    class_reason  TEXT    NOT NULL,
    origin        TEXT    NOT NULL DEFAULT 'discovered' CHECK (origin IN ('discovered', 'found')),
    first_seen_at TEXT    NOT NULL,
    last_seen_at  TEXT    NOT NULL,
    gone          INTEGER NOT NULL DEFAULT 0,
    badge         TEXT    NULL CHECK (badge IN ('new', 'changed')),
    UNIQUE (server_id, path)
) STRICT;

CREATE TABLE mcp_choices (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    server_id INTEGER NOT NULL REFERENCES mcp_servers(id) ON DELETE CASCADE,
    team_id   INTEGER NULL REFERENCES teams(id) ON DELETE CASCADE,
    path      TEXT    NOT NULL,
    decision  TEXT    NOT NULL CHECK (decision IN ('allow', 'ask', 'deny')),
    rule_id   INTEGER NULL REFERENCES mcp_rules(id) ON DELETE SET NULL,
    chosen_at TEXT    NOT NULL,
    note      TEXT    NULL
) STRICT;
CREATE UNIQUE INDEX ux_mcp_choices ON mcp_choices(server_id, COALESCE(team_id, -1), path);
