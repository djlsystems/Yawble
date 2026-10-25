using Harness.Contracts;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// The outcome module's tables on SQL Server - the accounting, the outcomes and their links, and the
/// admission holds - as SQLite's <c>OutcomeSchema</c> leaves them after <c>outcome-007</c>, in one
/// baseline step. That class's remarks are the argument for each table; this one says only where
/// the dialect makes a difference.
///
/// <para>
/// THE IDS ARE THE SQLITE MODULE'S OWN PREFIX (<c>outcome-</c>), so a later SQLite step and its SQL
/// Server twin share one id. Types follow the plan's rules: BIGINT for INTEGER, IDENTITY for an
/// AUTOINCREMENT key, NVARCHAR(40) for an instant, NVARCHAR(200) for a team or member id, at most
/// NVARCHAR(450) for other keyed text, NVARCHAR(MAX) for the rest. Every column SQLite declares
/// <c>COLLATE NOCASE</c> takes <see cref="SqlServerSql.NoCase"/>, so its index and its comparisons
/// fold case as SQLite's do. <c>workflow_ledger.outcome_id_at_close</c> is text here: SQLite's column
/// is INTEGER only by affinity, and holds an outcome's GUID.
/// </para>
///
/// <para>
/// THE APPEND-ONLY RULES ARE TRIGGERS, as on SQLite, so a later retention job cannot break them by
/// accident either. SQLite's are BEFORE triggers that fire per row; these are AFTER triggers that
/// fire per statement, so each first asks whether the statement touched a row. A refusal THROWs,
/// which rolls back the statement's transaction. A CREATE TRIGGER must begin its own batch, and a
/// step is one batch, so each is run through <c>EXEC</c>.
/// </para>
/// </summary>
public static class SqlServerOutcomeSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "outcome.sqlserver-baseline-1",
            $"""
             CREATE TABLE usage_ledger (
                 run_seq               BIGINT        NOT NULL PRIMARY KEY,
                 correlation           BIGINT        NOT NULL,
                 team_id               NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NULL,
                 team_name             NVARCHAR(MAX) NULL,
                 member                NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 member_kind           NVARCHAR(MAX) NULL,
                 agent_preset          NVARCHAR(MAX) NULL,
                 run_outcome           NVARCHAR(MAX) NOT NULL,
                 queued_at             NVARCHAR(40)  NULL,
                 started_at            NVARCHAR(40)  NULL,
                 ended_at              NVARCHAR(40)  NOT NULL,
                 measured              BIGINT        NOT NULL,
                 tokens_in             BIGINT        NULL,
                 tokens_cached_in      BIGINT        NULL,
                 tokens_cache_creation BIGINT        NULL,
                 tokens_out            BIGINT        NULL,
                 tokens_combined       BIGINT        NULL,
                 billable              BIGINT        NULL,
                 trigger_source        NVARCHAR(400) NULL,
                 trigger_fired_at      NVARCHAR(40)  NULL,
                 backfilled            BIGINT        NOT NULL DEFAULT 0
             );

             CREATE INDEX ix_usage_ledger_correlation ON usage_ledger(correlation, run_seq);
             CREATE INDEX ix_usage_ledger_member ON usage_ledger(team_id, member, run_seq);
             CREATE INDEX ix_usage_ledger_trigger ON usage_ledger(trigger_source, trigger_fired_at);
             CREATE INDEX ix_usage_ledger_ended ON usage_ledger(ended_at);
             CREATE INDEX ix_usage_ledger_team_ended ON usage_ledger(team_id, ended_at);

             CREATE TABLE workflow_ledger (
                 close_seq           BIGINT        NOT NULL PRIMARY KEY,
                 correlation         BIGINT        NOT NULL,
                 team_id             NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NULL,
                 team_name           NVARCHAR(MAX) NULL,
                 root_at             NVARCHAR(40)  NULL,
                 closed_at           NVARCHAR(40)  NOT NULL,
                 how_closed          NVARCHAR(MAX) NOT NULL,
                 outcome_id_at_close NVARCHAR(MAX) NULL,
                 backfilled          BIGINT        NOT NULL DEFAULT 0
             );

             CREATE INDEX ix_workflow_ledger_correlation ON workflow_ledger(correlation, close_seq);
             CREATE INDEX ix_workflow_ledger_closed ON workflow_ledger(closed_at);

             CREATE TABLE outcomes (
                 id              NVARCHAR(450) NOT NULL PRIMARY KEY,
                 name            NVARCHAR(MAX) NOT NULL,
                 name_key        NVARCHAR(450) NOT NULL,
                 description     NVARCHAR(MAX) NOT NULL DEFAULT N'',
                 status          NVARCHAR(40)  NOT NULL,
                 merged_into     NVARCHAR(450) NULL,
                 source          NVARCHAR(MAX) NOT NULL DEFAULT N'local',
                 target_metric   NVARCHAR(MAX) NULL,
                 target_unit     NVARCHAR(MAX) NULL,
                 target_value    NVARCHAR(MAX) NULL,
                 created_by      NVARCHAR(MAX) NOT NULL,
                 created_by_kind NVARCHAR(MAX) NOT NULL,
                 created_at      NVARCHAR(40)  NOT NULL,
                 updated_at      NVARCHAR(40)  NOT NULL,
                 confirmed_by    NVARCHAR(MAX) NULL,
                 confirmed_at    NVARCHAR(40)  NULL,
                 value_amount    NVARCHAR(MAX) NULL
             );

             CREATE UNIQUE INDEX ux_outcomes_live_name ON outcomes(name_key)
                 WHERE status IN (N'proposed', N'active');

             CREATE TABLE workflow_outcome_links (
                 id                   BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                 correlation          BIGINT        NOT NULL,
                 outcome_id           NVARCHAR(450) NULL,
                 team_id              NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NULL,
                 team_name_at_link    NVARCHAR(MAX) NULL,
                 outcome_name_at_link NVARCHAR(MAX) NULL,
                 set_by               NVARCHAR(MAX) NOT NULL,
                 set_by_kind          NVARCHAR(MAX) NOT NULL,
                 set_at               NVARCHAR(40)  NOT NULL,
                 how                  NVARCHAR(MAX) NOT NULL
             );

             CREATE INDEX ix_workflow_outcome_links_correlation ON workflow_outcome_links(correlation, id);
             CREATE INDEX ix_workflow_outcome_links_outcome ON workflow_outcome_links(outcome_id);

             CREATE TABLE delivery_ledger (
                 delivery_seq     BIGINT        NOT NULL PRIMARY KEY,
                 correlation      BIGINT        NOT NULL,
                 queued_at        NVARCHAR(40)  NOT NULL,
                 trigger_source   NVARCHAR(MAX) NULL,
                 trigger_fired_at NVARCHAR(40)  NULL
             );

             CREATE TABLE nudge_ledger (
                 nudge_seq   BIGINT       NOT NULL PRIMARY KEY,
                 correlation BIGINT       NOT NULL,
                 nudged_at   NVARCHAR(40) NOT NULL
             );

             CREATE INDEX ix_nudge_ledger_correlation ON nudge_ledger(correlation, nudge_seq);

             CREATE TABLE admission_holds (
                 id           BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                 team_id      NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 team_name    NVARCHAR(MAX) NULL,
                 member       NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 delivery_seq BIGINT        NULL,
                 held_at      NVARCHAR(40)  NOT NULL,
                 released_at  NVARCHAR(40)  NULL,
                 reason_kind  NVARCHAR(MAX) NOT NULL,
                 reason       NVARCHAR(MAX) NOT NULL,
                 unfinished   BIGINT        NOT NULL DEFAULT 0
             );

             CREATE INDEX ix_admission_holds_team_held ON admission_holds(team_id, held_at);
             CREATE INDEX ix_admission_holds_open ON admission_holds(team_id, member) WHERE released_at IS NULL;

             {AppendOnly("usage_ledger")}
             {AppendOnly("workflow_ledger")}
             {AppendOnly("workflow_outcome_links")}
             {AppendOnly("delivery_ledger")}
             {AppendOnly("nudge_ledger")}

             -- THE ONE UPDATE A HOLD TAKES IS ITS CLOSE: released_at set once, and unfinished only to 1.
             -- Every other column compared with EXCEPT, which treats two NULLs as equal as SQLite's
             -- IS NOT does. The id is an IDENTITY, which no UPDATE can change.
             EXEC(N'CREATE TRIGGER admission_holds_close_only ON admission_holds AFTER UPDATE AS
             BEGIN
                 SET NOCOUNT ON;
                 IF EXISTS (
                     SELECT 1 FROM inserted i JOIN deleted d ON d.id = i.id
                     WHERE d.released_at IS NOT NULL
                        OR i.released_at IS NULL
                        OR EXISTS (SELECT i.team_id, i.team_name, i.member, i.delivery_seq, i.held_at, i.reason_kind, i.reason
                                   EXCEPT
                                   SELECT d.team_id, d.team_name, d.member, d.delivery_seq, d.held_at, d.reason_kind, d.reason)
                        OR (i.unfinished <> d.unfinished AND i.unfinished <> 1))
                     THROW 51001, N''admission_holds is append-only: a row is only closed, once'', 1;
             END');

             EXEC(N'CREATE TRIGGER admission_holds_no_delete ON admission_holds AFTER DELETE AS
             BEGIN
                 SET NOCOUNT ON;
                 IF EXISTS (SELECT 1 FROM deleted) THROW 51001, N''admission_holds is append-only'', 1;
             END');
             """),
    ];

    /// <summary>The two triggers that make <paramref name="table"/> append-only, as SQLite's
    /// <c>&lt;table&gt;_no_update</c> and <c>&lt;table&gt;_no_delete</c>.</summary>
    private static string AppendOnly(string table) =>
        $"""
         EXEC(N'CREATE TRIGGER {table}_no_update ON {table} AFTER UPDATE AS
         BEGIN
             SET NOCOUNT ON;
             IF EXISTS (SELECT 1 FROM inserted) THROW 51001, N''{table} is append-only'', 1;
         END');

         EXEC(N'CREATE TRIGGER {table}_no_delete ON {table} AFTER DELETE AS
         BEGIN
             SET NOCOUNT ON;
             IF EXISTS (SELECT 1 FROM deleted) THROW 51001, N''{table} is append-only'', 1;
         END');
         """;
}
