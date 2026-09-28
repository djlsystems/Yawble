using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Identity;

/// <summary>
/// <c>unfinished_removals</c> over the same SQLite file the accounts and the tenant log use.
/// Mechanical sibling of <see cref="SqliteTenantLog"/>: same connection string, same
/// <c>Pooling=false</c>, same reliance on <c>AuthSchema</c>'s steps having been applied first.
/// </summary>
public sealed class SqliteUnfinishedRemovals(string databasePath) : IUnfinishedRemovals
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    public async Task RecordAsync(UnfinishedRemoval removal, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO unfinished_removals (path, kind, team, member, remaining, recorded_at, attempts)
            VALUES ($path, $kind, $team, $member, $remaining, $at, 1)
            ON CONFLICT(path) DO UPDATE SET
                kind = excluded.kind, team = excluded.team, member = excluded.member,
                remaining = excluded.remaining, attempts = unfinished_removals.attempts + 1
            """;
        command.Parameters.AddWithValue("$path", removal.Path);
        command.Parameters.AddWithValue("$kind", removal.Kind);
        command.Parameters.AddWithValue("$team", removal.Team);
        command.Parameters.AddWithValue("$member", (object?)removal.Member ?? DBNull.Value);
        command.Parameters.AddWithValue("$remaining", JsonSerializer.Serialize(removal.Remaining));
        command.Parameters.AddWithValue("$at", removal.RecordedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<UnfinishedRemoval>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, kind, team, member, remaining, recorded_at, attempts FROM unfinished_removals ORDER BY path";

        return await ReadAsync(command, ct);
    }

    public async Task<UnfinishedRemoval?> FindAsync(string path, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, kind, team, member, remaining, recorded_at, attempts FROM unfinished_removals WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);

        return (await ReadAsync(command, ct)).SingleOrDefault();
    }

    public async Task ForgetAsync(string path, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM unfinished_removals WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<UnfinishedRemoval>> ReadAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<UnfinishedRemoval>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new UnfinishedRemoval(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt32(6)));
        }

        return rows;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
