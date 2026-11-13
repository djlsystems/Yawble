-- 0012 the Concierge: the person's chats with an agent that knows every team. Each conversation keeps its messages
-- (the person's and the agent's answers); a turn is one headless run of the conversation's agent CLI. The notes are the
-- Concierge's own memory across conversations.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
CREATE TABLE concierge_conversations (
    id         TEXT PRIMARY KEY,
    title      TEXT NOT NULL,
    cli        TEXT NOT NULL,
    team_id    INTEGER NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE concierge_messages (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    conversation_id TEXT NOT NULL REFERENCES concierge_conversations(id) ON DELETE CASCADE,
    role            TEXT NOT NULL CHECK (role IN ('user', 'assistant')),
    text            TEXT NOT NULL,
    at              TEXT NOT NULL,
    state           TEXT NULL CHECK (state IS NULL OR state IN ('done', 'failed', 'stopped')),
    error           TEXT NULL,
    tools           TEXT NOT NULL DEFAULT '[]',
    cli             TEXT NULL,
    run_key         TEXT NULL
);

CREATE INDEX concierge_messages_by_conversation ON concierge_messages(conversation_id, id);

CREATE TABLE concierge_notes (
    id         INTEGER PRIMARY KEY CHECK (id = 1),
    text       TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
