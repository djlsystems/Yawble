-- 0045 workflow expiry: an open workflow nobody works is closed by the app as "expired" after the team's
-- workflowExpiryDays without activity; closing a workflow (any way) ends its open cards.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- The expiry itself is a message on the log (workflow.expired, source host), like completed and closed: new activity
-- reopens it the same way. What is not on the log lives here.

-- The team's expiry in days (1-90). NULL: the default, 3 days.
ALTER TABLE team_settings ADD COLUMN workflow_expiry_days INTEGER NULL;

-- Per workflow (its root seq): when the person last pressed Extend (the idle clock starts again from it), and the one
-- expiry warning for a workflow waiting on the person's answer: warned_for is the idle-since time it was given for (a
-- later idle stretch warns again), warned_question the question it names.
CREATE TABLE workflow_expiry (
    workflow        INTEGER PRIMARY KEY,
    extended_at     TEXT    NULL,
    warned_for      TEXT    NULL,
    warned_at       TEXT    NULL,
    warned_question INTEGER NULL
) STRICT;

-- A card's end: when its workflow ended while the card was not ended yet, and how (completed, closed or expired).
-- New work on the card (it is told again) clears both.
ALTER TABLE board_cards ADD COLUMN ended_at TEXT NULL;
ALTER TABLE board_cards ADD COLUMN ended_how TEXT NULL;
