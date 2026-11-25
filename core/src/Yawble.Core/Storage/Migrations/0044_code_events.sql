-- 0044 the Monitor's code events: what really reached the team repository, read by monitor.codeEvents.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- One row per event: kind commit (a member's new commit on its branch, or the Manager's own on the team branch),
-- merge-team (a member branch merged into team/<team>), landed (the team branch merged into the base branch) or push
-- (a branch the app pushed to origin). Recorded by the host in the TEAM repository when a fetch-back, a Merge to main
-- or a publish moves a branch; never by running git in a member's clone.
--   member:      the member whose work it is (a commit's author, a merge-team's merged member); null when none.
--   branch:      the branch the commit is on / the merged member branch / the base branch / the pushed branch.
--   files:       JSON [{path, status (A|M|D|R), oldPath?}]; '[]' for a push.
--   at:          a commit's own committer time; the moment of the merge or push for the others (UTC round-trip text).
--   recorded_at: when the host recorded it (a commit made before a long run's end is recorded at that end).
--   workflow_id: the workflow whose run brought it in, when known.
--   extra:       JSON of kind-specific extras (into, branches), or null.
CREATE TABLE IF NOT EXISTS code_events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    kind        TEXT NOT NULL,
    team        TEXT NOT NULL,
    member      TEXT,
    branch      TEXT NOT NULL,
    sha         TEXT NOT NULL,
    message     TEXT NOT NULL DEFAULT '',
    files       TEXT NOT NULL DEFAULT '[]',
    at          TEXT NOT NULL,
    recorded_at TEXT NOT NULL,
    workflow_id INTEGER,
    extra       TEXT
);

-- The same commit on the same branch is one event, however often its branch is fetched back.
CREATE UNIQUE INDEX IF NOT EXISTS ux_code_events_key ON code_events(kind, team, branch, sha);
CREATE INDEX IF NOT EXISTS ix_code_events_team_at ON code_events(team, at);
