-- 0064 a team's folder list.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- team_folders: folders on this computer outside the members' own folders that every member of the team may read
-- (access 'read') or read and write ('readwrite'), with who added each and when. The person adds one in Team settings >
-- Folders (policy.addTeamFolder) or by answering a folder ask for the whole team. At launch every member run of the team
-- gets these folders as if they were in its loosen block (PolicyStore.Effective), members hired later too; a workflow
-- member's file steps get them as well. Remove (policy.removeTeamFolder) deletes the row; the next run no longer has it.
CREATE TABLE team_folders (
    team_id   INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    path      TEXT    NOT NULL,
    access    TEXT    NOT NULL CHECK (access IN ('read', 'readwrite')),
    added_by  TEXT    NOT NULL,
    added_via TEXT    NULL,
    added_at  TEXT    NOT NULL,
    PRIMARY KEY (team_id, path)
) STRICT;
