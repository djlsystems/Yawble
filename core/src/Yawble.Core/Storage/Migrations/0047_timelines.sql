-- 0047 time accounting: every card's state changes with their time and category, so a card and a workflow can
-- say how long they took (created -> last done) and where that time went (Board/Timeline.cs computes it).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- When the card first had a member and was queued or told (its planned time is created_at).
ALTER TABLE board_cards ADD COLUMN started_at TEXT NULL;
-- 1: the card's timeline is still to be filled in from its board_trail lines (cards made before this step), done the
-- first time anything reads or writes it. timeline_rebuilt: it was filled in that way ("rebuilt from history").
ALTER TABLE board_cards ADD COLUMN timeline_rebuild INTEGER NOT NULL DEFAULT 0;
ALTER TABLE board_cards ADD COLUMN timeline_rebuilt INTEGER NOT NULL DEFAULT 0;
UPDATE board_cards SET timeline_rebuild = 1;

-- One line of a timeline. kind:
--   state   the card's category from at on (category, done: it is in Done; until: a Scheduled state's wake time,
--           after which the card is Done again when done = 1, else Idle);
--   stall   an agent run showed no activity (the idle watchdog's signal) from at to until: Stalled;
--   hold    an agent run waited for the heavy-work lease from at to until: Waiting for a slot;
--   marker  a moment on the axis (marker: handback, decision-asked, decision-answered, watchdog-end, expired).
-- card_id is NULL for a stall, hold or marker of a member that has no card in the workflow (the Manager's own runs):
-- those count toward the workflow only.
CREATE TABLE board_timeline (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    card_id  INTEGER NULL,
    workflow INTEGER NULL,
    member   TEXT    NULL,
    kind     TEXT    NOT NULL CHECK (kind IN ('state', 'stall', 'hold', 'marker')),
    at       TEXT    NOT NULL,
    until    TEXT    NULL,
    category TEXT    NULL,
    done     INTEGER NOT NULL DEFAULT 0,
    marker   TEXT    NULL,
    rebuilt  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX board_timeline_card ON board_timeline (card_id, at);
CREATE INDEX board_timeline_workflow ON board_timeline (workflow, at);
