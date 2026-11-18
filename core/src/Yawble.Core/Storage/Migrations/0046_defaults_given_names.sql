-- 0046 saved defaults (Settings > Concierge and Teams) and members' first names.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- A member's first name: the words after the job word of the name it was given ("Developer Priya" -> 'Priya'), set at
-- every hire, team creation and Add member. NULL when the name had no words after the job word. A first name is
-- "used" when any member of any team (archived teams too, Managers not) has it, compared case-insensitively.
ALTER TABLE members ADD COLUMN given_name TEXT NULL;

-- Members from before this step: from the id, the part after the first run of a capital and lowercase letters, when
-- it starts with a capital ("DeveloperTomas" -> 'Tomas'). Ids that do not split that way stay NULL.
WITH RECURSIVE run(id, name, i) AS (
    SELECT id, name, 1 FROM members
    WHERE role = 'member' AND substr(name, 1, 1) GLOB '[A-Z]' AND substr(name, 2, 1) GLOB '[a-z]'
    UNION ALL
    SELECT id, name, i + 1 FROM run WHERE i < length(name) AND substr(name, i + 1, 1) GLOB '[a-z]'
),
cut(id, given) AS (
    SELECT id, substr(name, max(i) + 1) FROM run GROUP BY id
)
UPDATE members SET given_name = (SELECT given FROM cut WHERE cut.id = members.id)
WHERE id IN (SELECT id FROM cut WHERE given GLOB '[A-Z]*');

-- The person's saved defaults, one JSON value per key:
--   concierge.defaultAgent  the agent new Concierge conversations start on (a string; absent = first available)
--   teams.manager           {name, cli}: the Manager of a new team
--   teams.members           [{name, cli, onNewTeams}]: optional members offered for a new team
--   teams.memberNames       [string]: the list new members' first names are picked from
--   teams.country           a country code from the bundled list (absent = en-US)
CREATE TABLE app_defaults (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;
