-- 0022 more than one repository per team.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- A team keeps its primary repository (team_repos, 0007: the run's cwd) and may have more, each by a short name
-- (the folder name members see). A repository on this computer, never granted to a sandbox itself, as team_repos.
--   writable 1: every member gets its own clone at <team root>/members/<member>/repos/<name>, on its branch, fetched
--               back into path after each run and pushed to path's origin like the primary's branches; the team's
--               shared view of it is <team root>/shared/<name>.git (read-only to members).
--   writable 0: a reference. The host keeps one checkout of base_branch at <team root>/reference/<name>, granted
--               read-only to every member and brought up to date before each run. Nothing is ever written back.
-- source: the address it was cloned from, when the app cloned it (NULL for a folder the person picked).
CREATE TABLE team_extra_repos (
    team_id     INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    name        TEXT    NOT NULL,
    path        TEXT    NOT NULL,
    writable    INTEGER NOT NULL CHECK (writable IN (0, 1)),
    base_branch TEXT    NOT NULL,
    source      TEXT    NULL,
    created_at  TEXT    NOT NULL,
    PRIMARY KEY (team_id, name)
) STRICT;

-- A member's clone of a writable extra repository (as member_clones, 0007, for the primary).
CREATE TABLE member_extra_clones (
    member_id   INTEGER NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    repo_name   TEXT    NOT NULL,
    path        TEXT    NOT NULL,
    branch      TEXT    NOT NULL,
    created_at  TEXT    NOT NULL,
    fetched_at  TEXT    NULL,
    fetched_sha TEXT    NULL,
    last_error  TEXT    NULL,
    PRIMARY KEY (member_id, repo_name)
) STRICT;
