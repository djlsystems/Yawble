-- 0023 every dispatch of a backlog item, not only the latest.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- backlog_items keeps the latest dispatch (dispatched_team / _workflow / _at, 0014) for the list; this is the history
-- the item's page shows: which team, which workflow (the bus correlation id), when and by whom. A workflow's state,
-- cards and usage are read live from the bus, the board and usage_events.
CREATE TABLE backlog_dispatches (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id       INTEGER NOT NULL REFERENCES backlog_items(id) ON DELETE CASCADE,
    team          TEXT    NOT NULL,
    workflow      INTEGER NOT NULL,
    dispatched_at TEXT    NOT NULL,
    dispatched_by TEXT    NULL
) STRICT;
CREATE INDEX ix_backlog_dispatches_item ON backlog_dispatches(item_id);

-- The dispatches made before this step: one each, from the item's latest.
INSERT INTO backlog_dispatches (item_id, team, workflow, dispatched_at)
SELECT id, dispatched_team, dispatched_workflow, dispatched_at FROM backlog_items
WHERE dispatched_team IS NOT NULL AND dispatched_workflow IS NOT NULL AND dispatched_at IS NOT NULL;
