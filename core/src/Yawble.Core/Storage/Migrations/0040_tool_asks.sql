-- 0040 tool asks: an agent asked for a developer tool from the app's fixed catalog (request_tool), and the person decides.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- One open ask per (team, tool): a repeat while it waits only counts up (count, last_seen, the latest member and reason).
-- state: pending (waiting for the person; error holds winget's message after a failed install, and Try again is offered),
-- installing (the person clicked Install; winget runs in the app's process), installed (the program was found
-- afterwards; every open ask for that tool closes), dismissed (Not now: the team is not asked about that tool again for
-- 24 hours, decided_at). tool is the catalog's name (Integrations/ToolAsks.cs), never a winget id an agent gave.
CREATE TABLE tool_asks (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    at            TEXT    NOT NULL,
    team_id       INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    tool          TEXT    NOT NULL,
    member        TEXT    NOT NULL,
    reason        TEXT    NULL,
    count         INTEGER NOT NULL DEFAULT 1,
    last_seen     TEXT    NOT NULL,
    workflow      INTEGER NULL,
    causation_seq INTEGER NULL,
    state         TEXT    NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'installing', 'installed', 'dismissed')),
    attempts      INTEGER NOT NULL DEFAULT 0,
    error         TEXT    NULL,
    decided_by    TEXT    NULL,
    decided_via   TEXT    NULL,
    decided_at    TEXT    NULL,
    note          TEXT    NULL
) STRICT;
CREATE UNIQUE INDEX ux_tool_asks_open ON tool_asks(team_id, tool) WHERE state IN ('pending', 'installing');
CREATE INDEX ix_tool_asks_team_tool ON tool_asks(team_id, tool, state);
CREATE INDEX ix_tool_asks_at ON tool_asks(at);

-- What happened to each ask, in order: asked, asked again, not now, install (the exact command), installed, failed
-- (winget's exit code and message). The Approvals and All activity logs read it.
CREATE TABLE tool_ask_events (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    ask_id  INTEGER NOT NULL REFERENCES tool_asks(id) ON DELETE CASCADE,
    at      TEXT    NOT NULL,
    kind    TEXT    NOT NULL CHECK (kind IN ('asked', 'asked again', 'not now', 'install', 'installed', 'failed')),
    by_user TEXT    NULL,
    via     TEXT    NULL,
    detail  TEXT    NULL
) STRICT;
CREATE INDEX ix_tool_ask_events_ask ON tool_ask_events(ask_id);
CREATE INDEX ix_tool_ask_events_at ON tool_ask_events(at);
