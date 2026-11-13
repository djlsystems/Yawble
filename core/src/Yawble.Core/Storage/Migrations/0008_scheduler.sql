-- 0008 the global run scheduler.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- One queue for every sandbox: a member's run ('run') and an interactive terminal session
-- ('interactive') wait in the same strict FIFO, id order, with no jump and no reserved slot. A member holds at most
-- one entry of either kind (the UNIQUE member_id of 0002). Interactive entries do not survive a restart: the window
-- that asked for one is gone, so recovery deletes them; queued runs keep their order.
ALTER TABLE run_queue ADD COLUMN kind TEXT NOT NULL DEFAULT 'run' CHECK (kind IN ('run', 'interactive'));

-- How a run ended, as the card shows it: timeout, idle, stopped, credential, start_failed, failed, interrupted.
-- NULL for a completed or still running run. runs.reason keeps the sentence.
ALTER TABLE runs ADD COLUMN failure_class TEXT NULL;

-- Interactive terminal sessions: they hold a global run slot but carry no batch, so they are not runs.
-- queued_at / started_at record the wait like a run's, for the run history.
CREATE TABLE sessions (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    member_id  INTEGER NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    queue_id   INTEGER NOT NULL,
    state      TEXT    NOT NULL CHECK (state IN ('running', 'ended', 'stopped', 'failed', 'interrupted')),
    queued_at  TEXT    NOT NULL,
    started_at TEXT    NOT NULL,
    ended_at   TEXT    NULL,
    reason     TEXT    NULL
) STRICT;
CREATE INDEX ix_sessions_state ON sessions(state);
CREATE INDEX ix_sessions_member ON sessions(member_id, id);
