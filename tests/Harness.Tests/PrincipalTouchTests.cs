using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// `principals.last_used_at` is written at most once a minute per principal - a request a few
/// seconds after the last write touches nothing - except that a read of the value (what
/// `CredentialUseRunner` does before a run) makes the next request write, so a run's first call
/// still moves it once per run.
/// </summary>
public sealed class PrincipalTouchTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-touch-{Guid.NewGuid():N}");
    private readonly ManualTime _clock = new(Start);
    private readonly SqlitePrincipalStore _principals;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PrincipalTouchTests()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "auth.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _principals = new SqlitePrincipalStore(database, clock: _clock);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Two_requests_within_a_minute_write_last_used_once()
    {
        var key = await _principals.MintAsync("alpha/Dev", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);

        Assert.NotNull(await _principals.ResolveAsync(key, Ct));
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.NotNull(await _principals.ResolveAsync(key, Ct));

        Assert.Equal(Start, await _principals.LastUsedAtAsync("alpha/Dev", Ct));

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.NotNull(await _principals.ResolveAsync(key, Ct));

        Assert.Equal(Start.AddSeconds(61), await _principals.LastUsedAtAsync("alpha/Dev", Ct));
    }

    [Fact]
    public async Task A_read_of_the_value_makes_the_next_request_write_so_a_run_still_moves_it()
    {
        var key = await _principals.MintAsync("alpha/Dev", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        Assert.NotNull(await _principals.ResolveAsync(key, Ct));

        // The runner's probe before the run, then the run's first call two seconds later.
        var before = await _principals.LastUsedAtAsync("alpha/Dev", Ct);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull(await _principals.ResolveAsync(key, Ct));
        var after = await _principals.LastUsedAtAsync("alpha/Dev", Ct);

        Assert.Equal(Start, before);
        Assert.Equal(Start.AddSeconds(2), after);
    }

    [Fact]
    public async Task Two_principals_are_throttled_apart()
    {
        var dev = await _principals.MintAsync("alpha/Dev", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        Assert.NotNull(await _principals.ResolveAsync(dev, Ct));
        _clock.Advance(TimeSpan.FromSeconds(5));
        var tester = await _principals.MintAsync("alpha/Tester", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        Assert.NotNull(await _principals.ResolveAsync(tester, Ct));

        Assert.Equal(Start, await _principals.LastUsedAtAsync("alpha/Dev", Ct));
        Assert.Equal(Start.AddSeconds(5), await _principals.LastUsedAtAsync("alpha/Tester", Ct));
    }
}
