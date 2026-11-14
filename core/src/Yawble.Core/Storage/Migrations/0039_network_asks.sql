-- 0039 network asks: an agent was refused a host (the run's gateway, or its request_host tool), and the person decides.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- One pending ask per (team, member, host): a repeat while it waits only counts up (count, last_seen). The person's
-- answer closes it: allowed for the agent, its team or everywhere (scope), or denied. A denied host is not asked again
-- for that member for 24 hours (decided_at). method and path_sample say what was refused when the agent gave no reason
-- (CONNECT, or the method and a short path without its query). workflow and causation_seq are the agent's workflow when
-- it asked with request_host (NULL for a refusal by the gateway).
CREATE TABLE network_asks (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    at            TEXT    NOT NULL,
    team_id       INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    member        TEXT    NOT NULL,
    host          TEXT    NOT NULL,
    reason        TEXT    NULL,
    method        TEXT    NULL,
    path_sample   TEXT    NULL,
    count         INTEGER NOT NULL DEFAULT 1,
    last_seen     TEXT    NOT NULL,
    workflow      INTEGER NULL,
    causation_seq INTEGER NULL,
    state         TEXT    NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'allowed', 'denied')),
    scope         TEXT    NULL CHECK (scope IS NULL OR scope IN ('agent', 'team', 'everywhere')),
    decided_by    TEXT    NULL,
    decided_via   TEXT    NULL,
    decided_at    TEXT    NULL,
    note          TEXT    NULL
) STRICT;
CREATE UNIQUE INDEX ux_network_asks_pending ON network_asks(team_id, member, host) WHERE state = 'pending';
CREATE INDEX ix_network_asks_member_host ON network_asks(member, host, state);
CREATE INDEX ix_network_asks_at ON network_asks(at);
