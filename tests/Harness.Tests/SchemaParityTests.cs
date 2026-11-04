using System.Text.RegularExpressions;
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

    /// <summary>The expenses step is one id on both providers, and each makes the table append-only and
    /// a row superseded at most once: the two triggers and the unique index on <c>supersedes</c>.</summary>
    [Fact]
    public void The_expenses_step_is_append_only_and_supersedes_once_on_both_providers()
    {
        var sqlite = Assert.Single(SchemaModules.All, s => s.Id == "outcome-013").Sql;
        var sqlServer = Assert.Single(SqlServerSchemaModules.All, s => s.Id == "outcome-013").Sql;

        foreach (var sql in new[] { sqlite, sqlServer })
        {
            Assert.Contains("CREATE TABLE expenses", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE UNIQUE INDEX ux_expenses_supersedes ON expenses(supersedes) WHERE supersedes IS NOT NULL", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TRIGGER expenses_no_update", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TRIGGER expenses_no_delete", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("CHECK", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>The origin steps are one id each on both providers, and add the nullable columns and
    /// nothing else: <c>messages-007</c> the five on <c>messages</c>, <c>outcome-014</c> the two on
    /// <c>usage_ledger</c>. <see cref="Tables_match"/> holds the columns themselves alike.</summary>
    [Fact]
    public void The_origin_steps_add_only_nullable_columns_on_both_providers()
    {
        foreach (var (id, table, columns) in new[]
        {
            ("messages-007", "messages", new[] { "origin_kind", "origin_actor_id", "origin_actor_email", "origin_trigger", "publisher_kind" }),
            ("outcome-014", "usage_ledger", new[] { "origin_kind", "origin_actor_id" }),
        })
        {
            var sqlite = Assert.Single(SchemaModules.All, s => s.Id == id).Sql;
            var sqlServer = Assert.Single(SqlServerSchemaModules.All, s => s.Id == id).Sql;

            foreach (var sql in new[] { sqlite, sqlServer })
            {
                Assert.Contains($"ALTER TABLE {table} ADD", sql, StringComparison.Ordinal);
                foreach (var column in columns) Assert.Matches($@"\b{column}\s+\w+(\(MAX\))?\s+NULL", sql);
                Assert.DoesNotContain("CREATE", sql, StringComparison.Ordinal);
                Assert.DoesNotContain("UPDATE", sql, StringComparison.Ordinal);
                Assert.DoesNotContain("NOT NULL", sql, StringComparison.Ordinal);
            }
        }
    }

    /// <summary><c>outcome-015</c> is one id on both providers and adds <c>usage_ledger.reason_code</c>,
    /// nullable, and nothing else. <see cref="Tables_match"/> holds the column itself alike.</summary>
    [Fact]
    public void The_reason_code_step_adds_only_a_nullable_column_on_both_providers()
    {
        var sqlite = Assert.Single(SchemaModules.All, s => s.Id == "outcome-015").Sql;
        var sqlServer = Assert.Single(SqlServerSchemaModules.All, s => s.Id == "outcome-015").Sql;

        foreach (var sql in new[] { sqlite, sqlServer })
        {
            Assert.Single(Regex.Matches(sql, "ALTER TABLE", RegexOptions.None));
            Assert.Contains("ALTER TABLE usage_ledger ADD", sql, StringComparison.Ordinal);
            Assert.Matches(@"\breason_code\s+\w+(\(MAX\))?\s+NULL;", sql);
            Assert.DoesNotContain("CREATE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("UPDATE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("NOT NULL", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(",", sql, StringComparison.Ordinal);
        }
    }

    /// <summary><c>outcome-010</c> is one id on both providers, and creates <c>intervention_ledger</c>,
    /// its two indexes and its two append-only triggers, and nothing else: no other table is touched.
    /// <see cref="Tables_match"/> holds the columns themselves alike.</summary>
    [Fact]
    public void The_intervention_step_creates_only_the_append_only_ledger_on_both_providers()
    {
        var sqlite = Assert.Single(SchemaModules.All, s => s.Id == "outcome-010").Sql;
        var sqlServer = Assert.Single(SqlServerSchemaModules.All, s => s.Id == "outcome-010").Sql;

        foreach (var sql in new[] { sqlite, sqlServer })
        {
            Assert.Single(Regex.Matches(sql, "CREATE TABLE", RegexOptions.None));
            Assert.Contains("CREATE TABLE intervention_ledger", sql, StringComparison.Ordinal);
            foreach (var column in new[]
            {
                "id", "occurred_at", "actor_id", "actor_email", "kind", "team_id", "correlation",
                "outcome_id_at_event", "related_seq", "duration_seconds", "duration_measured",
            })
            {
                Assert.Matches($@"\n\s*{column}\s", sql);
            }

            Assert.Contains("CREATE INDEX ix_intervention_ledger_team ON intervention_ledger(team_id, occurred_at)", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE INDEX ix_intervention_ledger_actor ON intervention_ledger(actor_id, occurred_at)", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TRIGGER intervention_ledger_no_update", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TRIGGER intervention_ledger_no_delete", sql, StringComparison.Ordinal);
            Assert.Matches(@"duration_seconds\s+\w+\s+NULL,", sql);
            Assert.DoesNotContain("ALTER TABLE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("INSERT", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A TEAM ID FOLDS CASE ON BOTH PROVIDERS, AND THIS NEEDS NO SQL SERVER. <see cref="Tables_match"/>
    /// compares the two live schemas, but it skips wherever <c>HARNESS_TEST_SQLSERVER</c> is unset, which
    /// let SQLite's <c>intervention_ledger.team_id</c> ship BINARY beside SQL Server's NOCASE twin. Here
    /// SQLite's own declared collation (<c>sqlite3_table_column_metadata</c>) is read for every
    /// <c>team_id</c> column on a fresh volume, and every SQL Server <c>team_id</c> declaration is read
    /// from its step text: each must fold case, as <c>ContainerId</c>'s equality and every reader
    /// (<c>HumanFigures.OfTeam</c>, the OrdinalIgnoreCase floors) do.
    /// </summary>
    [Fact]
    public async Task Every_team_id_column_folds_case_on_both_providers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sqlite = new SqliteContractDatabase();

        var binary = new List<string>();
        var seen = new List<string>();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlite.Path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(ct);
            foreach (var table in await ProviderSchemaComparison.SqliteTablesAsync(sqlite.Path, ct))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM pragma_table_xinfo($table) WHERE name = 'team_id'";
                command.Parameters.AddWithValue("$table", table);
                if (await command.ExecuteScalarAsync(ct) is null) continue;

                var result = SQLitePCL.raw.sqlite3_table_column_metadata(
                    connection.Handle, "main", table, "team_id", out _, out var collation, out _, out _, out _);
                Assert.Equal(SQLitePCL.raw.SQLITE_OK, result);
                seen.Add(table);
                if (!string.Equals(collation, "NOCASE", StringComparison.OrdinalIgnoreCase)) binary.Add($"{table}.team_id {collation}");
            }
        }

        Assert.Contains("intervention_ledger", seen);
        Assert.Empty(binary);

        var declarations = SqlServerSchemaModules.All
            .SelectMany(s => Regex.Matches(s.Sql, @"\bteam_id\s+NVARCHAR\([^)]*\)[^,\n]*").Select(m => (s.Id, m.Value)))
            .ToList();
        Assert.Contains(declarations, d => d.Id == "outcome-010");
        Assert.All(declarations, d => Assert.Contains($"COLLATE {SqlServerSql.NoCase}", d.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>intervention_ledger</c>'s team id is matched as a team is: a row written under one spelling is
    /// found by another, through the column and its <c>ix_intervention_ledger_team</c> index, on SQLite.
    /// </summary>
    [Fact]
    public async Task An_intervention_row_is_found_by_its_team_in_any_case_on_SQLite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sqlite = new SqliteContractDatabase();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlite.Path, Pooling = false }.ToString());
        await connection.OpenAsync(ct);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO intervention_ledger (occurred_at, actor_id, kind, team_id)
                VALUES ('2026-11-04T00:00:00.0000000+00:00', 'person-1', 'instruction', 'AlphaTeam')
                """;
            await insert.ExecuteNonQueryAsync(ct);
        }

        foreach (var spelling in new[] { "AlphaTeam", "alphateam", "ALPHATEAM" })
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM intervention_ledger INDEXED BY ix_intervention_ledger_team WHERE team_id = $team";
            count.Parameters.AddWithValue("$team", spelling);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(ct))!);
        }
    }

    /// <summary><c>outcome-011</c> is one id on both providers and adds <c>outcomes.presentation</c>, nullable,
    /// held to the closed set <see cref="Harness.Contracts.OutcomePresentation.All"/> by a CHECK, and nothing else.
    /// <see cref="Tables_match"/> holds the column itself alike.</summary>
    [Fact]
    public void The_presentation_step_adds_only_a_nullable_checked_column_on_both_providers()
    {
        var sqlite = Assert.Single(SchemaModules.All, s => s.Id == "outcome-011").Sql;
        var sqlServer = Assert.Single(SqlServerSchemaModules.All, s => s.Id == "outcome-011").Sql;

        foreach (var sql in new[] { sqlite, sqlServer })
        {
            Assert.Single(Regex.Matches(sql, "ALTER TABLE", RegexOptions.None));
            Assert.Contains("ALTER TABLE outcomes ADD", sql, StringComparison.Ordinal);
            Assert.Matches(@"\bpresentation\s+\w+(\(\d+\))?\s+NULL\b", sql);
            Assert.Matches(@"CHECK \(presentation IN \(N?'project', N?'operational'\)\)", sql);
            Assert.DoesNotContain("CREATE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("UPDATE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("NOT NULL", sql, StringComparison.Ordinal);
        }

        Assert.Equal(["project", "operational"], Harness.Contracts.OutcomePresentation.All);
    }

    /// <summary>On SQLite the CHECK refuses a presentation outside the set, and NULL - never picked - is kept.</summary>
    [Fact]
    public async Task The_presentation_column_refuses_a_value_outside_the_set()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presentation-{Guid.NewGuid():N}.db");
        try
        {
            await new Harness.Messaging.SchemaMigrator(path).ApplyAsync(SchemaModules.All, ct: TestContext.Current.CancellationToken);
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            async Task<int> InsertAsync(string id, string? presentation)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO outcomes (id, name, name_key, status, created_by, created_by_kind, created_at, updated_at, presentation)
                    VALUES ($id, $id, $id, 'active', 'p', 'person', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', $presentation)
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$presentation", (object?)presentation ?? DBNull.Value);
                return await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(1, await InsertAsync("a", null));
            Assert.Equal(1, await InsertAsync("b", "project"));
            Assert.Equal(1, await InsertAsync("c", "operational"));
            var refused = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => InsertAsync("d", "Operational"));
            Assert.Contains("CHECK", refused.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
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
