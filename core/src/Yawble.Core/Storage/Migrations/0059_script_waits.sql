-- 0059 a script run can wait: a tell step that waits parks the run (status 'waiting') on a workflow until it ends or the
-- wait's timeout passes; the run holds no run slot while it waits and resumes from its checkpoint. The wait is kept on the run:
-- wait_kind ('workflow' for a tell; other steps that wait add their own kind), wait_workflow_id, wait_until (UTC ISO; NULL = no
-- timeout). members.script_max_tells: the most tells one run of a script member may make (NULL = 5).
-- SQLite cannot change a CHECK in place, so script_runs is rebuilt with the same columns, rows, ids and indexes. The migrator runs
-- with foreign_keys ON inside a transaction, so dropping the old table cascades into script_run_steps: the steps are kept aside
-- first and put back with their own ids.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
ALTER TABLE members ADD COLUMN script_max_tells INTEGER NULL;

CREATE TEMP TABLE script_run_steps_0059 AS
    SELECT id, run_id, kind, step_id, type, input, output, status, truncated, started_at, ended_at FROM script_run_steps;
CREATE TEMP TABLE script_seq_0059 AS SELECT name, seq FROM sqlite_sequence WHERE name IN ('script_runs', 'script_run_steps');

CREATE TABLE script_runs_0059 (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id          INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member_id        INTEGER NULL REFERENCES members(id) ON DELETE CASCADE,
    script_id        INTEGER NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    script_version   INTEGER NOT NULL,
    trigger          TEXT    NOT NULL,
    input            TEXT    NULL,
    settings         TEXT    NULL,
    bindings         TEXT    NULL,
    status           TEXT    NOT NULL CHECK (status IN ('running', 'waiting', 'succeeded', 'failed', 'skipped', 'interrupted')),
    dry_run          INTEGER NOT NULL DEFAULT 0,
    test             INTEGER NOT NULL DEFAULT 0,
    outputs          TEXT    NULL,
    failed_step_id   TEXT    NULL,
    reason           TEXT    NULL,
    checkpoint       TEXT    NULL,
    started_at       TEXT    NOT NULL,
    ended_at         TEXT    NULL,
    wait_kind        TEXT    NULL,
    wait_workflow_id INTEGER NULL,
    wait_until       TEXT    NULL
) STRICT;
INSERT INTO script_runs_0059 (id, team_id, member_id, script_id, script_version, trigger, input, settings, bindings, status, dry_run, test,
                              outputs, failed_step_id, reason, checkpoint, started_at, ended_at)
    SELECT id, team_id, member_id, script_id, script_version, trigger, input, settings, bindings, status, dry_run, test,
           outputs, failed_step_id, reason, checkpoint, started_at, ended_at
      FROM script_runs;
DROP TABLE script_runs;
ALTER TABLE script_runs_0059 RENAME TO script_runs;
CREATE INDEX ix_script_runs_member ON script_runs(member_id, id);
CREATE INDEX ix_script_runs_team ON script_runs(team_id, id);
CREATE INDEX ix_script_runs_status ON script_runs(status);

DELETE FROM script_run_steps;
INSERT INTO script_run_steps (id, run_id, kind, step_id, type, input, output, status, truncated, started_at, ended_at)
    SELECT id, run_id, kind, step_id, type, input, output, status, truncated, started_at, ended_at FROM script_run_steps_0059;

-- Ids are never reused: the AUTOINCREMENT counters stay where they were.
DELETE FROM sqlite_sequence WHERE name IN (SELECT name FROM script_seq_0059);
INSERT INTO sqlite_sequence (name, seq) SELECT name, seq FROM script_seq_0059;

DROP TABLE script_run_steps_0059;
DROP TABLE script_seq_0059;
