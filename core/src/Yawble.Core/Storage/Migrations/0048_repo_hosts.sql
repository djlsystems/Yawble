-- 0048 repository providers: the person's per-repository override of where a repository is hosted.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- repo: "main" for the team's primary repository (team_repos), else an extra repository's name (team_extra_repos).
-- provider: github, gitlab, bitbucket, azuredevops, gitea or none; NULL: detected from the origin.
-- link_pattern: web link templates with {base} {path} {commit} {branch} {file} {from} {to} {pr}; NULL: the provider's own.
-- No row: everything detected.
CREATE TABLE team_repo_hosts (
    team_id      INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    repo         TEXT    NOT NULL,
    provider     TEXT    NULL CHECK (provider IN ('github', 'gitlab', 'bitbucket', 'azuredevops', 'gitea', 'none')),
    link_pattern TEXT    NULL,
    updated_at   TEXT    NOT NULL,
    PRIMARY KEY (team_id, repo)
) STRICT;