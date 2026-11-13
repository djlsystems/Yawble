-- 0035 Custom agents and the agent change log.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- One row per custom agent (a duplicate of a built-in), and per built-in that has settings of its own (its default model).
-- A built-in's row has custom = 0 and its name is the CLI's name; only default_model is used. Argument lists are JSON arrays of strings.
CREATE TABLE agent_profiles (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT    NOT NULL COLLATE NOCASE UNIQUE,
    base_cli      TEXT    NOT NULL,
    custom        INTEGER NOT NULL DEFAULT 1,
    default_model TEXT    NOT NULL DEFAULT '',
    args_before   TEXT    NOT NULL DEFAULT '[]',
    args_after    TEXT    NOT NULL DEFAULT '[]',
    removed_args  TEXT    NOT NULL DEFAULT '[]',
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL
) STRICT;

-- Every change of a member's, a Manager's or the Concierge's agent or model, of an agent's default model, and of a custom
-- agent's arguments: one row, never edited or deleted. Local only; not sent over the Cloud Link.
-- subject: 'member:<team>/<name>' | 'manager:<team>' | 'concierge:<conversation>' | 'agent:<name>'.
-- effective_model: the model after defaults were resolved (never empty unless the CLI's own default applies: then 'cli-default').
CREATE TABLE agent_changes (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    at              TEXT    NOT NULL,
    kind            TEXT    NOT NULL,
    subject         TEXT    NOT NULL,
    from_agent      TEXT    NULL,
    to_agent        TEXT    NULL,
    from_model      TEXT    NULL,
    to_model        TEXT    NULL,
    effective_model TEXT    NOT NULL,
    actor_via       TEXT    NOT NULL,
    actor_user      TEXT    NULL,
    how             TEXT    NOT NULL,
    detail          TEXT    NULL
) STRICT;
CREATE INDEX agent_changes_subject ON agent_changes (subject, id);

CREATE TRIGGER agent_changes_no_update BEFORE UPDATE ON agent_changes BEGIN SELECT RAISE(ABORT, 'agent_changes is append-only'); END;
CREATE TRIGGER agent_changes_no_delete BEFORE DELETE ON agent_changes BEGIN SELECT RAISE(ABORT, 'agent_changes is append-only'); END;
