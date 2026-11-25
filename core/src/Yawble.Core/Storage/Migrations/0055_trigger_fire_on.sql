-- 0055 folder trigger "Fire on": which settled changes fire a folder trigger. 'added' (files added; a new
-- trigger's default), 'added-or-changed' (added, or changed in size), 'any' (added, removed or changed: the behaviour
-- before this step). Folder triggers saved before this keep 'any'; other kinds leave it NULL.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
ALTER TABLE triggers ADD COLUMN fire_on TEXT NULL CHECK (fire_on IS NULL OR fire_on IN ('added', 'added-or-changed', 'any'));
UPDATE triggers SET fire_on = 'any' WHERE watch_root IS NOT NULL;
