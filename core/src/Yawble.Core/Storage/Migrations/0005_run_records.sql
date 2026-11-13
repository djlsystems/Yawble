-- 0005 run records (the launcher's runs and the card's reasons). One row per sandbox, headless or
-- interactive. bus_run_id is runs.id of 0002 for a member run; NULL for an interactive session (no batch).
-- end_class: completed, failed, timeout, idle, stopped, interrupted, start_failed, credential.
-- kill_latency_ms: from the kill decision (timeout, idle, Stop, shutdown) to the sandbox reporting it gone;
-- tree_survived = 1 when it was not gone within the 5 s bound and the handle was freed instead.
-- guardrails: JSON array of post-run check events (a credentials file found and deleted).
-- Append-only: never edit this file once shipped.
CREATE TABLE run_records (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_key         TEXT    NOT NULL UNIQUE,
    bus_run_id      INTEGER NULL,
    team            TEXT    NOT NULL,
    member          TEXT    NOT NULL,
    cli             TEXT    NOT NULL,
    kind            TEXT    NOT NULL CHECK (kind IN ('headless', 'interactive')),
    backend         TEXT    NOT NULL,
    sandbox_pid     INTEGER NULL,
    end_class       TEXT    NULL,
    exit_code       INTEGER NULL,
    sandbox_timed_out   INTEGER NOT NULL DEFAULT 0,
    kill_latency_ms INTEGER NULL,
    tree_survived   INTEGER NOT NULL DEFAULT 0,
    duration_ms     INTEGER NULL,
    reason          TEXT    NULL,
    guardrails      TEXT    NOT NULL DEFAULT '[]',
    started_at      TEXT    NOT NULL,
    ended_at        TEXT    NULL
) STRICT;
CREATE INDEX ix_run_records_bus_run ON run_records(bus_run_id);
CREATE INDEX ix_run_records_member ON run_records(team, member, id);
