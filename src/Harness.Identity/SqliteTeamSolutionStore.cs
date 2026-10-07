using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// <see cref="ITeamSolutionStore"/> over the one database (<c>auth-016</c>). A write commits with
/// its <c>tenant_events</c> row (<see cref="TenantAuditRow"/>) or not at all.
/// </summary>
public sealed class SqliteTeamSolutionStore(
    string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss) : ITeamSolutionStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    private const string Select =
        """
        SELECT team, package_id, name, version, folder, installed_at, installed_by, updated_at,
               manifest, digests, members, triggers, plugins, uninstalled_at, answers
          FROM team_solutions
        """;

    public async Task<TeamSolutionRow?> FindAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"{Select} WHERE team = $team AND uninstalled_at IS NULL";
        command.Parameters.AddWithValue("$team", team);

        return (await ReadAsync(command, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<TeamSolutionRow>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"{Select} WHERE uninstalled_at IS NULL ORDER BY team";

        return await ReadAsync(command, ct);
    }

    public async Task<TeamSolutionRow?> FindUninstalledAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"{Select} WHERE team = $team AND uninstalled_at IS NOT NULL";
        command.Parameters.AddWithValue("$team", team);

        return (await ReadAsync(command, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<TeamSolutionRow>> ListUninstalledAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"{Select} WHERE uninstalled_at IS NOT NULL ORDER BY team";

        return await ReadAsync(command, ct);
    }

    public async Task SaveAsync(TeamSolutionRow row, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO team_solutions
                    (team, package_id, name, version, folder, installed_at, installed_by, updated_at,
                     manifest, digests, members, triggers, plugins, uninstalled_at, answers)
                VALUES ($team, $id, $name, $version, $folder, $installedAt, $installedBy, $updatedAt,
                        $manifest, $digests, $members, $triggers, $plugins, $uninstalledAt, $answers)
                ON CONFLICT(team) DO UPDATE SET
                    package_id = excluded.package_id, name = excluded.name, version = excluded.version,
                    folder = excluded.folder, installed_at = excluded.installed_at,
                    installed_by = excluded.installed_by, updated_at = excluded.updated_at,
                    manifest = excluded.manifest, digests = excluded.digests, members = excluded.members,
                    triggers = excluded.triggers, plugins = excluded.plugins,
                    uninstalled_at = excluded.uninstalled_at, answers = excluded.answers
                """;
            command.Parameters.AddWithValue("$team", row.Team);
            command.Parameters.AddWithValue("$id", row.PackageId);
            command.Parameters.AddWithValue("$name", row.Name);
            command.Parameters.AddWithValue("$version", row.Version);
            command.Parameters.AddWithValue("$folder", row.Folder);
            command.Parameters.AddWithValue("$installedAt", row.InstalledAt.ToString("O"));
            command.Parameters.AddWithValue("$installedBy", (object?)row.InstalledBy ?? DBNull.Value);
            command.Parameters.AddWithValue("$updatedAt", (object?)row.UpdatedAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$manifest", row.Manifest);
            command.Parameters.AddWithValue("$digests", JsonSerializer.Serialize(row.Digests));
            command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(row.Members));
            command.Parameters.AddWithValue("$triggers", JsonSerializer.Serialize(row.Triggers));
            command.Parameters.AddWithValue("$plugins", JsonSerializer.Serialize(row.Plugins));
            command.Parameters.AddWithValue("$uninstalledAt", (object?)row.UninstalledAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$answers", (object?)row.Answers ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> DeleteAsync(string team, TriggerAudit? audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        int deleted;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM team_solutions WHERE team = $team";
            command.Parameters.AddWithValue("$team", team);
            deleted = await command.ExecuteNonQueryAsync(ct);
        }

        if (deleted > 0 && audit is not null) await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);

        return deleted > 0;
    }

    private static async Task<IReadOnlyList<TeamSolutionRow>> ReadAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<TeamSolutionRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TeamSolutionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.GetString(8),
                Map(reader.GetString(9)),
                Map(reader.GetString(10)),
                Map(reader.GetString(11)),
                Map(reader.GetString(12)))
            {
                UninstalledAt = reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
                Answers = reader.IsDBNull(14) ? null : reader.GetString(14),
            });
        }

        return rows;
    }

    private static IReadOnlyDictionary<string, string> Map(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } map
            ? new Dictionary<string, string>(map, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection, durability: durability);
        return connection;
    }
}
