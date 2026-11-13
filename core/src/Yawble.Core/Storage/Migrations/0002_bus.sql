-- 0002 the bus.
-- Teams and members, the message log, subscriptions, cursors, pending deliveries, runs and the global run_queue.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

CREATE TABLE teams (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    created_at TEXT    NOT NULL
) STRICT;

-- A member is addressed as "<team>/<name>". role 'manager' gets the manager role text and subscriptions;
-- exactly one per team (ux_members_manager). cli is the agent CLI preset ('claude', 'grok', ...).
CREATE TABLE members (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    team_id      INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    name         TEXT    NOT NULL COLLATE NOCASE,
    role         TEXT    NOT NULL CHECK (role IN ('manager', 'member')),
    cli          TEXT    NOT NULL DEFAULT 'claude',
    instructions TEXT    NOT NULL DEFAULT '',
    created_at   TEXT    NOT NULL,
    UNIQUE (team_id, name)
) STRICT;
CREATE UNIQUE INDEX ux_members_manager ON members(team_id) WHERE role = 'manager';

-- The log. seq, correlation_id and depth are assigned by the log from causation_seq, never by a publisher.
-- correlation_id is the seq of the workflow's root message (a root correlates to itself).
CREATE TABLE messages (
    seq            INTEGER PRIMARY KEY AUTOINCREMENT,
    type           TEXT    NOT NULL,
    payload        TEXT    NOT NULL,
    source         TEXT    NOT NULL,
    correlation_id INTEGER NOT NULL DEFAULT 0,
    causation_seq  INTEGER NULL REFERENCES messages(seq),
    depth          INTEGER NOT NULL,
    occurred_at    TEXT    NOT NULL
) STRICT;
CREATE INDEX ix_messages_seq_type ON messages(seq, type);
CREATE INDEX ix_messages_correlation ON messages(correlation_id, seq);

-- What each subscriber ("<team>/<member>") is woken by, matched on the exact type.
CREATE TABLE subscriptions (
    subscriber TEXT NOT NULL COLLATE NOCASE,
    type       TEXT NOT NULL,
    PRIMARY KEY (subscriber, type)
) STRICT;

-- Where each subscriber has read up to. Moves in the same transaction that writes the pending deliveries.
CREATE TABLE cursors (
    subscriber TEXT    PRIMARY KEY COLLATE NOCASE,
    position   INTEGER NOT NULL
) STRICT;

-- Accepted and not finished. started = 0: re-offered after a restart (at least once until a run starts).
-- started = 1: belongs to run_id; after a restart that run is reported interrupted, never re-run (at most once).
CREATE TABLE pending_deliveries (
    subscriber TEXT    NOT NULL COLLATE NOCASE,
    seq        INTEGER NOT NULL REFERENCES messages(seq),
    started    INTEGER NOT NULL DEFAULT 0,
    run_id     INTEGER NULL,
    PRIMARY KEY (subscriber, seq)
) STRICT;

-- One row per run (one sandbox). queue_id is the run_queue entry it was admitted from,
-- so admission order is checkable: runs ordered by id have increasing queue_id (strict FIFO).
CREATE TABLE runs (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    member_id      INTEGER NOT NULL REFERENCES members(id) ON DELETE CASCADE,
    queue_id       INTEGER NOT NULL,
    causation_seq  INTEGER NOT NULL REFERENCES messages(seq),
    correlation_id INTEGER NOT NULL,
    items          INTEGER NOT NULL,
    state          TEXT    NOT NULL CHECK (state IN ('running', 'completed', 'failed', 'interrupted')),
    queued_at      TEXT    NOT NULL,
    started_at     TEXT    NOT NULL,
    ended_at       TEXT    NULL,
    reason         TEXT    NULL
) STRICT;
CREATE INDEX ix_runs_state ON runs(state);
CREATE INDEX ix_runs_member ON runs(member_id, id);

-- The global run queue: one shared FIFO for every team. id order IS admission order. At most one entry
-- per member: a queued member keeps collecting instructions and takes them as one larger batch when admitted.
CREATE TABLE run_queue (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    member_id   INTEGER NOT NULL UNIQUE REFERENCES members(id) ON DELETE CASCADE,
    enqueued_at TEXT    NOT NULL
) STRICT;
