using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// THE SPEND SQL AGREES WITH <see cref="InvocationUsage.BillableTokens"/>.
///
/// Two implementations of one weighting - C# for a run, SQL for a workflow - and nothing but this
/// keeps them together. Each shape goes through a real run, so the payload keys are the ones
/// <c>AgentContainer</c> actually writes rather than ones a test spelled, and the figures are chosen
/// so a changed weight or a dropped term moves the answer.
/// </summary>
public sealed class SpendSqlTests
{
    [Fact]
    public Task Cache_reads_are_weighted_as_BillableTokens_weights_them() =>
        AgreesAsync(new InvocationUsage(1_000, 200, "claude", cachedIn: 12_345));

    [Fact]
    public Task Cache_writes_are_weighted_as_BillableTokens_weights_them() =>
        AgreesAsync(new InvocationUsage(1_000, 200, "claude", cacheCreation: 1_003));

    [Fact]
    public Task A_combined_total_is_counted_as_BillableTokens_counts_it() =>
        AgreesAsync(InvocationUsage.Combined(4_321, "codex"));

    private static async Task AgreesAsync(InvocationUsage usage)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var bed = new ContainerTestBed();

        var worker = new ContainerId("Alpha", "DeveloperRowan");
        await bed.AddAsync(worker);
        bed.Agent.Behaviour = _ => Task.FromResult(new AgentResult(0, "done", Usage: usage));

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(worker), """{"instruction":"build it"}""", "console"), ct);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 1));

        var completed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));
        var spend = await bed.Store.GetWorkflowSpendAsync(completed.CorrelationId, ct);

        Assert.Equal(1, spend.RunsWithMeasuredUsage);
        Assert.Equal(usage.BillableTokens(), spend.TokensSpent);
    }
}
