-- 0029 shared Connections (API key / token) and APIs discovered from OpenAPI specs.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- connections: the secret VALUES live in the OS credential store under "connection/{store_key}/{fieldKey}", never here.
-- store_key is a random GUID per connection: ids restart at 1 in every data dir, and the OS store is shared by all of
-- them on one computer, so keying by id would let two instances overwrite (and send) each other's secrets.
-- fields is JSON [{key,label,secret,value?}]; value is present only for non-secret fields.
-- kind: 'secret', 'oauth' or 'website' (0031, 0032); this step writes only 'secret'.
-- apis: a web service the person added by its URL; its operations are api_commands, offered to teams under rules.
-- api_commands is mcp_commands with api_id, plus method, route (path template), tag and params (JSON schema).
-- path is the action id (operationId, or "METHOD /path" when the spec has none).
CREATE TABLE connections (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    store_key     TEXT    NOT NULL UNIQUE,
    kind          TEXT    NOT NULL CHECK (kind IN ('secret', 'oauth', 'website')),
    fields        TEXT    NOT NULL,
    status        TEXT    NOT NULL DEFAULT 'untested' CHECK (status IN ('untested', 'works', 'needs_reconnect')),
    status_reason TEXT    NULL,
    tested_at     TEXT    NULL,
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL
) STRICT;

CREATE TABLE apis (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    base_url      TEXT    NOT NULL,
    docs_url      TEXT    NULL,
    spec_url      TEXT    NULL,
    title         TEXT    NULL,
    connection_id INTEGER NULL REFERENCES connections(id) ON DELETE SET NULL,
    auth          TEXT    NULL,
    status        TEXT    NULL,
    enabled_all   INTEGER NOT NULL DEFAULT 0,
    discover_runs INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL
) STRICT;

CREATE TABLE api_teams (
    api_id  INTEGER NOT NULL REFERENCES apis(id) ON DELETE CASCADE,
    team_id INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    PRIMARY KEY (api_id, team_id)
) STRICT;

CREATE TABLE api_commands (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    api_id        INTEGER NOT NULL REFERENCES apis(id) ON DELETE CASCADE,
    path          TEXT    NOT NULL,
    summary       TEXT    NULL,
    method        TEXT    NOT NULL,
    route         TEXT    NOT NULL,
    tag           TEXT    NULL,
    params        TEXT    NOT NULL DEFAULT '{}',
    class         TEXT    NOT NULL CHECK (class IN ('readonly', 'change', 'destructive', 'credential', 'unknown')),
    class_source  TEXT    NOT NULL CHECK (class_source IN ('floor', 'annotation', 'verb', 'method', 'none')),
    class_reason  TEXT    NOT NULL,
    origin        TEXT    NOT NULL DEFAULT 'discovered' CHECK (origin IN ('discovered', 'found')),
    first_seen_at TEXT    NOT NULL,
    last_seen_at  TEXT    NOT NULL,
    gone          INTEGER NOT NULL DEFAULT 0,
    badge         TEXT    NULL CHECK (badge IN ('new', 'changed')),
    UNIQUE (api_id, path)
) STRICT;

CREATE TABLE api_rules (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    api_id     INTEGER NOT NULL REFERENCES apis(id) ON DELETE CASCADE,
    team_id    INTEGER NULL REFERENCES teams(id) ON DELETE CASCADE,
    pattern    TEXT    NOT NULL,
    decision   TEXT    NOT NULL CHECK (decision IN ('allow', 'ask', 'deny')),
    created_at TEXT    NOT NULL
) STRICT;

CREATE TABLE api_choices (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    api_id    INTEGER NOT NULL REFERENCES apis(id) ON DELETE CASCADE,
    team_id   INTEGER NULL REFERENCES teams(id) ON DELETE CASCADE,
    path      TEXT    NOT NULL,
    decision  TEXT    NOT NULL CHECK (decision IN ('allow', 'ask', 'deny')),
    rule_id   INTEGER NULL REFERENCES api_rules(id) ON DELETE SET NULL,
    chosen_at TEXT    NOT NULL,
    note      TEXT    NULL
) STRICT;
CREATE UNIQUE INDEX ux_api_choices ON api_choices(api_id, COALESCE(team_id, -1), path);
