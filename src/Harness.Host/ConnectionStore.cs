using System.Globalization;
using System.Text.Json;
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
    string? StatusReason)
{
    public const string Ok = "ok";

    public const string NeedsReconnect = "needs-reconnect";

    /// <summary>"'Work mail' (person@example.com)", as every sentence names a connection.</summary>
    public string Named => string.Equals(Name, Account, StringComparison.Ordinal) ? $"'{Name}'" : $"'{Name}' ({Account})";
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
    DateTimeOffset? UsedAt);

/// <summary>A plugin member binding a connection, by slot.</summary>
public sealed record ConnectionUse(string Team, string Member, string Slot);

/// <summary>
/// <c>oauth_providers</c>, <c>connections</c>, <c>connection_flows</c> (schema step auth-014). EVERY
/// credential column - the client secret, the refresh and access tokens, the PKCE verifier - is
/// Data Protection ciphertext under the instance's <c>&lt;dataRoot&gt;/keys</c>. Every write a person
/// makes lands with its <c>tenant_events</c> row in the same transaction, or not at all.
/// </summary>
public sealed class ConnectionStore(string databasePath, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Harness.Connections.v1");

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    // ---- providers ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<StoredProvider>> ProvidersAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, kind, name, client_id, client_secret_protected IS NOT NULL, tenant, authorize_url, token_url, userinfo_url, default_scopes FROM oauth_providers ORDER BY id";

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
            "SELECT id, kind, name, client_id, client_secret_protected IS NOT NULL, tenant, authorize_url, token_url, userinfo_url, default_scopes FROM oauth_providers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProvider(reader) : null;
    }

    private static StoredProvider ReadProvider(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), NullableText(reader, 2), NullableText(reader, 3),
        reader.GetBoolean(4), NullableText(reader, 5), NullableText(reader, 6), NullableText(reader, 7),
        NullableText(reader, 8), List(reader.GetString(9)));

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
            command.Parameters.AddWithValue("$secret", secret is { Length: > 0 } ? _protector.Protect(secret) : DBNull.Value);
            command.Parameters.AddWithValue("$keepSecret", secret is null);
            command.Parameters.AddWithValue("$tenant", (object?)provider.Tenant ?? DBNull.Value);
            command.Parameters.AddWithValue("$authorize", (object?)provider.AuthorizeUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$token", (object?)provider.TokenUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$userinfo", (object?)provider.UserinfoUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(provider.DefaultScopes));
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

    private const string ConnectionColumns =
        "id, name, provider, account, scopes, connected_at, refreshed_at, status, status_reason";

    public async Task<IReadOnlyList<ConnectionRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ConnectionColumns} FROM connections ORDER BY name COLLATE NOCASE, id";

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
        command.CommandText = $"SELECT {ConnectionColumns} FROM connections WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadConnection(reader) : null;
    }

    private static ConnectionRecord ReadConnection(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), List(reader.GetString(4)),
        Time(reader.GetString(5)), NullableText(reader, 6) is { } refreshed ? Time(refreshed) : null,
        reader.GetString(7), NullableText(reader, 8));

    /// <summary>The tokens, decrypted, for the Host's own refresh and run. Never answered by a route.</summary>
    public async Task<ConnectionTokens?> TokensAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT refresh_token_protected, access_token_protected, access_expires_at FROM connections WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ConnectionTokens(
            Unprotect(NullableText(reader, 0)), Unprotect(NullableText(reader, 1)),
            NullableText(reader, 2) is { } expires ? Time(expires) : null);
    }

    public async Task InsertAsync(
        ConnectionRecord record, ConnectionTokens tokens, string? connectedBy, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO connections (id, name, provider, account, scopes, refresh_token_protected, access_token_protected,
                                         access_expires_at, connected_at, refreshed_at, status, status_reason, connected_by)
                VALUES ($id, $name, $provider, $account, $scopes, $refresh, $access, $expires, $at, NULL, 'ok', NULL, $by)
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$name", record.Name);
            command.Parameters.AddWithValue("$provider", record.Provider);
            command.Parameters.AddWithValue("$account", record.Account);
            command.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(record.Scopes));
            AddTokens(command, tokens);
            command.Parameters.AddWithValue("$at", record.ConnectedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$by", (object?)connectedBy ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>A Reconnect: new tokens and scopes, and the status back to <c>ok</c>. A refresh token
    /// the provider did not re-issue keeps the stored one.</summary>
    public async Task ReconnectAsync(
        string id, IReadOnlyList<string> scopes, ConnectionTokens tokens, TriggerAudit audit, CancellationToken ct = default)
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
                    status_reason = NULL
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(scopes));
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

    /// <summary>Deletes the connection and its tokens, refused (the users) while any member binds it.
    /// The check and the delete are one transaction, so a binding made in between cannot be orphaned.</summary>
    public async Task<(bool Deleted, IReadOnlyList<ConnectionUse> UsedBy)> DeleteAsync(
        string id, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var used = await UsedByAsync(connection, transaction, id, ct);
        if (used.Count > 0) return (false, used);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM connections WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            if (await command.ExecuteNonQueryAsync(ct) == 0) return (false, []);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return (true, []);
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
            foreach (var (slot, connectionId) in JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2)) ?? [])
            {
                list.Add((connectionId, new ConnectionUse(reader.GetString(0), reader.GetString(1), slot)));
            }
        }

        return list;
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
            sweep.Parameters.AddWithValue("$cutoff", flow.IssuedAt.AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
            await sweep.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO connection_flows (state, user_id, provider, scopes, name, reconnect_id, redirect_uri, loopback, verifier_protected, issued_at)
                VALUES ($state, $user, $provider, $scopes, $name, $reconnect, $redirect, $loopback, $verifier, $at)
                """;
            command.Parameters.AddWithValue("$state", flow.State);
            command.Parameters.AddWithValue("$user", flow.UserId);
            command.Parameters.AddWithValue("$provider", flow.Provider);
            command.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(flow.Scopes));
            command.Parameters.AddWithValue("$name", (object?)flow.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("$reconnect", (object?)flow.ReconnectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$redirect", flow.RedirectUri);
            command.Parameters.AddWithValue("$loopback", flow.Loopback);
            command.Parameters.AddWithValue("$verifier", _protector.Protect(flow.CodeVerifier));
            command.Parameters.AddWithValue("$at", flow.IssuedAt.ToString("O", CultureInfo.InvariantCulture));
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
                "SELECT state, user_id, provider, scopes, name, reconnect_id, redirect_uri, loopback, verifier_protected, issued_at, used_at FROM connection_flows WHERE state = $state";
            read.Parameters.AddWithValue("$state", state);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                flow = new ConnectionFlow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), List(reader.GetString(3)),
                    NullableText(reader, 4), NullableText(reader, 5), reader.GetString(6), reader.GetBoolean(7),
                    Unprotect(reader.GetString(8)) ?? "", Time(reader.GetString(9)),
                    NullableText(reader, 10) is { } used ? Time(used) : null);
            }
        }

        if (flow is null || (userId is not null && !string.Equals(flow.UserId, userId, StringComparison.Ordinal))) return null;

        if (flow.UsedAt is null)
        {
            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE connection_flows SET used_at = $now WHERE state = $state AND used_at IS NULL";
            mark.Parameters.AddWithValue("$state", state);
            mark.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            if (await mark.ExecuteNonQueryAsync(ct) == 0) flow = flow with { UsedAt = now };
        }

        await transaction.CommitAsync(ct);
        return flow;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private void AddTokens(SqliteCommand command, ConnectionTokens tokens)
    {
        command.Parameters.AddWithValue("$refresh", tokens.RefreshToken is { Length: > 0 } refresh ? _protector.Protect(refresh) : DBNull.Value);
        command.Parameters.AddWithValue("$access", tokens.AccessToken is { Length: > 0 } access ? _protector.Protect(access) : DBNull.Value);
        command.Parameters.AddWithValue("$expires", tokens.AccessExpiresAt is { } expires ? expires.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
    }

    private string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A key ring that lost its key: the credential is gone, and says so as a missing one.
            return null;
        }
    }

    private static string? NullableText(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static IReadOnlyList<string> List(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
