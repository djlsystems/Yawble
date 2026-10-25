using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IUnfinishedRemovals"/>, as each provider must keep it. The re-record is
/// <c>FreshVolumeTests.An_unfinished_removal_from_before_the_host_left_step_has_nothing_left_on_record</c>'s
/// (same values, from a row the store wrote); the rest are the interface's remarks, the case map and
/// concurrency.
/// </summary>
public abstract class UnfinishedRemovalsContract : IdentityContract
{
    private static readonly DateTimeOffset At = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    protected async Task<IUnfinishedRemovals> CreateAsync() => (await DatabaseAsync()).Removals();

    [Fact]
    public async Task A_re_record_counts_the_attempt_keeps_when_it_was_first_recorded_and_takes_what_the_host_left()
    {
        var store = await CreateAsync();
        await store.RecordAsync(new UnfinishedRemoval("/data/w", RemovalKinds.Emptied, "old-team", null, ["/data/w/a/f"], At, 1), Ct);

        var old = (await store.FindAsync("/data/w", Ct))!;
        Assert.Null(old.HostLeft);
        Assert.Equal(["/data/w/a/f"], old.Remaining);
        Assert.Equal((RemovalKinds.Emptied, "old-team", (string?)null, At, 1), (old.Kind, old.Team, old.Member, old.RecordedAt, old.Attempts));

        await store.RecordAsync(old with { HostLeft = new Dictionary<string, long> { ["/data/w/a"] = 42 }, RecordedAt = At.AddDays(1) }, Ct);
        var again = (await store.FindAsync("/data/w", Ct))!;
        Assert.Equal(42, again.HostLeft!["/data/w/a"]);
        Assert.Equal(2, again.Attempts);
        Assert.Equal(old.RecordedAt, again.RecordedAt);

        await store.RecordAsync(again with { Kind = RemovalKinds.Workspace, Member = "Dev", Remaining = [], HostLeft = new Dictionary<string, long>() }, Ct);
        var third = (await store.FindAsync("/data/w", Ct))!;
        Assert.Equal((RemovalKinds.Workspace, "Dev", 3), (third.Kind, third.Member, third.Attempts));
        Assert.Empty(third.Remaining);
        Assert.Null(third.HostLeft);
    }

    [Fact]
    public async Task Removals_are_listed_by_path_and_a_forgotten_one_is_gone()
    {
        var store = await CreateAsync();
        foreach (var path in new[] { "/data/b", "/data/a", "/data/c" })
        {
            await store.RecordAsync(new UnfinishedRemoval(path, RemovalKinds.TeamRoot, "t", null, [path], At, 1), Ct);
        }

        Assert.Equal(["/data/a", "/data/b", "/data/c"], (await store.ListAsync(Ct)).Select(r => r.Path));

        await store.ForgetAsync("/data/b", Ct);
        await store.ForgetAsync("/data/missing", Ct);

        Assert.Equal(["/data/a", "/data/c"], (await store.ListAsync(Ct)).Select(r => r.Path));
        Assert.Null(await store.FindAsync("/data/b", Ct));
    }

    /// <summary>THE CASE MAP OF A REMOVAL: its path is exact, as SQLite's BINARY key and a Linux path are.</summary>
    [Fact]
    public async Task Paths_differing_only_in_case_are_two_removals()
    {
        var store = await CreateAsync();
        await store.RecordAsync(new UnfinishedRemoval("/data/Team", RemovalKinds.TeamRoot, "Team", null, [], At, 1), Ct);
        await store.RecordAsync(new UnfinishedRemoval("/data/team", RemovalKinds.TeamRoot, "team", null, [], At, 1), Ct);

        Assert.Equal(["/data/Team", "/data/team"], (await store.ListAsync(Ct)).Select(r => r.Path));
        Assert.Null(await store.FindAsync("/DATA/TEAM", Ct));

        await store.ForgetAsync("/data/TEAM", Ct);
        Assert.Equal(2, (await store.ListAsync(Ct)).Count);
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight attempts at one folder recorded at once. Every record succeeds, one row
    /// stands, and it counts all eight: no attempt is lost to another's read of the count.
    /// </summary>
    [Fact]
    public async Task Concurrent_records_of_one_path_count_every_attempt()
    {
        var store = await CreateAsync();

        for (var round = 0; round < 12; round++)
        {
            var path = $"/data/raced{round}";
            await RaceAsync(8, async i =>
            {
                await store.RecordAsync(new UnfinishedRemoval(path, RemovalKinds.Emptied, "t", null, [$"{path}/{i}"], At, 1), Ct);
                return i;
            });

            Assert.Equal(8, (await store.FindAsync(path, Ct))!.Attempts);
        }
    }
}

public sealed class SqliteUnfinishedRemovalsTests : UnfinishedRemovalsContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerUnfinishedRemovalsTests : UnfinishedRemovalsContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
