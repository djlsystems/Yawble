-- 0058 the built-in Email integration (B133, card 590): a mailbox (an email Connection) offered to teams, whose scripts read it through
-- the integration's actions (list, get, search, thread, markRead, move; send and reply later, under a send policy).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- email_integrations: one per mailbox the person offers; name is how a script member's binding names it. connection_id is the email
-- Connection it signs in with (NULL when that Connection was removed: the actions then refuse). The password is never here: it stays in
-- the credential store under the Connection. enabled_all offers it to every team; email_integration_teams to some.
-- The send policy (phase 2: recipient allowlist with levels, per-run and per-day caps) is a later step's columns or table.
CREATE TABLE email_integrations (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    connection_id INTEGER NULL REFERENCES connections(id) ON DELETE SET NULL,
    enabled_all   INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL
) STRICT;

CREATE TABLE email_integration_teams (
    email_id INTEGER NOT NULL REFERENCES email_integrations(id) ON DELETE CASCADE,
    team_id  INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    PRIMARY KEY (email_id, team_id)
) STRICT;
