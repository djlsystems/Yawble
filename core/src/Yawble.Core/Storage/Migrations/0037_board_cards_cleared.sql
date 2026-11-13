-- 0037 "Clear done": the person hides a Done card from the board view without deleting it. A board-view flag only:
-- workflows, backlog counts, Monitor, the Concierge and the agents' board tool still see the card. Moving the card out
-- of Done clears it again (BoardStore.Save).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
ALTER TABLE board_cards ADD COLUMN cleared_at TEXT NULL;
