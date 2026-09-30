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
/// They, and <c>queued_at</c>, are read from <c>delivery_ledger</c> (<c>outcome-002</c>), recorded
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

        // WHAT A RUN ANSWERED AND WHERE A BUDGET'S WINDOW STARTS, recorded when they happen rather
        // than looked up on the log when the run ends: a Reset in between deletes the rows the
        // lookup follows. `delivery_ledger` is written with the `pending_deliveries` row that
        // accepts a delivery (`SqlitePendingDeliveries.AddAsync`), `nudge_ledger` with the nudge's
        // own log row (`LedgerRows.WriteAsync`). Both are append-only, as the two tables above.
        new MigrationStep(
            "outcome-002",
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
    ];
}
