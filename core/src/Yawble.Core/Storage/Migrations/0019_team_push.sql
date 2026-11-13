-- 0019 whether the app pushes a team's branches to its repository's origin after runs (the GitHub built-in):
-- on by default; the person can turn it off per team.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
ALTER TABLE teams ADD COLUMN push_to_origin INTEGER NOT NULL DEFAULT 1;
