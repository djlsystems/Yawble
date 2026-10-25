using Microsoft.Data.Sqlite;
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

    /// <summary>The hasher and the unknown user's hash, shared with the SQL Server store so a hash
    /// either writes verifies on the other. See <see cref="UserPasswords"/>.</summary>
    private readonly UserPasswords _passwords;

    /// <summary>What a store built without <c>hashIterations</c> uses. See
    /// <see cref="UserPasswords.DefaultHashIterations"/>.</summary>
    public static int DefaultHashIterations => UserPasswords.DefaultHashIterations;

    /// <param name="hashIterations">PBKDF2 iterations, or null for the framework default. See
    /// <see cref="UserPasswords(int?)"/>.</param>
    public SqliteUserStore(
        string databasePath,
        int? hashIterations = null,
        SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _passwords = new UserPasswords(hashIterations);

        _connectionString = SqliteOpen.ConnectionString(databasePath);

        _durability = durability;
    }

    /// <summary>Trimmed and lowercased. An identifier a person types is the same identifier
    /// whatever case they typed it in, and the UNIQUE index has to see that too or two accounts
    /// differing only by case both exist.</summary>
    public static string Normalise(string email) => UserPasswords.Normalise(email);

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
        // The hash is NOT bound to the email (see UserPasswords.Hash), which is why this method does
        // not need to know the address at all.
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id";
        command.Parameters.AddWithValue("$hash", _passwords.Hash(id, password));
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
        command.Parameters.AddWithValue("$hash", _passwords.Hash(user.Email, password));
        command.Parameters.AddWithValue("$at", Instant.Now());

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
            // Deliberate work on a path that could return immediately - see UserPasswords.Verify.
            _passwords.Verify(normalised, null, password);
            return null;
        }

        return _passwords.Verify(normalised, reader.GetString(2), password)
            ? new StoredUser(reader.GetString(0), reader.GetString(1))
            : null;
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

    public async Task<string?> AppearanceForAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT appearance FROM users WHERE id = $id";
        command.Parameters.AddWithValue("$id", userId);

        var value = await command.ExecuteScalarAsync(ct);

        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>A narrow single-column update, as <see cref="SetCurrentTeamAsync"/> is, for its reason.</summary>
    public async Task SetAppearanceAsync(string userId, string appearance, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE users SET appearance = $appearance WHERE id = $id";
        command.Parameters.AddWithValue("$appearance", appearance);
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
