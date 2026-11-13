-- 0036 the Yawble companion: external clients (e.g. Claude Code) paired with this app, and who they were in the audit.
-- companion_clients: one row per paired client name. key_hash is the SHA-256 (hex) of the client key; the key itself
--   is shown once at pairing and never stored. level is what the client may do (read < act < destructive).
--   A revoked client keeps its row (revoked_at) so the audit still names it; pairing the same name again replaces it.
-- audit_log.client: the paired client name when via = 'external' (user_name is the account at this computer).
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
CREATE TABLE companion_clients (
    id           INTEGER PRIMARY KEY,
    name         TEXT NOT NULL UNIQUE,
    key_hash     TEXT NOT NULL,
    level        TEXT NOT NULL CHECK(level IN ('read','act','destructive')),
    paired_at    TEXT NOT NULL,
    paired_by    TEXT NULL,
    last_used_at TEXT NULL,
    revoked_at   TEXT NULL
) STRICT;
ALTER TABLE audit_log ADD COLUMN client TEXT NULL;
