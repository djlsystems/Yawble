using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// Machine principals: containers and console sessions.
///
/// A credential is 256 bits of CSPRNG output stored as a SHA-256 hash, and a FAST hash is correct
/// here where a password hash would not be. There is no dictionary to attack and nothing for a work
/// factor to slow down - while a password hash on this path would put a deliberate delay on every
/// CLI call every agent makes.
/// </summary>
public sealed class SqlitePrincipalStore : IPrincipalStore
{
    private const int PrefixLength = 8;

    private readonly string _connectionString;

    /// <summary>Absence means the safe value. Only the test fixture asks for anything else.</summary>
    private readonly SqliteDurability _durability;

    public SqlitePrincipalStore(string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,

            // Off, matching SqliteUserStore: a pooled native handle stays open past Dispose() on
            // Windows, which is what turns a raw file read racing a just-closed connection into a
            // sharing-violation IOException instead of a clean read.
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

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
        credential = string.IsNullOrWhiteSpace(credential)
            ? Base64UrlEncode(RandomNumberGenerator.GetBytes(32))
            : credential.Trim();

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // Replaces rather than adds: a container recreated under the same name must not leave its
        // predecessor's credential working.
        //
        // That is why an API KEY's id has to be generated per key rather than derived from its
        // owner - minting a second key for the same person under a derived id would silently
        // revoke the first, and the person would have no reason to look.
        command.CommandText =
            """
            INSERT INTO principals
                (id, kind, team, hash, prefix, permits, created_at, owner_user_id, label)
            VALUES ($id, $kind, $team, $hash, $prefix, $permits, $at, $owner, $label)
            ON CONFLICT(id) DO UPDATE SET
                kind = excluded.kind,
                team = excluded.team,
                hash = excluded.hash,
                prefix = excluded.prefix,
                permits = excluded.permits,
                created_at = excluded.created_at,
                owner_user_id = excluded.owner_user_id,
                label = excluded.label,
                last_used_at = NULL
            """;
        // A person's CURRENT TEAM is deliberately not here and not on this table at all: it lives on
        // `users`, because a browser and the Concierge it drives are two principals for one person.
        // That is also what makes it survive the Reload this upsert implements - see
        // UserCurrentTeamTests.The_current_team_survives_a_credential_rotation.
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$team", (object?)team ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", Hash(credential));
        // Min() because a SUPPLIED credential may be shorter than the prefix a generated one has.
        command.Parameters.AddWithValue(
            "$prefix", credential[..Math.Min(PrefixLength, credential.Length)]);
        command.Parameters.AddWithValue("$permits", JsonSerializer.Serialize(permits.ToArray()));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$owner", (object?)ownerUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);

        return credential;
    }

    public async Task<Principal?> ResolveAsync(string credential, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credential)) return null;

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, kind, permits, owner_user_id FROM principals WHERE hash = $hash";
        command.Parameters.AddWithValue("$hash", Hash(credential));

        string id;
        string kind;
        string permits;
        string? owner;

        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;

            id = reader.GetString(0);
            kind = reader.GetString(1);
            permits = reader.GetString(2);
            owner = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        // A permits value we cannot read is a REFUSAL, never an assumption of freedom. Treating
        // authority we cannot parse as unlimited is how one corrupt row becomes an escalation.
        string[]? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<string[]>(permits);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null) return null;

        // A kind we cannot read is refused for the same reason, and as a REFUSAL rather than an
        // unhandled exception. Enum.Parse threw, which reached the caller as a 500 - a fault,
        // where "this credential is not usable" is the honest answer and the one the handler
        // already knows how to give.
        if (!Enum.TryParse<PrincipalKind>(kind, out var parsedKind)) return null;

        // AFTER both parses, never before. Stamped first, a row that could not be read still
        // recorded a successful use - and last_used_at is exactly the signal that tells a live
        // credential from an abandoned one in the credential roster.
        await TouchAsync(connection, id, ct);

        return new Principal(
            id,
            parsedKind,
            new HashSet<string>(parsed, StringComparer.Ordinal),
            owner);
    }

    public async Task<string?> TeamForAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT team FROM principals WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        var result = await command.ExecuteScalarAsync(ct);

        return result as string;
    }

    public async Task RevokeAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM principals WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> RevokeForTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // The `team` COLUMN, not the id's shape. A container's id happens to be its qualified name,
        // but an API key's is not and other rows need not be either - so matching
        // on the column is what makes this cover every credential the team owns rather than only
        // the ones whose id we can guess.
        //
        // NOCASE because a team id is compared case-insensitively everywhere else, and a
        // case-sensitive revoke would leave the credential it was called to remove.
        command.CommandText = "DELETE FROM principals WHERE team = $team COLLATE NOCASE";
        command.Parameters.AddWithValue("$team", team);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<ApiKeySummary>> ListApiKeysForAsync(
        string ownerUserId, CancellationToken ct = default) =>
        ListAsync("AND owner_user_id = $owner", ownerUserId, ct);

    public Task<IReadOnlyList<ApiKeySummary>> ListAllApiKeysAsync(CancellationToken ct = default) =>
        ListAsync(filter: null, owner: null, ct);

    /// <summary>
    /// The one query behind both listings. `kind = 'ApiKey'` sits in the FIXED half rather than in
    /// the caller-supplied filter, so neither caller can be written in a way that omits it.
    /// </summary>
    private async Task<IReadOnlyList<ApiKeySummary>> ListAsync(
        string? filter, string? owner, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT id, label, prefix, owner_user_id, created_at, last_used_at "
            + "FROM principals "
            + $"WHERE kind = 'ApiKey' AND owner_user_id IS NOT NULL {filter} "
            + "ORDER BY created_at DESC, id DESC";

        if (owner is not null) command.Parameters.AddWithValue("$owner", owner);

        var keys = new List<ApiKeySummary>();

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            keys.Add(new ApiKeySummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }

        return keys;
    }

    public async Task<string?> OwnerOfApiKeyAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT owner_user_id FROM principals WHERE id = $id AND kind = 'ApiKey'";
        command.Parameters.AddWithValue("$id", id);

        var value = await command.ExecuteScalarAsync(ct);

        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>
    /// Null for a credential that has never been used AND for an id nobody minted. Nothing has to
    /// tell those apart: both mean "no request has been served under this id", which is the same
    /// answer to the only question asked of it.
    /// </summary>
    public async Task<DateTimeOffset?> LastUsedAtAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT last_used_at FROM principals WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        var value = await command.ExecuteScalarAsync(ct);

        return value is null or DBNull
            ? null
            : DateTimeOffset.Parse((string)value, null, DateTimeStyles.RoundtripKind);
    }

    private static async Task TouchAsync(
        SqliteConnection connection, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE principals SET last_used_at = $at WHERE id = $id";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Hash(string credential) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // BOTH pragmas, on EVERY connection, because both are per-connection and Pooling = false
        // means every Open() is a fresh one. principals carries no foreign key of its own, but it
        // writes the same file users lives in, and the pragma is invisible at the
        // call site - a connection here left off this list is a silent way for a future FK on this
        // table, or a cascade depended on from elsewhere in the same transaction, to stop firing.
        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }
}
