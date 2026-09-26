using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// The message log's tables, as one step. SchemaMigrator applies it and records it.
///
/// ONE STEP holding the tables as they stand. A database that recorded step ids this build does
/// not hold is refused as "written by a newer build", and the answer is to recreate the volume. A
/// change is a NEW step after this one, and this id is permanent.
///
/// THE PRAGMAS ARE NOT HERE. journal_mode, foreign_keys and busy_timeout are set by the migrator on
/// every connection it opens: the last two are per-connection and a recorded step would set them
/// once, leaving every later connection without them.
/// </summary>
public static class MessageSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "messages-001",
            """
            CREATE TABLE messages (
                seq            INTEGER PRIMARY KEY AUTOINCREMENT,
                type           TEXT    NOT NULL,
                payload        TEXT    NOT NULL,
                source         TEXT    NOT NULL,

                -- The seq of the message that began this workflow. Self-referential for a root,
                -- which is what lets every message belong to exactly one workflow with no separate
                -- id to generate. Written after the insert, because it can be this row's own seq.
                correlation_id INTEGER NOT NULL DEFAULT 0,

                causation_seq  INTEGER NULL REFERENCES messages(seq),
                depth          INTEGER NOT NULL,
                occurred_at    TEXT    NOT NULL
            );

            -- The read path a subscriber takes on every poll: "after my cursor, of these types,
            -- oldest first". Without this it is a table scan per subscriber per poll.
            CREATE INDEX ix_messages_seq_type ON messages(seq, type);

            -- Following one workflow, which is what the UI groups by.
            CREATE INDEX ix_messages_correlation ON messages(correlation_id, seq);

            -- Where each subscriber has read up to. The single row per subscriber IS the change
            -- that makes more than one consumer possible -- see ICursors.
            CREATE TABLE cursors (
                subscriber TEXT    PRIMARY KEY,
                position   INTEGER NOT NULL
            );

            CREATE TABLE subscriptions (
                subscriber TEXT NOT NULL,
                type       TEXT NOT NULL,
                PRIMARY KEY (subscriber, type)
            );

            -- What each container has accepted and not finished. COLLATE NOCASE because
            -- ContainerId equality is case-insensitive.
            CREATE TABLE pending_deliveries (
                subscriber TEXT    NOT NULL COLLATE NOCASE,
                seq        INTEGER NOT NULL,
                started    INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (subscriber, seq)
            );
            """),
    ];
}
