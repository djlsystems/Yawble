using Harness.Host;
using Harness.SqlServer;
using Harness.Tests.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace Harness.Tests;

/// <summary>
/// A SCHEMA STEP IS WRITTEN FOR BOTH PROVIDERS WITH THE SAME ID, and the two fresh databases end with
/// the same tables. SQL Server does not replay SQLite's history: each module has one baseline step
/// (<c>&lt;module&gt;.sqlserver-baseline-N</c>) creating its tables as they stood after the SQLite steps
/// frozen in <see cref="SqliteBaselineStepIds"/>. From there the two lists grow together, so a SQLite
/// step written after the baseline with no SQL Server step of the same id (or the reverse) is an
/// instance that starts on one provider with a table, a column or a seed row the other never gets.
///
/// <para>
/// THE BASELINE LISTS ARE LITERALS, NOT COMPUTED. Read from <see cref="SchemaModules.All"/> they would
/// grow with every new step and exempt it, which is the one thing this test exists to refuse. They
/// change only when a new SQL Server baseline is cut, which is a deliberate act with its own review.
/// </para>
/// </summary>
public sealed class SchemaParityTests
{
    /// <summary>The SQLite step ids the SQL Server baselines stand for: every id in
    /// <see cref="SchemaModules.All"/> when the baselines were written. Frozen.</summary>
    internal static readonly IReadOnlyList<string> SqliteBaselineStepIds =
    [
        "messages-001", "messages-002", "messages-003", "messages-004", "messages-005", "messages-006",
        "auth-001", "auth-002", "auth-003", "auth-004", "auth-005", "auth-006", "auth-007", "auth-008",
        "auth-009", "auth-010", "auth-011", "auth-012", "auth-013", "auth-014", "auth-015", "auth-016",
        "auth-017", "auth-018", "auth-019", "auth-020", "auth-021", "auth-022", "auth-023", "auth-024",
        "auth-025", "auth-026", "auth-027", "auth-028", "auth-029",
        "skill-001", "skill-002", "skill-003", "skill-004",
        "backlog-001", "backlog-002", "backlog-003", "backlog-004",
        "outcome-001", "outcome-002", "outcome-003", "outcome-004", "outcome-005", "outcome-006", "outcome-007",
    ];

    /// <summary>The SQL Server baseline steps, which have no SQLite twin by design. Frozen.</summary>
    internal static readonly IReadOnlyList<string> SqlServerBaselineStepIds =
    [
        "messages.sqlserver-baseline-1",
        "auth.sqlserver-baseline-1", "auth.sqlserver-baseline-2", "auth.sqlserver-baseline-3",
        "skill.sqlserver-baseline-1",
        "backlog.sqlserver-baseline-1",
        "outcome.sqlserver-baseline-1",
    ];

