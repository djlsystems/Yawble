-- yawble:foreign-keys-off
-- 0060 the dispatched work is a deliverable (was "workflow") and the automation feature is a workflow (was "script").
-- Every table, column and index named after either is renamed; the two tables whose CHECK holds the old member/run
-- kind 'script' are rebuilt (SQLite's 12-step change: the migrator runs this file with foreign keys off and checks them
-- before it commits); stored kinds, event types and keys are rewritten. Free text (messages, trails, logs, audit rows)
-- is history and stays as written.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- Ids are never reused: the AUTOINCREMENT counters of the rebuilt tables are kept aside and put back.
CREATE TEMP TABLE seq_0060 AS SELECT name, seq FROM sqlite_sequence WHERE name IN ('members', 'run_records');

-- members: rebuilt for its CHECK
CREATE TABLE members_0060 (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id      INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    name         TEXT    NOT NULL COLLATE NOCASE,
    role         TEXT    NOT NULL CHECK (role IN ('manager', 'member')),
    cli          TEXT    NOT NULL DEFAULT 'claude',
    instructions TEXT    NOT NULL DEFAULT '',
    created_at   TEXT    NOT NULL, given_name TEXT NULL, kind TEXT NOT NULL DEFAULT 'agent' CHECK (kind IN ('agent', 'workflow')), workflow_id INTEGER NULL REFERENCES workflows(id), workflow_version INTEGER NULL, settings TEXT NULL, bindings TEXT NULL, workflow_timeout_min INTEGER NULL, hire_role TEXT NULL, workflow_max_tells INTEGER NULL,
    UNIQUE (team_id, name)
) STRICT;
INSERT INTO members_0060 (id, team_id, name, role, cli, instructions, created_at, given_name, kind, workflow_id, workflow_version, settings, bindings, workflow_timeout_min, hire_role, workflow_max_tells)
    SELECT id, team_id, name, role, cli, instructions, created_at, given_name, CASE kind WHEN 'script' THEN 'workflow' ELSE kind END, script_id, script_version, settings, bindings, script_timeout_min, hire_role, script_max_tells FROM members;
DROP TABLE members;
ALTER TABLE members_0060 RENAME TO members;
CREATE UNIQUE INDEX ux_members_manager ON members(team_id) WHERE role = 'manager';

