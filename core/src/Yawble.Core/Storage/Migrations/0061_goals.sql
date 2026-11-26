-- 0061 goals: the person's own names for what deliverables are for. A goal has a name (unique ignoring case) and a
-- description. A deliverable (its root message's seq) has at most one goal: deliverable_goals keys on the deliverable, and
-- a deliverable with no row is Unassigned. Deleting a goal unassigns its deliverables (the rows go with it).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
CREATE TABLE goals (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    description TEXT    NOT NULL DEFAULT '',
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
) STRICT;

CREATE TABLE deliverable_goals (
    deliverable INTEGER PRIMARY KEY,
    goal_id     INTEGER NOT NULL REFERENCES goals(id) ON DELETE CASCADE,
    set_at      TEXT    NOT NULL
) STRICT;
CREATE INDEX ix_deliverable_goals_goal ON deliverable_goals(goal_id);
