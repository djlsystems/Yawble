-- 0048 a card's run the person stopped (B097, card 437): the card's status can be 'stopped' (Board/BoardProjector.cs,
-- Lanes.Stopped). SQLite cannot change a CHECK in place, so board_cards is rebuilt with the same columns, rows, ids and
-- indexes. The migrator runs with foreign_keys ON inside a transaction (the pragma cannot be turned off there), so
-- dropping the old table cascades into board_trail: the trail is kept aside first and put back with its own ids.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
CREATE TEMP TABLE board_trail_0048 AS SELECT id, card_id, at, source, kind, text FROM board_trail;
CREATE TEMP TABLE board_seq_0048 AS SELECT name, seq FROM sqlite_sequence WHERE name IN ('board_cards', 'board_trail');

CREATE TABLE board_cards_0048 (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    team             TEXT NOT NULL,
    member           TEXT NULL,
    item             TEXT NULL,
    title            TEXT NOT NULL,
    body             TEXT NOT NULL DEFAULT '',
    status           TEXT NOT NULL DEFAULT 'queued'
                     CHECK (status IN ('queued', 'running', 'stopped', 'blocked', 'needs-decision', 'failed', 'completed', 'handback', 'done')),
    lane             TEXT NOT NULL DEFAULT 'todo' CHECK (lane IN ('todo', 'in-progress', 'blocked', 'done')),
    placed           INTEGER NOT NULL DEFAULT 0,
    awaiting_manager INTEGER NOT NULL DEFAULT 0,
    workflow         INTEGER NULL,
    created_at       TEXT NOT NULL,
    updated_at       TEXT NOT NULL,
    cleared_at       TEXT NULL,
    ended_at         TEXT NULL,
    ended_how        TEXT NULL,
    started_at       TEXT NULL,
    timeline_rebuild INTEGER NOT NULL DEFAULT 0,
    timeline_rebuilt INTEGER NOT NULL DEFAULT 0
);
INSERT INTO board_cards_0048 (id, team, member, item, title, body, status, lane, placed, awaiting_manager, workflow, created_at,
                              updated_at, cleared_at, ended_at, ended_how, started_at, timeline_rebuild, timeline_rebuilt)
    SELECT id, team, member, item, title, body, status, lane, placed, awaiting_manager, workflow, created_at,
           updated_at, cleared_at, ended_at, ended_how, started_at, timeline_rebuild, timeline_rebuilt
      FROM board_cards;
DROP TABLE board_cards;
ALTER TABLE board_cards_0048 RENAME TO board_cards;
CREATE INDEX board_cards_team ON board_cards (team, lane);
CREATE INDEX board_cards_member ON board_cards (team, member, workflow);

DELETE FROM board_trail;
INSERT INTO board_trail (id, card_id, at, source, kind, text) SELECT id, card_id, at, source, kind, text FROM board_trail_0048;

-- Ids are never reused: the AUTOINCREMENT counters stay where they were.
DELETE FROM sqlite_sequence WHERE name IN (SELECT name FROM board_seq_0048);
INSERT INTO sqlite_sequence (name, seq) SELECT name, seq FROM board_seq_0048;

DROP TABLE board_trail_0048;
DROP TABLE board_seq_0048;
