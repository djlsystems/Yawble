using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="ITeamSolutionStore"/> over <c>team_solutions</c> on SQL Server: SQLite's
/// <c>SqliteTeamSolutionStore</c> write for write. A row's columns and its reading are
/// <see cref="TeamSolutionRows"/>', shared with it. A write commits with its <c>tenant_events</c> row
/// (<see cref="SqlServerTenantAuditRow"/>) or not at all.
///
/// <para>
/// THE TEAM FOLDS CASE, AS SQLITE'S <c>COLLATE NOCASE</c> KEY DOES, and a trailing space decides a
/// lookup or a delete as it does there (<see cref="SqlServerSql.Same"/>). The upsert matches as the
/// key itself compares, so it never fails on that key: a save names the team's stored id, which a
/// container name's rules keep free of spaces.
/// </para>
///
/// <para>
/// THE SAVE IS ONE <c>MERGE</c> UNDER <c>HOLDLOCK</c>. SQLite's upsert runs alone; here two saves of
/// one team's first row would otherwise both find none, and one would fail on the key. The merge holds
/// the team's key until it commits, so the second replaces the row the first made.
/// </para>
///
/// <para>
/// LOCK ORDER: THE <c>team_solutions</c> ROW, THEN <c>tenant_events</c>, whose rows are inserts that
/// read nothing. The table has no foreign key, so no write here takes a <c>teams</c> row.
/// </para>
/// </summary>
public sealed class SqlServerTeamSolutionStore(string connectionString) : ITeamSolutionStore
{
    private static readonly string TeamMatch = SqlServerSql.Same("team", "@team");

    private const string Select = $"SELECT {TeamSolutionRows.Columns} FROM team_solutions";

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public async Task<TeamSolutionRow?> FindAsync(string team, CancellationToken ct = default) =>
        (await ReadAsync($"{Select} WHERE {TeamMatch} AND uninstalled_at IS NULL", team, ct)).SingleOrDefault();

    public Task<IReadOnlyList<TeamSolutionRow>> ListAsync(CancellationToken ct = default) =>
        ReadAsync($"{Select} WHERE uninstalled_at IS NULL ORDER BY {SqlServerSql.NoCaseOrder("team")}", null, ct);

    public async Task<TeamSolutionRow?> FindUninstalledAsync(string team, CancellationToken ct = default) =>
        (await ReadAsync($"{Select} WHERE {TeamMatch} AND uninstalled_at IS NOT NULL", team, ct)).SingleOrDefault();

    public Task<IReadOnlyList<TeamSolutionRow>> ListUninstalledAsync(CancellationToken ct = default) =>
        ReadAsync($"{Select} WHERE uninstalled_at IS NOT NULL ORDER BY {SqlServerSql.NoCaseOrder("team")}", null, ct);

    public Task SaveAsync(TeamSolutionRow row, TriggerAudit audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        $"""
                         MERGE team_solutions WITH (HOLDLOCK) AS s
                         USING (SELECT @team AS team) AS n ON s.team = n.team
                         WHEN MATCHED THEN UPDATE SET
                             package_id = @id, name = @name, version = @version,
                             folder = @folder, installed_at = @installedAt,
                             installed_by = @installedBy, updated_at = @updatedAt,
                             manifest = @manifest, digests = @digests, members = @members,
                             triggers = @triggers, plugins = @plugins,
                             uninstalled_at = @uninstalledAt, answers = @answers,
                             plugin_minimums = @pluginMinimums
                         WHEN NOT MATCHED THEN INSERT
                             (team, package_id, name, version, folder, installed_at, installed_by, updated_at,
                              manifest, digests, members, triggers, plugins, uninstalled_at, answers, plugin_minimums)
                             VALUES (@team, @id, @name, @version, @folder, @installedAt, @installedBy, @updatedAt,
                                     @manifest, @digests, @members, @triggers, @plugins, @uninstalledAt, @answers, @pluginMinimums);
                         """;
                    foreach (var (name, value) in TeamSolutionRows.SaveValues(row)) command.Parameters.AddWithValue("@" + name, value);
                    await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            ct);

    public Task<bool> DeleteAsync(string team, TriggerAudit? audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                int deleted;

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM team_solutions WHERE {TeamMatch}";
                    command.Parameters.AddWithValue("@team", team);
                    deleted = await command.ExecuteNonQueryAsync(token);
                }

                if (deleted > 0 && audit is not null) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);

                return deleted > 0;
            },
            ct);

    private Task<IReadOnlyList<TeamSolutionRow>> ReadAsync(string sql, string? team, CancellationToken ct) =>
        SqlServerOpen.RunAsync<IReadOnlyList<TeamSolutionRow>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (team is not null) command.Parameters.AddWithValue("@team", team);

                var rows = new List<TeamSolutionRow>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(TeamSolutionRows.Read(reader));

                return rows;
            },
            ct);
}
