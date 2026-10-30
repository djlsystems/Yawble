using System.Data.Common;
using Harness.Contracts;
using Harness.SqlServer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IConnectionStore"/>: <c>oauth_providers</c>, <c>connections</c>,
/// <c>connection_flows</c> and <c>imap_connections</c> (<c>auth.sqlserver-baseline-3</c>),
/// <see cref="ConnectionStore"/> statement for statement, whose remarks are the argument. What a row holds
/// and how it reads back are <see cref="ConnectionRows"/>', and every credential column is sealed by
/// <see cref="ConnectionCipher"/>, both shared with it: a token, a client secret, a PKCE verifier or a
/// mailbox password written by either provider is the same ciphertext text and opens through the other.
/// Every write a person makes lands with its tenant row in one transaction, through
/// <see cref="SqlServerTenantAuditRow"/>.
///
/// <para>
/// IDS COMPARE EXACTLY, AS SQLITE'S BINARY COLUMNS DO, their length included
/// (<see cref="SqlServerSql.Same"/>): a provider id, a connection id or a flow's state from a request
/// with a trailing space finds nothing here, as it finds nothing on SQLite. The list is in SQLite's
/// NOCASE order of the name (<see cref="SqlServerSql.NoCaseOrder"/>), a shorter name first where only
/// padding tells two apart, then the id.
/// </para>
///
/// <para>
/// THE READ-THEN-WRITES HOLD WHAT THEY READ. A provider's save is one <c>MERGE ... HOLDLOCK</c>, so two
/// first saves make one row. A provider's removal holds its row and then its connections under
/// <c>UPDLOCK, HOLDLOCK</c>, so a connection of it added meanwhile waits and is seen, never orphaned. A
/// connection's removal holds the bindings it checked (<c>team_member_config</c>), so a member bound to it
/// meanwhile waits and is seen. A flow is consumed under its row's update lock, and marked used only
/// while unused, so two callbacks with one state cannot both win. A refresh and every other write is one
/// statement on one row: two refreshes racing leave one of them whole, never a mix.
/// </para>
///
/// <para>
/// LOCK ORDER, EVERY WRITE: <c>team_member_config</c> (a connection's removal alone), then
/// <c>oauth_providers</c>, then <c>connections</c>, then <c>imap_connections</c>, then
/// <c>connection_flows</c>, then <c>tenant_events</c>. No write here takes a <c>teams</c> or
/// <c>team_members</c> row, and a settings save (teams, member, settings) takes nothing here, so the
/// bindings held by a removal are the end of every chain they are in.
/// </para>
/// </summary>
public sealed class SqlServerConnectionStore(string connectionString, IDataProtectionProvider protection) : IConnectionStore
{
    private readonly ConnectionCipher _cipher = new(protection);

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    private const string ProviderSelect =
        "SELECT id, kind, name, client_id, CAST(CASE WHEN client_secret_protected IS NULL THEN 0 ELSE 1 END AS BIGINT), "
        + ConnectionRows.ProviderTail + " FROM oauth_providers";

    private const string ConnectionSelect =
        "SELECT " + ConnectionRows.ConnectionHead + ", "
        + "CAST(CASE WHEN m.password_protected IS NULL THEN 0 ELSE 1 END AS BIGINT), c.tenant, " + ConnectionRows.CredentialTail + " "
        + "FROM connections c LEFT JOIN imap_connections m ON m.id = c.id " + ConnectionRows.CredentialJoin;

