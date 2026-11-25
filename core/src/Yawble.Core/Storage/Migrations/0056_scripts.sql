-- 0056 scripts: the global script and schema libraries, script members, their saved state, and script runs.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- scripts: one row per script, keyed by the document's own id (script_key, e.g. inbox-sms-alert). Every save keeps a new
-- row in script_versions (version 1, 2, ... per script) with the whole JSON document and the check's result (JSON); ok = 0
-- is a draft that no member runs. latest_version is the newest saved; runnable_version the newest that checked clean.
CREATE TABLE scripts (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    script_key       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    name             TEXT    NOT NULL,
    description      TEXT    NOT NULL DEFAULT '',
    latest_version   INTEGER NOT NULL,
    runnable_version INTEGER NULL,
    created_at       TEXT    NOT NULL,
    updated_at       TEXT    NOT NULL
) STRICT;

CREATE TABLE script_versions (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    script_id    INTEGER NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    version      INTEGER NOT NULL,
    name         TEXT    NOT NULL,
    description  TEXT    NOT NULL DEFAULT '',
    document     TEXT    NOT NULL,
    ok           INTEGER NOT NULL,
    check_result TEXT    NOT NULL,
    created_at   TEXT    NOT NULL
) STRICT;
CREATE UNIQUE INDEX ux_script_versions ON script_versions(script_id, version);

-- schemas: the person's named object types (the built-in ones live in code, read-only). document is the JSON Schema subset.
CREATE TABLE schemas (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    name           TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    description    TEXT    NOT NULL DEFAULT '',
    latest_version INTEGER NOT NULL,
    created_at     TEXT    NOT NULL,
    updated_at     TEXT    NOT NULL
) STRICT;

CREATE TABLE schema_versions (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    schema_id    INTEGER NOT NULL REFERENCES schemas(id) ON DELETE CASCADE,
    version      INTEGER NOT NULL,
    description  TEXT    NOT NULL DEFAULT '',
    document     TEXT    NOT NULL,
    check_result TEXT    NOT NULL DEFAULT '{}',
    created_at   TEXT    NOT NULL
) STRICT;
CREATE UNIQUE INDEX ux_schema_versions ON schema_versions(schema_id, version);

-- A member is an agent (as before) or a script member: its runs are the core's script engine, never an agent CLI.
-- script_version NULL follows the script's newest runnable version; settings and bindings are JSON objects (bindings: each
-- requires name to one of the team's integrations). script_timeout_min: the run timeout (NULL = 10 minutes).
ALTER TABLE members ADD COLUMN kind TEXT NOT NULL DEFAULT 'agent' CHECK (kind IN ('agent', 'script'));
ALTER TABLE members ADD COLUMN script_id INTEGER NULL REFERENCES scripts(id);
ALTER TABLE members ADD COLUMN script_version INTEGER NULL;
ALTER TABLE members ADD COLUMN settings TEXT NULL;
ALTER TABLE members ADD COLUMN bindings TEXT NULL;
ALTER TABLE members ADD COLUMN script_timeout_min INTEGER NULL;

-- A script member's saved variables (JSON object), kept between runs and written only when a run succeeds;
-- consecutive_failures counts failed runs in a row, and failure_decision_seq is the one decision card opened at three.
CREATE TABLE script_member_state (
    member_id            INTEGER PRIMARY KEY REFERENCES members(id) ON DELETE CASCADE,
    variables            TEXT    NOT NULL DEFAULT '{}',
    consecutive_failures INTEGER NOT NULL DEFAULT 0,
    failure_decision_seq INTEGER NULL,
    updated_at           TEXT    NOT NULL
) STRICT;

