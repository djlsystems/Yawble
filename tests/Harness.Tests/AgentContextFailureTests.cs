using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A CONTEXT FAILURE ENDS THE RUN ONCE. An agent member's ledger history is built
/// INSIDE the runner call now, so a context builder that throws, or one a person's Stop cancels,
/// ends that run the ordinary way: exactly one failed row after its `started`, and the member takes
/// its next instruction. Before, the build ran outside the try - a throw left `started` with no
/// terminal row, and a Stop ended the consumer loop for good.
/// </summary>
public sealed class AgentContextFailureTests
{
    private static readonly ContainerId Dev = new("alpha", "dev");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fails its first build as <paramref name="first"/> says, then answers empty history.</summary>
    private sealed class FirstBuildFails(Func<CancellationToken, Task<string>> first) : IContextBuilder
    {
        private int _calls;

        public Task<string> BuildAsync(ContainerId container, Message waking, ArtifactLimits limits, long sinceSeq, CancellationToken ct = default) =>
            Interlocked.Increment(ref _calls) == 1 ? first(ct) : Task.FromResult("");
    }

    private static async Task<IReadOnlyList<Message>> RowsAsync(ContainerTestBed bed) =>
        await bed.Store.ReadAfterAsync(0, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct);

    private static async Task<IReadOnlyList<Message>> TerminalAsync(ContainerTestBed bed) =>
        (await RowsAsync(bed)).Where(m => m.Type != MessageTypes.Started).ToList();

    private static async Task<MemberRuntime> HireAsync(ContainerTestBed bed, IContextBuilder context) =>
        await bed.Host.AddAsync(ContainerTestBed.Definition(Dev), new AgentMemberRunner(bed.Agent, context), Ct);

    private static Task TellAsync(ContainerTestBed bed, string instruction) =>
        bed.Store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Dev), $$"""{"instruction":"{{instruction}}"}""", "console"), Ct);

    /// <summary>The second instruction is run to completion by the agent, and the first left one
    /// failed row, with `started` rows and terminal rows paired one to one.</summary>
    private static async Task AssertRecoveredAsync(ContainerTestBed bed, string failedReason)
    {
        await TellAsync(bed, "second");
        Assert.True(await bed.PumpUntilAsync(async () => (await TerminalAsync(bed)).Count >= 2, attempts: 200));
        await bed.SettleAsync();

        var terminal = await TerminalAsync(bed);
        Assert.Equal([MessageTypes.Failed, MessageTypes.Completed], terminal.Select(m => m.Type));
        Assert.Contains(failedReason, terminal[0].Payload, StringComparison.Ordinal);
        Assert.Equal(2, (await RowsAsync(bed)).Count(m => m.Type == MessageTypes.Started));

        var ran = Assert.Single(bed.Agent.Invocations);
        Assert.Contains("second", ran.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_context_builder_that_throws_fails_that_run_once_and_the_member_goes_on()
    {
        await using var bed = new ContainerTestBed();
        var member = await HireAsync(bed, new FirstBuildFails(_ => throw new InvalidOperationException("the ledger is unreadable")));

        await TellAsync(bed, "first");
        Assert.True(await bed.PumpUntilAsync(async () => (await TerminalAsync(bed)).Count == 1));
        Assert.Equal("the ledger is unreadable", member.Snapshot().Failed);

        await AssertRecoveredAsync(bed, "the ledger is unreadable");
        Assert.Equal(ContainerState.Idle, member.State);
    }

    [Fact]
    public async Task A_context_build_cancelled_by_Stop_fails_that_run_once_and_the_member_goes_on()
    {
        await using var bed = new ContainerTestBed();
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var member = await HireAsync(bed, new FirstBuildFails(async ct =>
        {
            building.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        }));

        await TellAsync(bed, "first");
        Assert.True(await bed.PumpUntilAsync(() => building.Task.IsCompleted));
        Assert.True(member.Stop());

        Assert.True(await bed.PumpUntilAsync(async () => (await TerminalAsync(bed)).Count == 1));
        Assert.Equal(FailureClasses.Interrupted, member.Snapshot().FailureClass);

        await AssertRecoveredAsync(bed, "This run was stopped from the board.");
        Assert.Equal(ContainerState.Idle, member.State);
    }
}
