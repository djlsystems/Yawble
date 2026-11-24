-- 0053 folder asks: an agent asked for read or read-write access to a folder on this computer (request_folder),
-- and the person decides in Needs you. Append-only: never edit this file once shipped; change the schema in a later step.
--
-- One open ask per (member, path, access): a repeat while it waits returns the same ask. member_id is the member the
-- access is for (a Manager may ask for a member of its own team: asked_by is then the Manager, "Team/Name"); member is
-- that member as "Team/Name". path is the resolved absolute path (PolicyPaths.Resolve), access read | readwrite.
-- path_exists: whether the folder existed when it was asked for (folder.asks also says whether it exists now).
-- state: pending (waiting for the person), approved (granted_access says what was granted: never wider than access; the
-- member's loosen block got the path through PolicyStore.SaveLoosen, as policy.saveLoosen writes it), denied (the asker
-- is told), closed (the member was removed from the team while it waited).
CREATE TABLE folder_asks (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    at             TEXT    NOT NULL,
    team_id        INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member_id      INTEGER NOT NULL,
    member         TEXT    NOT NULL,
    asked_by       TEXT    NOT NULL,
    path           TEXT    NOT NULL,
    access         TEXT    NOT NULL CHECK (access IN ('read', 'readwrite')),
    reason         TEXT    NOT NULL,
    path_exists    INTEGER NOT NULL DEFAULT 0,
    state          TEXT    NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'approved', 'denied', 'closed')),
    granted_access TEXT    NULL CHECK (granted_access IS NULL OR granted_access IN ('read', 'readwrite')),
    decided_by     TEXT    NULL,
    decided_via    TEXT    NULL,
    decided_at     TEXT    NULL,
    note           TEXT    NULL,
    workflow       INTEGER NULL,
    causation_seq  INTEGER NULL
) STRICT;
CREATE UNIQUE INDEX ux_folder_asks_open ON folder_asks(member_id, path, access) WHERE state = 'pending';
CREATE INDEX ix_folder_asks_team ON folder_asks(team_id, state);

-- What happened to each ask, in order: asked, asked again, approved (what was granted), denied, closed. The audit log
-- has the person's folder.answer call itself (who, how).
CREATE TABLE folder_ask_events (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    ask_id  INTEGER NOT NULL REFERENCES folder_asks(id) ON DELETE CASCADE,
    at      TEXT    NOT NULL,
    kind    TEXT    NOT NULL CHECK (kind IN ('asked', 'asked again', 'approved', 'denied', 'closed')),
    by_user TEXT    NULL,
    via     TEXT    NULL,
    detail  TEXT    NULL
) STRICT;
CREATE INDEX ix_folder_ask_events_ask ON folder_ask_events(ask_id);
