using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer.Messaging;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ISubscriptions"/>, as each provider must keep it: the store's own remarks (a set is
/// replaced wholesale, read back in order) and the interface's (a team is cleared whatever its case).
/// The SQLite tests reach these only through a running host (<c>TeamQuiet</c>, the subscription
/// tests over <c>ContainerTestBed</c>).
/// </summary>
public abstract class SubscriptionsContract : IAsyncDisposable
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<ISubscriptions> CreateAsync();

    public abstract ValueTask DisposeAsync();

    [Fact]
    public async Task A_subscription_set_is_replaced_wholesale_and_read_back_in_order()
    {
        var store = await CreateAsync();
        var dev = new ContainerId("Alpha", "Dev");

        await store.SetAsync(dev, ["b.second", "a.first", "c.third"], Ct);
        await store.SetAsync(dev, ["z.last", "a.first", "a.first"], Ct);

        Assert.Equal(["a.first", "z.last"], await store.ForAsync(dev, Ct));
        Assert.Empty(await store.ForAsync(new ContainerId("Alpha", "Other"), Ct));
    }

    [Fact]
    public async Task The_subscribers_are_every_container_with_a_subscription()
    {
        var store = await CreateAsync();
        await store.SetAsync(new ContainerId("Beta", "Dev"), ["x"], Ct);
        await store.SetAsync(new ContainerId("Alpha", "Dev"), ["x", "y"], Ct);
        await store.SetAsync(new ContainerId("Alpha", "Idle"), [], Ct);

        Assert.Equal(
            [new ContainerId("Alpha", "Dev"), new ContainerId("Beta", "Dev")],
            await store.SubscribersAsync(Ct));
    }

    [Fact]
    public async Task Clearing_a_team_removes_every_subscription_of_it_whatever_the_case_and_no_other()
    {
        var store = await CreateAsync();
        await store.SetAsync(new ContainerId("Alpha", "Dev"), ["x", "y"], Ct);
        await store.SetAsync(new ContainerId("alpha", "Manager"), ["x"], Ct);
        await store.SetAsync(new ContainerId("Alphabet", "Dev"), ["x"], Ct);

        Assert.Equal(3, await store.ClearTeamAsync("ALPHA", Ct));

        Assert.Equal([new ContainerId("Alphabet", "Dev")], await store.SubscribersAsync(Ct));
    }
}

public sealed class SqliteSubscriptionsTests : SubscriptionsContract
{
    private readonly SqliteContractDatabase _database = new();

    protected override Task<ISubscriptions> CreateAsync() => Task.FromResult<ISubscriptions>(new SqliteMessageStore(_database.Path));

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class SqlServerSubscriptionsTests : SubscriptionsContract
{
    private SqlServerContractDatabase? _database;

    protected override async Task<ISubscriptions> CreateAsync() =>
        new SqlServerMessageStore((_database = await SqlServerContractDatabase.CreateAsync(Ct)).ConnectionString);

    public override async ValueTask DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }
}
