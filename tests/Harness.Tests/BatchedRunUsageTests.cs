using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// ONE RUN IS BILLED ONCE, HOWEVER MANY DELIVERIES IT ANSWERED. A run that takes several messages
/// in one batch closes each of them with its own terminal row. The spend SQL sums rows, so only
/// one of those rows may carry the run's token figures; otherwise the run is billed once per
/// delivery.
/// </summary>
public sealed class BatchedRunUsageTests
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    [Fact]
    public async Task A_run_that_answers_two_deliveries_is_billed_once()
    {
        await using var bed = new ContainerTestBed();
        var ct = TestContext.Current.CancellationToken;
        await bed.AddAsync(Dev);

        var firstRunRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        bed.Agent.Behaviour = async _ =>
        {
            if (Interlocked.Increment(ref runs) == 1) await firstRunRelease.Task;
            return new AgentResult(0, "done", Usage: new InvocationUsage(100, 40, "claude"));
        };

        var root = await bed.Store.AppendAsync(Instruction("first"), ct);
        Assert.True(await bed.PumpUntilAsync(() => Task.FromResult(Volatile.Read(ref runs) == 1)));

        // Two deliveries in the same workflow arrive while the first run is still going; the next
        // run takes both as one batch.
        await bed.Store.AppendAsync(Instruction("second", root.Seq), ct);
        await bed.Store.AppendAsync(Instruction("third", root.Seq), ct);
        await bed.SettleAsync();
        firstRunRelease.SetResult();

        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count == 3));
        Assert.Equal(2, bed.Agent.RunsFor(Dev));

        // Each delivery is still closed by its own row; only one of the batch's rows carries figures.
        var carrying = (await bed.OfTypeAsync(MessageTypes.Completed))
            .Count(row => JsonDocument.Parse(row.Payload).RootElement.TryGetProperty("tokensIn", out var v)
                && v.ValueKind == JsonValueKind.Number);
        Assert.Equal(2, carrying);

        var spend = await bed.Store.GetWorkflowSpendAsync(root.CorrelationId, ct);
        Assert.Equal(2 * 140, spend.TokensSpent);
        Assert.Equal(2, spend.RunsWithMeasuredUsage);
        Assert.Equal(0, spend.RunsWithoutUsage);
    }

    private static NewMessage Instruction(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }), "console", causation);
}
