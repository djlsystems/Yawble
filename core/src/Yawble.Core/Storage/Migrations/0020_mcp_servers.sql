-- 0020 MCP servers as integrations: a local MCP server the person
-- runs on this computer (stdio), its tools offered to the teams they choose, each call under rules. The app keeps no
-- key or token for it: the server reads its own sign-in from the person's environment or files, as anywhere else.
-- Their approvals and calls share integration_asks and integration_calls (integration "mcp:<name>").
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
CREATE TABLE mcp_servers (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    command     TEXT    NOT NULL,
    args        TEXT    NOT NULL DEFAULT '[]',
    tools       TEXT    NULL,
    status      TEXT    NULL,
    enabled_all INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
) STRICT;

CREATE TABLE mcp_server_teams (
    server_id INTEGER NOT NULL REFERENCES mcp_servers(id) ON DELETE CASCADE,
    team_id   INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    PRIMARY KEY (server_id, team_id)
) STRICT;

-- pattern matches the tool's name (e.g. list_* or create_issue); team_id NULL: every team.
CREATE TABLE mcp_rules (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    server_id  INTEGER NOT NULL REFERENCES mcp_servers(id) ON DELETE CASCADE,
    team_id    INTEGER NULL REFERENCES teams(id) ON DELETE CASCADE,
    pattern    TEXT    NOT NULL,
    decision   TEXT    NOT NULL CHECK (decision IN ('allow', 'ask', 'deny')),
    created_at TEXT    NOT NULL
) STRICT;
