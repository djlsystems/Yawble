using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer.Ledger;
using Harness.SqlServer.Messaging;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IWakeStore"/>, as each provider must keep it. The round trip is
/// <c>FreshVolumeTests.The_wakes_step_applies_to_an_empty_and_an_existing_volume</c>'s, the same
/// values; the transaction writes go through <see cref="IMessageLog.AppendWithinAsync"/> over the same
/// database, as <c>WakeMe</c> uses them.
/// </summary>
public abstract class WakeStoreContract : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<IWakeStore> CreateAsync();

    /// <summary>The log over the same database, called after <see cref="CreateAsync"/>.</summary>
    protected abstract IMessageLog Log { get; }

    public abstract ValueTask DisposeAsync();

    private async Task<PendingWake> InsertAsync(IWakeStore wakes, PendingWake wake)
    {
        PendingWake? stored = null;
        await Log.AppendWithinAsync(
            async (connection, transaction, token) =>
            {
                stored = await wakes.InsertWithinAsync(connection, transaction, wake, token);
                return null;
            },
            (_, _, _, _) => Task.CompletedTask,
            Ct);
        return stored!;
    }

    [Fact]
    public async Task A_pending_wake_is_read_back_by_member_workflow_and_due_time_and_taken_once()
    {
        var wakes = await CreateAsync();
        var stored = await InsertAsync(wakes, new PendingWake(0, "Alpha", "Dev", 1, 1, At, At.AddMinutes(10), "look again"));

        Assert.Equal(stored, await wakes.FindAsync(stored.Id, Ct));
        Assert.Equal(stored, Assert.Single(await wakes.ForWorkflowAsync(1, Ct)));
        Assert.Equal(stored, Assert.Single(await wakes.ForTeamAsync("alpha", Ct)));
        Assert.Empty(await wakes.DueAsync(At.AddMinutes(9), Ct));
        Assert.Equal(stored, Assert.Single(await wakes.DueAsync(At.AddMinutes(10), Ct)));
        Assert.Equal(At.AddMinutes(10), await wakes.EarliestDueAsync(Ct));

        PendingWake? first = null, second = null;
        await Log.AppendWithinAsync(
            async (connection, transaction, token) =>
            {
                first = await wakes.TakeWithinAsync(connection, transaction, stored.Id, token);
                second = await wakes.TakeWithinAsync(connection, transaction, stored.Id, token);
                return null;
            },
            (_, _, _, _) => Task.CompletedTask,
            Ct);

        Assert.Equal(stored, first);
        Assert.Null(second);
        Assert.Empty(await wakes.AllAsync(Ct));
        Assert.Null(await wakes.EarliestDueAsync(Ct));
    }

    [Fact]
    public async Task A_members_wakes_are_read_earliest_first_inside_the_transaction_whatever_the_case()
    {
        var wakes = await CreateAsync();
        var later = await InsertAsync(wakes, new PendingWake(0, "Alpha", "Dev", 1, 1, At, At.AddHours(2), "later"));
        var sooner = await InsertAsync(wakes, new PendingWake(0, "Alpha", "Dev", 2, 1, At, At.AddHours(1), "sooner"));
        await InsertAsync(wakes, new PendingWake(0, "Alpha", "Ops", 1, 1, At, At.AddMinutes(1), "someone else"));

        IReadOnlyList<PendingWake> mine = [];
        await Log.AppendWithinAsync(
            async (connection, transaction, token) =>
            {
                mine = await wakes.ForMemberWithinAsync(connection, transaction, new ContainerId("ALPHA", "dev"), token);
                await wakes.SetAskedWithinAsync(connection, transaction, later.Id, 77, token);
                return null;
            },
            (_, _, _, _) => Task.CompletedTask,
            Ct);

        Assert.Equal([sooner, later], mine);
        Assert.Equal(77, (await wakes.FindAsync(later.Id, Ct))!.AskedSeq);
        Assert.Equal(["Dev", "Dev", "Ops"], (await wakes.AllAsync(Ct)).Select(w => w.Member).Order(StringComparer.Ordinal));
        Assert.Equal(3, await wakes.DeleteForTeamAsync("alpha", Ct));
        Assert.Empty(await wakes.AllAsync(Ct));
    }

    /// <summary>
    /// REVIEW FOCUS 2: TWO WRITERS AT ONCE. Two firings of one wake racing - each in its own append
    /// transaction, as the sweep and a cancel each take one - and exactly one of them takes the row.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_claims_take_one_row()
    {
        var wakes = await CreateAsync();
        var stored = await InsertAsync(wakes, new PendingWake(0, "Alpha", "Dev", 1, 1, At, At.AddMinutes(10), "once"));

        var taken = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            PendingWake? mine = null;
            await Log.AppendWithinAsync(
                async (connection, transaction, token) =>
                {
                    mine = await wakes.TakeWithinAsync(connection, transaction, stored.Id, token);
                    return null;
                },
                (_, _, _, _) => Task.CompletedTask,
                Ct);
            return mine;
        }));

        Assert.Equal(stored, Assert.Single(taken, w => w is not null));
    }
}

public sealed class SqliteWakeStoreTests : WakeStoreContract
{
    private readonly SqliteContractDatabase _database = new();

    protected override Task<IWakeStore> CreateAsync() => Task.FromResult<IWakeStore>(new SqliteWakeStore(_database.Path));

    protected override IMessageLog Log => new SqliteMessageStore(_database.Path);

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class SqlServerWakeStoreTests : WakeStoreContract
{
    private SqlServerContractDatabase? _database;

    protected override async Task<IWakeStore> CreateAsync() =>
        new SqlServerWakeStore((_database = await SqlServerContractDatabase.CreateAsync(Ct)).ConnectionString);

    protected override IMessageLog Log => new SqlServerMessageStore(_database!.ConnectionString, new SqlServerAppendRows());

    public override async ValueTask DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }
}
