-- 0057 email Connections (B133): connections.kind may be 'email', a mailbox signed in over IMAP and SMTP (Email/EmailConnection.cs).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- The kind's CHECK cannot be altered in place, so the table is rebuilt with every row, column and id kept. apis and
-- mcp_servers reference connections(id) ON DELETE SET NULL, and dropping the old table clears those references (the
-- migrator runs with foreign keys on, inside a transaction), so they are saved first and put back after the rename.
-- An email Connection's fields are address, imapHost, imapPort, smtpHost, smtpPort and user (non-secret, in the row) and
-- password (secret: only in the credential store, never here).
CREATE TEMP TABLE keep_api_connections_0057 AS SELECT id, connection_id FROM apis WHERE connection_id IS NOT NULL;
CREATE TEMP TABLE keep_mcp_connections_0057 AS SELECT id, connection_id FROM mcp_servers WHERE connection_id IS NOT NULL;

CREATE TABLE connections_0057 (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    store_key     TEXT    NOT NULL UNIQUE,
    kind          TEXT    NOT NULL CHECK (kind IN ('secret', 'oauth', 'website', 'email')),
    fields        TEXT    NOT NULL,
    status        TEXT    NOT NULL DEFAULT 'untested' CHECK (status IN ('untested', 'works', 'needs_reconnect')),
    status_reason TEXT    NULL,
    tested_at     TEXT    NULL,
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL
) STRICT;
INSERT INTO connections_0057 (id, name, store_key, kind, fields, status, status_reason, tested_at, created_at, updated_at)
SELECT id, name, store_key, kind, fields, status, status_reason, tested_at, created_at, updated_at FROM connections;
-- ids are never reused: the new table's sequence starts where the old one was
INSERT INTO sqlite_sequence (name, seq) SELECT 'connections_0057', 0 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'connections_0057');
UPDATE sqlite_sequence SET seq = max(seq, coalesce((SELECT seq FROM sqlite_sequence WHERE name = 'connections'), 0)) WHERE name = 'connections_0057';
DROP TABLE connections;
ALTER TABLE connections_0057 RENAME TO connections;

UPDATE apis SET connection_id = (SELECT k.connection_id FROM keep_api_connections_0057 k WHERE k.id = apis.id)
WHERE id IN (SELECT id FROM keep_api_connections_0057);
UPDATE mcp_servers SET connection_id = (SELECT k.connection_id FROM keep_mcp_connections_0057 k WHERE k.id = mcp_servers.id)
WHERE id IN (SELECT id FROM keep_mcp_connections_0057);
DROP TABLE keep_api_connections_0057;
DROP TABLE keep_mcp_connections_0057;
