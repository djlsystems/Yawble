-- 0025 which of the team's repositories an integration call runs in (0022 made teams have more than one).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- A CLI runs on the host, in the person's own checkout of a team repository: its main one (NULL) or one of its others
-- by name. An asked call keeps the name, so the approved call runs where it was asked to.
ALTER TABLE integration_asks ADD COLUMN repo TEXT NULL;
