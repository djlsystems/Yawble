-- 0013 the Concierge keeps tabs on teams: a watch is a follow-up the Concierge promised in a conversation (a workflow it
-- dispatched, or a team the person wants watched). The core matches team events against the active watches with no
-- agent running and posts each match into the conversation as an 'update' message; unread counts them until the
-- person opens the conversation. A watch with follow_up also wakes the Concierge to review what arrived.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- concierge_messages gains the 'update' role and what an update was about (team, kind, the bus message's seq).
CREATE TABLE concierge_messages_v2 (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    conversation_id TEXT NOT NULL REFERENCES concierge_conversations(id) ON DELETE CASCADE,
    role            TEXT NOT NULL CHECK (role IN ('user', 'assistant', 'update')),
    text            TEXT NOT NULL,
    at              TEXT NOT NULL,
    state           TEXT NULL CHECK (state IS NULL OR state IN ('done', 'failed', 'stopped')),
    error           TEXT NULL,
    tools           TEXT NOT NULL DEFAULT '[]',
    cli             TEXT NULL,
    run_key         TEXT NULL,
    team            TEXT NULL,
    kind            TEXT NULL,
    seq             INTEGER NULL
);
INSERT INTO concierge_messages_v2 (id, conversation_id, role, text, at, state, error, tools, cli, run_key)
    SELECT id, conversation_id, role, text, at, state, error, tools, cli, run_key FROM concierge_messages;
DROP TABLE concierge_messages;
ALTER TABLE concierge_messages_v2 RENAME TO concierge_messages;
CREATE INDEX concierge_messages_by_conversation ON concierge_messages(conversation_id, id);

ALTER TABLE concierge_conversations ADD COLUMN unread INTEGER NOT NULL DEFAULT 0;

CREATE TABLE concierge_watches (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    conversation_id TEXT NOT NULL REFERENCES concierge_conversations(id) ON DELETE CASCADE,
    team            TEXT NOT NULL,
    workflow        INTEGER NULL,            -- NULL: the whole team
    what            TEXT NOT NULL,           -- what is being watched for, in the Concierge's words
    follow_up       INTEGER NOT NULL DEFAULT 0,
    since_seq       INTEGER NOT NULL,        -- bus messages after this one are still to be matched
    created_at      TEXT NOT NULL,
    expires_at      TEXT NOT NULL,
    state           TEXT NOT NULL CHECK (state IN ('active', 'done', 'cancelled', 'expired')),
    ended_at        TEXT NULL
);
CREATE INDEX concierge_watches_by_state ON concierge_watches(state, conversation_id);
