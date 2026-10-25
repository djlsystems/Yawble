using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer.Messaging;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IPendingDeliveries"/>, as each provider must keep it. The first is
/// <c>BatchItemOutcomeTests</c>' (a deferral names its run, for the member and the team); the rest are
/// the interface's remarks (idempotent, never clears Started; the team sweep is by team whatever the
/// case) and Review Focus 2's two writers at once.
/// </summary>
public abstract class PendingDeliveriesContract : IAsyncDisposable
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<IPendingDeliveries> CreateAsync();

    public abstract ValueTask DisposeAsync();

    [Fact]
    public async Task A_deferred_pending_delivery_names_its_run_for_the_member_and_the_team()
    {
        var pending = await CreateAsync();

        await pending.AddAsync(Dev, 10, Ct);
        await pending.StartAsync(Dev, 10, Ct);
        await pending.AddAsync(Dev, 11, Ct);
        await pending.DeferAsync(Dev, 10, fromRun: 9, Ct);

        Assert.Equal(
            [new PendingDelivery(10, false, 9), new PendingDelivery(11, false)],
            await pending.ForAsync(Dev, Ct));
        Assert.Equal(
            [new TeamPendingDelivery(Dev.ToString(), 10, false, 9), new TeamPendingDelivery(Dev.ToString(), 11, false)],
            await pending.ForTeamAsync("Alpha", Ct));
    }

    [Fact]
    public async Task A_re_offer_of_a_started_delivery_does_not_make_it_look_unstarted()
    {
        var pending = await CreateAsync();

        await pending.AddAsync(Dev, 10, Ct);
        await pending.StartAsync(Dev, 10, Ct);
        await pending.AddAsync(Dev, 10, Ct);

        Assert.Equal([new PendingDelivery(10, true)], await pending.ForAsync(Dev, Ct));
    }

    [Fact]
    public async Task A_delivery_is_one_row_whatever_the_case_of_its_subscriber()
    {
        var pending = await CreateAsync();

        await pending.AddAsync(Dev, 10, Ct);
        await pending.AddAsync(new ContainerId("ALPHA", "dev"), 10, Ct);
        await pending.RemoveAsync(new ContainerId("alpha", "DEV"), 11, Ct);

        Assert.Equal([new PendingDelivery(10, false)], await pending.ForAsync(new ContainerId("alpha", "dev"), Ct));
    }

    [Fact]
    public async Task Removing_a_team_takes_every_delivery_of_it_whatever_the_case_and_no_other()
    {
        var pending = await CreateAsync();
        await pending.AddAsync(Dev, 1, Ct);
        await pending.AddAsync(new ContainerId("alpha", "Manager"), 2, Ct);
        await pending.AddAsync(new ContainerId("Alphabet", "Dev"), 3, Ct);
        await pending.AddAsync(new ContainerId("Beta", "Dev"), 4, Ct);

        Assert.Empty(await pending.ForTeamAsync("Alp", Ct));
        Assert.Equal(2, await pending.RemoveAllForTeamAsync("ALPHA", Ct));
        Assert.Equal(1, await pending.RemoveAllAsync(new ContainerId("beta", "dev"), Ct));

        Assert.Equal([new TeamPendingDelivery("Alphabet/Dev", 3, false)], await pending.AllAsync(Ct));
    }

    [Fact]
    public async Task Every_delivery_is_listed_by_subscriber_then_seq()
    {
        var pending = await CreateAsync();
        await pending.AddAsync(new ContainerId("Beta", "Dev"), 1, Ct);
        await pending.AddAsync(new ContainerId("Alpha", "Ops"), 3, Ct);
        await pending.AddAsync(new ContainerId("Alpha", "Ops"), 2, Ct);
        await pending.AddAsync(new ContainerId("Alpha", "dev"), 9, Ct);

        Assert.Equal(
            ["Alpha/dev 9", "Alpha/Ops 2", "Alpha/Ops 3", "Beta/Dev 1"],
            (await pending.AllAsync(Ct)).Select(row => $"{row.Subscriber} {row.Seq}"));
    }

    /// <summary>
    /// REVIEW FOCUS 2: TWO WRITERS AT ONCE. Sweeps offering the same delivery concurrently - a
    /// restart's resume racing a live offer - leave one row, and none of them fails on the key.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_claims_take_one_row()
    {
        var pending = await CreateAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            pending.AddAsync(i % 2 == 0 ? Dev : new ContainerId("alpha", "dev"), 42, Ct)));

        Assert.Equal([new PendingDelivery(42, false)], await pending.ForAsync(Dev, Ct));
    }
}

public sealed class SqlitePendingDeliveriesTests : PendingDeliveriesContract
{
    private readonly SqliteContractDatabase _database = new();

    protected override Task<IPendingDeliveries> CreateAsync() =>
        Task.FromResult<IPendingDeliveries>(new SqlitePendingDeliveries(_database.Path));

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class SqlServerPendingDeliveriesTests : PendingDeliveriesContract
{
    private SqlServerContractDatabase? _database;

    protected override async Task<IPendingDeliveries> CreateAsync() =>
        new SqlServerPendingDeliveries((_database = await SqlServerContractDatabase.CreateAsync(Ct)).ConnectionString);

    public override async ValueTask DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }
}
