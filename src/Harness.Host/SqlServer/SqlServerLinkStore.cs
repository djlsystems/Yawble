using Harness.Contracts;
using Harness.Host.Link;
using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="ILinkStore"/>: <c>management_link</c> (link-001),
/// <see cref="LinkStore"/> statement for statement. A save replaces the row in one transaction, so a
/// reader sees the old link or the new one, never none between them.
/// </summary>
public sealed class SqlServerLinkStore(string connectionString) : ILinkStore
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<StoredLink?> GetAsync(CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {LinkRows.Columns} FROM management_link WHERE id = 1";

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? LinkRows.Read(reader) : null;
            },
            ct);

    public Task SaveAsync(StoredLink link, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM management_link WITH (UPDLOCK, HOLDLOCK)";
                    await delete.ExecuteNonQueryAsync(token);
                }

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = LinkRows.Insert("@");
                    LinkRows.Bind(insert, "@", link);
                    await insert.ExecuteNonQueryAsync(token);
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public Task MarkAsync(LinkState state, DateTimeOffset at, string? reason, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            "UPDATE management_link SET state = @state, state_since = @since, reason = @reason WHERE id = 1",
            command =>
            {
                LinkRows.Add(command, "@state", LinkRows.Text(state));
                LinkRows.Add(command, "@since", Instant.Stamp(at));
                LinkRows.Add(command, "@reason", reason);
            },
            ct);

    public Task ReportedAsync(DateTimeOffset at, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            "UPDATE management_link SET last_report_at = @at WHERE id = 1",
            command => LinkRows.Add(command, "@at", Instant.Stamp(at)),
            ct);

    public Task ClearAsync(CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(_connectionString, "DELETE FROM management_link", _ => { }, ct);
}