    /// <summary>
    /// Tables one provider has and the other must not, each with why. Every entry must still exist on
    /// its side (<see cref="Tables_match"/> checks), so the list cannot hide a table that went away or
    /// grow quietly into an exemption for a forgotten one.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> SqliteOnlyTables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["schema_migrations"] = "SQLite's migrator's ledger; SQL Server's is schema_steps.",
        ["skills_fts"] = "SQLite's FTS5 index; SQL Server searches in code (SkillSearch), with no index.",
        ["skills_fts_config"] = "an FTS5 shadow table of skills_fts.",
        ["skills_fts_data"] = "an FTS5 shadow table of skills_fts.",
        ["skills_fts_docsize"] = "an FTS5 shadow table of skills_fts.",
        ["skills_fts_idx"] = "an FTS5 shadow table of skills_fts.",
    };

    /// <inheritdoc cref="SqliteOnlyTables"/>
    internal static readonly IReadOnlyDictionary<string, string> SqlServerOnlyTables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["schema_steps"] = "SQL Server's migrator's ledger; SQLite's is schema_migrations.",
    };

    [Fact]
    public void Every_step_after_the_baseline_exists_in_both()
    {
        var sqlite = SchemaModules.All.Select(s => s.Id).ToArray();
        var sqlServer = SqlServerSchemaModules.All.Select(s => s.Id).ToArray();

        Assert.Empty(Unmatched(sqlite, sqlServer));

        // The frozen lists are still what they say: a baseline id that left its list is a step deleted,
        // which the migrator would read as a database from a newer build (AGENTS.md: a superset).
        Assert.Empty(SqliteBaselineStepIds.Except(sqlite, StringComparer.Ordinal));
        Assert.Empty(SqlServerBaselineStepIds.Except(sqlServer, StringComparer.Ordinal));
    }

    [Fact]
    public void A_sqlite_step_with_no_sql_server_twin_is_named_and_so_is_the_reverse()
    {
        var sqlite = SchemaModules.All.Select(s => s.Id).ToArray();
        var sqlServer = SqlServerSchemaModules.All.Select(s => s.Id).ToArray();

        // A scratch step on one side only, in a list made here: no real step is added to either module.
        Assert.Equal(
            ["SQLite step 'messages-999' has no SQL Server step with the same id."],
            Unmatched([.. sqlite, "messages-999"], sqlServer));
        Assert.Equal(
            ["SQL Server step 'messages-999' has no SQLite step with the same id."],
            Unmatched(sqlite, [.. sqlServer, "messages-999"]));

        // Written on both with the same id: nothing to say.
        Assert.Empty(Unmatched([.. sqlite, "messages-999"], [.. sqlServer, "messages-999"]));
    }

    [Fact]
    public async Task Tables_match()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sqlServer = await SqlServerTestDatabase.CreateAsync(ct);
        await using var sqlite = new SqliteContractDatabase();

        var sqliteTables = await ProviderSchemaComparison.SqliteTablesAsync(sqlite.Path, ct);
        var sqlServerTables = await ProviderSchemaComparison.SqlServerTablesAsync(sqlServer.ConnectionString, ct);

        Assert.Empty(SqliteOnlyTables.Keys.Except(sqliteTables, StringComparer.Ordinal));
        Assert.Empty(SqlServerOnlyTables.Keys.Except(sqlServerTables, StringComparer.Ordinal));

        var shared = sqliteTables.Where(t => !SqliteOnlyTables.ContainsKey(t)).ToArray();
        Assert.Equal(shared, sqlServerTables.Where(t => !SqlServerOnlyTables.ContainsKey(t)).Order(StringComparer.Ordinal).ToArray());

        await ProviderSchemaComparison.AssertSameColumnsAsync(sqlite.Path, sqlServer.ConnectionString, shared);
    }

    [Fact]
    public async Task A_column_that_folds_case_on_one_provider_only_is_named()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sqlServer = await SqlServerTestDatabase.CreateAsync(ct, applyModules: false);
        var directory = Directory.CreateTempSubdirectory("harness-parity-").FullName;

        try
        {
            var path = Path.Combine(directory, "scratch.db");
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync(ct);
                await using var create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE parity_scratch (id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, note TEXT NULL)";
                await create.ExecuteNonQueryAsync(ct);
            }

            async Task SqlServerTableAsync(string idCollation, string noteCollation)
            {
                await using var connection = new SqlConnection(sqlServer.ConnectionString);
                await connection.OpenAsync(ct);
                await using var create = connection.CreateCommand();
                create.CommandText =
                    "DROP TABLE IF EXISTS parity_scratch; "
                    + $"CREATE TABLE parity_scratch (id NVARCHAR(100) COLLATE {idCollation} NOT NULL PRIMARY KEY, note NVARCHAR(MAX) COLLATE {noteCollation} NULL)";
                await create.ExecuteNonQueryAsync(ct);
            }

            // The twin as the baselines write it: the NOCASE key folds, the BINARY note does not.
            await SqlServerTableAsync(SqlServerSql.NoCase, SqlServerMigrator.RequiredCollation);
            await ProviderSchemaComparison.AssertSameColumnsAsync(path, sqlServer.ConnectionString, ["parity_scratch"]);

            // A NOCASE column whose twin compares by code point.
            await SqlServerTableAsync(SqlServerMigrator.RequiredCollation, SqlServerMigrator.RequiredCollation);
            var binary = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() =>
                ProviderSchemaComparison.AssertSameColumnsAsync(path, sqlServer.ConnectionString, ["parity_scratch"]));
            Assert.Contains("parity_scratch.id NOT NULL NOCASE", binary.Message);

            // A BINARY column whose twin folds case.
            await SqlServerTableAsync(SqlServerSql.NoCase, SqlServerSql.NoCase);
            var folded = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() =>
                ProviderSchemaComparison.AssertSameColumnsAsync(path, sqlServer.ConnectionString, ["parity_scratch"]));
            Assert.Contains("parity_scratch.note NULL NOCASE", folded.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Every step after its provider's baseline with no step of the same id on the other provider, as a
    /// sentence each. Ids compare exactly: under the SQL Server collation an id differing only in case
    /// is a different step.
    /// </summary>
    internal static IReadOnlyList<string> Unmatched(IReadOnlyList<string> sqlite, IReadOnlyList<string> sqlServer)
    {
        var sqliteAfter = sqlite.Except(SqliteBaselineStepIds, StringComparer.Ordinal).ToArray();
        var sqlServerAfter = sqlServer.Except(SqlServerBaselineStepIds, StringComparer.Ordinal).ToArray();

        return
        [
            .. sqliteAfter.Except(sqlServerAfter, StringComparer.Ordinal)
                .Select(id => $"SQLite step '{id}' has no SQL Server step with the same id."),
            .. sqlServerAfter.Except(sqliteAfter, StringComparer.Ordinal)
                .Select(id => $"SQL Server step '{id}' has no SQLite step with the same id."),
        ];
    }
}
