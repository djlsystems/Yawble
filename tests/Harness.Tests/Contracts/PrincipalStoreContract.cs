using System.Security.Cryptography;
using System.Text;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IPrincipalStore"/>, as each provider must keep it. The three throttle tests are
/// <c>PrincipalTouchTests</c>', same names and values; the rest are the interface's own remarks (a
/// credential is returned once and kept only as its hash, a re-mint replaces, revoking by team is by
/// the column whatever the case, the owner question answers only for an API key) and the review's
/// credential and concurrency rules.
/// </summary>
public abstract class PrincipalStoreContract : IdentityContract
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _clock = new(Start);

    protected async Task<IPrincipalStore> CreateAsync() => (await DatabaseAsync()).Principals(_clock);

    [Fact]
    public async Task Two_requests_within_a_minute_write_last_used_once()
    {
        var principals = await CreateAsync();
        var key = await principals.MintAsync("alpha/Dev", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);

        Assert.NotNull(await principals.ResolveAsync(key, Ct));
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.NotNull(await principals.ResolveAsync(key, Ct));

        Assert.Equal(Start, await principals.LastUsedAtAsync("alpha/Dev", Ct));

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.NotNull(await principals.ResolveAsync(key, Ct));

        Assert.Equal(Start.AddSeconds(61), await principals.LastUsedAtAsync("alpha/Dev", Ct));
    }

    [Fact]
    public async Task A_read_of_the_value_makes_the_next_request_write_so_a_run_still_moves_it()
    {
        var principals = await CreateAsync();
        var key = await principals.MintAsync("alpha/Dev", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        Assert.NotNull(await principals.ResolveAsync(key, Ct));

        var before = await principals.LastUsedAtAsync("alpha/Dev", Ct);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull(await principals.ResolveAsync(key, Ct));
        var after = await principals.LastUsedAtAsync("alpha/Dev", Ct);

        Assert.Equal(Start, before);
        Assert.Equal(Start.AddSeconds(2), after);
    }

    [Fact]
    public async Task Two_principals_are_throttled_apart()
    {
        var principals = await CreateAsync();
        var dev = await principals.MintAsync("alpha/Dev", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        Assert.NotNull(await principals.ResolveAsync(dev, Ct));
        _clock.Advance(TimeSpan.FromSeconds(5));
        var tester = await principals.MintAsync("alpha/Tester", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        Assert.NotNull(await principals.ResolveAsync(tester, Ct));

        Assert.Equal(Start, await principals.LastUsedAtAsync("alpha/Dev", Ct));
        Assert.Equal(Start.AddSeconds(5), await principals.LastUsedAtAsync("alpha/Tester", Ct));
    }

    [Fact]
    public async Task A_minted_credential_resolves_to_its_principal_and_nothing_else_does()
    {
        var principals = await CreateAsync();
        var key = await principals.MintAsync(
            "key-1", PrincipalKind.ApiKey, null, new HashSet<string> { Permits.Read, Permits.Tell }, ownerUserId: null, label: "CI", ct: Ct);

        var principal = await principals.ResolveAsync(key, Ct);

        Assert.NotNull(principal);
        Assert.Equal(("key-1", PrincipalKind.ApiKey, (string?)null), (principal.Id, principal.Kind, principal.OwnerUserId));
        Assert.True(principal.Permits.SetEquals([Permits.Read, Permits.Tell]));
        Assert.Null(await principals.ResolveAsync(key.ToUpperInvariant() + "x", Ct));
        Assert.Null(await principals.ResolveAsync("  ", Ct));
        Assert.Null(await principals.LastUsedAtAsync("nobody", Ct));
    }

    /// <summary>A container recreated under its name must not leave its predecessor's credential working.</summary>
    [Fact]
    public async Task A_re_mint_under_one_id_revokes_the_previous_credential_and_clears_last_used()
    {
        var principals = await CreateAsync();
        var first = await principals.MintAsync("alpha/Dev", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        Assert.NotNull(await principals.ResolveAsync(first, Ct));
        Assert.Equal(Start, await principals.LastUsedAtAsync("alpha/Dev", Ct));

        var second = await principals.MintAsync("alpha/Dev", PrincipalKind.Container, "beta", Permits.All, ct: Ct);

        Assert.Null(await principals.ResolveAsync(first, Ct));
        Assert.Null(await principals.LastUsedAtAsync("alpha/Dev", Ct));
        Assert.Equal("alpha/Dev", (await principals.ResolveAsync(second, Ct))!.Id);
        Assert.Equal("beta", await principals.TeamForAsync("alpha/Dev", Ct));
    }

    /// <summary>
    /// CREDENTIALS ARE STORED AS TODAY: only the upper-case hex SHA-256 of the credential's UTF-8, and
    /// its first eight characters as the prefix. A supplied credential is trimmed first.
    /// </summary>
    [Fact]
    public async Task A_supplied_credential_is_trimmed_and_kept_only_as_its_hash_and_prefix()
    {
        var database = await DatabaseAsync();
        var principals = database.Principals(_clock);

        var returned = await principals.MintAsync("host", PrincipalKind.User, null, Permits.All, credential: "  fixed-bootstrap-credential  ", ct: Ct);

        var row = Assert.Single(await database.RowsAsync("SELECT hash, prefix FROM principals WHERE id = 'host'"));
        Assert.Equal("fixed-bootstrap-credential", returned);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixed-bootstrap-credential"))), row[0]);
        Assert.Equal("fixed-bo", row[1]);
        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM principals WHERE hash LIKE '%fixed%' OR permits LIKE '%fixed%'"));
        Assert.NotNull(await principals.ResolveAsync("fixed-bootstrap-credential", Ct));

        var tiny = await principals.MintAsync("tiny", PrincipalKind.User, null, Permits.All, credential: "abc", ct: Ct);
        Assert.Equal("abc", await database.TextAsync("SELECT prefix FROM principals WHERE id = 'tiny'"));
        Assert.NotNull(await principals.ResolveAsync(tiny, Ct));
    }

    /// <summary>Authority that cannot be read is a refusal, and a refused row records no use.</summary>
    [Fact]
    public async Task A_row_whose_permits_or_kind_cannot_be_read_is_refused_and_not_touched()
    {
        var database = await DatabaseAsync();
        var principals = database.Principals(_clock);
        var bad = await principals.MintAsync("bad-permits", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        var odd = await principals.MintAsync("bad-kind", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);

        await database.ExecuteAsync("UPDATE principals SET permits = 'not json' WHERE id = 'bad-permits'");
        await database.ExecuteAsync("UPDATE principals SET kind = 'Bogus' WHERE id = 'bad-kind'");

        Assert.Null(await principals.ResolveAsync(bad, Ct));
        Assert.Null(await principals.ResolveAsync(odd, Ct));
        Assert.Null(await principals.LastUsedAtAsync("bad-permits", Ct));
        Assert.Null(await principals.LastUsedAtAsync("bad-kind", Ct));
    }

    /// <summary>
    /// BY THE TEAM COLUMN, WHATEVER ITS CASE: every credential of the team goes, an API key minted
    /// against it included, and another team's stay. A principal's id is exact.
    /// </summary>
    [Fact]
    public async Task Revoking_by_team_removes_every_credential_of_the_team_in_any_case_and_no_other()
    {
        var principals = await CreateAsync();
        var dev = await principals.MintAsync("Alpha/Dev", PrincipalKind.Container, "Alpha", Permits.All, ct: Ct);
        var session = await principals.MintAsync("console-1", PrincipalKind.Concierge, "alpha", Permits.All, ct: Ct);
        var beta = await principals.MintAsync("Beta/Dev", PrincipalKind.Container, "Beta", Permits.All, ct: Ct);

        Assert.Equal("Alpha", await principals.TeamForAsync("Alpha/Dev", Ct));
        Assert.Null(await principals.TeamForAsync("alpha/dev", Ct));

        Assert.Equal(2, await principals.RevokeForTeamAsync("ALPHA", Ct));

        Assert.Null(await principals.ResolveAsync(dev, Ct));
        Assert.Null(await principals.ResolveAsync(session, Ct));
        Assert.NotNull(await principals.ResolveAsync(beta, Ct));

        await principals.RevokeAsync("beta/dev", Ct);
        Assert.NotNull(await principals.ResolveAsync(beta, Ct));
        await principals.RevokeAsync("Beta/Dev", Ct);
        Assert.Null(await principals.ResolveAsync(beta, Ct));
    }

    [Fact]
    public async Task Api_keys_are_listed_newest_first_per_owner_and_for_the_tenant_and_only_keys_have_an_owner()
    {
        var database = await DatabaseAsync();
        var users = database.Users();
        var principals = database.Principals(_clock);
        var alice = await users.CreateAsync("alice@example.test", "pw", Ct);
        var bob = await users.CreateAsync("bob@example.test", "pw", Ct);

        await principals.MintAsync("key-a1", PrincipalKind.ApiKey, null, Permits.All, ownerUserId: alice.Id, label: "first", ct: Ct);
        await principals.MintAsync("key-b1", PrincipalKind.ApiKey, null, Permits.All, ownerUserId: bob.Id, label: null, ct: Ct);
        await principals.MintAsync("key-a2", PrincipalKind.ApiKey, null, Permits.All, ownerUserId: alice.Id, label: "second", ct: Ct);
        await principals.MintAsync("console:alice", PrincipalKind.Concierge, "Alpha", Permits.All, ownerUserId: alice.Id, ct: Ct);
        await database.ExecuteAsync("UPDATE principals SET created_at = '2026-10-01T00:00:00.0000000+00:00' WHERE id = 'key-a1'");
        await database.ExecuteAsync("UPDATE principals SET created_at = '2026-10-02T00:00:00.0000000+00:00' WHERE id = 'key-b1'");
        await database.ExecuteAsync("UPDATE principals SET created_at = '2026-10-03T00:00:00.0000000+00:00' WHERE id = 'key-a2'");

        var mine = await principals.ListApiKeysForAsync(alice.Id, Ct);
        Assert.Equal(["key-a2", "key-a1"], mine.Select(k => k.Id));
        Assert.Equal(("second", alice.Id, new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), (DateTimeOffset?)null),
            (mine[0].Label, mine[0].OwnerUserId, mine[0].CreatedAt, mine[0].LastUsedAt));
        Assert.Equal(8, mine[0].Prefix.Length);

        Assert.Equal(["key-a2", "key-b1", "key-a1"], (await principals.ListAllApiKeysAsync(Ct)).Select(k => k.Id));
        Assert.Null((await principals.ListAllApiKeysAsync(Ct)).Single(k => k.Id == "key-b1").Label);

        Assert.Equal(alice.Id, await principals.OwnerOfApiKeyAsync("key-a1", Ct));
        Assert.Null(await principals.OwnerOfApiKeyAsync("console:alice", Ct));
        Assert.Null(await principals.OwnerOfApiKeyAsync("nobody", Ct));
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight mints of one id at once - a container recreated while its predecessor's
    /// restore runs. Every mint succeeds, one row stands, and exactly one of the eight credentials works.
    /// </summary>
    [Fact]
    public async Task Concurrent_mints_of_one_id_leave_one_row_and_one_working_credential()
    {
        var database = await DatabaseAsync();
        var principals = database.Principals(_clock);

        for (var round = 0; round < 12; round++)
        {
            var id = $"alpha/Raced{round}";
            var credentials = await RaceAsync(8, _ => principals.MintAsync(id, PrincipalKind.Container, "alpha", Permits.All, ct: Ct));

            Assert.Equal(1, await database.LongAsync($"SELECT COUNT(*) FROM principals WHERE id = '{id}'"));
            var working = 0;
            foreach (var credential in credentials)
            {
                if (await principals.ResolveAsync(credential, Ct) is not null) working++;
            }

            Assert.Equal(1, working);
        }
    }
}

public sealed class SqlitePrincipalStoreTests : PrincipalStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerPrincipalStoreTests : PrincipalStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
