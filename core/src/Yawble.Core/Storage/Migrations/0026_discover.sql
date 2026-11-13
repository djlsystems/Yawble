-- 0026 Discover: the commands of a CLI found by walking its help, and the person's checklist choices.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- integration_commands: the discovered tree, global (a CLI is the same for every team), one row per command path.
-- integration_choices: the person's saved ticks, per scope (team_id NULL = every team); each owns the integration_rules row
-- it generated (rule_id), which is what the rules engine reads. integration_discovery: one row per CLI about its last walk.

CREATE TABLE integration_commands (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    integration_id INTEGER NOT NULL REFERENCES integrations(id) ON DELETE CASCADE,
    path           TEXT    NOT NULL,
    parent_path    TEXT    NOT NULL,
    summary        TEXT    NULL,
    has_children   INTEGER NOT NULL,
    class          TEXT    NOT NULL CHECK (class IN ('readonly', 'change', 'destructive', 'credential', 'group', 'unknown')),
    class_source   TEXT    NOT NULL CHECK (class_source IN ('floor', 'preset', 'verb', 'model', 'none')),
    class_reason   TEXT    NOT NULL,
    seen_version   TEXT    NULL,
    first_seen_at  TEXT    NOT NULL,
    last_seen_at   TEXT    NOT NULL,
    gone           INTEGER NOT NULL DEFAULT 0,
    help_hash      TEXT    NULL,
    help_unreadable INTEGER NOT NULL DEFAULT 0,
    badge          TEXT    NULL CHECK (badge IN ('new', 'changed')),
    UNIQUE (integration_id, path)
) STRICT;

CREATE TABLE integration_choices (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    integration_id INTEGER NOT NULL REFERENCES integrations(id) ON DELETE CASCADE,
    team_id        INTEGER NULL REFERENCES teams(id) ON DELETE CASCADE,
    path           TEXT    NOT NULL,
    decision       TEXT    NOT NULL CHECK (decision IN ('allow', 'ask', 'deny')),
    rule_id        INTEGER NULL REFERENCES integration_rules(id) ON DELETE SET NULL,
    chosen_at      TEXT    NOT NULL,
    note           TEXT    NULL
) STRICT;
CREATE UNIQUE INDEX ux_integration_choices ON integration_choices(integration_id, COALESCE(team_id, -1), path);

CREATE TABLE integration_discovery (
    integration_id INTEGER PRIMARY KEY REFERENCES integrations(id) ON DELETE CASCADE,
    discovered_at  TEXT    NOT NULL,
    version        TEXT    NULL,
    help_flag      TEXT    NULL,
    runs           INTEGER NOT NULL,
    command_count  INTEGER NOT NULL,
    partial        INTEGER NOT NULL,
    partial_reason TEXT    NULL,
    error          TEXT    NULL
) STRICT;
