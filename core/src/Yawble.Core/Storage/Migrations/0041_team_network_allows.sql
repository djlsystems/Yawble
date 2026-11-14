-- 0041 a team's network allow list, and network asks keyed by the member's id.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- team_network_allows: "Allow for this team" (a network ask's answer) writes one row per (team, host), with who added it
-- and when. At launch every member run of the team gets these hosts as if they were in its loosen block, through the
-- policy merger's floor rules (PolicyStore.Effective). Hosts the old "Allow for this team" wrote into members' loosen
-- blocks stay there as they are. Remove (policy.removeTeamHost) deletes the row; the next run no longer has the host.
CREATE TABLE team_network_allows (
    team_id   INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    host      TEXT    NOT NULL,
    added_by  TEXT    NOT NULL,
    added_via TEXT    NULL,
    added_at  TEXT    NOT NULL,
    PRIMARY KEY (team_id, host)
) STRICT;

-- network_asks gains member_id (the asking member's id: the 24-hour deny is that member's, so a later hire with the same
-- name is asked again) and the 'closed' state (the member was removed from the team while the ask waited). Rebuilt
-- because SQLite cannot change a CHECK; rows are copied as they are, member_id filled from the members still there.
CREATE TABLE network_asks_v2 (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    at            TEXT    NOT NULL,
    team_id       INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member        TEXT    NOT NULL,
    member_id     INTEGER NULL,
    host          TEXT    NOT NULL,
    reason        TEXT    NULL,
    method        TEXT    NULL,
    path_sample   TEXT    NULL,
    count         INTEGER NOT NULL DEFAULT 1,
    last_seen     TEXT    NOT NULL,
    workflow      INTEGER NULL,
    causation_seq INTEGER NULL,
    state         TEXT    NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'allowed', 'denied', 'closed')),
    scope         TEXT    NULL CHECK (scope IS NULL OR scope IN ('agent', 'team', 'everywhere')),
    decided_by    TEXT    NULL,
    decided_via   TEXT    NULL,
    decided_at    TEXT    NULL,
    note          TEXT    NULL
) STRICT;
INSERT INTO network_asks_v2 (id, at, team_id, member, member_id, host, reason, method, path_sample, count, last_seen, workflow, causation_seq,
                             state, scope, decided_by, decided_via, decided_at, note)
    SELECT a.id, a.at, a.team_id, a.member,
           (SELECT m.id FROM members m JOIN teams t ON t.id = m.team_id WHERE t.id = a.team_id AND t.name || '/' || m.name = a.member),
           a.host, a.reason, a.method, a.path_sample, a.count, a.last_seen, a.workflow, a.causation_seq,
           a.state, a.scope, a.decided_by, a.decided_via, a.decided_at, a.note
      FROM network_asks a;
DROP TABLE network_asks;
ALTER TABLE network_asks_v2 RENAME TO network_asks;
CREATE UNIQUE INDEX ux_network_asks_pending ON network_asks(team_id, member, host) WHERE state = 'pending';
CREATE INDEX ix_network_asks_member_host ON network_asks(member, host, state);
CREATE INDEX ix_network_asks_member_id ON network_asks(member_id, host, state);
CREATE INDEX ix_network_asks_at ON network_asks(at);
