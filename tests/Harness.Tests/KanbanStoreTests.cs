using System.Reflection;
using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Tests;

/// <summary>
/// The Kanban board is ONE projection shared by every reader: appended incrementally from the log,
/// read under a lock, scoped by each team's floor, and never served stale after a failure or a purge.
/// </summary>
public sealed class KanbanStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ContainerId Alpha = new("alpha", "Dev");
    private static readonly ContainerId Beta = new("beta", "Dev");

    private static Task<Message> InstructionAsync(ContainerTestBed bed, ContainerId member, string text) =>
        bed.Store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(member), $$"""{"instruction":"{{text}}"}""", "console"), Ct);

    [Fact]
    public async Task A_second_read_with_nothing_new_reads_the_log_once()
    {
        await using var bed = new ContainerTestBed();
        await InstructionAsync(bed, Alpha, "one");
        var log = LogProxy.Over(bed.Store);
        var store = new KanbanStore(log);

        var first = await store.GetBoardAsync(ct: Ct);
        var second = await store.GetBoardAsync(ct: Ct);

        Assert.Single(first.Cards);
        Assert.Same(first.Cards[0], second.Cards[0]);
        Assert.Equal(1, LogProxy.Of(log).RangeReads);
        Assert.Equal(2, LogProxy.Of(log).HighestReads);
    }

    [Fact]
    public async Task Rows_appended_since_the_last_read_are_projected_without_a_rebuild()
    {
        await using var bed = new ContainerTestBed();
        await InstructionAsync(bed, Alpha, "one");
        var log = LogProxy.Over(bed.Store);
        var store = new KanbanStore(log);

        Assert.Single((await store.GetBoardAsync(ct: Ct)).Cards);
        await InstructionAsync(bed, Alpha, "two");

        var board = await store.GetBoardAsync(ct: Ct);

        Assert.Equal(2, board.Cards.Count);
        Assert.Equal(2, LogProxy.Of(log).RangeReads);
        Assert.Equal(0, LogProxy.Of(log).RangeReadsFromZeroAfterTheFirst);
    }

    [Fact]
    public async Task Two_concurrent_reads_with_different_team_filters_each_get_their_own_team()
    {
        await using var bed = new ContainerTestBed();
        for (var i = 0; i < 20; i++)
        {
            await InstructionAsync(bed, Alpha, $"a{i}");
            await InstructionAsync(bed, Beta, $"b{i}");
        }
        var store = new KanbanStore(bed.Store);

        var boards = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => store.GetBoardAsync(new KanbanFilter(Team: i % 2 == 0 ? "alpha" : "beta"), Ct)));

        for (var i = 0; i < boards.Length; i++)
        {
            var team = i % 2 == 0 ? "alpha" : "beta";
            Assert.Equal(20, boards[i].Cards.Count);
            Assert.All(boards[i].Cards, card => Assert.Equal(team, card.Team));
        }
    }

    [Fact]
    public async Task A_read_that_fails_throws_and_the_next_read_recovers()
    {
        await using var bed = new ContainerTestBed();
        await InstructionAsync(bed, Alpha, "one");
        var log = LogProxy.Over(bed.Store);
        var store = new KanbanStore(log);
        LogProxy.Of(log).FailNextRangeRead = true;

        await Assert.ThrowsAsync<IOException>(() => store.GetBoardAsync(ct: Ct));

        Assert.Single((await store.GetBoardAsync(ct: Ct)).Cards);
    }

    [Fact]
    public async Task A_row_a_teams_floor_excludes_is_not_on_its_board()
    {
        await using var bed = new ContainerTestBed();
        var early = await InstructionAsync(bed, Alpha, "before the floor");
        await InstructionAsync(bed, Alpha, "after the floor");
        await InstructionAsync(bed, Beta, "beta keeps everything");
        var store = new KanbanStore(bed.Store, team => team == "alpha" ? early.Seq : 0);

        var board = await store.GetBoardAsync(ct: Ct);

        Assert.Equal(2, board.Cards.Count);
        Assert.DoesNotContain(board.Cards, card => card.WorkflowSeq == early.Seq);
        Assert.Single((await store.GetBoardAsync(new KanbanFilter(Team: "alpha"), Ct)).Cards);
    }

    [Fact]
    public async Task Invalidate_drops_a_purged_row_from_the_board()
    {
        await using var bed = new ContainerTestBed();
        var gone = await InstructionAsync(bed, Alpha, "to be purged");
        await InstructionAsync(bed, Alpha, "kept");
        var store = new KanbanStore(bed.Store);
        Assert.Equal(2, (await store.GetBoardAsync(ct: Ct)).Cards.Count);

        await bed.Store.DeleteAsync([gone.Seq], Ct);
        Assert.Equal(2, (await store.GetBoardAsync(ct: Ct)).Cards.Count);
        store.Invalidate();

        Assert.Single((await store.GetBoardAsync(ct: Ct)).Cards);
    }

    /// <summary>The real store behind a counting, optionally failing, proxy of its interface.</summary>
    public class LogProxy : DispatchProxy
    {
        private IMessageLog _inner = null!;

        public int RangeReads { get; private set; }
        public int HighestReads { get; private set; }
        public int RangeReadsFromZeroAfterTheFirst { get; private set; }
        public bool FailNextRangeRead { get; set; }

        public static IMessageLog Over(IMessageLog inner)
        {
            var proxy = Create<IMessageLog, LogProxy>();
            ((LogProxy)(object)proxy)._inner = inner;
            return proxy;
        }

        public static LogProxy Of(IMessageLog proxy) => (LogProxy)(object)proxy;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IMessageLog.ReadRangeAsync))
            {
                if (FailNextRangeRead)
                {
                    FailNextRangeRead = false;
                    throw new IOException("the database is away");
                }
                if (RangeReads > 0 && (long)args![0]! == 0) RangeReadsFromZeroAfterTheFirst++;
                RangeReads++;
            }
            else if (targetMethod.Name == nameof(IMessageLog.HighestSeqAsync))
            {
                HighestReads++;
            }

            return targetMethod.Invoke(_inner, args);
        }
    }
}
