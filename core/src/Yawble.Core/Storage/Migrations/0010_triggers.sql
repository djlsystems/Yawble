-- 0010 triggers and wakes: work that starts on its own.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- A trigger tells one member an instruction on a schedule, as a new workflow (source "trigger:<id>").
-- kind: every (interval_sec, optional anchor fire_at), cron (cron + timezone, optional start fire_at), once (fire_at).
-- next_due_at is the next fire (UTC, ISO 8601); NULL when it will never fire again (a once that fired, or disabled).
-- last_outcome: fired | skipped | missed | member-missing. missed_count counts fires that passed while the app was
-- not running (recorded, never caught up).
CREATE TABLE triggers (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id        INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member_id      INTEGER NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    name           TEXT    NOT NULL,
    kind           TEXT    NOT NULL CHECK (kind IN ('every', 'cron', 'once')),
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
    updated_at     TEXT    NOT NULL
) STRICT;
CREATE INDEX ix_triggers_due ON triggers(next_due_at) WHERE next_due_at IS NOT NULL AND enabled = 1;

-- A wake (the wake_me tool): a member asked to be woken once, after a delay, inside the workflow it is working in. It is
-- delivered as an instruction to the same member caused by the message the member's run was reacting to.
CREATE TABLE wakes (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    member_id     INTEGER NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    causation_seq INTEGER NOT NULL,
    due_at        TEXT    NOT NULL,
    note          TEXT    NOT NULL,
    fired_seq     INTEGER NULL,
    created_at    TEXT    NOT NULL
) STRICT;
CREATE INDEX ix_wakes_due ON wakes(due_at) WHERE fired_seq IS NULL;
