-- 0034 bringing a team's branches up to its base branch before a workflow starts.
-- teams.sync_to_base: per team, on by default: "Bring branches up to <base> before new work". Off = no sync, only warnings.
-- teams.sync_note: the note left by a sync that conflicted (the Manager's first instruction carries it, and the person
--   sees it as a Needs You item); NULL when the team's branches are in step with the base or the sync is off.
-- run_records.base_commit: the base branch's commit the member's clone was prepared against when the run started.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
ALTER TABLE teams ADD COLUMN sync_to_base INTEGER NOT NULL DEFAULT 1;
ALTER TABLE teams ADD COLUMN sync_note TEXT NULL;
ALTER TABLE run_records ADD COLUMN base_commit TEXT NULL;