-- run_records: rebuilt for its CHECK
CREATE TABLE run_records_0060 (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_key         TEXT    NOT NULL UNIQUE,
    bus_run_id      INTEGER NULL,
    team            TEXT    NOT NULL,
    member          TEXT    NOT NULL,
    cli             TEXT    NOT NULL,
    kind            TEXT    NOT NULL CHECK (kind IN ('headless', 'interactive', 'workflow')),
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
INSERT INTO run_records_0060 (id, run_key, bus_run_id, team, member, cli, kind, backend, sandbox_pid, end_class, exit_code, sandbox_timed_out, kill_latency_ms, tree_survived, duration_ms, reason, guardrails, started_at, ended_at, via, user_name, agent, model, model_source, cli_version, argv, base_commit)
    SELECT id, CASE WHEN run_key LIKE 'script:%' THEN 'workflow:' || substr(run_key, 8) ELSE run_key END, bus_run_id, team, member, cli, CASE kind WHEN 'script' THEN 'workflow' ELSE kind END, backend, sandbox_pid, end_class, exit_code, sandbox_timed_out, kill_latency_ms, tree_survived, duration_ms, reason, guardrails, started_at, ended_at, via, user_name, agent, model, model_source, cli_version, argv, base_commit FROM run_records;
DROP TABLE run_records;
ALTER TABLE run_records_0060 RENAME TO run_records;
CREATE INDEX ix_run_records_bus_run ON run_records(bus_run_id);
CREATE INDEX ix_run_records_member ON run_records(team, member, id);
CREATE INDEX ix_run_records_started ON run_records(started_at);

ALTER TABLE backlog_dispatches RENAME COLUMN workflow TO deliverable;
ALTER TABLE backlog_items RENAME COLUMN dispatched_workflow TO dispatched_deliverable;
ALTER TABLE board_cards RENAME COLUMN workflow TO deliverable;
ALTER TABLE board_timeline RENAME COLUMN workflow TO deliverable;
ALTER TABLE code_events RENAME COLUMN workflow_id TO deliverable_id;
ALTER TABLE concierge_watches RENAME COLUMN workflow TO deliverable;
ALTER TABLE folder_asks RENAME COLUMN workflow TO deliverable;
ALTER TABLE integration_asks RENAME COLUMN workflow TO deliverable;
ALTER TABLE integration_calls RENAME COLUMN workflow TO deliverable;
ALTER TABLE network_asks RENAME COLUMN workflow TO deliverable;
ALTER TABLE script_member_state RENAME TO workflow_member_state;
ALTER TABLE script_run_steps RENAME TO workflow_run_steps;
ALTER TABLE script_runs RENAME TO workflow_runs;
ALTER TABLE workflow_runs RENAME COLUMN script_id TO workflow_id;
ALTER TABLE workflow_runs RENAME COLUMN script_version TO workflow_version;
ALTER TABLE workflow_runs RENAME COLUMN wait_workflow_id TO wait_deliverable_id;
ALTER TABLE script_versions RENAME TO workflow_versions;
ALTER TABLE workflow_versions RENAME COLUMN script_id TO workflow_id;
ALTER TABLE scripts RENAME TO workflows;
ALTER TABLE workflows RENAME COLUMN script_key TO workflow_key;
ALTER TABLE skill_versions RENAME COLUMN workflow_id TO deliverable_id;
ALTER TABLE team_settings RENAME COLUMN workflow_expiry_days TO deliverable_expiry_days;
ALTER TABLE tool_asks RENAME COLUMN workflow TO deliverable;
ALTER TABLE workflow_expiry RENAME TO deliverable_expiry;
ALTER TABLE deliverable_expiry RENAME COLUMN workflow TO deliverable;

DROP INDEX board_timeline_workflow;
CREATE INDEX board_timeline_deliverable ON board_timeline (deliverable, at);
DROP INDEX ix_script_run_steps_run;
CREATE INDEX ix_workflow_run_steps_run ON workflow_run_steps(run_id, id);
DROP INDEX ix_script_runs_member;
CREATE INDEX ix_workflow_runs_member ON workflow_runs(member_id, id);
DROP INDEX ix_script_runs_status;
CREATE INDEX ix_workflow_runs_status ON workflow_runs(status);
DROP INDEX ix_script_runs_team;
CREATE INDEX ix_workflow_runs_team ON workflow_runs(team_id, id);
DROP INDEX ux_script_versions;
CREATE UNIQUE INDEX ux_workflow_versions ON workflow_versions(workflow_id, version);

DELETE FROM sqlite_sequence WHERE name IN (SELECT name FROM seq_0060);
INSERT INTO sqlite_sequence (name, seq) SELECT name, seq FROM seq_0060;
DROP TABLE seq_0060;

UPDATE messages SET type = 'deliverable.' || substr(type, 10) WHERE type LIKE 'workflow.%';
UPDATE messages SET payload = replace(replace(payload, '"idleWorkflowOffer"', '"idleDeliverableOffer"'), '"workflow.', '"deliverable.')
    WHERE payload LIKE '%idleWorkflowOffer%' OR payload LIKE '%"workflow.%';
UPDATE board_trail SET kind = 'deliverable' || substr(kind, 9) WHERE kind LIKE 'workflow %';
UPDATE integration_calls SET via = 'workflow' WHERE via = 'script';
UPDATE integration_calls SET args = replace(replace(args, '"script runs ', '"workflow runs '), '"script run ', '"workflow run ')
    WHERE args LIKE '%script run%';
UPDATE app_defaults SET key = 'workflows.' || substr(key, 9) WHERE key LIKE 'scripts.%';
UPDATE workflow_runs SET wait_kind = 'deliverable' WHERE wait_kind = 'workflow';
UPDATE workflow_runs SET checkpoint = replace(checkpoint, '"workflow":', '"deliverable":') WHERE checkpoint LIKE '%"workflow":%';
UPDATE workflow_run_steps SET output = replace(output, '"workflow":', '"deliverable":') WHERE output LIKE '%"workflow":%';
UPDATE workflow_versions SET document = replace(replace(document, 'yawble-script/1', 'yawble-workflow/1'), '.workflow', '.deliverable');
