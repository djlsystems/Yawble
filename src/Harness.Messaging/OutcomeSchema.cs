using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// The accounting: what every finished run cost and how long every workflow took, kept apart from
/// the message log so that nothing which trims the log can take it.
///
/// <para>
/// THE LOG IS A MEMBER'S MEMORY; THIS IS THE BOOKS. Reset's "Delete memory" deletes a member's log
/// rows, and team deletion leaves every spend read asking by a team that is gone. Spend read from the
/// log went with them. These two tables are written with the log row they account for, in the same
/// transaction (<see cref="LedgerRows"/>), and nothing ever updates or deletes one of their rows:
/// the triggers below refuse it, so a later retention job cannot do it by accident either.
/// </para>
///
/// <para>
/// A MODULE OF ITS OWN, in the same database file as <c>messages</c> because its rows are written
/// inside the append's transaction. Registered in <c>SchemaModules</c>. A shipped step is never
/// edited; a change is a NEW step after the last one, and every id is permanent.
/// </para>
///
/// <para>
/// NULL IS "NOT MEASURED" AND IS NEVER 0. A run that reported no usage has NULL in every token column
/// and in <c>billable</c>, and <c>measured</c> 0; it is counted, never summed as a zero. A plugin's
/// run ran no model: it is measured, has no tokens, and <c>billable</c> 0.
/// </para>
///
/// <para>
/// <c>trigger_source</c> and <c>trigger_fired_at</c> name the trigger fire a run answered, directly
/// or as a Manager run woken by such a run's <c>completed</c>, <c>failed</c> or <c>handback</c> row.
/// They, and <c>queued_at</c>, are read from <c>delivery_ledger</c> (<c>outcome-003</c>), recorded
/// when the delivery was accepted, because the instruction rows that link a run to its trigger are
/// exactly what a Reset deletes; a run whose delivery was accepted before that step falls back to
/// the log, and keeps NULL when the log no longer holds them. <c>close_seq</c> is the
/// <c>workflow.completed</c> or <c>workflow.closed</c> row's seq, so each appears once.
/// </para>
/// </summary>
public static class OutcomeSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "outcome-001",
            """
            CREATE TABLE usage_ledger (
                run_seq               INTEGER PRIMARY KEY,
                correlation           INTEGER NOT NULL,
                team_id               TEXT    NULL COLLATE NOCASE,
                team_name             TEXT    NULL,
                member                TEXT    NOT NULL COLLATE NOCASE,
                member_kind           TEXT    NULL,
                agent_preset          TEXT    NULL,
                run_outcome           TEXT    NOT NULL,
                queued_at             TEXT    NULL,
                started_at            TEXT    NULL,
                ended_at              TEXT    NOT NULL,
                measured              INTEGER NOT NULL,
                tokens_in             INTEGER NULL,
                tokens_cached_in      INTEGER NULL,
                tokens_cache_creation INTEGER NULL,
                tokens_out            INTEGER NULL,
                tokens_combined       INTEGER NULL,
                billable              INTEGER NULL,
                trigger_source        TEXT    NULL,
                trigger_fired_at      TEXT    NULL,
                backfilled            INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX ix_usage_ledger_correlation ON usage_ledger(correlation, run_seq);
            CREATE INDEX ix_usage_ledger_member ON usage_ledger(team_id, member, run_seq);
            CREATE INDEX ix_usage_ledger_trigger ON usage_ledger(trigger_source, trigger_fired_at);
            CREATE INDEX ix_usage_ledger_ended ON usage_ledger(ended_at);

            CREATE TABLE workflow_ledger (
                close_seq           INTEGER PRIMARY KEY,
                correlation         INTEGER NOT NULL,
                team_id             TEXT    NULL COLLATE NOCASE,
                team_name           TEXT    NULL,
                root_at             TEXT    NULL,
                closed_at           TEXT    NOT NULL,
                how_closed          TEXT    NOT NULL,
                outcome_id_at_close INTEGER NULL,
                backfilled          INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX ix_workflow_ledger_correlation ON workflow_ledger(correlation, close_seq);
            CREATE INDEX ix_workflow_ledger_closed ON workflow_ledger(closed_at);

            CREATE TRIGGER usage_ledger_no_update BEFORE UPDATE ON usage_ledger
            BEGIN SELECT RAISE(ABORT, 'usage_ledger is append-only'); END;

            CREATE TRIGGER usage_ledger_no_delete BEFORE DELETE ON usage_ledger
            BEGIN SELECT RAISE(ABORT, 'usage_ledger is append-only'); END;

            CREATE TRIGGER workflow_ledger_no_update BEFORE UPDATE ON workflow_ledger
            BEGIN SELECT RAISE(ABORT, 'workflow_ledger is append-only'); END;

            CREATE TRIGGER workflow_ledger_no_delete BEFORE DELETE ON workflow_ledger
            BEGIN SELECT RAISE(ABORT, 'workflow_ledger is append-only'); END;
            """),

        // THE OUTCOMES AND THEIR LINKS. `outcomes.id` is a GUID written with its hyphens, so it
        // stays text wherever it is copied (`workflow_ledger.outcome_id_at_close` is INTEGER). A
        // name is unique among `proposed` and `active` only, compared by `name_key` (trimmed,
        // whitespace runs one space, lower-cased): a retired or merged name may be used again.
        // `merged_into` is set on a merged outcome and reads follow it; nothing rewrites a link.
        //
        // `workflow_outcome_links` is APPEND-ONLY, like the ledger: a workflow's outcome is its
        // newest row, a move is a new row, and a rename or merge never touches one - the name and
        // team snapshots say what they were when the link was made. `how` is dispatch, trigger,
        // tell, manager or person; `set_by_kind` is person, member or platform.
        new MigrationStep(
            "outcome-002",
            """
            CREATE TABLE outcomes (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL,
                name_key        TEXT NOT NULL,
                description     TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL,
                merged_into     TEXT NULL,
                source          TEXT NOT NULL DEFAULT 'local',
                target_metric   TEXT NULL,
                target_unit     TEXT NULL,
                target_value    TEXT NULL,
                created_by      TEXT NOT NULL,
                created_by_kind TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                confirmed_by    TEXT NULL,
                confirmed_at    TEXT NULL
            );

            CREATE UNIQUE INDEX ux_outcomes_live_name ON outcomes(name_key)
                WHERE status IN ('proposed', 'active');

            CREATE TABLE workflow_outcome_links (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                correlation          INTEGER NOT NULL,
                outcome_id           TEXT    NOT NULL,
                team_id              TEXT    NULL COLLATE NOCASE,
                team_name_at_link    TEXT    NULL,
                outcome_name_at_link TEXT    NOT NULL,
                set_by               TEXT    NOT NULL,
                set_by_kind          TEXT    NOT NULL,
                set_at               TEXT    NOT NULL,
                how                  TEXT    NOT NULL
            );

            CREATE INDEX ix_workflow_outcome_links_correlation ON workflow_outcome_links(correlation, id);
            CREATE INDEX ix_workflow_outcome_links_outcome ON workflow_outcome_links(outcome_id);

            CREATE TRIGGER workflow_outcome_links_no_update BEFORE UPDATE ON workflow_outcome_links
            BEGIN SELECT RAISE(ABORT, 'workflow_outcome_links is append-only'); END;

            CREATE TRIGGER workflow_outcome_links_no_delete BEFORE DELETE ON workflow_outcome_links
            BEGIN SELECT RAISE(ABORT, 'workflow_outcome_links is append-only'); END;
            """),

        // WHAT A RUN ANSWERED AND WHERE A BUDGET'S WINDOW STARTS, recorded when they happen rather
        // than looked up on the log when the run ends: a Reset in between deletes the rows the
        // lookup follows. `delivery_ledger` is written with the `pending_deliveries` row that
        // accepts a delivery (`SqlitePendingDeliveries.AddAsync`), `nudge_ledger` with the nudge's
        // own log row (`LedgerRows.WriteAsync`). Both are append-only, as the two tables above.
        new MigrationStep(
            "outcome-003",
            """
            CREATE TABLE delivery_ledger (
                delivery_seq     INTEGER PRIMARY KEY,
                correlation      INTEGER NOT NULL,
                queued_at        TEXT    NOT NULL,
                trigger_source   TEXT    NULL,
                trigger_fired_at TEXT    NULL
            );

            CREATE TABLE nudge_ledger (
                nudge_seq   INTEGER PRIMARY KEY,
                correlation INTEGER NOT NULL,
                nudged_at   TEXT    NOT NULL
            );

            CREATE INDEX ix_nudge_ledger_correlation ON nudge_ledger(correlation, nudge_seq);

            CREATE TRIGGER delivery_ledger_no_update BEFORE UPDATE ON delivery_ledger
            BEGIN SELECT RAISE(ABORT, 'delivery_ledger is append-only'); END;

            CREATE TRIGGER delivery_ledger_no_delete BEFORE DELETE ON delivery_ledger
            BEGIN SELECT RAISE(ABORT, 'delivery_ledger is append-only'); END;

            CREATE TRIGGER nudge_ledger_no_update BEFORE UPDATE ON nudge_ledger
            BEGIN SELECT RAISE(ABORT, 'nudge_ledger is append-only'); END;

            CREATE TRIGGER nudge_ledger_no_delete BEFORE DELETE ON nudge_ledger
            BEGIN SELECT RAISE(ABORT, 'nudge_ledger is append-only'); END;
            """),

        // AN UNLINK IS A ROW TOO. A person's "None" in Change outcome… appends a link that names no
        // outcome, so the workflow counts under No outcome and its history keeps what it served:
        // `outcome_id` and `outcome_name_at_link` become nullable, which SQLite does only by building
        // the table again. Every row is copied with its id, so `AUTOINCREMENT` carries on after the
        // last one; the indexes and the append-only triggers are made again on the new table. A
        // DROP TABLE fires no DELETE trigger, so the old table goes without passing through them.
        new MigrationStep(
            "outcome-004",
            """
            CREATE TABLE workflow_outcome_links_new (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                correlation          INTEGER NOT NULL,
                outcome_id           TEXT    NULL,
                team_id              TEXT    NULL COLLATE NOCASE,
                team_name_at_link    TEXT    NULL,
                outcome_name_at_link TEXT    NULL,
                set_by               TEXT    NOT NULL,
                set_by_kind          TEXT    NOT NULL,
                set_at               TEXT    NOT NULL,
                how                  TEXT    NOT NULL
            );

            INSERT INTO workflow_outcome_links_new (
                id, correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link,
                set_by, set_by_kind, set_at, how)
            SELECT id, correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link,
                set_by, set_by_kind, set_at, how
            FROM workflow_outcome_links;

            DROP TABLE workflow_outcome_links;

            ALTER TABLE workflow_outcome_links_new RENAME TO workflow_outcome_links;

            CREATE INDEX ix_workflow_outcome_links_correlation ON workflow_outcome_links(correlation, id);
            CREATE INDEX ix_workflow_outcome_links_outcome ON workflow_outcome_links(outcome_id);

            CREATE TRIGGER workflow_outcome_links_no_update BEFORE UPDATE ON workflow_outcome_links
            BEGIN SELECT RAISE(ABORT, 'workflow_outcome_links is append-only'); END;

            CREATE TRIGGER workflow_outcome_links_no_delete BEFORE DELETE ON workflow_outcome_links
            BEGIN SELECT RAISE(ABORT, 'workflow_outcome_links is append-only'); END;
            """),
    ];
}
