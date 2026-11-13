-- 0007 team repositories and per-member clones.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- Each team's own local repository, on the host and never granted to any sandbox. managed = 1: the app made it
-- at <appdata>/teams/<team>/repo; managed = 0: a folder the person picked. base_branch is what a new member's
-- branch starts from. The app never pushes it anywhere.
CREATE TABLE team_repos (
    team_id     INTEGER PRIMARY KEY REFERENCES teams(id) ON DELETE CASCADE,
    path        TEXT    NOT NULL,
    managed     INTEGER NOT NULL CHECK (managed IN (0, 1)),
    base_branch TEXT    NOT NULL,
    created_at  TEXT    NOT NULL
) STRICT;

-- A member's own clone (git clone --local --no-hardlinks, no remote) at
-- <appdata>/teams/<team>/members/<member>/repo, the run's cwd and its implicit read/write grant (RunLayout.ClonePath).
-- branch is the member's one branch; after every run the host fetches it into the team repository.
-- last_check: JSON array of what the post-run git check found and removed (remotes, credential helpers, credential files).
CREATE TABLE member_clones (
    member_id   INTEGER PRIMARY KEY REFERENCES members(id) ON DELETE CASCADE,
    path        TEXT    NOT NULL,
    branch      TEXT    NOT NULL,
    created_at  TEXT    NOT NULL,
    fetched_at  TEXT    NULL,
    fetched_sha TEXT    NULL,
    last_error  TEXT    NULL,
    last_check  TEXT    NULL
) STRICT;
