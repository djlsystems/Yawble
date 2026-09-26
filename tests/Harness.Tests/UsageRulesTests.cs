using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// NOTHING IS ESTIMATED. UNKNOWN STAYS UNKNOWN. A run whose agent reports no usage publishes no
/// token figures - null, not zeros - and is counted as a run without usage, never priced.
/// </summary>
public sealed class UnknownUsageTests
{
    [Fact]
    public async Task A_run_that_reports_no_usage_publishes_no_token_figures_and_is_counted_as_unmeasured()
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;
        var dev = new ContainerId("Alpha", "Dev");
        await bed.AddAsync(dev);
        bed.Agent.Behaviour = _ => Task.FromResult(new AgentResult(0, "a long answer with no usage report"));

        var root = await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(dev), """{"instruction":"go"}""", "console"), ct);
        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 1));

        using var payload = JsonDocument.Parse(Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed)).Payload);
        Assert.True(Unknown(payload.RootElement, "tokensIn"));
        Assert.True(Unknown(payload.RootElement, "tokensOut"));
        Assert.True(Unknown(payload.RootElement, "tokensTotal"));

        var spend = await bed.Store.GetWorkflowSpendAsync(root.CorrelationId, ct);
        Assert.Equal(0, spend.TokensSpent);
        Assert.Equal(0, spend.RunsWithMeasuredUsage);
        Assert.Equal(1, spend.RunsWithoutUsage);
    }

    // Null or absent: either reads as NULL to the spend SQL. A number - zero included - is an estimate.
    private static bool Unknown(JsonElement payload, string field) =>
        !payload.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null;

    [Fact]
    public void A_combined_total_leaves_the_split_unknown()
    {
        var usage = InvocationUsage.Combined(900, "codex");

        Assert.Null(usage.TokensIn);
        Assert.Null(usage.TokensOut);
    }
}

/// <summary>
/// A WORKFLOW BUDGET READS SPEND SINCE THE LAST NUDGE, AND THE WAKE USES THAT SAME FIGURE. A nudge
/// is an instruction caused by the correlation itself; it is how a person clears the counter. The
/// pump refuses a wake over the bound and allows one after a nudge.
/// </summary>
public sealed class WorkflowBudgetAtTheWakeTests
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    [Fact]
    public async Task Spend_since_the_nudge_counts_only_runs_after_it()
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;

        var root = await bed.Store.AppendAsync(Instruction("first"), ct);
        var before = await bed.Store.AppendAsync(Completed(100, 40, root.Seq), ct);
        var nudge = await bed.Store.AppendAsync(Instruction("carry on", root.CorrelationId), ct);
        await bed.Store.AppendAsync(Completed(5, 5, nudge.Seq), ct);

        Assert.Equal(root.CorrelationId, before.CorrelationId);
        Assert.Equal(150, (await bed.Store.GetWorkflowSpendAsync(root.CorrelationId, ct)).TokensSpent);
        Assert.Equal(10, (await bed.Store.GetSpendSinceNudgeAsync(root.CorrelationId, ct)).TokensSpent);
    }

    [Fact]
    public async Task A_wake_over_the_bound_is_refused_and_a_nudge_lets_the_next_one_run()
    {
        await using var bed = new ContainerTestBed(workflowSpendLimit: 100);
        var ct = TestContext.Current.CancellationToken;
        await bed.AddAsync(Dev);
        bed.Agent.Behaviour = _ => Task.FromResult(
            new AgentResult(0, "done", Usage: new InvocationUsage(100, 40, "claude")));

        var root = await bed.Store.AppendAsync(Instruction("first"), ct);
        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 1));
        var completed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));

        // Inside the workflow, caused by the run: 140 spent against a bound of 100.
        await bed.Store.AppendAsync(Instruction("again", completed.Seq), ct);
        await bed.SettleAsync();
        Assert.Equal(1, bed.Agent.RunsFor(Dev));

        // A nudge: caused by the correlation itself. Spend since it is zero, so it runs.
        await bed.Store.AppendAsync(Instruction("carry on", root.CorrelationId), ct);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(Dev) == 2));
    }

    private static NewMessage Instruction(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }), "console", causation);

    private static NewMessage Completed(int tokensIn, int tokensOut, long causation) =>
        new(MessageTypes.Completed, JsonSerializer.Serialize(new { output = "x", tokensIn, tokensOut }), Dev.ToString(), causation);
}
