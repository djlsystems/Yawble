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

        // A batch item the agent DEFERRED goes back on this table as its own pending delivery,
        // un-started, naming the run it was deferred from (that run's `started` seq). NULL on every
        // other row. A restart re-offers it as its own run, as the live runtime does.
        new MigrationStep(
            "messages-002",
            """
            ALTER TABLE pending_deliveries ADD COLUMN deferred_from_run INTEGER NULL;
            """),

        // THE WAKES A MEMBER ASKED FOR AND THAT HAVE NOT FIRED: one row each, removed in the same
        // transaction as the row that fires, skips or cancels it, so a restart keeps every pending
        // wake and none is acted on twice. The log holds the history; this holds only what is due.
        new MigrationStep(
            "messages-003",
            """
            CREATE TABLE wakes (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                team         TEXT    NOT NULL COLLATE NOCASE,
                member       TEXT    NOT NULL COLLATE NOCASE,
                workflow     INTEGER NOT NULL,
                asked_seq    INTEGER NOT NULL,
                asked_at     TEXT    NOT NULL,
                due_at       TEXT    NOT NULL,
                note         TEXT    NOT NULL
            );

            CREATE INDEX ix_wakes_due ON wakes(due_at);
            CREATE INDEX ix_wakes_member ON wakes(team, member);
            CREATE INDEX ix_wakes_workflow ON wakes(workflow);
            """),

        // WHAT A RUNNING MEMBER'S WORKTREES DID, one row per read that found something, for the
        // Monitor's replay. Paths, actions, shas and subjects only: never a file's contents or a
        // diff. `at` is round-trip UTC text of one width, so ordering by it is ordering by time.
        // Pruned with the log (IMessageLog.DeleteAsync), removed with the team and by Delete memory.
        new MigrationStep(
            "messages-004",
            """
            CREATE TABLE file_changes (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                team         TEXT    NOT NULL COLLATE NOCASE,
                member       TEXT    NOT NULL COLLATE NOCASE,
                repo         TEXT    NOT NULL,
                at           TEXT    NOT NULL,
                correlation  INTEGER NULL,
                files_json   TEXT    NOT NULL,
                commits_json TEXT    NOT NULL,
                more         INTEGER NOT NULL
            );

            CREATE INDEX ix_file_changes_at ON file_changes(at, id);
            CREATE INDEX ix_file_changes_member ON file_changes(team, member);
            """),
    ];

    /// <summary>How <c>file_changes.at</c> spells an instant: round-trip UTC text of one width, so
    /// comparing the text compares the time.</summary>
    public static string FileChangeInstant(DateTimeOffset at) =>
        at.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
}
