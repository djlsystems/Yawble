-- 0033 the default model per built-in agent, and what each run actually ran with.
-- agent_defaults: one row per built-in CLI ('claude', 'grok', ...); default_model '' = the CLI's own default.
-- A member follows it unless the member has its own model (member_settings.model).
-- run_records gets, at run start, the values the run really used, so cost over time can be computed from the runs
-- themselves even when settings changed mid-way (all UTC, ISO 8601 in started_at / ended_at):
--   agent         the agent profile the run used: the built-in's CLI id, or a custom agent's name
--   model         the RESOLVED model (member override > agent default > CLI default), never just "default";
--                 '' only when no model is named anywhere and the CLI chose its own default
--   model_source  member | agent | cli
--   cli_version   what the CLI reported when the run started (NULL when it did not say)
--   argv          JSON array: the actual command line, the prompt cut to 200 characters, secrets and run tokens redacted
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
CREATE TABLE agent_defaults (
    cli           TEXT PRIMARY KEY,
    default_model TEXT NOT NULL DEFAULT '',
    updated_at    TEXT NOT NULL
) STRICT;

ALTER TABLE run_records ADD COLUMN agent TEXT NULL;
ALTER TABLE run_records ADD COLUMN model TEXT NULL;
ALTER TABLE run_records ADD COLUMN model_source TEXT NULL;
ALTER TABLE run_records ADD COLUMN cli_version TEXT NULL;
ALTER TABLE run_records ADD COLUMN argv TEXT NULL;
