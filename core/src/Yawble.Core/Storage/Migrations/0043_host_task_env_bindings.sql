-- 0043 a host task can take environment variables from Connections (B023).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- env_from_connections: a JSON array of {name, connectionId, field, connectionName}: which variable comes from which
-- Connection's secret field. It holds where a value is, NEVER a value: the secret stays in the credential store and is read
-- when a run starts. '[]' for a task with none, and for every task from before 0043.
ALTER TABLE host_tasks ADD COLUMN env_from_connections TEXT NOT NULL DEFAULT '[]';
