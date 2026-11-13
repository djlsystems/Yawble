-- 0021 events and event triggers.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- A watch runs an integration's read-only command on this computer every interval_sec (no model run), reads its JSON
-- output as a list of items, and publishes an event (bus type event.<event_name>) for each item whose key_field value
-- it has not seen. The first look only remembers. Its command must be allowed for the team by the rules.
CREATE TABLE watches (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id      INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    name         TEXT    NOT NULL,
    integration  TEXT    NOT NULL,
    args         TEXT    NOT NULL,
    key_field    TEXT    NOT NULL,
    event_name   TEXT    NOT NULL,
    interval_sec INTEGER NOT NULL,
    enabled      INTEGER NOT NULL DEFAULT 1,
    seen         TEXT    NULL,
    next_due_at  TEXT    NULL,
    last_outcome TEXT    NULL,
    last_reason  TEXT    NULL,
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL
) STRICT;

-- An event trigger (kind 'event'): event_name is what it listens for (a * pattern, e.g. web.pr*). The kind's CHECK
-- cannot be altered in place, so the triggers table is rebuilt with every row and column kept (nothing references it).
CREATE TABLE triggers_0021 (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id        INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member_id      INTEGER NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    name           TEXT    NOT NULL,
    kind           TEXT    NOT NULL CHECK (kind IN ('every', 'cron', 'once', 'folder', 'event')),
    interval_sec   INTEGER NULL,
    cron           TEXT    NULL,
    timezone       TEXT    NULL,
    fire_at        TEXT    NULL,
    instruction    TEXT    NOT NULL,
    only_when_idle INTEGER NOT NULL DEFAULT 1,
    enabled        INTEGER NOT NULL DEFAULT 1,
    next_due_at    TEXT    NULL,
    last_fired_at  TEXT    NULL,
    last_outcome   TEXT    NULL,
    last_reason    TEXT    NULL,
    last_seq       INTEGER NULL,
    missed_count   INTEGER NOT NULL DEFAULT 0,
    created_at     TEXT    NOT NULL,
    updated_at     TEXT    NOT NULL,
    wake_manager   TEXT    NOT NULL DEFAULT 'always' CHECK (wake_manager IN ('always', 'onHandbackOrFailure', 'never')),
    saved_by       TEXT    NULL,
    saved_via      TEXT    NULL,
    watch_root     TEXT    NULL CHECK (watch_root IS NULL OR watch_root IN ('documents', 'host')),
    watch_path     TEXT    NULL,
    watch_glob     TEXT    NULL,
    quiet_sec      INTEGER NULL,
    watch_state    TEXT    NULL,
    event_name     TEXT    NULL
) STRICT;
INSERT INTO triggers_0021 (id, team_id, member_id, name, kind, interval_sec, cron, timezone, fire_at, instruction, only_when_idle,
                           enabled, next_due_at, last_fired_at, last_outcome, last_reason, last_seq, missed_count, created_at,
                           updated_at, wake_manager, saved_by, saved_via, watch_root, watch_path, watch_glob, quiet_sec, watch_state)
SELECT id, team_id, member_id, name, kind, interval_sec, cron, timezone, fire_at, instruction, only_when_idle,
       enabled, next_due_at, last_fired_at, last_outcome, last_reason, last_seq, missed_count, created_at,
       updated_at, wake_manager, saved_by, saved_via, watch_root, watch_path, watch_glob, quiet_sec, watch_state
FROM triggers;
DROP TABLE triggers;
ALTER TABLE triggers_0021 RENAME TO triggers;
CREATE INDEX ix_triggers_due ON triggers(next_due_at) WHERE next_due_at IS NOT NULL AND enabled = 1;
