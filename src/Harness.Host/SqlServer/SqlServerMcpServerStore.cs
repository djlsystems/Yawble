using Harness.Contracts;
using Harness.SqlServer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IMcpServerStore"/>: <c>mcp_servers</c> and <c>team_mcp_servers</c>
/// (auth-033), <see cref="McpServerStore"/> statement for statement. Ids compare exactly, as SQLite's binary
/// columns do (<see cref="SqlServerSql.Same"/>); a name folds case, as SQLite's NOCASE column does, and is
/// ordered as it is (<see cref="SqlServerSql.NoCaseOrder"/>). A save holds the names it checked
/// (<c>UPDLOCK, HOLDLOCK</c>), so two saves of one name cannot both land. Lock order, every write:
/// <c>mcp_servers</c>, then <c>team_mcp_servers</c>, then <c>tenant_events</c>.
/// </summary>
public sealed class SqlServerMcpServerStore(string connectionString, IDataProtectionProvider protection) : IMcpServerStore
{
    private readonly ConnectionCipher _cipher = new(protection);

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    private static readonly string IdMatch = SqlServerSql.Same("id", "@id");

    private static readonly string ServerMatch = SqlServerSql.Same("server_id", "@id");

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    public Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<McpServerRecord>>(
            async (connection, token) =>
            {
                await using var command = Command(connection, null,
                    $"SELECT {McpServerRows.Columns} FROM mcp_servers ORDER BY {SqlServerSql.NoCaseOrder("name")}, DATALENGTH(name), id");

                var list = new List<McpServerRecord>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(McpServerRows.Read(reader));
                return list;
            },
            ct);

