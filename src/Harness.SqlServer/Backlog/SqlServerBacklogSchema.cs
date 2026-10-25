using Harness.Contracts;

namespace Harness.SqlServer.Backlog;

/// <summary>
/// The backlog's tables on SQL Server, as SQLite's <c>BacklogSchema</c> leaves them after
/// <c>backlog-004</c>, in one baseline step. That class's remarks are the argument for each table and
/// column (the citation as the stored key, the one-row counter it is minted from, no foreign key from a
/// dispatch to its item); this one says only where the dialect makes a difference.
///
/// <para>
/// Types follow the other baselines' rules: BIGINT for INTEGER, IDENTITY for an AUTOINCREMENT key,
/// FLOAT for REAL, NVARCHAR(40) for an instant the store stamps, NVARCHAR(MAX) for text no key or index
/// holds (an archive or freeze time the caller passes is such text), and every column SQLite declares
/// <c>COLLATE NOCASE</c> takes <see cref="SqlServerSql.NoCase"/>.
/// </para>
///
/// <para>
/// KEYED TEXT IS AS WIDE AS WHAT BOUNDS IT. <c>backlog_items.id</c> and <c>backlog_dispatches.item</c>
/// are NVARCHAR(32): the citation <c>PlatformBacklogId.Format</c> renders from a number, a <c>B</c> and
/// at most 13 characters, and the only spelling any store writes.
/// </para>
///
/// <para>
/// A REPOSITORY NAME IS UNBOUNDED TEXT KEYED BY ITS HASH. The tips, bases and missed starts are keyed by
/// dispatch and repository, and a repository's name is the last segment of a URL a person gave
/// (<c>RepoUrls.DeriveName</c>), which nothing bounds in length and which may end in a space. So
/// <c>repo</c> is NVARCHAR(MAX) and the key is <c>repo_key</c>, the SHA-256 of the name lower-cased,
/// computed and persisted: lower-cased because SQLite's key folds case (<c>COLLATE NOCASE</c>), and
/// hashed whole, so a trailing space makes another key as it does there. Every match names the key and
/// the text (<c>SqlServerBacklogStore.RepoMatch</c>). <c>repo_key</c> is the one column SQLite has no
/// twin of; the parity test exempts it by name.
/// </para>
///
/// <para>
/// ONE INDEX SQLITE DOES NOT HAVE: <c>ix_backlog_dispatches_correlation</c>. A publish finds its dispatch
/// by correlation, the retention pass keeps every workflow a dispatch names, and an outcome link finds the
/// items a workflow dispatched; without it each reads every dispatch, and under a lock the outcome link
/// would hold them all.
/// </para>
/// </summary>
public static class SqlServerBacklogSchema
{
    private const string NoCase = SqlServerSql.NoCase;

    /// <summary>The key a repository's rows are found by: its name lower-cased, hashed.</summary>
    internal const string RepoKey = "CAST(HASHBYTES('SHA2_256', LOWER(repo)) AS BINARY(32))";

    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "backlog.sqlserver-baseline-1",
            $"""
             CREATE TABLE backlog_id_sequence (
                 id    BIGINT NOT NULL PRIMARY KEY CHECK (id = 1),
                 last  BIGINT NOT NULL
             );

             INSERT INTO backlog_id_sequence (id, last) VALUES (1, 0);

             CREATE TABLE backlog_items (
                 id           NVARCHAR(32)  NOT NULL PRIMARY KEY,
                 team         NVARCHAR(MAX) COLLATE {NoCase} NULL,
                 title        NVARCHAR(MAX) NOT NULL,
                 body         NVARCHAR(MAX) NOT NULL,
                 state        NVARCHAR(MAX) NOT NULL,
                 position     FLOAT         NOT NULL,
                 archived_at  NVARCHAR(MAX) NULL,
                 created_at   NVARCHAR(40)  NOT NULL,
                 updated_at   NVARCHAR(40)  NOT NULL,
                 created_by   NVARCHAR(MAX) NOT NULL,
                 outcome_id   NVARCHAR(MAX) NULL
             );

             CREATE INDEX backlog_items_position ON backlog_items(position);

             CREATE TABLE backlog_dispatches (
                 id             BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                 item           NVARCHAR(32)  NOT NULL,
                 team_id        NVARCHAR(MAX) COLLATE {NoCase} NOT NULL,
                 team_name      NVARCHAR(MAX) NOT NULL,
                 correlation    BIGINT        NOT NULL,
                 dispatched_at  NVARCHAR(40)  NOT NULL,
                 dispatched_by  NVARCHAR(MAX) NOT NULL,
                 frozen_at      NVARCHAR(MAX) NULL,
                 frozen_stats   NVARCHAR(MAX) NULL,
                 landed_at      NVARCHAR(40)  NULL,
                 landed_sha     NVARCHAR(MAX) NULL,
                 landed_branch  NVARCHAR(MAX) NULL
             );

             CREATE INDEX backlog_dispatches_item ON backlog_dispatches(item, id);
             CREATE INDEX ix_backlog_dispatches_correlation ON backlog_dispatches(correlation);

             CREATE TABLE backlog_dispatch_tips (
                 dispatch     BIGINT        NOT NULL,
                 repo         NVARCHAR(MAX) COLLATE {NoCase} NOT NULL,
                 repo_key     AS {RepoKey} PERSISTED NOT NULL,
                 sha          NVARCHAR(MAX) NOT NULL,
                 recorded_at  NVARCHAR(40)  NOT NULL,
                 PRIMARY KEY (dispatch, repo_key)
             );

             CREATE TABLE backlog_dispatch_bases (
                 dispatch     BIGINT        NOT NULL,
                 repo         NVARCHAR(MAX) COLLATE {NoCase} NOT NULL,
                 repo_key     AS {RepoKey} PERSISTED NOT NULL,
                 default_sha  NVARCHAR(MAX) NOT NULL,
                 team_sha     NVARCHAR(MAX) NULL,
                 recorded_at  NVARCHAR(40)  NOT NULL,
                 PRIMARY KEY (dispatch, repo_key)
             );

             CREATE TABLE backlog_dispatch_missed_starts (
                 dispatch     BIGINT        NOT NULL,
                 repo         NVARCHAR(MAX) COLLATE {NoCase} NOT NULL,
                 repo_key     AS {RepoKey} PERSISTED NOT NULL,
                 reason       NVARCHAR(MAX) NOT NULL,
                 team_sha     NVARCHAR(MAX) NULL,
                 default_sha  NVARCHAR(MAX) NULL,
                 stopped_at   NVARCHAR(40)  NULL,
                 recorded_at  NVARCHAR(40)  NOT NULL,
                 PRIMARY KEY (dispatch, repo_key)
             );
             """),
    ];
}
