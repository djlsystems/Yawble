-- 0016 team actions: pause, archive and the team's environment variables.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- paused_at:   the team takes no new runs: its work stays queued and runs when it is resumed; its triggers skip.
-- archived_at: put away, kept whole (members, settings, repository, history). It does no work and takes no
--              instructions; it was quiet when archived, and comes back paused when unarchived.
-- env:         the team's environment variables, a JSON object of name -> value, given to every run of its members.
--              Plain text, like the rest of this file's data: for coordination (test accounts, endpoints), not secrets.
ALTER TABLE teams ADD COLUMN paused_at TEXT NULL;
ALTER TABLE teams ADD COLUMN archived_at TEXT NULL;
ALTER TABLE teams ADD COLUMN env TEXT NOT NULL DEFAULT '{}';
