using Harness.Contracts;
using Harness.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>A connected account as every route may show it: no token, ever.</summary>
public sealed record ConnectionRecord(
    string Id,
    string Name,
    string Provider,
    string Account,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? RefreshedAt,
    string Status,
    string? StatusReason,
    MailboxSettings? Mailbox = null,
    string? Tenant = null,
    CredentialSettings? Credential = null)
{
    public const string Ok = "ok";

    public const string NeedsReconnect = "needs-reconnect";

    /// <summary>"'Work mail' (person@example.com)", as every sentence names a connection.</summary>
    public string Named => Account.Length == 0 || string.Equals(Name, Account, StringComparison.Ordinal) ? $"'{Name}'" : $"'{Name}' ({Account})";

    /// <summary><c>oauth</c>, <c>imap</c>, <c>website</c>, <c>apikey</c> or <c>login</c>: see <see cref="ConnectionKinds"/>.</summary>
    public string Kind => ConnectionKinds.Of(Provider);
}

/// <summary>The tokens of one connection, decrypted for the Host's own use and never answered.</summary>
public sealed record ConnectionTokens(string? RefreshToken, string? AccessToken, DateTimeOffset? AccessExpiresAt);

/// <summary>A started flow waiting for its code. The verifier never leaves the Host.</summary>
public sealed record ConnectionFlow(
    string State,
    string UserId,
    string Provider,
    IReadOnlyList<string> Scopes,
    string? Name,
    string? ReconnectId,
    string RedirectUri,
    bool Loopback,
    string CodeVerifier,
    DateTimeOffset IssuedAt,
    DateTimeOffset? UsedAt,
    string? Tenant = null);

/// <summary>A plugin member binding a connection, by slot.</summary>
public sealed record ConnectionUse(string Team, string Member, string Slot);

/// <summary>
/// <c>oauth_providers</c>, <c>connections</c>, <c>connection_flows</c> (schema step auth-014) and
/// <c>imap_connections</c> (auth-020). EVERY credential column - the client secret, the refresh and
/// access tokens, the PKCE verifier, a mailbox's app password - is
/// Data Protection ciphertext under the instance's <c>&lt;dataRoot&gt;/keys</c>. Every write a person
/// makes lands with its <c>tenant_events</c> row in the same transaction, or not at all.
/// </summary>
public sealed class ConnectionStore(string databasePath, IDataProtectionProvider protection) : IConnectionStore
{
    private readonly ConnectionCipher _cipher = new(protection);

    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    // ---- providers ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<StoredProvider>> ProvidersAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT id, kind, name, client_id, client_secret_protected IS NOT NULL, {ConnectionRows.ProviderTail} FROM oauth_providers ORDER BY id";

