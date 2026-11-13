-- 0014 the Board and the backlog. A card is one piece of a team's work: born when a member is told
-- something (one card per member per workflow), or planned by the Manager unassigned and claimed by a tell that names
-- it. Its status follows the bus (the projector, Board/BoardProjector.cs); its lane follows its status until a person
-- or the Manager moves it. The backlog is the person's list of work not yet given to a team.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
CREATE TABLE board_cards (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    team             TEXT NOT NULL,
    member           TEXT NULL,
    item             TEXT NULL,
    title            TEXT NOT NULL,
    body             TEXT NOT NULL DEFAULT '',
    status           TEXT NOT NULL DEFAULT 'queued'
                     CHECK (status IN ('queued', 'running', 'blocked', 'needs-decision', 'failed', 'completed', 'handback', 'done')),
    lane             TEXT NOT NULL DEFAULT 'todo' CHECK (lane IN ('todo', 'in-progress', 'blocked', 'done')),
    placed           INTEGER NOT NULL DEFAULT 0,
    awaiting_manager INTEGER NOT NULL DEFAULT 0,
    workflow         INTEGER NULL,
    created_at       TEXT NOT NULL,
    updated_at       TEXT NOT NULL
);
CREATE INDEX board_cards_team ON board_cards (team, lane);
CREATE INDEX board_cards_member ON board_cards (team, member, workflow);

CREATE TABLE board_trail (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    card_id INTEGER NOT NULL REFERENCES board_cards(id) ON DELETE CASCADE,
    at      TEXT NOT NULL,
    source  TEXT NOT NULL,
    kind    TEXT NOT NULL,
    text    TEXT NOT NULL
);
CREATE INDEX board_trail_card ON board_trail (card_id, id);

CREATE TABLE backlog_items (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    team                TEXT NULL,
    title               TEXT NOT NULL,
    body                TEXT NOT NULL DEFAULT '',
    state               TEXT NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'ready', 'declared', 'implemented')),
    position            REAL NOT NULL,
    archived_at         TEXT NULL,
    dispatched_team     TEXT NULL,
    dispatched_workflow INTEGER NULL,
    dispatched_at       TEXT NULL,
    created_at          TEXT NOT NULL,
    updated_at          TEXT NOT NULL
);

-- How far the projector has read the bus.
CREATE TABLE board_cursor (
    id  INTEGER PRIMARY KEY CHECK (id = 1),
    seq INTEGER NOT NULL
);