    public Task<McpServerRecord?> GetAsync(string id, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, $"SELECT {McpServerRows.Columns} FROM mcp_servers WHERE {IdMatch}");
                command.Parameters.AddWithValue("@id", id);

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? McpServerRows.Read(reader) : null;
            },
            ct);

    public Task<string?> UrlAsync(string id, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, $"SELECT url_protected FROM mcp_servers WHERE {IdMatch}");
                command.Parameters.AddWithValue("@id", id);
                return _cipher.Unprotect(await command.ExecuteScalarAsync(token) as string);
            },
            ct);

    public Task<McpServerWrite> AddAsync(McpServerRecord server, string? url, TriggerAudit audit, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                if (await NameTakenAsync(connection, transaction, server.Name, server.Id, token)) return McpServerWrite.NameTaken;

                await using (var command = Command(connection, transaction,
                    """
                    INSERT INTO mcp_servers (id, name, kind, url_shown, url_protected, connection_id, field, header_name, value_template,
                        command, package, version, args_json, env_json, status, created_at, updated_at)
                    VALUES (@id, @name, @kind, @urlShown, @urlProtected, @connection, @field, @header, @template,
                        @command, @package, @version, @args, @env, @status, @created, @updated)
                    """))
                {
                    McpServerRows.Settings(command, "@", server, _cipher.ProtectOrNull(url));
                    await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return McpServerWrite.Done;
            },
            ct);

    public Task<McpServerWrite> UpdateAsync(McpServerRecord server, string? url, TriggerAudit audit, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                if (await NameTakenAsync(connection, transaction, server.Name, server.Id, token)) return McpServerWrite.NameTaken;

                await using (var command = Command(connection, transaction,
                    $"""
                     UPDATE mcp_servers SET name = @name, kind = @kind, url_shown = @urlShown,
                         url_protected = CASE WHEN @keepUrl = 1 THEN url_protected ELSE @urlProtected END,
                         connection_id = @connection, field = @field, header_name = @header, value_template = @template,
                         command = @command, package = @package, version = @version, args_json = @args, env_json = @env,
                         status = @status, status_reason = NULL,
                         tools_json = CASE WHEN @kind = N'url' THEN tools_json ELSE NULL END, updated_at = @updated
                     WHERE {IdMatch}
                     """))
                {
                    McpServerRows.Settings(command, "@", server, _cipher.ProtectOrNull(url));
                    command.Parameters.AddWithValue("@keepUrl", url is null ? 1 : 0);
                    if (await command.ExecuteNonQueryAsync(token) == 0) return McpServerWrite.Missing;
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return McpServerWrite.Done;
            },
            ct);

    public Task RecordCheckAsync(
        string id, string? status, string? reason, IReadOnlyList<string>? tools, DateTimeOffset at, TriggerAudit audit, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var command = Command(connection, transaction,
                    $"""
                     UPDATE mcp_servers SET
                         status = COALESCE(@status, status),
                         status_reason = CASE WHEN @status IS NULL THEN status_reason ELSE @reason END,
                         tools_json = COALESCE(@tools, tools_json),
                         checked_at = @at
                     WHERE {IdMatch}
                     """))
                {
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.Add("@status", System.Data.SqlDbType.NVarChar, 40).Value = (object?)status ?? DBNull.Value;
                    command.Parameters.Add("@reason", System.Data.SqlDbType.NVarChar, -1).Value = (object?)reason ?? DBNull.Value;
                    command.Parameters.Add("@tools", System.Data.SqlDbType.NVarChar, -1).Value =
                        tools is null ? DBNull.Value : McpServerRecord.ListJson(tools);
                    command.Parameters.AddWithValue("@at", Instant.Stamp(at));
                    await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public Task<IReadOnlyList<string>?> RemoveAsync(
        string id, Func<IReadOnlyList<string>, IReadOnlyList<TriggerAudit>> rows, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<string>?>(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var held = Command(connection, transaction, $"SELECT COUNT(*) FROM mcp_servers WITH (UPDLOCK, HOLDLOCK) WHERE {IdMatch}"))
                {
                    held.Parameters.AddWithValue("@id", id);
                    if (Convert.ToInt64(await held.ExecuteScalarAsync(token)) == 0) return null;
                }

                var teams = new List<string>();
                await using (var command = Command(connection, transaction,
                    $"SELECT team FROM team_mcp_servers WITH (UPDLOCK, HOLDLOCK) WHERE {ServerMatch} ORDER BY {SqlServerSql.NoCaseOrder("team")}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) teams.Add(reader.GetString(0));
                }

                await using (var command = Command(connection, transaction,
                    $"DELETE FROM team_mcp_servers WHERE {ServerMatch}; DELETE FROM mcp_servers WHERE {IdMatch};"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    await command.ExecuteNonQueryAsync(token);
                }

                foreach (var row in rows(teams)) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, row, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return teams;
            },
            ct);

    public Task<IReadOnlyList<TeamMcpServer>> AssignedAsync(string team, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<TeamMcpServer>>(
            async (connection, token) =>
            {
                await using var command = Command(connection, null,
                    "SELECT team, server_id, assigned_at FROM team_mcp_servers WHERE team = @team ORDER BY assigned_at, server_id");
                command.Parameters.AddWithValue("@team", team);

                var list = new List<TeamMcpServer>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(new TeamMcpServer(reader.GetString(0), reader.GetString(1), Instant.Parse(reader.GetString(2))));
                return list;
            },
            ct);

    public Task<ILookup<string, string>> AllAssignmentsAsync(CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, $"SELECT server_id, team FROM team_mcp_servers ORDER BY {SqlServerSql.NoCaseOrder("team")}");

                var list = new List<(string Id, string Team)>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add((reader.GetString(0), reader.GetString(1)));
                return list.ToLookup(a => a.Id, a => a.Team, StringComparer.Ordinal);
            },
            ct);

    public Task<AssignOutcome> AssignAsync(string team, string id, TriggerAudit audit, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var exists = Command(connection, transaction, $"SELECT COUNT(*) FROM mcp_servers WITH (UPDLOCK, HOLDLOCK) WHERE {IdMatch}"))
                {
                    exists.Parameters.AddWithValue("@id", id);
                    if (Convert.ToInt64(await exists.ExecuteScalarAsync(token)) == 0) return AssignOutcome.NoConnection;
                }

                await using (var command = Command(connection, transaction,
                    $"""
                     IF NOT EXISTS (SELECT 1 FROM team_mcp_servers WITH (UPDLOCK, HOLDLOCK) WHERE team = @team AND {ServerMatch})
                         INSERT INTO team_mcp_servers (team, server_id, assigned_at) VALUES (@team, @id, @at);
                     """))
                {
                    command.Parameters.AddWithValue("@team", team);
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@at", Instant.Now());
                    if (await command.ExecuteNonQueryAsync(token) <= 0) return AssignOutcome.Already;
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return AssignOutcome.Assigned;
            },
            ct);

    public Task<bool> UnassignAsync(string team, string id, TriggerAudit audit, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var command = Command(connection, transaction, $"DELETE FROM team_mcp_servers WHERE team = @team AND {ServerMatch}"))
                {
                    command.Parameters.AddWithValue("@team", team);
                    command.Parameters.AddWithValue("@id", id);
                    if (await command.ExecuteNonQueryAsync(token) == 0) return false;
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    private static async Task<bool> NameTakenAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string id, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            $"SELECT COUNT(*) FROM mcp_servers WITH (UPDLOCK, HOLDLOCK) WHERE name = @name AND NOT {IdMatch}");
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }
}
