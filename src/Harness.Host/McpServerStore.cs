using System.Data.Common;
using Harness.Contracts;
using Harness.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>What an MCP server's row holds and how it reads back, shared by both providers' stores.</summary>
public static class McpServerRows
{
    public const string Columns =
        "id, name, kind, url_shown, connection_id, field, header_name, value_template, command, package, version, "
        + "args_json, env_json, status, status_reason, tools_json, checked_at, created_at, updated_at";

    public static McpServerRecord Read(DbDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        return new McpServerRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(3), Text(4), Text(5), Text(6), Text(7),
            Text(8), Text(9), Text(10),
            McpServerRecord.ReadList<string>(Text(11)), McpServerRecord.ReadList<McpServerEnv>(Text(12)),
            reader.GetString(13), Text(14),
            Text(15) is { } tools ? McpServerRecord.ReadList<string>(tools) : null,
            Text(16) is { } checkedAt ? Instant.Parse(checkedAt) : null,
            Instant.Parse(reader.GetString(17)), Instant.Parse(reader.GetString(18)));
    }

    /// <summary>The settings columns a save writes, by parameter (<paramref name="prefix"/> is <c>$</c> or <c>@</c>).</summary>
    public static void Settings(DbCommand command, string prefix, McpServerRecord server, object sealedUrl)
    {
        void Add(string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = prefix + name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        Add("id", server.Id);
        Add("name", server.Name);
        Add("kind", server.Kind);
        Add("urlShown", server.UrlShown);
        Add("urlProtected", sealedUrl);
        Add("connection", server.ConnectionId);
        Add("field", server.Field);
        Add("header", server.HeaderName);
        Add("template", server.ValueTemplate);
        Add("command", server.Command);
        Add("package", server.Package);
        Add("version", server.Version);
        Add("args", McpServerRecord.ListJson(server.Args));
        Add("env", McpServerRecord.ListJson(server.Env));
        Add("status", server.Status);
        Add("created", Instant.Stamp(server.CreatedAt));
        Add("updated", Instant.Stamp(server.UpdatedAt));
    }

    public static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>
/// The SQLite <see cref="IMcpServerStore"/>: <c>mcp_servers</c> and <c>team_mcp_servers</c> (auth-033) in
/// the instance database. The whole URL is sealed by <see cref="ConnectionCipher"/>, as a connection's
/// secrets are; every write lands with its tenant rows in one transaction.
/// </summary>
public sealed class McpServerStore(string databasePath, IDataProtectionProvider protection) : IMcpServerStore
{
    private readonly ConnectionCipher _cipher = new(protection);

    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    public async Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {McpServerRows.Columns} FROM mcp_servers ORDER BY name COLLATE NOCASE, id";

        var list = new List<McpServerRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(McpServerRows.Read(reader));
        return list;
    }

    public async Task<McpServerRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {McpServerRows.Columns} FROM mcp_servers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? McpServerRows.Read(reader) : null;
    }

    public async Task<string?> UrlAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT url_protected FROM mcp_servers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return _cipher.Unprotect(await command.ExecuteScalarAsync(ct) as string);
    }

    public async Task<McpServerWrite> AddAsync(McpServerRecord server, string? url, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await NameTakenAsync(connection, transaction, server.Name, server.Id, ct)) return McpServerWrite.NameTaken;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO mcp_servers (id, name, kind, url_shown, url_protected, connection_id, field, header_name, value_template,
                    command, package, version, args_json, env_json, status, created_at, updated_at)
                VALUES ($id, $name, $kind, $urlShown, $urlProtected, $connection, $field, $header, $template,
                    $command, $package, $version, $args, $env, $status, $created, $updated)
                """;
            McpServerRows.Settings(command, "$", server, _cipher.ProtectOrNull(url));
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return McpServerWrite.Done;
    }

    public async Task<McpServerWrite> UpdateAsync(McpServerRecord server, string? url, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await NameTakenAsync(connection, transaction, server.Name, server.Id, ct)) return McpServerWrite.NameTaken;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE mcp_servers SET name = $name, kind = $kind, url_shown = $urlShown,
                    url_protected = CASE WHEN $keepUrl THEN url_protected ELSE $urlProtected END,
                    connection_id = $connection, field = $field, header_name = $header, value_template = $template,
                    command = $command, package = $package, version = $version, args_json = $args, env_json = $env,
                    status = $status, status_reason = NULL,
                    tools_json = CASE WHEN $kind = 'web' THEN tools_json ELSE NULL END, updated_at = $updated
                WHERE id = $id
                """;
            McpServerRows.Settings(command, "$", server, _cipher.ProtectOrNull(url));
            command.Parameters.AddWithValue("$keepUrl", url is null);
            if (await command.ExecuteNonQueryAsync(ct) == 0) return McpServerWrite.Missing;
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return McpServerWrite.Done;
    }

    public async Task RecordCheckAsync(
        string id, string? status, string? reason, IReadOnlyList<string>? tools, DateTimeOffset at, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE mcp_servers SET
                    status = COALESCE($status, status),
                    status_reason = CASE WHEN $status IS NULL THEN status_reason ELSE $reason END,
                    tools_json = COALESCE($tools, tools_json),
                    checked_at = $at
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$tools", tools is null ? DBNull.Value : McpServerRecord.ListJson(tools));
            command.Parameters.AddWithValue("$at", Instant.Stamp(at));
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>?> RemoveAsync(
        string id, Func<IReadOnlyList<string>, IReadOnlyList<TriggerAudit>> rows, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var teams = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT team FROM team_mcp_servers WHERE server_id = $id ORDER BY team COLLATE NOCASE";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) teams.Add(reader.GetString(0));
        }

        // EVERY TEAM'S ASSIGNMENT GOES WITH IT, here and not by the foreign key alone, in this transaction.
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM team_mcp_servers WHERE server_id = $id; DELETE FROM mcp_servers WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            if (await command.ExecuteNonQueryAsync(ct) == 0) return null;
        }

        foreach (var row in rows(teams)) await TenantAuditRow.AppendAsync(connection, transaction, row, ct);
        await transaction.CommitAsync(ct);
        return teams;
    }

    public async Task<IReadOnlyList<TeamMcpServer>> AssignedAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT team, server_id, assigned_at FROM team_mcp_servers WHERE team = $team ORDER BY assigned_at, server_id";
        command.Parameters.AddWithValue("$team", team);

        var list = new List<TeamMcpServer>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new TeamMcpServer(reader.GetString(0), reader.GetString(1), Instant.Parse(reader.GetString(2))));
        return list;
    }

    public async Task<ILookup<string, string>> AllAssignmentsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT server_id, team FROM team_mcp_servers ORDER BY team COLLATE NOCASE";

        var list = new List<(string Id, string Team)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add((reader.GetString(0), reader.GetString(1)));
        return list.ToLookup(a => a.Id, a => a.Team, StringComparer.Ordinal);
    }

    public async Task<AssignOutcome> AssignAsync(string team, string id, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM mcp_servers WHERE id = $id";
            exists.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) return AssignOutcome.NoConnection;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO team_mcp_servers (team, server_id, assigned_at) VALUES ($team, $id, $at)";
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", Instant.Now());
            if (await command.ExecuteNonQueryAsync(ct) == 0) return AssignOutcome.Already;
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return AssignOutcome.Assigned;
    }

    public async Task<bool> UnassignAsync(string team, string id, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM team_mcp_servers WHERE team = $team AND server_id = $id";
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$id", id);
            if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task<bool> NameTakenAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM mcp_servers WHERE name = $name COLLATE NOCASE AND id <> $id";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
