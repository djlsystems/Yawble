-- 0052 skill versions (B100): every write of a team's team_retros skill, kept so the person can see what changed and go back.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- Keyed by team and skill name (not skills.id: saving a skill replaces its row). version counts 1, 2, ... per team and name.
-- writer: the member who wrote it with team_retros_write (the Manager), or 'person' for a save or restore in Settings > Skills.
-- workflow_id: the workflow the Manager's retro ran in; NULL for the person. restored_from: the version a restore brought back.
CREATE TABLE skill_versions (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id       INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    name          TEXT    NOT NULL,
    version       INTEGER NOT NULL,
    body          TEXT    NOT NULL,
    writer        TEXT    NOT NULL,
    workflow_id   INTEGER NULL,
    restored_from INTEGER NULL,
    written_at    TEXT    NOT NULL
) STRICT;
CREATE UNIQUE INDEX ux_skill_versions ON skill_versions(team_id, name, version);
