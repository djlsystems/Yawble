-- 0017 folder-change triggers: a trigger can watch a folder and tell its member
-- when files were added, removed or changed size there. The kind's CHECK cannot be altered in place, so the table is
-- rebuilt with every row and column kept (nothing references it).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- kind 'folder': interval_sec is how often the folder is looked at; watch_root is 'documents' (watch_path inside the
-- team's documents) or 'host' (watch_path an absolute folder on this computer); watch_glob filters file names
-- (e.g. *.pdf); quiet_sec is how long a change must stay still before it fires (a copy in progress fires once, after);
-- watch_state is the watcher's own memory (the baseline listing and a change waiting to settle), JSON.
CREATE TABLE triggers_0017 (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id        INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member_id      INTEGER NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    name           TEXT    NOT NULL,
    kind           TEXT    NOT NULL CHECK (kind IN ('every', 'cron', 'once', 'folder')),
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
    watch_state    TEXT    NULL
) STRICT;
INSERT INTO triggers_0017 (id, team_id, member_id, name, kind, interval_sec, cron, timezone, fire_at, instruction, only_when_idle,
                           enabled, next_due_at, last_fired_at, last_outcome, last_reason, last_seq, missed_count, created_at,
                           updated_at, wake_manager, saved_by, saved_via)
SELECT id, team_id, member_id, name, kind, interval_sec, cron, timezone, fire_at, instruction, only_when_idle,
       enabled, next_due_at, last_fired_at, last_outcome, last_reason, last_seq, missed_count, created_at,
       updated_at, wake_manager, saved_by, saved_via
FROM triggers;
DROP TABLE triggers;
ALTER TABLE triggers_0017 RENAME TO triggers;
CREATE INDEX ix_triggers_due ON triggers(next_due_at) WHERE next_due_at IS NOT NULL AND enabled = 1;
