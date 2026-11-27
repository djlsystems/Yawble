-- 0063 team environment values are encrypted at rest. teams.env stays a JSON object of name -> value; each value is
-- text that starts with "enc:v1:" (AES-256-GCM, sealed by Teams.TeamEnvVault under a key kept in the computer's
-- credential store). The app seals the values at each start, not this step: SQL cannot reach the key.
-- This step changes no table. It is recorded so that a version of the app that reads teams.env as plain values does
-- not open a database that holds sealed ones.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
SELECT 1;