-- One row per script run: a member's (a trigger fired, a tell) or a Test run (test = 1, member_id NULL allowed).
-- A fire that came while the member's run was going is a row with status 'skipped' and the reason.
-- checkpoint: the engine's JSON checkpoint after the last completed step (a run cut off by a restart resumes from it).
CREATE TABLE script_runs (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id        INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member_id      INTEGER NULL REFERENCES members(id) ON DELETE CASCADE,
    script_id      INTEGER NOT NULL REFERENCES scripts(id) ON DELETE CASCADE,
    script_version INTEGER NOT NULL,
    trigger        TEXT    NOT NULL,
    input          TEXT    NULL,
    settings       TEXT    NULL,
    bindings       TEXT    NULL,
    status         TEXT    NOT NULL CHECK (status IN ('running', 'succeeded', 'failed', 'skipped', 'interrupted')),
    dry_run        INTEGER NOT NULL DEFAULT 0,
    test           INTEGER NOT NULL DEFAULT 0,
    outputs        TEXT    NULL,
    failed_step_id TEXT    NULL,
    reason         TEXT    NULL,
    checkpoint     TEXT    NULL,
    started_at     TEXT    NOT NULL,
    ended_at       TEXT    NULL
) STRICT;
CREATE INDEX ix_script_runs_member ON script_runs(member_id, id);
CREATE INDEX ix_script_runs_team ON script_runs(team_id, id);
CREATE INDEX ix_script_runs_status ON script_runs(status);

-- Each step of a run as the engine journals it: input and output JSON (truncated, scrubbed with SecretScrubber),
-- status, start and end. kind 'step' is a step; kind 'log' is a line of the run's log (text in output).
CREATE TABLE script_run_steps (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id     INTEGER NOT NULL REFERENCES script_runs(id) ON DELETE CASCADE,
    kind       TEXT    NOT NULL DEFAULT 'step' CHECK (kind IN ('step', 'log')),
    step_id    TEXT    NULL,
    type       TEXT    NULL,
    input      TEXT    NULL,
    output     TEXT    NULL,
    status     TEXT    NOT NULL,
    truncated  INTEGER NOT NULL DEFAULT 0,
    started_at TEXT    NOT NULL,
    ended_at   TEXT    NULL
) STRICT;
CREATE INDEX ix_script_run_steps_run ON script_run_steps(run_id, id);

-- run_records.kind gains 'script' (script runs are recorded there too). The CHECK cannot be altered in place, so the
-- table is rebuilt with every row and column kept (nothing references it).
CREATE TABLE run_records_0056 (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_key         TEXT    NOT NULL UNIQUE,
    bus_run_id      INTEGER NULL,
    team            TEXT    NOT NULL,
    member          TEXT    NOT NULL,
    cli             TEXT    NOT NULL,
    kind            TEXT    NOT NULL CHECK (kind IN ('headless', 'interactive', 'script')),
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
    ended_at        TEXT    NULL,
    via             TEXT    NULL,
    user_name       TEXT    NULL,
    agent           TEXT    NULL,
    model           TEXT    NULL,
    model_source    TEXT    NULL,
    cli_version     TEXT    NULL,
    argv            TEXT    NULL,
    base_commit     TEXT    NULL
) STRICT;
INSERT INTO run_records_0056 (id, run_key, bus_run_id, team, member, cli, kind, backend, sandbox_pid, end_class, exit_code,
                              sandbox_timed_out, kill_latency_ms, tree_survived, duration_ms, reason, guardrails, started_at,
                              ended_at, via, user_name, agent, model, model_source, cli_version, argv, base_commit)
SELECT id, run_key, bus_run_id, team, member, cli, kind, backend, sandbox_pid, end_class, exit_code,
       sandbox_timed_out, kill_latency_ms, tree_survived, duration_ms, reason, guardrails, started_at,
       ended_at, via, user_name, agent, model, model_source, cli_version, argv, base_commit
FROM run_records;
DROP TABLE run_records;
ALTER TABLE run_records_0056 RENAME TO run_records;
CREATE INDEX ix_run_records_bus_run ON run_records(bus_run_id);
CREATE INDEX ix_run_records_member ON run_records(team, member, id);
CREATE INDEX ix_run_records_started ON run_records(started_at);
