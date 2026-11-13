-- 0027 host tasks: named programs the person defines, that a team's agents can
-- request and that run on this computer outside the sandbox, only on merged code, under an approval tied to what the
-- program will actually execute (the definition plus the entry-point files).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- The definitions. Only the person's window writes it. env_names holds variable NAMES, never values.
CREATE TABLE host_tasks (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id    INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    name       TEXT    NOT NULL COLLATE NOCASE,
    repo       TEXT    NOT NULL,
    program    TEXT    NOT NULL,
    args       TEXT    NOT NULL,
    workdir    TEXT    NOT NULL,
    timeout_s  INTEGER NOT NULL CHECK (timeout_s BETWEEN 10 AND 7200),
    env_names  TEXT    NOT NULL,
    watch      TEXT    NOT NULL,
    pin_mode   TEXT    NOT NULL CHECK (pin_mode IN ('files', 'commit')),
    command_line_only INTEGER NOT NULL DEFAULT 0,
    created_at TEXT    NOT NULL,
    updated_at TEXT    NOT NULL,
    updated_by TEXT    NULL,
    UNIQUE (team_id, name)
) STRICT;

-- The current "Always for this team": at most one row per task. files_json is [{path, blob}] (git blob ids, the
-- content identity of each pinned file at commit_sha); definition_json is the canonical definition as approved.
CREATE TABLE host_task_approvals (
    task_id         INTEGER PRIMARY KEY REFERENCES host_tasks(id) ON DELETE CASCADE,
    definition_hash TEXT NOT NULL,
    files_hash      TEXT NOT NULL,
    definition_json TEXT NOT NULL,
    files_json      TEXT NOT NULL,
    commit_sha      TEXT NOT NULL,
    approved_by     TEXT NULL,
    via             TEXT NULL,
    approved_at     TEXT NOT NULL
) STRICT;

-- Asks and calls reuse the integrations' tables (integration = 'task:<name>'). An ask stores what the person was shown.
ALTER TABLE integration_asks ADD COLUMN task_id INTEGER NULL;
ALTER TABLE integration_asks ADD COLUMN commit_sha TEXT NULL;
ALTER TABLE integration_asks ADD COLUMN definition_hash TEXT NULL;
ALTER TABLE integration_asks ADD COLUMN files_hash TEXT NULL;
ALTER TABLE integration_asks ADD COLUMN detail_json TEXT NULL;

-- What only a run has. task_id goes NULL when the task is deleted; the record stays.
CREATE TABLE host_task_runs (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id          INTEGER NULL REFERENCES host_tasks(id) ON DELETE SET NULL,
    team_id          INTEGER NOT NULL,
    task_name        TEXT    NOT NULL,
    repo_path        TEXT    NOT NULL,
    ask_id           INTEGER NULL,
    requested_by     TEXT    NULL,
    started_at       TEXT    NOT NULL,
    ended_at         TEXT    NULL,
    commit_sha       TEXT    NULL,
    head_after       TEXT    NULL,
    definition_hash  TEXT    NULL,
    files_hash       TEXT    NULL,
    program_resolved TEXT    NULL,
    exit_code        INTEGER NULL,
    timed_out        INTEGER NOT NULL DEFAULT 0,
    killed_by        TEXT    NULL CHECK (killed_by IS NULL OR killed_by IN ('timeout', 'app-exit', 'person')),
    output_bytes     INTEGER NULL,
    truncated        INTEGER NOT NULL DEFAULT 0,
    log_path         TEXT    NULL,
    log_sha256       TEXT    NULL,
    dirty_after      INTEGER NULL,
    missing_env      TEXT    NULL,
    state            TEXT    NOT NULL CHECK (state IN ('running', 'done', 'interrupted', 'refused')),
    via              TEXT    NULL,
    user_name        TEXT    NULL
) STRICT;
CREATE INDEX ix_host_task_runs_task ON host_task_runs(task_id, id);
CREATE INDEX ix_host_task_runs_running ON host_task_runs(state) WHERE state = 'running';
