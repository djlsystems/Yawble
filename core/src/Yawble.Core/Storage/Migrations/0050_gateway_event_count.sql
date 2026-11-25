-- 0050 A host denied again in the same run adds to its first host-denied row instead of writing another: count
-- is how many times the row's event happened (1 for every other row, and for every row written before this step).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
ALTER TABLE gateway_events ADD COLUMN count INTEGER NOT NULL DEFAULT 1;
