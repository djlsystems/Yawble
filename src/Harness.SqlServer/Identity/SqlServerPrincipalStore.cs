using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="IPrincipalStore"/> over <c>principals</c> on SQL Server: SQLite's
/// <c>SqlitePrincipalStore</c> statement for statement, whose remarks are the argument. The credential,
/// its hash, its prefix and the reading back of a row are <see cref="PrincipalCredentials"/>', and the
/// once-a-minute touch is <see cref="PrincipalTouches"/>, both shared with it: a credential minted by
/// either store resolves on the other.
///
/// <para>
/// A RE-MINT IS ONE <c>MERGE</c> UNDER <c>HOLDLOCK</c>. SQLite's upsert runs alone; here two mints of
/// one id at once (a container recreated while its restore runs) would otherwise both find no row and
/// both insert, and one would fail on the key. The merge holds the id's key range until it commits, so
/// the second waits and replaces the first. Every other write is one statement on one key.
/// </para>
/// </summary>
public sealed class SqlServerPrincipalStore(string connectionString, TimeProvider? clock = null) : IPrincipalStore
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);
    private readonly PrincipalTouches _touches = new(clock);

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    private Task<int> ExecuteAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        SqlServerOpen.ExecuteAsync(_connectionString, sql, bind, ct);

    public async Task<string> MintAsync(
        string id,
        PrincipalKind kind,
        string? team,
        IReadOnlySet<string> permits,
        string? credential = null,
        string? ownerUserId = null,
        string? label = null,
        CancellationToken ct = default)
    {
        credential = PrincipalCredentials.Credential(credential);
        var hash = PrincipalCredentials.Hash(credential);
        var prefix = PrincipalCredentials.Prefix(credential);

        // Replaces rather than adds: a container recreated under the same name must not leave its
        // predecessor's credential working.
        await ExecuteAsync(
            """
            MERGE principals WITH (HOLDLOCK) AS p
            USING (SELECT @id AS id) AS n ON p.id = n.id
            WHEN MATCHED THEN UPDATE SET
                kind = @kind, team = @team, hash = @hash, prefix = @prefix, permits = @permits,
                created_at = @at, owner_user_id = @owner, label = @label, last_used_at = NULL
            WHEN NOT MATCHED THEN INSERT
                (id, kind, team, hash, prefix, permits, created_at, owner_user_id, label)
                VALUES (@id, @kind, @team, @hash, @prefix, @permits, @at, @owner, @label);
            """,
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@kind", kind.ToString());
                command.Parameters.AddWithValue("@team", (object?)team ?? DBNull.Value);
                command.Parameters.AddWithValue("@hash", hash);
                command.Parameters.AddWithValue("@prefix", prefix);
                command.Parameters.AddWithValue("@permits", PrincipalCredentials.PermitsJson(permits));
                command.Parameters.AddWithValue("@at", Instant.Now());
                command.Parameters.AddWithValue("@owner", (object?)ownerUserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);
            },
            ct);

        return credential;
    }

    public async Task<Principal?> ResolveAsync(string credential, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credential)) return null;

        return await RunAsync(
            async (connection, token) =>
            {
                string id, kind, permits;
                string? owner;

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, kind, permits, owner_user_id FROM principals WHERE hash = @hash";
                    command.Parameters.AddWithValue("@hash", PrincipalCredentials.Hash(credential));

                    await using var reader = await command.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) return null;

                    id = reader.GetString(0);
                    kind = reader.GetString(1);
                    permits = reader.GetString(2);
                    owner = reader.IsDBNull(3) ? null : reader.GetString(3);
                }

                if (PrincipalCredentials.Read(id, kind, permits, owner) is not { } principal) return null;

                // AFTER both parses, never before, and at most once a minute: see SqlitePrincipalStore.
                if (_touches.Due(id) is { } now)
                {
                    await using var touch = connection.CreateCommand();
                    touch.CommandText = "UPDATE principals SET last_used_at = @at WHERE id = @id";
                    touch.Parameters.AddWithValue("@at", Instant.Stamp(now));
                    touch.Parameters.AddWithValue("@id", id);
                    await touch.ExecuteNonQueryAsync(token);
                    _touches.Touched(id, now);
                }

                return principal;
            },
            ct);
    }

    public Task<string?> TeamForAsync(string id, CancellationToken ct = default) =>
        TextAsync("SELECT team FROM principals WHERE id = @id", id, ct);

    public Task RevokeAsync(string id, CancellationToken ct = default) =>
        ExecuteAsync("DELETE FROM principals WHERE id = @id", command => command.Parameters.AddWithValue("@id", id), ct);

    /// <summary>The <c>team</c> column, case-insensitively, as SQLite's <c>COLLATE NOCASE</c> asks.</summary>
    public Task<int> RevokeForTeamAsync(string team, CancellationToken ct = default) =>
        ExecuteAsync(
            $"DELETE FROM principals WHERE team COLLATE {SqlServerSql.NoCase} = @team",
            command => command.Parameters.AddWithValue("@team", team),
            ct);

    public Task<IReadOnlyList<ApiKeySummary>> ListApiKeysForAsync(string ownerUserId, CancellationToken ct = default) =>
        ListAsync("AND owner_user_id = @owner", ownerUserId, ct);

    public Task<IReadOnlyList<ApiKeySummary>> ListAllApiKeysAsync(CancellationToken ct = default) =>
        ListAsync(filter: null, owner: null, ct);

    /// <summary>The one query behind both listings, `kind = 'ApiKey'` in its fixed half, as on SQLite.</summary>
    private Task<IReadOnlyList<ApiKeySummary>> ListAsync(string? filter, string? owner, CancellationToken ct) =>
        RunAsync<IReadOnlyList<ApiKeySummary>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {PrincipalCredentials.ApiKeyColumns} FROM principals "
                    + $"WHERE kind = 'ApiKey' AND owner_user_id IS NOT NULL {filter} "
                    + "ORDER BY created_at DESC, id DESC";

                if (owner is not null) command.Parameters.AddWithValue("@owner", owner);

                var keys = new List<ApiKeySummary>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) keys.Add(PrincipalCredentials.ReadApiKey(reader));

                return keys;
            },
            ct);

    public Task<string?> OwnerOfApiKeyAsync(string id, CancellationToken ct = default) =>
        TextAsync("SELECT owner_user_id FROM principals WHERE id = @id AND kind = 'ApiKey'", id, ct);

    public async Task<DateTimeOffset?> LastUsedAtAsync(string id, CancellationToken ct = default)
    {
        _touches.Watched(id);

        return await TextAsync("SELECT last_used_at FROM principals WHERE id = @id", id, ct) is { } value
            ? Instant.Parse(value)
            : null;
    }

    private Task<string?> TextAsync(string sql, string id, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@id", id);
                return await command.ExecuteScalarAsync(token) is string value ? value : null;
            },
            ct);
}
