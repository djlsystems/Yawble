-- 0015 provenance: who started each piece of work, how, and when, and what the model requests used, so
-- cost and outcomes can be worked out from real data; nothing here limits or prices anything.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- via:  how it came in. app (this computer's window), cloud (a viewer through the Cloud Link), concierge (the Concierge
--       acting for the person), trigger (a schedule), agent (a team member or Manager), host (the app itself).
-- user: the person behind it: the account signed in to this computer, or the cloud viewer's account. NULL for an agent
--       or the app itself; their work carries the origin's user instead.
-- origin_*: the first message of the chain this one belongs to (its causation root): who started everything.
ALTER TABLE messages ADD COLUMN via TEXT NULL;
ALTER TABLE messages ADD COLUMN user_name TEXT NULL;
ALTER TABLE messages ADD COLUMN origin_seq INTEGER NULL;
ALTER TABLE messages ADD COLUMN origin_via TEXT NULL;
ALTER TABLE messages ADD COLUMN origin_user TEXT NULL;
CREATE INDEX ix_messages_origin ON messages(origin_seq);

-- A run that is not a bus run (a Concierge turn, an interactive terminal, an agent test) records who started it; a bus
-- run's origin is its causation message's.
ALTER TABLE run_records ADD COLUMN via TEXT NULL;
ALTER TABLE run_records ADD COLUMN user_name TEXT NULL;

-- Who last saved a trigger: its fires carry that person as their origin.
ALTER TABLE triggers ADD COLUMN saved_by TEXT NULL;
ALTER TABLE triggers ADD COLUMN saved_via TEXT NULL;

-- One row per model request the run gateway forwarded, with the provider's own usage figures (as reported, not
-- priced). usage is the provider's usage object as it came; the columns are what could be read from it.
CREATE TABLE usage_events (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    at                 TEXT    NOT NULL,
    run_key            TEXT    NULL,
    run_number         INTEGER NULL,
    member             TEXT    NULL,
    vhost              TEXT    NOT NULL,
    path               TEXT    NOT NULL,
    status             INTEGER NOT NULL,
    model              TEXT    NULL,
    input_tokens       INTEGER NULL,
    output_tokens      INTEGER NULL,
    cache_read_tokens  INTEGER NULL,
    cache_write_tokens INTEGER NULL,
    usage              TEXT    NULL
) STRICT;
CREATE INDEX ix_usage_events_run ON usage_events(run_key);
CREATE INDEX ix_usage_events_at ON usage_events(at);

-- Every call that changes something, from this computer's window or a cloud viewer: who, how, what, and how it ended.
-- target holds only identifying fields (team, member, id, name, ...), never the call's other parameters.
CREATE TABLE audit_log (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    at        TEXT    NOT NULL,
    via       TEXT    NOT NULL,
    user_name TEXT    NULL,
    method    TEXT    NOT NULL,
    target    TEXT    NULL,
    outcome   TEXT    NOT NULL
) STRICT;
CREATE INDEX ix_audit_log_at ON audit_log(at);
