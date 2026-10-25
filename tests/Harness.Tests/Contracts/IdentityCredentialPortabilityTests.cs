using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// A CREDENTIAL WRITTEN BY ONE PROVIDER'S STORE VERIFIES THROUGH THE OTHER'S: a person's password hash
/// and a principal's credential hash are the same text on both, so rows moved between the two (an
/// instance copied from SQLite to SQL Server) keep every password and key working. Each row is copied
/// as stored, column for column, past the stores.
/// </summary>
public sealed class IdentityCredentialPortabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string UserColumns = "id, email, password_hash, created_at";
    private const string PrincipalColumns = "id, kind, team, hash, prefix, permits, created_at, owner_user_id";

    [Fact]
    public async Task A_password_and_a_key_written_on_sqlite_verify_on_sql_server()
    {
        await using var sqlServer = await SqlServerIdentityDatabase.CreateAsync();
        await using var sqlite = new SqliteIdentityDatabase();

        var (user, key) = await WriteAsync(sqlite);
        await CopyAsync(sqlite, sqlServer);

        await AssertVerifiesAsync(sqlServer, user, key);
    }

    [Fact]
    public async Task A_password_and_a_key_written_on_sql_server_verify_on_sqlite()
    {
        await using var sqlServer = await SqlServerIdentityDatabase.CreateAsync();
        await using var sqlite = new SqliteIdentityDatabase();

        var (user, key) = await WriteAsync(sqlServer);
        await CopyAsync(sqlServer, sqlite);

        await AssertVerifiesAsync(sqlite, user, key);
    }

    private static async Task<(StoredUser User, string Key)> WriteAsync(IdentityDatabase database)
    {
        var user = await database.Users(hashIterations: 1000).CreateAsync("alice@example.test", "correct horse battery", Ct);
        var key = await database.Principals().MintAsync("key-1", PrincipalKind.ApiKey, null, Permits.All, ownerUserId: user.Id, label: "CI", ct: Ct);
        return (user, key);
    }

    private static async Task CopyAsync(IdentityDatabase from, IdentityDatabase to)
    {
        foreach (var (table, columns) in new[] { ("users", UserColumns), ("principals", PrincipalColumns) })
        {
            foreach (var row in await from.RowsAsync($"SELECT {columns} FROM {table}"))
            {
                // Every value here is an id, a base64 or hex hash, a JSON list of permit names or an
                // instant: none holds a quote.
                var values = string.Join(", ", row.Select(v => v is null ? "NULL" : $"'{v}'"));
                await to.ExecuteAsync($"INSERT INTO {table} ({columns}) VALUES ({values})");
            }
        }
    }

    private static async Task AssertVerifiesAsync(IdentityDatabase database, StoredUser user, string key)
    {
        Assert.Equal(user, await database.Users(hashIterations: null).VerifyAsync("Alice@example.test", "correct horse battery", Ct));
        Assert.Null(await database.Users().VerifyAsync("alice@example.test", "wrong", Ct));

        var principal = await database.Principals().ResolveAsync(key, Ct);
        Assert.NotNull(principal);
        Assert.Equal(("key-1", PrincipalKind.ApiKey, user.Id), (principal.Id, principal.Kind, principal.OwnerUserId));
        Assert.True(principal.Permits.SetEquals(Permits.All));
    }
}
