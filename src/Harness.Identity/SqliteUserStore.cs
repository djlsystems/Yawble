using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// Users, over the same SQLite file the message log uses.
///
/// PasswordHasher does the security-relevant part - the algorithm, the salt, the iteration count -
/// and it arrives without EF Core, which is precisely why this codebase can decline to own password
/// hashing without also acquiring a second storage mechanism.
/// </summary>
public sealed class SqliteUserStore : IUserStore
{
    private readonly string _connectionString;

    /// <summary>Absence means the safe value, exactly as `hashIterations` above means the strong one.</summary>
    private readonly SqliteDurability _durability;
    private readonly PasswordHasher<string> _hasher;

    /// <summary>
    /// What a store built without <c>hashIterations</c> uses - i.e. what production uses.
    ///
    /// READ from the framework rather than restated as a literal, and that is the point of it
    /// existing at all: a copy of "100,000" here would be a second store of a fact that Microsoft
    /// owns and raises between releases (it was 10,000 before .NET 8), and the copy would go stale
    /// silently while reading as deliberate. Anything comparing against the default - the start-up
    /// warning, the spec that pins it - asks this and gets whatever the framework actually does.
    /// </summary>
    public static int DefaultHashIterations { get; } = new PasswordHasherOptions().IterationCount;

    /// <summary>
    /// A hash of a password nobody holds, verified against when there is no such user.
    ///
    /// Without it an unknown email returns in microseconds while a known one pays a full PBKDF2
    /// verification - tens of milliseconds, trivially measurable across a network, on an anonymous
    /// endpoint that an instance may expose through a tunnel. That
    /// timing difference answers "does this account exist?", which is exactly the question
    /// VerifyAsync's return value is careful never to answer.
    ///
    /// Hashed once per store rather than per call: the point is to spend the same work as a real
    /// verification, not to spend it twice. There is one store per process, so "once per store" costs
    /// the same as a `static readonly` would.
    ///
    /// It MUST come from <see cref="_hasher"/> rather than from a hasher of its own. The iteration
    /// count is embedded in the hash and verification reads it from there, so a hash made by a
    /// DIFFERENT hasher costs whatever that one was configured for - which is the timing difference
    /// this field exists to remove, reintroduced through the field itself.
    /// </summary>
    private readonly string _unknownUserHash;

