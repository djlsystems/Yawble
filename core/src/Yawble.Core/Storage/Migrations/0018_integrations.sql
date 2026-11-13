-- 0018 integrations: the person's own CLIs, run on this
-- computer through the app's MCP server, under rules; and the person's approvals of calls that ask first.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- An integration the person enabled: a CLI (kind 'cli') by name (gh, az, ...), found at path, with what the CLI said
-- about itself (version, help) when it was looked at. enabled_all: for every team; otherwise per team below.
CREATE TABLE integrations (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    kind        TEXT    NOT NULL CHECK (kind IN ('cli')),
    name        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    path        TEXT    NULL,
    version     TEXT    NULL,
    manifest    TEXT    NULL,
    status      TEXT    NULL,
    enabled_all INTEGER NOT NULL DEFAULT 0,
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
) STRICT;

CREATE TABLE integration_teams (
    integration_id INTEGER NOT NULL REFERENCES integrations(id) ON DELETE CASCADE,
    team_id        INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    PRIMARY KEY (integration_id, team_id)
) STRICT;

-- The person's rules: an argument pattern ("pr merge*") and what happens. team_id NULL: every team (global).
CREATE TABLE integration_rules (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    integration_id INTEGER NOT NULL REFERENCES integrations(id) ON DELETE CASCADE,
    team_id        INTEGER NULL REFERENCES teams(id) ON DELETE CASCADE,
    pattern        TEXT    NOT NULL,
    decision       TEXT    NOT NULL CHECK (decision IN ('allow', 'ask', 'deny')),
    created_at     TEXT    NOT NULL
) STRICT;

-- A call that asks first: waiting for the person, then approved (and run) or declined.
CREATE TABLE integration_asks (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    at            TEXT    NOT NULL,
    integration   TEXT    NOT NULL,
    team_id       INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member        TEXT    NOT NULL,
    args          TEXT    NOT NULL,
    reason        TEXT    NOT NULL,
    workflow      INTEGER NOT NULL,
    causation_seq INTEGER NOT NULL,
    state         TEXT    NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'approved', 'declined')),
    decided_by    TEXT    NULL,
    decided_at    TEXT    NULL,
    note          TEXT    NULL
) STRICT;
CREATE INDEX ix_integration_asks_pending ON integration_asks(state) WHERE state = 'pending';

-- Every call: what ran (or did not), the rule that decided, how it ended, and who started the work it served.
CREATE TABLE integration_calls (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    at           TEXT    NOT NULL,
    integration  TEXT    NOT NULL,
    team         TEXT    NULL,
    member       TEXT    NULL,
    workflow     INTEGER NULL,
    args         TEXT    NOT NULL,
    decision     TEXT    NOT NULL,
    rule         TEXT    NULL,
    exit_code    INTEGER NULL,
    duration_ms  INTEGER NULL,
    output_bytes INTEGER NULL,
    via          TEXT    NULL,
    user_name    TEXT    NULL
) STRICT;
CREATE INDEX ix_integration_calls_at ON integration_calls(at);