    private static readonly string IdMatch = SqlServerSql.Same("id", "@id");

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    /// <summary>One write and its tenant row, in one transaction; <paramref name="write"/> answering false
    /// drops both and answers false.</summary>
    private Task<bool> WriteAsync(
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<bool>> write, TriggerAudit? audit, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                if (!await write(connection, transaction, token)) return false;
                if (audit is not null) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    // ---- providers ------------------------------------------------------------------------------

    public Task<IReadOnlyList<StoredProvider>> ProvidersAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<StoredProvider>>(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, $"{ProviderSelect} ORDER BY id");

                var list = new List<StoredProvider>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(ConnectionRows.ReadProvider(reader));
                return list;
            },
            ct);

    public Task<StoredProvider?> ProviderAsync(string id, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, $"{ProviderSelect} WHERE {IdMatch}");
                command.Parameters.AddWithValue("@id", id);

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? ConnectionRows.ReadProvider(reader) : null;
            },
            ct);

    public async Task<string?> ClientSecretAsync(string id, CancellationToken ct = default) =>
        _cipher.Unprotect(await ScalarTextAsync($"SELECT client_secret_protected FROM oauth_providers WHERE {IdMatch}", id, ct));

    public Task SaveProviderAsync(StoredProvider provider, string? secret, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = Command(
                    connection, transaction,
                    """
                    MERGE oauth_providers WITH (HOLDLOCK) AS t
                    USING (SELECT @id AS id) AS n
                    ON t.id = n.id
                    WHEN MATCHED THEN UPDATE SET
                        kind = @kind,
                        name = @name,
                        client_id = @clientId,
                        client_secret_protected = CASE WHEN @keepSecret = 1 THEN t.client_secret_protected ELSE @secret END,
                        tenant = @tenant,
                        authorize_url = @authorize,
                        token_url = @token,
                        userinfo_url = @userinfo,
                        default_scopes = @scopes,
                        updated_at = @at
                    WHEN NOT MATCHED THEN
                        INSERT (id, kind, name, client_id, client_secret_protected, tenant, authorize_url, token_url, userinfo_url, default_scopes, updated_at)
                        VALUES (@id, @kind, @name, @clientId, @secret, @tenant, @authorize, @token, @userinfo, @scopes, @at);
                    """);
                command.Parameters.AddWithValue("@id", provider.Id);
                command.Parameters.AddWithValue("@kind", provider.Kind);
                AddText(command, "@name", provider.Name);
                AddText(command, "@clientId", provider.ClientId);
                AddText(command, "@secret", _cipher.ProtectOrNull(secret) as string);
                command.Parameters.AddWithValue("@keepSecret", secret is null ? 1 : 0);
                AddText(command, "@tenant", provider.Tenant);
                AddText(command, "@authorize", provider.AuthorizeUrl);
                AddText(command, "@token", provider.TokenUrl);
                AddText(command, "@userinfo", provider.UserinfoUrl);
                command.Parameters.AddWithValue("@scopes", ConnectionRows.Scopes(provider.DefaultScopes));
                command.Parameters.AddWithValue("@at", Instant.Now());
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            audit, ct);

    public async Task<(bool Removed, IReadOnlyList<string> Connections)> DeleteProviderAsync(string id, TriggerAudit audit, CancellationToken ct = default)
    {
        IReadOnlyList<string> names = [];

        var removed = await WriteAsync(
            async (connection, transaction, token) =>
            {
                // THE PROVIDER, THEN EVERY CONNECTION OF IT, HELD: a connection inserted meanwhile waits.
                await using (var hold = Command(connection, transaction, $"SELECT COUNT(*) FROM oauth_providers WITH (UPDLOCK, HOLDLOCK) WHERE {IdMatch}"))
                {
                    hold.Parameters.AddWithValue("@id", id);
                    await hold.ExecuteScalarAsync(token);
                }

                var found = new List<string>();
                await using (var command = Command(
                    connection, transaction,
                    $"SELECT name FROM connections WITH (UPDLOCK, HOLDLOCK) WHERE {SqlServerSql.Same("provider", "@id")} ORDER BY name, DATALENGTH(name)"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) found.Add(reader.GetString(0));
                }

                names = found;
                if (found.Count > 0) return false;

                await using var delete = Command(connection, transaction, $"DELETE FROM oauth_providers WHERE {IdMatch}");
                delete.Parameters.AddWithValue("@id", id);
                await delete.ExecuteNonQueryAsync(token);
                return true;
            },
            audit, ct);

        return removed ? (true, []) : (false, names);
    }

    // ---- connections ----------------------------------------------------------------------------

    public Task<IReadOnlyList<ConnectionRecord>> ListAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<ConnectionRecord>>(
            async (connection, token) =>
            {
                await using var command = Command(
                    connection, null,
                    $"{ConnectionSelect} ORDER BY {SqlServerSql.NoCaseOrder("c.name")}, DATALENGTH(c.name), c.id");

                var list = new List<ConnectionRecord>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add(ConnectionRows.ReadConnection(reader));
                return list;
            },
            ct);

    public Task<ConnectionRecord?> GetAsync(string id, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, $"{ConnectionSelect} WHERE {SqlServerSql.Same("c.id", "@id")}");
                command.Parameters.AddWithValue("@id", id);
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? ConnectionRows.ReadConnection(reader) : null;
            },
            ct);

    public Task<ConnectionTokens?> TokensAsync(string id, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = Command(
                    connection, null,
                    $"SELECT refresh_token_protected, access_token_protected, access_expires_at FROM connections WHERE {IdMatch}");
                command.Parameters.AddWithValue("@id", id);
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? ConnectionRows.ReadTokens(reader, _cipher) : null;
            },
            ct);

    public Task InsertAsync(
        ConnectionRecord record, ConnectionTokens tokens, string? connectedBy, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await EnsureNameFreeAsync(connection, transaction, record.Name, null, token);

                await using var command = Command(
                    connection, transaction,
                    """
                    INSERT INTO connections (id, name, provider, account, scopes, refresh_token_protected, access_token_protected,
                                             access_expires_at, connected_at, refreshed_at, status, status_reason, connected_by, tenant)
                    VALUES (@id, @name, @provider, @account, @scopes, @refresh, @access, @expires, @at, NULL, 'ok', NULL, @by, @tenant)
                    """);
                command.Parameters.AddWithValue("@id", record.Id);
                command.Parameters.AddWithValue("@name", record.Name);
                command.Parameters.AddWithValue("@provider", record.Provider);
                command.Parameters.AddWithValue("@account", record.Account);
                command.Parameters.AddWithValue("@scopes", ConnectionRows.Scopes(record.Scopes));
                AddTokens(command, tokens);
                command.Parameters.AddWithValue("@at", Instant.Stamp(record.ConnectedAt));
                AddText(command, "@by", connectedBy);
                AddText(command, "@tenant", record.Tenant);
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            audit, ct);

    public Task ReconnectAsync(
        string id, IReadOnlyList<string> scopes, ConnectionTokens tokens, TriggerAudit audit, CancellationToken ct = default, string? tenant = null,
        string? account = null) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = Command(
                    connection, transaction,
                    $"""
                     UPDATE connections SET
                         scopes = @scopes,
                         refresh_token_protected = COALESCE(@refresh, refresh_token_protected),
                         access_token_protected = @access,
                         access_expires_at = @expires,
                         refreshed_at = @now,
                         status = 'ok',
                         status_reason = NULL,
                         tenant = @tenant,
                         account = COALESCE(@account, account)
                     WHERE {IdMatch}
                     """);
                command.Parameters.AddWithValue("@id", id);
                AddText(command, "@account", account);
                command.Parameters.AddWithValue("@scopes", ConnectionRows.Scopes(scopes));
                AddText(command, "@tenant", tenant);
                command.Parameters.AddWithValue("@now", Instant.Now());
                AddTokens(command, tokens);
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            audit, ct);

    public Task StoreRefreshAsync(string id, ConnectionTokens tokens, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"""
             UPDATE connections SET
                 refresh_token_protected = COALESCE(@refresh, refresh_token_protected),
                 access_token_protected = @access,
                 access_expires_at = @expires,
                 refreshed_at = @now
             WHERE {IdMatch}
             """,
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@now", Instant.Now());
                AddTokens(command, tokens);
            },
            ct);

    public Task StoreRotatedRefreshAsync(string id, string? refreshToken, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"""
             UPDATE connections SET
                 refresh_token_protected = COALESCE(@refresh, refresh_token_protected),
                 refreshed_at = @now
             WHERE {IdMatch}
             """,
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@now", Instant.Now());
                AddText(command, "@refresh", _cipher.ProtectOrNull(refreshToken) as string);
            },
            ct);

    public Task MarkNeedsReconnectAsync(string id, string reason, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = Command(
                    connection, transaction,
                    $"UPDATE connections SET status = 'needs-reconnect', status_reason = @reason, access_token_protected = NULL, access_expires_at = NULL WHERE {IdMatch}");
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@reason", reason);
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            audit, ct);

    public Task RecordTestAsync(string id, string? needsYou, DateTimeOffset at, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = Command(
                    connection, transaction,
                    needsYou is null
                        ? $"UPDATE connections SET status = 'ok', status_reason = NULL, refreshed_at = @at WHERE {IdMatch}"
                        : $"UPDATE connections SET status = 'needs-reconnect', status_reason = @reason, access_token_protected = NULL, access_expires_at = NULL WHERE {IdMatch}");
                command.Parameters.AddWithValue("@id", id);
                if (needsYou is null) command.Parameters.AddWithValue("@at", Instant.Stamp(at));
                else command.Parameters.AddWithValue("@reason", needsYou);
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            audit, ct);

    public Task<bool> RenameAsync(string id, string name, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await EnsureNameFreeAsync(connection, transaction, name, id, token);

                await using var command = Command(connection, transaction, $"UPDATE connections SET name = @name WHERE {IdMatch}");
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@name", name);
                return await command.ExecuteNonQueryAsync(token) > 0;
            },
            audit, ct);

    public async Task<(bool Deleted, IReadOnlyList<ConnectionUse> UsedBy)> DeleteAsync(
        string id, TriggerAudit audit, CancellationToken ct = default,
        Func<IReadOnlyList<ConnectionUse>, IReadOnlyList<TriggerAudit>>? unbind = null)
    {
        IReadOnlyList<ConnectionUse> used = [];
        var refused = false;

        var deleted = await RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                refused = false;

                // THE BINDINGS, HELD: a member bound to this connection meanwhile waits, and is seen.
                var rows = await BindingRowsAsync(connection, transaction, held: true, token);
                used = [.. rows.SelectMany(r => ConnectionRows.Bindings(r.Team, r.Member, r.Json)).Where(b => b.Id == id).Select(b => b.Use)];
                if (used.Count > 0 && unbind is null)
                {
                    refused = true;
                    return false;
                }

                await using (var command = Command(connection, transaction, $"DELETE FROM connections WHERE {IdMatch}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    if (await command.ExecuteNonQueryAsync(token) == 0) return false;
                }

                foreach (var table in new[] { "imap_connections", "credential_connections" })
                {
                    await using var command = Command(connection, transaction, $"DELETE FROM {table} WHERE {IdMatch}");
                    command.Parameters.AddWithValue("@id", id);
                    await command.ExecuteNonQueryAsync(token);
                }

                // EVERY TEAM IT WAS ASSIGNED TO loses it here, in the same transaction.
                await using (var command = Command(connection, transaction, $"DELETE FROM team_connections WHERE {SqlServerSql.Same("connection_id", "@id")}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    await command.ExecuteNonQueryAsync(token);
                }

                // EVERY MEMBER THAT BOUND IT loses those slots here, in the same transaction.
                foreach (var row in rows.Where(r => used.Any(u => u.Team == r.Team && u.Member == r.Member)))
                {
                    await using var command = Command(
                        connection, transaction,
                        $"UPDATE team_member_config SET connections_json = @json WHERE {SqlServerSql.Same("team", "@team")} AND {SqlServerSql.Same("name", "@name")}");
                    command.Parameters.AddWithValue("@team", row.Team);
                    command.Parameters.AddWithValue("@name", row.Member);
                    command.Parameters.AddWithValue("@json", ConnectionRows.Unbound(row.Json, id));
                    await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                if (used.Count > 0)
                {
                    foreach (var member in unbind!(used)) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, member, token);
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

        return deleted ? (true, used) : (false, refused ? used : []);
    }

    public Task<bool> NameTakenAsync(string name, string? exceptId = null, CancellationToken ct = default) =>
        RunAsync((connection, token) => NameTakenAsync(connection, null, name, exceptId, token), ct);

    /// <summary>Held under <c>UPDLOCK, HOLDLOCK</c> inside a write, so two writes naming one name wait on
    /// each other and the second sees the first.</summary>
    private static async Task<bool> NameTakenAsync(
        SqlConnection connection, SqlTransaction? transaction, string name, string? exceptId, CancellationToken ct)
    {
        await using var command = Command(
            connection, transaction, $"SELECT id, name FROM connections {(transaction is null ? "" : "WITH (UPDLOCK, HOLDLOCK) ")}");

        var key = ConnectionRows.NameKey(name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetString(0) != exceptId && ConnectionRows.NameKey(reader.GetString(1)) == key) return true;
        }

        return false;
    }

    private static async Task EnsureNameFreeAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string? exceptId, CancellationToken ct)
    {
        if (await NameTakenAsync(connection, transaction, name, exceptId, ct)) throw new ConnectionNameTakenException(name.Trim());
    }

    public Task RecordAsync(TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync((_, _, _) => Task.FromResult(true), audit, ct);

    public Task<IReadOnlyList<ConnectionUse>> UsedByAsync(string id, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<ConnectionUse>>(
            async (connection, token) =>
                [.. (await BindingsAsync(connection, null, held: false, token)).Where(b => b.Id == id).Select(b => b.Use)],
            ct);

    public Task<ILookup<string, ConnectionUse>> AllUsesAsync(CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
                (await BindingsAsync(connection, null, held: false, token)).ToLookup(b => b.Id, b => b.Use, StringComparer.Ordinal),
            ct);

    /// <summary>Every binding on the instance in SQLite's (NOCASE team, member) order; held under
    /// <c>UPDLOCK, HOLDLOCK</c> for a removal's check.</summary>
    private static async Task<List<(string Team, string Member, string Json)>> BindingRowsAsync(
        SqlConnection connection, SqlTransaction? transaction, bool held, CancellationToken ct)
    {
        await using var command = Command(
            connection, transaction,
            $"SELECT team, name, connections_json FROM team_member_config {(held ? "WITH (UPDLOCK, HOLDLOCK) " : "")}"
            + $"WHERE connections_json <> N'{{}}' ORDER BY {SqlServerSql.NoCaseOrder("team")}, {SqlServerSql.NoCaseOrder("name")}");

        var list = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    private static async Task<List<(string Id, ConnectionUse Use)>> BindingsAsync(
        SqlConnection connection, SqlTransaction? transaction, bool held, CancellationToken ct)
    {
        await using var command = Command(
            connection, transaction,
            $"SELECT team, name, connections_json FROM team_member_config {(held ? "WITH (UPDLOCK, HOLDLOCK) " : "")}"
            + $"WHERE connections_json <> N'{{}}' ORDER BY {SqlServerSql.NoCaseOrder("team")}, {SqlServerSql.NoCaseOrder("name")}");

        var list = new List<(string, ConnectionUse)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.AddRange(ConnectionRows.Bindings(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return list;
    }

    // ---- team assignments -----------------------------------------------------------------------

    private static readonly string TeamMatch = "team = @team";

    public Task<IReadOnlyList<TeamConnection>> AssignedAsync(string team, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<TeamConnection>>(
            async (connection, token) =>
            {
                await using var command = Command(
                    connection, null,
                    $"SELECT team, connection_id, assigned_at FROM team_connections WHERE {TeamMatch} ORDER BY assigned_at, connection_id");
                command.Parameters.AddWithValue("@team", team);

                var list = new List<TeamConnection>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    list.Add(new TeamConnection(reader.GetString(0), reader.GetString(1), Instant.Parse(reader.GetString(2))));
                }

                return list;
            },
            ct);

    public Task<ILookup<string, string>> AllAssignmentsAsync(CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, "SELECT connection_id, team FROM team_connections ORDER BY team");

                var list = new List<(string Id, string Team)>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) list.Add((reader.GetString(0), reader.GetString(1)));
                return list.ToLookup(a => a.Id, a => a.Team, StringComparer.Ordinal);
            },
            ct);

    public Task<AssignOutcome> AssignAsync(string team, string connectionId, TriggerAudit audit, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var exists = Command(connection, transaction, $"SELECT COUNT(*) FROM connections WITH (UPDLOCK, HOLDLOCK) WHERE {IdMatch}"))
                {
                    exists.Parameters.AddWithValue("@id", connectionId);
                    if (Convert.ToInt64(await exists.ExecuteScalarAsync(token)) == 0) return AssignOutcome.NoConnection;
                }

                if (!await InsertAssignmentAsync(connection, transaction, team, connectionId, token)) return AssignOutcome.Already;

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return AssignOutcome.Assigned;
            },
            ct);

    public async Task AssignAllAsync(
        DbConnection connection, DbTransaction transaction, string team,
        IReadOnlyList<(string ConnectionId, TriggerAudit Audit)> assignments, CancellationToken ct = default)
    {
        var sql = (SqlConnection)connection;
        var inside = (SqlTransaction)transaction;

        foreach (var (id, audit) in assignments)
        {
            if (await InsertAssignmentAsync(sql, inside, team, id, ct)) await SqlServerTenantAuditRow.AppendAsync(sql, inside, audit, ct);
        }
    }

    public async Task<(bool Removed, IReadOnlyList<ConnectionUse> Unbound)> UnassignAsync(
        string team, string connectionId, TriggerAudit audit,
        Func<IReadOnlyList<ConnectionUse>, IReadOnlyList<TriggerAudit>> unbind, CancellationToken ct = default)
    {
        IReadOnlyList<ConnectionUse> used = [];

        var removed = await RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                // THE BINDINGS, HELD: a member of the team bound to it meanwhile waits, and is seen.
                var rows = (await BindingRowsAsync(connection, transaction, held: true, token))
                    .Where(r => string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                await using (var command = Command(
                    connection, transaction,
                    $"DELETE FROM team_connections WHERE {TeamMatch} AND {SqlServerSql.Same("connection_id", "@id")}"))
                {
                    command.Parameters.AddWithValue("@team", team);
                    command.Parameters.AddWithValue("@id", connectionId);
                    if (await command.ExecuteNonQueryAsync(token) == 0) return false;
                }

                used = [.. rows.SelectMany(r => ConnectionRows.Bindings(r.Team, r.Member, r.Json)).Where(b => b.Id == connectionId).Select(b => b.Use)];

                foreach (var row in rows.Where(r => used.Any(u => u.Member == r.Member)))
                {
                    await using var command = Command(
                        connection, transaction,
                        $"UPDATE team_member_config SET connections_json = @json WHERE {SqlServerSql.Same("team", "@team")} AND {SqlServerSql.Same("name", "@name")}");
                    command.Parameters.AddWithValue("@team", row.Team);
                    command.Parameters.AddWithValue("@name", row.Member);
                    command.Parameters.AddWithValue("@json", ConnectionRows.Unbound(row.Json, connectionId));
                    await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                if (used.Count > 0)
                {
                    foreach (var row in unbind(used)) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, row, token);
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

        return removed ? (true, used) : (false, []);
    }

    public Task<IReadOnlyList<string>> CopyAssignmentsAsync(
        string from, string to, Func<string, TriggerAudit> audit, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<string>>(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                var ids = new List<string>();
                await using (var command = Command(
                    connection, transaction,
                    $"SELECT connection_id FROM team_connections WHERE {TeamMatch} ORDER BY assigned_at, connection_id"))
                {
                    command.Parameters.AddWithValue("@team", from);
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
                }

                var copied = new List<string>();
                foreach (var id in ids)
                {
                    if (!await InsertAssignmentAsync(connection, transaction, to, id, token)) continue;
                    await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit(id), token);
                    copied.Add(id);
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return copied;
            },
            ct);

    /// <summary>One assignment, inside a write's transaction: false when it was already there.</summary>
    private static async Task<bool> InsertAssignmentAsync(
        SqlConnection connection, SqlTransaction transaction, string team, string connectionId, CancellationToken ct)
    {
        await using var command = Command(
            connection, transaction,
            "INSERT INTO team_connections (team, connection_id, assigned_at) "
            + "SELECT @team, @id, @at WHERE NOT EXISTS (SELECT 1 FROM team_connections WITH (UPDLOCK, HOLDLOCK) "
            + $"WHERE {TeamMatch} AND {SqlServerSql.Same("connection_id", "@id")})");
        command.Parameters.AddWithValue("@team", team);
        command.Parameters.AddWithValue("@id", connectionId);
        command.Parameters.AddWithValue("@at", Instant.Now());
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---- mailboxes ------------------------------------------------------------------------------

    public Task InsertMailboxAsync(
        ConnectionRecord record, MailboxSettings settings, string password, string? connectedBy, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await EnsureNameFreeAsync(connection, transaction, record.Name, null, token);

                await using (var command = Command(
                    connection, transaction,
                    """
                    INSERT INTO connections (id, name, provider, account, scopes, connected_at, refreshed_at, status, status_reason, connected_by)
                    VALUES (@id, @name, @provider, @account, '[]', @at, @verified, 'ok', NULL, @by)
                    """))
                {
                    command.Parameters.AddWithValue("@id", record.Id);
                    command.Parameters.AddWithValue("@name", record.Name);
                    command.Parameters.AddWithValue("@provider", MailboxSettings.Kind);
                    command.Parameters.AddWithValue("@account", record.Account);
                    command.Parameters.AddWithValue("@at", Instant.Stamp(record.ConnectedAt));
                    command.Parameters.AddWithValue("@verified", ConnectionRows.MailboxVerified(record));
                    AddText(command, "@by", connectedBy);
                    await command.ExecuteNonQueryAsync(token);
                }

                await using (var command = Command(
                    connection, transaction,
                    """
                    INSERT INTO imap_connections (id, username, imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security, preset, password_protected)
                    VALUES (@id, @username, @imapHost, @imapPort, @imapSecurity, @smtpHost, @smtpPort, @smtpSecurity, @preset, @password)
                    """))
                {
                    command.Parameters.AddWithValue("@id", record.Id);
                    command.Parameters.AddWithValue("@username", settings.Username);
                    command.Parameters.AddWithValue("@imapHost", settings.Imap.Host);
                    command.Parameters.AddWithValue("@imapPort", (long)settings.Imap.Port);
                    command.Parameters.AddWithValue("@imapSecurity", settings.Imap.Security);
                    command.Parameters.AddWithValue("@smtpHost", settings.Smtp.Host);
                    command.Parameters.AddWithValue("@smtpPort", (long)settings.Smtp.Port);
                    command.Parameters.AddWithValue("@smtpSecurity", settings.Smtp.Security);
                    AddText(command, "@preset", settings.Preset);
                    command.Parameters.AddWithValue("@password", _cipher.Protect(password));
                    await command.ExecuteNonQueryAsync(token);
                }

                return true;
            },
            audit, ct);

    public async Task<string?> MailboxPasswordAsync(string id, CancellationToken ct = default) =>
        _cipher.Unprotect(await ScalarTextAsync($"SELECT password_protected FROM imap_connections WHERE {IdMatch}", id, ct));

    public Task<bool> UpdateMailboxPasswordAsync(
        string id, string password, DateTimeOffset verifiedAt, TriggerAudit audit, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await using (var command = Command(connection, transaction, $"UPDATE imap_connections SET password_protected = @password WHERE {IdMatch}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@password", _cipher.Protect(password));
                    if (await command.ExecuteNonQueryAsync(token) == 0) return false;
                }

                await using (var command = Command(
                    connection, transaction, $"UPDATE connections SET status = 'ok', status_reason = NULL, refreshed_at = @at WHERE {IdMatch}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@at", Instant.Stamp(verifiedAt));
                    await command.ExecuteNonQueryAsync(token);
                }

                return true;
            },
            audit, ct);

    public Task StoreMailboxVerifiedAsync(string id, DateTimeOffset at, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"UPDATE connections SET refreshed_at = @at WHERE {IdMatch} AND status = 'ok'",
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@at", Instant.Stamp(at));
            },
            ct);

    public Task<bool> UpdateMailboxAsync(
        string id, string account, MailboxSettings settings, string? password, DateTimeOffset verifiedAt, TriggerAudit audit,
        CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await using (var command = Command(
                    connection, transaction,
                    $"""
                     UPDATE imap_connections SET
                         username = @username, imap_host = @imapHost, imap_port = @imapPort, imap_security = @imapSecurity,
                         smtp_host = @smtpHost, smtp_port = @smtpPort, smtp_security = @smtpSecurity,
                         password_protected = COALESCE(@password, password_protected)
                     WHERE {IdMatch}
                     """))
                {
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@username", settings.Username);
                    command.Parameters.AddWithValue("@imapHost", settings.Imap.Host);
                    command.Parameters.AddWithValue("@imapPort", (long)settings.Imap.Port);
                    command.Parameters.AddWithValue("@imapSecurity", settings.Imap.Security);
                    command.Parameters.AddWithValue("@smtpHost", settings.Smtp.Host);
                    command.Parameters.AddWithValue("@smtpPort", (long)settings.Smtp.Port);
                    command.Parameters.AddWithValue("@smtpSecurity", settings.Smtp.Security);
                    AddText(command, "@password", password is null ? null : _cipher.Protect(password));
                    if (await command.ExecuteNonQueryAsync(token) == 0) return false;
                }

                await using (var command = Command(
                    connection, transaction,
                    $"UPDATE connections SET account = @account, status = 'ok', status_reason = NULL, refreshed_at = @at WHERE {IdMatch}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@account", account);
                    command.Parameters.AddWithValue("@at", Instant.Stamp(verifiedAt));
                    await command.ExecuteNonQueryAsync(token);
                }

                return true;
            },
            audit, ct);

    // ---- credentials (website, API key, username and password) --------------------------------------

    public Task InsertCredentialAsync(
        ConnectionRecord record, IReadOnlyDictionary<string, string> secrets, string? connectedBy, TriggerAudit audit, CancellationToken ct = default)
    {
        var settings = record.Credential ?? throw new ArgumentException("A credential connection has its fields.", nameof(record));

        return WriteAsync(
            async (connection, transaction, token) =>
            {
                await EnsureNameFreeAsync(connection, transaction, record.Name, null, token);

                await using (var command = Command(
                    connection, transaction,
                    """
                    INSERT INTO connections (id, name, provider, account, scopes, connected_at, refreshed_at, status, status_reason, connected_by)
                    VALUES (@id, @name, @provider, @account, '[]', @at, NULL, 'ok', NULL, @by)
                    """))
                {
                    command.Parameters.AddWithValue("@id", record.Id);
                    command.Parameters.AddWithValue("@name", record.Name);
                    command.Parameters.AddWithValue("@provider", record.Provider);
                    command.Parameters.AddWithValue("@account", record.Account);
                    command.Parameters.AddWithValue("@at", Instant.Stamp(record.ConnectedAt));
                    AddText(command, "@by", connectedBy);
                    await command.ExecuteNonQueryAsync(token);
                }

                await using (var command = Command(
                    connection, transaction,
                    "INSERT INTO credential_connections (id, preset, fields, secrets_protected) VALUES (@id, @preset, @fields, @secrets)"))
                {
                    command.Parameters.AddWithValue("@id", record.Id);
                    AddText(command, "@preset", settings.Preset);
                    command.Parameters.AddWithValue("@fields", settings.FieldsJson());
                    AddText(command, "@secrets", ConnectionRows.SealSecrets(_cipher, secrets) as string);
                    await command.ExecuteNonQueryAsync(token);
                }

                return true;
            },
            audit, ct);
    }

    public Task<IReadOnlyDictionary<string, string>?> CredentialSecretsAsync(string id, CancellationToken ct = default) =>
        RunAsync<IReadOnlyDictionary<string, string>?>(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, $"SELECT secrets_protected FROM credential_connections WHERE {IdMatch}");
                command.Parameters.AddWithValue("@id", id);
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return null;

                return ConnectionRows.OpenSecrets(_cipher, ConnectionRows.NullableText(reader, 0));
            },
            ct);

    public Task<bool> UpdateCredentialAsync(
        string id, string account, CredentialSettings settings, IReadOnlyDictionary<string, string> secrets, TriggerAudit audit,
        CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                await using (var command = Command(
                    connection, transaction, $"UPDATE credential_connections SET fields = @fields, secrets_protected = @secrets WHERE {IdMatch}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@fields", settings.FieldsJson());
                    AddText(command, "@secrets", ConnectionRows.SealSecrets(_cipher, secrets) as string);
                    if (await command.ExecuteNonQueryAsync(token) == 0) return false;
                }

                await using (var command = Command(
                    connection, transaction, $"UPDATE connections SET account = @account, status = 'ok', status_reason = NULL WHERE {IdMatch}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@account", account);
                    await command.ExecuteNonQueryAsync(token);
                }

                return true;
            },
            audit, ct);

    // ---- flows ----------------------------------------------------------------------------------

    public Task InsertFlowAsync(ConnectionFlow flow, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                // Old flows go as new ones arrive: nothing needs a flow a day after it was issued.
                await using (var sweep = Command(connection, transaction, "DELETE FROM connection_flows WHERE issued_at < @cutoff"))
                {
                    sweep.Parameters.AddWithValue("@cutoff", ConnectionRows.FlowCutoff(flow));
                    await sweep.ExecuteNonQueryAsync(token);
                }

                await using (var command = Command(
                    connection, transaction,
                    """
                    INSERT INTO connection_flows (state, user_id, provider, scopes, name, reconnect_id, redirect_uri, loopback, verifier_protected, issued_at, tenant)
                    VALUES (@state, @user, @provider, @scopes, @name, @reconnect, @redirect, @loopback, @verifier, @at, @tenant)
                    """))
                {
                    command.Parameters.AddWithValue("@state", flow.State);
                    command.Parameters.AddWithValue("@user", flow.UserId);
                    command.Parameters.AddWithValue("@provider", flow.Provider);
                    command.Parameters.AddWithValue("@scopes", ConnectionRows.Scopes(flow.Scopes));
                    AddText(command, "@name", flow.Name);
                    AddText(command, "@reconnect", flow.ReconnectId);
                    command.Parameters.AddWithValue("@redirect", flow.RedirectUri);
                    command.Parameters.AddWithValue("@loopback", flow.Loopback ? 1L : 0L);
                    command.Parameters.AddWithValue("@verifier", _cipher.Protect(flow.CodeVerifier));
                    command.Parameters.AddWithValue("@at", Instant.Stamp(flow.IssuedAt));
                    AddText(command, "@tenant", flow.Tenant);
                    await command.ExecuteNonQueryAsync(token);
                }

                return true;
            },
            audit: null, ct);

    public Task<ConnectionFlow?> ConsumeFlowAsync(string state, DateTimeOffset now, string? userId, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                var match = SqlServerSql.Same("state", "@state");

                ConnectionFlow? flow = null;

                await using (var read = Command(
                    connection, transaction, $"SELECT {ConnectionRows.FlowColumns} FROM connection_flows WITH (UPDLOCK, HOLDLOCK) WHERE {match}"))
                {
                    read.Parameters.AddWithValue("@state", state);
                    await using var reader = await read.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token)) flow = ConnectionRows.ReadFlow(reader, _cipher.Unprotect);
                }

                if (flow is null || (userId is not null && !string.Equals(flow.UserId, userId, StringComparison.Ordinal))) return null;

                if (flow.UsedAt is null)
                {
                    await using var mark = Command(connection, transaction, $"UPDATE connection_flows SET used_at = @now WHERE {match} AND used_at IS NULL");
                    mark.Parameters.AddWithValue("@state", state);
                    mark.Parameters.AddWithValue("@now", Instant.Stamp(now));
                    if (await mark.ExecuteNonQueryAsync(token) == 0) flow = flow with { UsedAt = now };
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return flow;
            },
            ct);

    // ---- helpers --------------------------------------------------------------------------------

    private Task<string?> ScalarTextAsync(string sql, string id, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = Command(connection, null, sql);
                command.Parameters.AddWithValue("@id", id);
                return await command.ExecuteScalarAsync(token) as string;
            },
            ct);

    private void AddTokens(SqlCommand command, ConnectionTokens tokens)
    {
        var (refresh, access, expires) = ConnectionRows.Tokens(_cipher, tokens);
        AddText(command, "@refresh", refresh as string);
        AddText(command, "@access", access as string);
        AddText(command, "@expires", expires as string);
    }

    /// <summary>A text parameter that may be null, typed so a null still has a type: <c>COALESCE</c> and
    /// <c>CASE</c> take their type from it.</summary>
    private static void AddText(SqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, System.Data.SqlDbType.NVarChar, -1).Value = (object?)value ?? DBNull.Value;
}
