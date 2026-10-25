using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="IUserStore"/> over <c>users</c> on SQL Server: SQLite's <c>SqliteUserStore</c> statement
/// for statement, whose remarks are the argument. The hashing is <see cref="UserPasswords"/>', shared
/// with it, so a hash either writes is the same text and verifies on the other.
///
/// <para>
/// THE UNIQUE KEY DECIDES "TAKEN", as SQLite's does: every write here is one statement, so there is no
/// read to hold, and two sign-ups for one address at once leave one row and one refusal. The email is
/// lower-cased before it is written or asked for, and the column compares exactly.
/// </para>
/// </summary>
public sealed class SqlServerUserStore(string connectionString, int? hashIterations = null) : IUserStore
{
    /// <summary>SQL Server's numbers for a refused duplicate: a UNIQUE constraint, and a unique index.</summary>
    private const int UniqueConstraint = 2627;
    private const int UniqueIndex = 2601;

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);
    private readonly UserPasswords _passwords = new(hashIterations);

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    private Task<int> ExecuteAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        SqlServerOpen.ExecuteAsync(_connectionString, sql, bind, ct);

    private static bool IsTaken(SqlException exception) => exception.Number is UniqueConstraint or UniqueIndex;

    public Task<bool> AnyAsync(CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM users) THEN 1 ELSE 0 END";
                return Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 1;
            },
            ct);

    public Task<StoredUser?> FindAsync(string email, CancellationToken ct = default) =>
        FindOneAsync("SELECT id, email FROM users WHERE email = @value", UserPasswords.Normalise(email), ct);

    public Task<StoredUser?> FindByIdAsync(string id, CancellationToken ct = default) =>
        FindOneAsync("SELECT id, email FROM users WHERE id = @value", id, ct);

    private Task<StoredUser?> FindOneAsync(string sql, string value, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@value", value);

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? new StoredUser(reader.GetString(0), reader.GetString(1)) : null;
            },
            ct);

    public async Task<bool> TryUpdateEmailAsync(string id, string email, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync(
                "UPDATE users SET email = @email WHERE id = @id",
                command =>
                {
                    command.Parameters.AddWithValue("@email", UserPasswords.Normalise(email));
                    command.Parameters.AddWithValue("@id", id);
                },
                ct);
            return true;
        }
        catch (SqlException exception) when (IsTaken(exception))
        {
            // The UNIQUE key refused it, which is the only thing that makes "taken" true rather than
            // merely likely - as on SQLite, and for its reason.
            return false;
        }
    }

    public Task UpdatePasswordAsync(string id, string password, CancellationToken ct = default) =>
        ExecuteAsync(
            "UPDATE users SET password_hash = @hash WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@hash", _passwords.Hash(id, password));
                command.Parameters.AddWithValue("@id", id);
            },
            ct);

    public async Task<StoredUser> CreateAsync(string email, string password, CancellationToken ct = default)
    {
        var user = new StoredUser(Guid.NewGuid().ToString("N"), UserPasswords.Normalise(email));
        var hash = _passwords.Hash(user.Email, password);

        await ExecuteAsync(
            "INSERT INTO users (id, email, password_hash, created_at) VALUES (@id, @email, @hash, @at)",
            command =>
            {
                command.Parameters.AddWithValue("@id", user.Id);
                command.Parameters.AddWithValue("@email", user.Email);
                command.Parameters.AddWithValue("@hash", hash);
                command.Parameters.AddWithValue("@at", Instant.Now());
            },
            ct);

        return user;
    }

    public async Task<StoredUser?> TryCreateAsync(string email, string password, CancellationToken ct = default)
    {
        try
        {
            return await CreateAsync(email, password, ct);
        }
        catch (SqlException exception) when (IsTaken(exception))
        {
            return null;
        }
    }

    public async Task<StoredUser?> VerifyAsync(string email, string password, CancellationToken ct = default)
    {
        var normalised = UserPasswords.Normalise(email);

        var row = await RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, email, password_hash FROM users WHERE email = @email";
                command.Parameters.AddWithValue("@email", normalised);

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token)
                    ? (User: new StoredUser(reader.GetString(0), reader.GetString(1)), Hash: reader.GetString(2))
                    : ((StoredUser User, string Hash)?)null;
            },
            ct);

        // An unknown email is verified against the unknown user's hash, so both answers cost the same.
        return _passwords.Verify(normalised, row?.Hash, password) ? row!.Value.User : null;
    }

    public Task<IReadOnlyList<StoredUser>> ListAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<StoredUser>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, email FROM users ORDER BY email";

                var users = new List<StoredUser>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) users.Add(new StoredUser(reader.GetString(0), reader.GetString(1)));

                return users;
            },
            ct);

    /// <summary>The person's principals go by the foreign key's cascade, as on SQLite.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        ExecuteAsync("DELETE FROM users WHERE id = @id", command => command.Parameters.AddWithValue("@id", id), ct);

    public Task<string?> CurrentTeamForAsync(string userId, CancellationToken ct = default) =>
        TextAsync("SELECT current_team FROM users WHERE id = @id", userId, ct);

    /// <summary>A narrow single-column update, as on SQLite, for its reason.</summary>
    public Task SetCurrentTeamAsync(string userId, string? team, CancellationToken ct = default) =>
        ExecuteAsync(
            "UPDATE users SET current_team = @team WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@team", (object?)team ?? DBNull.Value);
                command.Parameters.AddWithValue("@id", userId);
            },
            ct);

    public Task<string?> AppearanceForAsync(string userId, CancellationToken ct = default) =>
        TextAsync("SELECT appearance FROM users WHERE id = @id", userId, ct);

    public Task SetAppearanceAsync(string userId, string appearance, CancellationToken ct = default) =>
        ExecuteAsync(
            "UPDATE users SET appearance = @appearance WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@appearance", appearance);
                command.Parameters.AddWithValue("@id", userId);
            },
            ct);

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
