using System.Data.Common;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Host.Link;

/// <summary>How a <c>management_link</c> row is written and read, shared by both providers' stores.</summary>
public static class LinkRows
{
    public const string Columns =
        "service_url, service_name, instance_name, credential_protected, attached_at, state, state_since, reason, last_report_at";

    /// <summary>A state as its column holds it: <c>connecting</c>, <c>connected</c>, <c>retrying</c> or <c>refused</c>.</summary>
    public static string Text(LinkState state) => state.ToString().ToLowerInvariant();

    public static LinkState State(string text) => Enum.Parse<LinkState>(text, ignoreCase: true);

    public static StoredLink Read(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        Instant.Parse(reader.GetString(4)), State(reader.GetString(5)), Instant.Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : Instant.Parse(reader.GetString(8)));

    /// <summary>Every column of <paramref name="link"/> as a parameter (<paramref name="prefix"/> is <c>$</c> or <c>@</c>).</summary>
    public static void Bind(DbCommand command, string prefix, StoredLink link)
    {
        Add(command, prefix + "url", link.ServiceUrl);
        Add(command, prefix + "service", link.ServiceName);
        Add(command, prefix + "instance", link.InstanceName);
        Add(command, prefix + "credential", link.CredentialCipher);
        Add(command, prefix + "attached", Instant.Stamp(link.AttachedAt));
        Add(command, prefix + "state", Text(link.State));
        Add(command, prefix + "since", Instant.Stamp(link.StateSince));
        Add(command, prefix + "reason", link.Reason);
        Add(command, prefix + "reported", link.LastReportAt is { } at ? Instant.Stamp(at) : null);
    }

    public static string Insert(string prefix) =>
        $"INSERT INTO management_link (id, {Columns}) VALUES (1, {prefix}url, {prefix}service, {prefix}instance, {prefix}credential, "
        + $"{prefix}attached, {prefix}state, {prefix}since, {prefix}reason, {prefix}reported)";

    public static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>
/// The SQLite <see cref="ILinkStore"/>: <c>management_link</c> (link-001) in the instance database. The
/// row holds the credential only as the ciphertext the caller sealed with <see cref="LinkCredential"/>.
/// </summary>
public sealed class LinkStore(string databasePath) : ILinkStore
{
    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    public async Task<StoredLink?> GetAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {LinkRows.Columns} FROM management_link WHERE id = 1";

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? LinkRows.Read(reader) : null;
    }

    public async Task SaveAsync(StoredLink link, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM management_link";
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = LinkRows.Insert("$");
            LinkRows.Bind(insert, "$", link);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task MarkAsync(LinkState state, DateTimeOffset at, string? reason, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE management_link SET state = $state, state_since = $since, reason = $reason WHERE id = 1";
        LinkRows.Add(command, "$state", LinkRows.Text(state));
        LinkRows.Add(command, "$since", Instant.Stamp(at));
        LinkRows.Add(command, "$reason", reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ReportedAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE management_link SET last_report_at = $at WHERE id = 1";
        LinkRows.Add(command, "$at", Instant.Stamp(at));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM management_link";
        await command.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