        var list = new List<StoredProvider>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(ReadProvider(reader));
        return list;
    }

    public async Task<StoredProvider?> ProviderAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT id, kind, name, client_id, client_secret_protected IS NOT NULL, {ConnectionRows.ProviderTail} FROM oauth_providers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProvider(reader) : null;
    }

    private static StoredProvider ReadProvider(SqliteDataReader reader) => ConnectionRows.ReadProvider(reader);

    /// <summary>The client secret, decrypted, for a request to the provider. Null when none is set.</summary>
    public async Task<string?> ClientSecretAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_secret_protected FROM oauth_providers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Unprotect(await command.ExecuteScalarAsync(ct) as string);
    }

    /// <param name="secret">Null keeps the stored secret; <c>""</c> clears it; anything else replaces it.</param>
    public async Task SaveProviderAsync(StoredProvider provider, string? secret, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO oauth_providers (id, kind, name, client_id, client_secret_protected, tenant, authorize_url, token_url, userinfo_url, default_scopes, updated_at)
                VALUES ($id, $kind, $name, $clientId, $secret, $tenant, $authorize, $token, $userinfo, $scopes, $at)
                ON CONFLICT (id) DO UPDATE SET
                    kind = excluded.kind,
                    name = excluded.name,
                    client_id = excluded.client_id,
                    client_secret_protected = CASE WHEN $keepSecret THEN oauth_providers.client_secret_protected ELSE excluded.client_secret_protected END,
                    tenant = excluded.tenant,
                    authorize_url = excluded.authorize_url,
                    token_url = excluded.token_url,
                    userinfo_url = excluded.userinfo_url,
                    default_scopes = excluded.default_scopes,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$id", provider.Id);
            command.Parameters.AddWithValue("$kind", provider.Kind);
            command.Parameters.AddWithValue("$name", (object?)provider.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("$clientId", (object?)provider.ClientId ?? DBNull.Value);
            command.Parameters.AddWithValue("$secret", _cipher.ProtectOrNull(secret));
            command.Parameters.AddWithValue("$keepSecret", secret is null);
            command.Parameters.AddWithValue("$tenant", (object?)provider.Tenant ?? DBNull.Value);
            command.Parameters.AddWithValue("$authorize", (object?)provider.AuthorizeUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$token", (object?)provider.TokenUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$userinfo", (object?)provider.UserinfoUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$scopes", ConnectionRows.Scopes(provider.DefaultScopes));
            command.Parameters.AddWithValue("$at", Now());
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Removes a custom provider, refused (false) while a connection of it exists.</summary>
    public async Task<(bool Removed, IReadOnlyList<string> Connections)> DeleteProviderAsync(string id, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var names = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM connections WHERE provider = $id ORDER BY name";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        }

        if (names.Count > 0) return (false, names);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM oauth_providers WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return (true, []);
    }

    // ---- connections ----------------------------------------------------------------------------

    /// <summary>A connection and, for a mailbox, its servers - never its password, only whether one is set.</summary>
    private const string ConnectionSelect =
        "SELECT " + ConnectionRows.ConnectionHead + ", "
        + "m.password_protected IS NOT NULL, c.tenant, " + ConnectionRows.CredentialTail + " "
        + "FROM connections c LEFT JOIN imap_connections m ON m.id = c.id " + ConnectionRows.CredentialJoin;

    public async Task<IReadOnlyList<ConnectionRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"{ConnectionSelect} ORDER BY c.name COLLATE NOCASE, c.id";

        var list = new List<ConnectionRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(ReadConnection(reader));
        return list;
    }

    public async Task<ConnectionRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await GetAsync(connection, null, id, ct);
    }

    private static async Task<ConnectionRecord?> GetAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{ConnectionSelect} WHERE c.id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadConnection(reader) : null;
    }

    private static ConnectionRecord ReadConnection(SqliteDataReader reader) => ConnectionRows.ReadConnection(reader);

    /// <summary>The tokens, decrypted, for the Host's own refresh and run. Never answered by a route.</summary>
    public async Task<ConnectionTokens?> TokensAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT refresh_token_protected, access_token_protected, access_expires_at FROM connections WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return ConnectionRows.ReadTokens(reader, _cipher);
    }

    public async Task InsertAsync(
        ConnectionRecord record, ConnectionTokens tokens, string? connectedBy, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await EnsureNameFreeAsync(connection, transaction, record.Name, null, ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO connections (id, name, provider, account, scopes, refresh_token_protected, access_token_protected,
                                         access_expires_at, connected_at, refreshed_at, status, status_reason, connected_by, tenant)
                VALUES ($id, $name, $provider, $account, $scopes, $refresh, $access, $expires, $at, NULL, 'ok', NULL, $by, $tenant)
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$name", record.Name);
            command.Parameters.AddWithValue("$provider", record.Provider);
            command.Parameters.AddWithValue("$account", record.Account);
            command.Parameters.AddWithValue("$scopes", ConnectionRows.Scopes(record.Scopes));
            AddTokens(command, tokens);
            command.Parameters.AddWithValue("$at", Instant.Stamp(record.ConnectedAt));
            command.Parameters.AddWithValue("$by", (object?)connectedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("$tenant", (object?)record.Tenant ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>A Reconnect: new tokens and scopes, the account and directory it signed in as and
    /// through, and the status back to <c>ok</c>. A refresh token the provider did not re-issue keeps
    /// the stored one.</summary>
    public async Task ReconnectAsync(
        string id, IReadOnlyList<string> scopes, ConnectionTokens tokens, TriggerAudit audit, CancellationToken ct = default, string? tenant = null,
        string? account = null)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE connections SET
                    scopes = $scopes,
                    refresh_token_protected = COALESCE($refresh, refresh_token_protected),
                    access_token_protected = $access,
                    access_expires_at = $expires,
                    refreshed_at = $now,
                    status = 'ok',
                    status_reason = NULL,
                    tenant = $tenant,
                    account = COALESCE($account, account)
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$account", (object?)account ?? DBNull.Value);
            command.Parameters.AddWithValue("$scopes", ConnectionRows.Scopes(scopes));
            command.Parameters.AddWithValue("$tenant", (object?)tenant ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Now());
            AddTokens(command, tokens);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// A successful refresh: the new access token, and a ROTATED refresh token when the provider
    /// issued one, committed before the caller hands the access token to anyone: a run never holds a
    /// token whose rotation the Host has not kept. (A write that fails after the provider rotated
    /// loses the new refresh token; the next refresh is then refused and the connection asks to be
    /// reconnected - said, never silent.) Not a person's act, so no tenant row: every refresh would
    /// flood it.
    /// </summary>
    public async Task StoreRefreshAsync(string id, ConnectionTokens tokens, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE connections SET
                refresh_token_protected = COALESCE($refresh, refresh_token_protected),
                access_token_protected = $access,
                access_expires_at = $expires,
                refreshed_at = $now
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", Now());
        AddTokens(command, tokens);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A redemption for one of a connection's further APIs: a ROTATED refresh token when the provider
    /// issued one, committed before the caller hands any token out, and when it was refreshed. The
    /// API's access token is not stored: the Host holds it in memory.
    /// </summary>
    public async Task StoreRotatedRefreshAsync(string id, string? refreshToken, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE connections SET
                refresh_token_protected = COALESCE($refresh, refresh_token_protected),
                refreshed_at = $now
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$refresh", _cipher.ProtectOrNull(refreshToken));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The provider refused a refresh: the connection needs a person, and says why.</summary>
    public async Task MarkNeedsReconnectAsync(string id, string reason, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE connections SET status = 'needs-reconnect', status_reason = $reason, access_token_protected = NULL, access_expires_at = NULL WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$reason", reason);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> RenameAsync(string id, string name, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await EnsureNameFreeAsync(connection, transaction, name, id, ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE connections SET name = $name WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Deletes the connection and its credentials; see <see cref="IConnectionStore.DeleteAsync"/>.
    /// The check, the unbinding and the delete are one transaction, so a binding made in between is
    /// either seen and unbound or refuses the delete: never orphaned.</summary>
    public async Task<(bool Deleted, IReadOnlyList<ConnectionUse> UsedBy)> DeleteAsync(
        string id, TriggerAudit audit, CancellationToken ct = default,
        Func<IReadOnlyList<ConnectionUse>, IReadOnlyList<TriggerAudit>>? unbind = null)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var bindings = await BindingRowsAsync(connection, transaction, ct);
        var used = bindings.SelectMany(r => ConnectionRows.Bindings(r.Team, r.Member, r.Json)).Where(b => b.Id == id).Select(b => b.Use).ToList();
        if (used.Count > 0 && unbind is null) return (false, used);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM connections WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            if (await command.ExecuteNonQueryAsync(ct) == 0) return (false, []);
        }

        foreach (var table in new[] { "imap_connections", "credential_connections" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct);
        }

        // EVERY TEAM IT WAS ASSIGNED TO loses it here, in the same transaction.
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM team_connections WHERE connection_id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct);
        }

        // EVERY MEMBER THAT BOUND IT loses those slots here, in the same transaction.
        foreach (var row in bindings.Where(r => used.Any(u => u.Team == r.Team && u.Member == r.Member)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE team_member_config SET connections_json = $json WHERE team = $team AND name = $name";
            command.Parameters.AddWithValue("$team", row.Team);
            command.Parameters.AddWithValue("$name", row.Member);
            command.Parameters.AddWithValue("$json", ConnectionRows.Unbound(row.Json, id));
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        if (used.Count > 0)
        {
            foreach (var row in unbind!(used)) await TenantAuditRow.AppendAsync(connection, transaction, row, ct);
        }

        await transaction.CommitAsync(ct);
        return (true, used);
    }

    public async Task<bool> NameTakenAsync(string name, string? exceptId = null, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await NameTakenAsync(connection, null, name, exceptId, ct);
    }

    private static async Task<bool> NameTakenAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string name, string? exceptId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name FROM connections";

        var key = ConnectionRows.NameKey(name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetString(0) != exceptId && ConnectionRows.NameKey(reader.GetString(1)) == key) return true;
        }

        return false;
    }

    /// <summary>Inside a write's transaction: the name is free, or the write throws and nothing lands.</summary>
    private static async Task EnsureNameFreeAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, string? exceptId, CancellationToken ct)
    {
        if (await NameTakenAsync(connection, transaction, name, exceptId, ct)) throw new ConnectionNameTakenException(name.Trim());
    }

    /// <summary>A tenant row on its own: what followed a write already committed.</summary>
    public async Task RecordAsync(TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Every plugin member that binds <paramref name="id"/>, by slot.</summary>
    public async Task<IReadOnlyList<ConnectionUse>> UsedByAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await UsedByAsync(connection, null, id, ct);
    }

    /// <summary>Every binding on the instance: connection id to its users.</summary>
    public async Task<ILookup<string, ConnectionUse>> AllUsesAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        return (await BindingsAsync(connection, null, ct)).ToLookup(b => b.Id, b => b.Use, StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyList<ConnectionUse>> UsedByAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct) =>
        [.. (await BindingsAsync(connection, transaction, ct)).Where(b => b.Id == id).Select(b => b.Use)];

    private static async Task<List<(string Team, string Member, string Json)>> BindingRowsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT team, name, connections_json FROM team_member_config WHERE connections_json <> '{}' ORDER BY team, name";

        var list = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    private static async Task<List<(string Id, ConnectionUse Use)>> BindingsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT team, name, connections_json FROM team_member_config WHERE connections_json <> '{}' ORDER BY team, name";

        var list = new List<(string, ConnectionUse)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.AddRange(ConnectionRows.Bindings(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return list;
    }

    // ---- team assignments -----------------------------------------------------------------------

    public async Task<IReadOnlyList<TeamConnection>> AssignedAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT team, connection_id, assigned_at FROM team_connections WHERE team = $team ORDER BY assigned_at, connection_id";
        command.Parameters.AddWithValue("$team", team);

        var list = new List<TeamConnection>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new TeamConnection(reader.GetString(0), reader.GetString(1), Instant.Parse(reader.GetString(2))));
        }

        return list;
    }

    public async Task<ILookup<string, string>> AllAssignmentsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT connection_id, team FROM team_connections ORDER BY team";

        var list = new List<(string Id, string Team)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add((reader.GetString(0), reader.GetString(1)));
        return list.ToLookup(a => a.Id, a => a.Team, StringComparer.Ordinal);
    }

    public async Task<AssignOutcome> AssignAsync(string team, string connectionId, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await GetAsync(connection, transaction, connectionId, ct) is null) return AssignOutcome.NoConnection;
        if (!await InsertAssignmentAsync(connection, transaction, team, connectionId, ct)) return AssignOutcome.Already;

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return AssignOutcome.Assigned;
    }

    public async Task AssignAllAsync(string team, IReadOnlyList<(string ConnectionId, TriggerAudit Audit)> assignments, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        foreach (var (id, audit) in assignments)
        {
            if (await InsertAssignmentAsync(connection, transaction, team, id, ct)) await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<(bool Removed, IReadOnlyList<ConnectionUse> Unbound)> UnassignAsync(
        string team, string connectionId, TriggerAudit audit,
        Func<IReadOnlyList<ConnectionUse>, IReadOnlyList<TriggerAudit>> unbind, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM team_connections WHERE team = $team AND connection_id = $id";
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$id", connectionId);
            if (await command.ExecuteNonQueryAsync(ct) == 0) return (false, []);
        }

        // EVERY MEMBER OF THE TEAM THAT BOUND IT loses those slots here, in the same transaction.
        var rows = (await BindingRowsAsync(connection, transaction, ct))
            .Where(r => string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var used = rows.SelectMany(r => ConnectionRows.Bindings(r.Team, r.Member, r.Json)).Where(b => b.Id == connectionId).Select(b => b.Use).ToList();

        foreach (var row in rows.Where(r => used.Any(u => u.Member == r.Member)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE team_member_config SET connections_json = $json WHERE team = $team AND name = $name";
            command.Parameters.AddWithValue("$team", row.Team);
            command.Parameters.AddWithValue("$name", row.Member);
            command.Parameters.AddWithValue("$json", ConnectionRows.Unbound(row.Json, connectionId));
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        if (used.Count > 0)
        {
            foreach (var row in unbind(used)) await TenantAuditRow.AppendAsync(connection, transaction, row, ct);
        }

        await transaction.CommitAsync(ct);
        return (true, used);
    }

    public async Task<IReadOnlyList<string>> CopyAssignmentsAsync(
        string from, string to, Func<string, TriggerAudit> audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT connection_id FROM team_connections WHERE team = $team ORDER BY assigned_at, connection_id";
            command.Parameters.AddWithValue("$team", from);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }

        var copied = new List<string>();
        foreach (var id in ids)
        {
            if (!await InsertAssignmentAsync(connection, transaction, to, id, ct)) continue;
            await TenantAuditRow.AppendAsync(connection, transaction, audit(id), ct);
            copied.Add(id);
        }

        await transaction.CommitAsync(ct);
        return copied;
    }

    /// <summary>One assignment, inside a write's transaction: false when it was already there.</summary>
    private static async Task<bool> InsertAssignmentAsync(
        SqliteConnection connection, SqliteTransaction transaction, string team, string connectionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT OR IGNORE INTO team_connections (team, connection_id, assigned_at) VALUES ($team, $id, $at)";
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$id", connectionId);
        command.Parameters.AddWithValue("$at", Now());
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---- mailboxes ------------------------------------------------------------------------------

    /// <summary>A new mailbox connection: its <c>connections</c> row, its servers and its app password
    /// as ciphertext, and the tenant row, in one transaction or not at all.</summary>
    public async Task InsertMailboxAsync(
        ConnectionRecord record, MailboxSettings settings, string password, string? connectedBy, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await EnsureNameFreeAsync(connection, transaction, record.Name, null, ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO connections (id, name, provider, account, scopes, connected_at, refreshed_at, status, status_reason, connected_by)
                VALUES ($id, $name, $provider, $account, '[]', $at, $verified, 'ok', NULL, $by)
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$name", record.Name);
            command.Parameters.AddWithValue("$provider", MailboxSettings.Kind);
            command.Parameters.AddWithValue("$account", record.Account);
            command.Parameters.AddWithValue("$at", Instant.Stamp(record.ConnectedAt));
            command.Parameters.AddWithValue("$verified", ConnectionRows.MailboxVerified(record));
            command.Parameters.AddWithValue("$by", (object?)connectedBy ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO imap_connections (id, username, imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security, preset, password_protected)
                VALUES ($id, $username, $imapHost, $imapPort, $imapSecurity, $smtpHost, $smtpPort, $smtpSecurity, $preset, $password)
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$username", settings.Username);
            command.Parameters.AddWithValue("$imapHost", settings.Imap.Host);
            command.Parameters.AddWithValue("$imapPort", settings.Imap.Port);
            command.Parameters.AddWithValue("$imapSecurity", settings.Imap.Security);
            command.Parameters.AddWithValue("$smtpHost", settings.Smtp.Host);
            command.Parameters.AddWithValue("$smtpPort", settings.Smtp.Port);
            command.Parameters.AddWithValue("$smtpSecurity", settings.Smtp.Security);
            command.Parameters.AddWithValue("$preset", (object?)settings.Preset ?? DBNull.Value);
            command.Parameters.AddWithValue("$password", _cipher.Protect(password));
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The app password, decrypted, for the Host's own login and a plugin run. Never
    /// answered by a route.</summary>
    public async Task<string?> MailboxPasswordAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT password_protected FROM imap_connections WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Unprotect(await command.ExecuteScalarAsync(ct) as string);
    }

    /// <summary>Update password: the new one as ciphertext, the status back to <c>ok</c> and the
    /// login's time, with the tenant row in the same transaction. False when there is no such mailbox.</summary>
    public async Task<bool> UpdateMailboxPasswordAsync(
        string id, string password, DateTimeOffset verifiedAt, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE imap_connections SET password_protected = $password WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$password", _cipher.Protect(password));
            if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE connections SET status = 'ok', status_reason = NULL, refreshed_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", Instant.Stamp(verifiedAt));
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>A run's login succeeded: when, so the next run within <see cref="Connections.MailboxRecheck"/>
    /// does not log in again. Not a person's act, so no tenant row.</summary>
    public async Task StoreMailboxVerifiedAsync(string id, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE connections SET refreshed_at = $at WHERE id = $id AND status = 'ok'";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", Instant.Stamp(at));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A mailbox's edit; see <see cref="IConnectionStore.UpdateMailboxAsync"/>.</summary>
    public async Task<bool> UpdateMailboxAsync(
        string id, string account, MailboxSettings settings, string? password, DateTimeOffset verifiedAt, TriggerAudit audit,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE imap_connections SET
                    username = $username, imap_host = $imapHost, imap_port = $imapPort, imap_security = $imapSecurity,
                    smtp_host = $smtpHost, smtp_port = $smtpPort, smtp_security = $smtpSecurity,
                    password_protected = COALESCE($password, password_protected)
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$username", settings.Username);
            command.Parameters.AddWithValue("$imapHost", settings.Imap.Host);
            command.Parameters.AddWithValue("$imapPort", settings.Imap.Port);
            command.Parameters.AddWithValue("$imapSecurity", settings.Imap.Security);
            command.Parameters.AddWithValue("$smtpHost", settings.Smtp.Host);
            command.Parameters.AddWithValue("$smtpPort", settings.Smtp.Port);
            command.Parameters.AddWithValue("$smtpSecurity", settings.Smtp.Security);
            command.Parameters.AddWithValue("$password", _cipher.ProtectOrNull(password));
            if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE connections SET account = $account, status = 'ok', status_reason = NULL, refreshed_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$at", Instant.Stamp(verifiedAt));
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    // ---- credentials (website, API key, username and password) --------------------------------------

    /// <summary>A new credential connection; see <see cref="IConnectionStore.InsertCredentialAsync"/>.</summary>
    public async Task InsertCredentialAsync(
        ConnectionRecord record, IReadOnlyDictionary<string, string> secrets, string? connectedBy, TriggerAudit audit, CancellationToken ct = default)
    {
        var settings = record.Credential ?? throw new ArgumentException("A credential connection has its fields.", nameof(record));

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await EnsureNameFreeAsync(connection, transaction, record.Name, null, ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO connections (id, name, provider, account, scopes, connected_at, refreshed_at, status, status_reason, connected_by)
                VALUES ($id, $name, $provider, $account, '[]', $at, NULL, 'ok', NULL, $by)
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$name", record.Name);
            command.Parameters.AddWithValue("$provider", record.Provider);
            command.Parameters.AddWithValue("$account", record.Account);
            command.Parameters.AddWithValue("$at", Instant.Stamp(record.ConnectedAt));
            command.Parameters.AddWithValue("$by", (object?)connectedBy ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO credential_connections (id, preset, fields, secrets_protected) VALUES ($id, $preset, $fields, $secrets)";
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$preset", (object?)settings.Preset ?? DBNull.Value);
            command.Parameters.AddWithValue("$fields", settings.FieldsJson());
            command.Parameters.AddWithValue("$secrets", ConnectionRows.SealSecrets(_cipher, secrets));
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>?> CredentialSecretsAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT secrets_protected FROM credential_connections WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return ConnectionRows.OpenSecrets(_cipher, ConnectionRows.NullableText(reader, 0));
    }

    public async Task<bool> UpdateCredentialAsync(
        string id, string account, CredentialSettings settings, IReadOnlyDictionary<string, string> secrets, TriggerAudit audit,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE credential_connections SET fields = $fields, secrets_protected = $secrets WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$fields", settings.FieldsJson());
            command.Parameters.AddWithValue("$secrets", ConnectionRows.SealSecrets(_cipher, secrets));
            if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE connections SET account = $account, status = 'ok', status_reason = NULL WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$account", account);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    // ---- flows ----------------------------------------------------------------------------------

    public async Task InsertFlowAsync(ConnectionFlow flow, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // Old flows go as new ones arrive: nothing needs a flow a day after it was issued.
        await using (var sweep = connection.CreateCommand())
        {
            sweep.Transaction = transaction;
            sweep.CommandText = "DELETE FROM connection_flows WHERE issued_at < $cutoff";
            sweep.Parameters.AddWithValue("$cutoff", ConnectionRows.FlowCutoff(flow));
            await sweep.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO connection_flows (state, user_id, provider, scopes, name, reconnect_id, redirect_uri, loopback, verifier_protected, issued_at, tenant)
                VALUES ($state, $user, $provider, $scopes, $name, $reconnect, $redirect, $loopback, $verifier, $at, $tenant)
                """;
            command.Parameters.AddWithValue("$state", flow.State);
            command.Parameters.AddWithValue("$user", flow.UserId);
            command.Parameters.AddWithValue("$provider", flow.Provider);
            command.Parameters.AddWithValue("$scopes", ConnectionRows.Scopes(flow.Scopes));
            command.Parameters.AddWithValue("$name", (object?)flow.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("$reconnect", (object?)flow.ReconnectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$redirect", flow.RedirectUri);
            command.Parameters.AddWithValue("$loopback", flow.Loopback);
            command.Parameters.AddWithValue("$verifier", _cipher.Protect(flow.CodeVerifier));
            command.Parameters.AddWithValue("$at", Instant.Stamp(flow.IssuedAt));
            command.Parameters.AddWithValue("$tenant", (object?)flow.Tenant ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Marks <paramref name="state"/> used and answers the flow as it was BEFORE - so a second caller
    /// sees <c>UsedAt</c> set and is refused. One statement, so two callbacks racing cannot both win.
    /// Null for a state never issued, and for one issued to someone other than <paramref name="userId"/>
    /// when that is given - which leaves it unconsumed, so nobody can spend another person's sign-in.
    /// </summary>
    public async Task<ConnectionFlow?> ConsumeFlowAsync(string state, DateTimeOffset now, string? userId, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        ConnectionFlow? flow = null;

        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                $"SELECT {ConnectionRows.FlowColumns} FROM connection_flows WHERE state = $state";
            read.Parameters.AddWithValue("$state", state);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) flow = ConnectionRows.ReadFlow(reader, _cipher.Unprotect);
        }

        if (flow is null || (userId is not null && !string.Equals(flow.UserId, userId, StringComparison.Ordinal))) return null;

        if (flow.UsedAt is null)
        {
            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE connection_flows SET used_at = $now WHERE state = $state AND used_at IS NULL";
            mark.Parameters.AddWithValue("$state", state);
            mark.Parameters.AddWithValue("$now", Instant.Stamp(now));
            if (await mark.ExecuteNonQueryAsync(ct) == 0) flow = flow with { UsedAt = now };
        }

        await transaction.CommitAsync(ct);
        return flow;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private void AddTokens(SqliteCommand command, ConnectionTokens tokens)
    {
        var (refresh, access, expires) = ConnectionRows.Tokens(_cipher, tokens);
        command.Parameters.AddWithValue("$refresh", refresh);
        command.Parameters.AddWithValue("$access", access);
        command.Parameters.AddWithValue("$expires", expires);
    }

    private string? Unprotect(string? protectedValue) => _cipher.Unprotect(protectedValue);

    private static string Now() => Instant.Now();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
