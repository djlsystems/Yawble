using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer.Messaging;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ICursors"/>, as each provider must keep it. The first two are
/// <c>CursorRemovalTests</c>' (a cursor is removed, never zeroed); the rest are the interface's own
/// remarks, which no SQLite test asked of the store directly.
/// </summary>
public abstract class CursorsContract : IAsyncDisposable
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<ICursors> CreateAsync();

    public abstract ValueTask DisposeAsync();

    [Fact]
    public async Task A_forgotten_cursor_no_longer_holds_retention_back()
    {
        var store = await CreateAsync();
        var gone = new ContainerId("Alpha", "Gone");
        var live = new ContainerId("Alpha", "Live");

        await store.AdvanceAsync(gone, 5, Ct);
        await store.AdvanceAsync(live, 9, Ct);
        Assert.Equal(5, await store.SlowestAsync(Ct));

        await store.ForgetAsync(gone, Ct);

        Assert.Equal(9, await store.SlowestAsync(Ct));
    }

    [Fact]
    public async Task Forgetting_the_last_cursor_leaves_no_position_rather_than_zero()
    {
        var store = await CreateAsync();
        var only = new ContainerId("Alpha", "Only");

        await store.AdvanceAsync(only, 7, Ct);
        await store.ForgetAsync(only, Ct);

        Assert.Null(await store.SlowestAsync(Ct));
    }

    [Fact]
    public async Task A_cursor_starts_at_zero_and_never_moves_backwards()
    {
        var store = await CreateAsync();
        var reader = new ContainerId("Alpha", "Dev");

        Assert.Equal(0, await store.PositionAsync(reader, Ct));

        await store.AdvanceAsync(reader, 12, Ct);
        await store.AdvanceAsync(reader, 4, Ct);
        await store.AdvanceAsync(reader, 12, Ct);

        Assert.Equal(12, await store.PositionAsync(reader, Ct));
    }

    [Fact]
    public async Task Forgetting_a_team_removes_every_cursor_of_it_whatever_the_case_and_no_other()
    {
        var store = await CreateAsync();
        await store.AdvanceAsync(new ContainerId("Alpha", "Dev"), 1, Ct);
        await store.AdvanceAsync(new ContainerId("alpha", "Manager"), 2, Ct);
        await store.AdvanceAsync(new ContainerId("Alphabet", "Dev"), 3, Ct);

        Assert.Equal(2, await store.ForgetTeamAsync("ALPHA", Ct));

        Assert.Equal(0, await store.PositionAsync(new ContainerId("Alpha", "Dev"), Ct));
        Assert.Equal(3, await store.PositionAsync(new ContainerId("Alphabet", "Dev"), Ct));
        Assert.Equal(3, await store.SlowestAsync(Ct));
    }
}

public sealed class SqliteCursorsTests : CursorsContract
{
    private readonly SqliteContractDatabase _database = new();

    protected override Task<ICursors> CreateAsync() => Task.FromResult<ICursors>(new SqliteMessageStore(_database.Path));

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class SqlServerCursorsTests : CursorsContract
{
    private SqlServerContractDatabase? _database;

    protected override async Task<ICursors> CreateAsync() =>
        new SqlServerMessageStore((_database = await SqlServerContractDatabase.CreateAsync(Ct)).ConnectionString);

    public override async ValueTask DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }
}
