-- 0006 the UI's settings. What the screens edit that the bus tables do not hold.
-- No foreign keys to 0002's tables, so this does not depend on the order of the other migrations; the core
-- deletes a member's and a team's rows here when it removes them.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- The repository a team works in (picked when the team is created; per-member clones are in 0007).
CREATE TABLE team_settings (
    team_id    INTEGER PRIMARY KEY,
    repo_path  TEXT    NULL,
    updated_at TEXT    NOT NULL
) STRICT;

-- The member's model (empty: the CLI's default) and which credential mode its runs use:
-- 'default' follows the app's switch for its CLI; otherwise 'subscription' or 'apiKey' (Claude Code),
-- 'login' or 'apiKey' (Grok).
CREATE TABLE member_settings (
    member_id  INTEGER PRIMARY KEY,
    model      TEXT    NOT NULL DEFAULT '',
    credential TEXT    NOT NULL DEFAULT 'default',
    updated_at TEXT    NOT NULL
) STRICT;
