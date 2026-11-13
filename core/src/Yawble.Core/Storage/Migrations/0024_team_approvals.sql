-- 0024 standing approvals: what the person has approved for a team once and for all.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- When the person answers a question with "Always approve for this team", they say in a few words what is approved
-- (e.g. "hiring members for the approved plan"). Every run of the team's Manager is told the list, so it goes ahead
-- and reports instead of asking again. The person removes one in the team's Manage dialog.
CREATE TABLE team_approvals (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id    INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    text       TEXT    NOT NULL,
    created_at TEXT    NOT NULL,
    created_by TEXT    NULL
) STRICT;
CREATE INDEX ix_team_approvals_team ON team_approvals(team_id);
