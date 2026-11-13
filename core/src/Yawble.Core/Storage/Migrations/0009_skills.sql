-- 0009 skills: the person's own skills, beside the built-in ones compiled into the core.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- A skill is a name, a one-line description, the roles it is offered to and a markdown body. A skill with no team
-- is offered in every team; one with a team only there (and it wins over a global one of the same name). Built-in
-- skills are not stored: they come with the core and cannot be edited, only shadowed by a custom skill of the same
-- name.
CREATE TABLE skills (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT    NOT NULL,
    description TEXT    NOT NULL,
    roles       TEXT    NOT NULL,              -- space-separated: manager member any
    body        TEXT    NOT NULL,
    team_id     INTEGER NULL REFERENCES teams(id) ON DELETE CASCADE,
    updated_at  TEXT    NOT NULL
) STRICT;
CREATE UNIQUE INDEX ux_skills_global_name ON skills(name) WHERE team_id IS NULL;
CREATE UNIQUE INDEX ux_skills_team_name ON skills(team_id, name) WHERE team_id IS NOT NULL;
