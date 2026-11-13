-- 0004 credentials, gateway and transcripts.
-- No secret is ever stored in this database: the credential VALUES live in the OS store (Credential Manager,
-- Keychain, Secret Service) or the owner-only 0600 fallback file, and the Grok session in the host Grok home.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- Which credential each vendor uses (the switch) and when it was set, so the app can warn before a
-- Claude setup-token (about a year) runs out. provider: 'claude' (mode 'subscription' | 'apikey') or
-- 'grok' (mode 'session' | 'apikey').
CREATE TABLE credential_settings (
    provider   TEXT PRIMARY KEY NOT NULL CHECK (provider IN ('claude', 'grok')),
    mode       TEXT NOT NULL,
    issued_at  TEXT NULL,
    updated_at TEXT NOT NULL,
    CHECK ((provider = 'claude' AND mode IN ('subscription', 'apikey')) OR (provider = 'grok' AND mode IN ('session', 'apikey')))
) STRICT;

-- The gateway log: every decision the per-run gateway takes that a person may need to see on the card
-- (denials, unauthorised calls, CONNECTs, upstream credential failures). Never a credential value.
-- kind: forward | path-denied | host-denied | connect-allowed | connect-denied | unauthorized | upstream-error |
--       credential-problem | bad-request. guardrail = 1 for events that indicate an attempt to leave the fence.
CREATE TABLE gateway_events (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id     INTEGER NULL,
    member     TEXT    NULL,
    at         TEXT    NOT NULL,
    kind       TEXT    NOT NULL,
    guardrail  INTEGER NOT NULL DEFAULT 0,
    vhost      TEXT    NULL,
    method     TEXT    NULL,
    target     TEXT    NULL,
    status     INTEGER NULL,
    detail     TEXT    NULL
) STRICT;
CREATE INDEX ix_gateway_events_run ON gateway_events(run_id, id);
CREATE INDEX ix_gateway_events_guardrail ON gateway_events(guardrail, id) WHERE guardrail = 1;

-- One transcript per run: the raw output of the agent's CLI, appended to an owner-only file on disk.
CREATE TABLE transcripts (
    run_id     INTEGER PRIMARY KEY NOT NULL,
    member     TEXT    NOT NULL,
    path       TEXT    NOT NULL,
    started_at TEXT    NOT NULL,
    ended_at   TEXT    NULL,
    bytes      INTEGER NOT NULL DEFAULT 0
) STRICT;