    /// <param name="hashIterations">
    /// PBKDF2 iterations, or null for the framework default - which is what production passes and
    /// what every caller that forgets this parameter gets. It exists for the test host: the default
    /// is ~100,000 iterations of HMAC-SHA512, deliberately, and the Host suite pays two of them per
    /// fixture across ~490 fixtures, which measured at over half that suite's total runtime.
    ///
    /// Optional-with-a-null-default is safe HERE in a way this codebase is usually right to
    /// distrust, and the reason is the direction it fails in: omitting it gives you the STRONG
    /// hasher. A parameter whose absence weakens a credential would be the shape to refuse.
    /// </param>
    public SqliteUserStore(
        string databasePath,
        int? hashIterations = null,
        SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _hasher = hashIterations is null
            ? new PasswordHasher<string>()
            : new PasswordHasher<string>(Options.Create(
                new PasswordHasherOptions { IterationCount = hashIterations.Value }));

        _unknownUserHash = _hasher.HashPassword("", Guid.NewGuid().ToString("N"));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,

            // Off, unlike Messaging's store. Pooling keeps the native connection - and its file
            // handle - alive past Dispose() so the NEXT open is cheap, which is exactly wrong here:
            // auth writes are rare and short (see AuthSchema's remarks), so that cost is not worth
            // paying, and a lingering handle keeps the database file open after the store is done
            // with it.
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

    /// <summary>Trimmed and lowercased. An identifier a person types is the same identifier
    /// whatever case they typed it in, and the UNIQUE index has to see that too or two accounts
    /// differing only by case both exist.</summary>
    public static string Normalise(string email) => email.Trim().ToLowerInvariant();

    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM users)";

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
    }

    public async Task<StoredUser?> FindAsync(string email, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, email FROM users WHERE email = $email";
        command.Parameters.AddWithValue("$email", Normalise(email));

        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new StoredUser(reader.GetString(0), reader.GetString(1))
            : null;
    }

    public async Task<StoredUser?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, email FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new StoredUser(reader.GetString(0), reader.GetString(1))
            : null;
    }

    public async Task<bool> TryUpdateEmailAsync(
        string id, string email, CancellationToken ct = default)
    {
        var normalised = Normalise(email);

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET email = $email WHERE id = $id";
        command.Parameters.AddWithValue("$email", normalised);
        command.Parameters.AddWithValue("$id", id);

        try
        {
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // The UNIQUE index refused it, which is the only thing that makes "taken" true rather
            // than merely likely. A SELECT first would be a check-then-act with a window in it -
            // two tabs on one account can race it, and the index is the thing that actually
            // decides. Constraint violation is error 19; the extended code distinguishes which
            // constraint, and users has exactly one.
            return false;
        }
    }

    public async Task UpdatePasswordAsync(
        string id, string password, CancellationToken ct = default)
    {
        // The hash is NOT bound to the email despite HashPassword taking one: PasswordHasher<T>
        // ignores its user argument entirely - it salts per call and stores the salt in the hash.
        // That is why changing an address does not invalidate a password, and why this method does
        // not need to know the address at all.
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id";
        command.Parameters.AddWithValue("$hash", _hasher.HashPassword(id, password));
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredUser> CreateAsync(
        string email, string password, CancellationToken ct = default)
    {
        var user = new StoredUser(Guid.NewGuid().ToString("N"), Normalise(email));

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO users (id, email, password_hash, created_at)
            VALUES ($id, $email, $hash, $at)
            """;
        command.Parameters.AddWithValue("$id", user.Id);
        command.Parameters.AddWithValue("$email", user.Email);
        command.Parameters.AddWithValue("$hash", _hasher.HashPassword(user.Email, password));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(ct);

        return user;
    }

    public async Task<StoredUser?> TryCreateAsync(
        string email, string password, CancellationToken ct = default)
    {
        try
        {
            return await CreateAsync(email, password, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Same idiom as TryUpdateEmailAsync above: the UNIQUE index on users.email is what
            // actually decides "taken" rather than a SELECT this could race.
            return null;
        }
    }

    public async Task<StoredUser?> VerifyAsync(
        string email, string password, CancellationToken ct = default)
    {
        var normalised = Normalise(email);

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, email, password_hash FROM users WHERE email = $email";
        command.Parameters.AddWithValue("$email", normalised);

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            // Deliberate work on a path that could return immediately - see _unknownUserHash. The
            // result is discarded because it cannot be anything but Failed; what matters is that
            // this branch costs what the branch below costs.
            _ = _hasher.VerifyHashedPassword(normalised, _unknownUserHash, password);
            return null;
        }

        var stored = reader.GetString(2);
        var outcome = _hasher.VerifyHashedPassword(normalised, stored, password);

        return outcome == PasswordVerificationResult.Failed
            ? null
            : new StoredUser(reader.GetString(0), reader.GetString(1));
    }

    public async Task<IReadOnlyList<StoredUser>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, email FROM users ORDER BY email";

        var users = new List<StoredUser>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            users.Add(new StoredUser(reader.GetString(0), reader.GetString(1)));
        }

        return users;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        // Rows that reference the user (API keys, principals) go by the cascades in AuthSchema, and
        // Open()'s foreign_keys pragma is what makes those cascades real.
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// NULL for an unknown id and NULL for a person pointed at nothing, and nothing here has to tell
    /// them apart: both answer "this caller has no current team", which is the same refusal with the
    /// same repair.
    /// </summary>
    public async Task<string?> CurrentTeamForAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT current_team FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", userId);

        var value = await command.ExecuteScalarAsync(ct);

        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>
    /// A NARROW single-column update. Reusing a whole-record upsert for it would be the change to
    /// refuse, for the reason SetFloorAsync already states elsewhere: such an upsert takes every
    /// column, so a caller writes back whatever the row held when it was read.
    /// </summary>
    public async Task SetCurrentTeamAsync(
        string userId, string? team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE users SET current_team = $team WHERE id = $id";
        command.Parameters.AddWithValue("$team", (object?)team ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", userId);

        await command.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // BOTH pragmas, on EVERY connection, because both are per-connection and Pooling = false
        // means every Open() is a fresh one. A connection without busy_timeout
        // fails on the first lock, and one without foreign_keys accepts every ON DELETE CASCADE
        // naming users and then quietly ignores it.
        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }
}
