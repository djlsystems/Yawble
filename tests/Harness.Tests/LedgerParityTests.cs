using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Harness.Tests;

/// <summary>
/// THE SPEND READS MOVED ONTO THE LEDGER AND CHARGE WHAT THEY ALWAYS CHARGED. One fixture - measured
/// runs with cache reads and writes, a combined total, an unmeasured run, an excluded estimate, a
/// plugin's run, a batched run's extra row, a nudge, and a trigger's day with a run it started, that
/// run's hand-back and the Manager runs both woke - read by the log's queries as they were
/// (<see cref="LogSpend"/>) and by the ledger's, which must give the same figures for all four
/// reads: workflow spend, spend since the nudge, the trigger's day and a member's recent cost.
/// </summary>
public sealed class LedgerParityTests : IDisposable
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");
    private static readonly ContainerId Echo = new("Alpha", "Echo");
    private static readonly ContainerId Manager = new("Alpha", "Manager");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-parity-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_directory, "messages.db");

    public LedgerParityTests()
    {
        Directory.CreateDirectory(_directory);
        new SchemaMigrator(Database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_log_queries_and_the_ledger_queries_agree_on_one_fixture()
    {
        var store = new SqliteMessageStore(Database);
        var ledger = new SqliteUsageLedger(Database);
        var old = new LogSpend(Database);

        // ---- one workflow: every kind of run, and a nudge part way ----
        var root = await store.AppendAsync(Instruction(Dev, "build it", "console"), Ct);
        await RunAsync(store, Dev, root.Seq, new { tokensIn = 1000, tokensOut = 200, tokensCachedIn = 5000, tokensCacheCreation = 400, tokensSource = "claude" });

        var failedDelivery = await store.AppendAsync(Instruction(Dev, "again", "console", root.Seq), Ct);
        await RunAsync(store, Dev, failedDelivery.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null }, MessageTypes.Failed);

        var codexDelivery = await store.AppendAsync(Instruction(Dev, "codex", "console", root.Seq), Ct);
        var batchedDelivery = await store.AppendAsync(Instruction(Dev, "batched", "console", root.Seq), Ct);
        await RunAsync(store, Dev, codexDelivery.Seq, new { tokensTotal = 777, tokensSource = "codex" });
        await store.AppendAsync(new NewMessage(MessageTypes.Completed,
            JsonSerializer.Serialize(new { tokensIn = (int?)null, tokensOut = (int?)null, usageCountedOn = codexDelivery.Seq }),
            Dev.ToString(), batchedDelivery.Seq), Ct);

        var estimateDelivery = await store.AppendAsync(Instruction(Dev, "estimate", "console", root.Seq), Ct);
        await RunAsync(store, Dev, estimateDelivery.Seq, new { tokensIn = 90, tokensOut = 10, tokensSource = UsageSource.ExcludedEstimate });

        var echoDelivery = await store.AppendAsync(Instruction(Echo, "echo", "console", root.Seq), Ct);
        await RunAsync(store, Echo, echoDelivery.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null, tokensSource = UsageSource.NoModel });

        // A nudge: an instruction whose causation is the correlation itself.
        var nudge = await store.AppendAsync(Instruction(Manager, "carry on", "person-1", root.Seq), Ct);
        await RunAsync(store, Manager, nudge.Seq, new { tokensIn = 300, tokensOut = 100, tokensCachedIn = 999, tokensSource = "claude" });
        var afterNudge = await store.AppendAsync(Instruction(Dev, "after", "console", root.Seq), Ct);
        await RunAsync(store, Dev, afterNudge.Seq, new { tokensIn = 20, tokensOut = 5, tokensCacheCreation = 7, tokensSource = "claude" });

        // ---- a trigger's day: a fire before the window, then two inside it ----
        var early = await store.AppendAsync(Instruction(Dev, "poll", "schedule:t1"), Ct);
        await RunAsync(store, Dev, early.Seq, new { tokensIn = 5000, tokensOut = 5000, tokensSource = "claude" });

        await Task.Delay(30, Ct);
        var since = DateTimeOffset.UtcNow;
        await Task.Delay(30, Ct);

        var fire = await store.AppendAsync(Instruction(Dev, "poll", "schedule:t1"), Ct);
        var handback = await store.AppendAsync(new NewMessage(MessageTypes.Handback, "{}", Dev.ToString(), fire.Seq), Ct);
        var devRun = await RunAsync(store, Dev, fire.Seq, new { tokensIn = 1500, tokensOut = 500, tokensSource = "claude" });

        // The Manager runs the fire's run woke: one on its hand-back, one on its completed row.
        await RunAsync(store, Manager, handback.Seq, new { tokensIn = 50, tokensOut = 5, tokensSource = "claude" });
        await RunAsync(store, Manager, devRun.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null });

        var eventFire = await store.AppendAsync(Instruction(Echo, "changed", "trigger:t1"), Ct);
        await RunAsync(store, Echo, eventFire.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null, tokensSource = UsageSource.NoModel });

        // Another trigger's run, which neither read may count.
        var other = await store.AppendAsync(Instruction(Dev, "other", "schedule:t2"), Ct);
        await RunAsync(store, Dev, other.Seq, new { tokensIn = 9, tokensOut = 9, tokensSource = "claude" });

        // ---- the four reads ----
        var workflowOld = await old.GetWorkflowSpendAsync(root.CorrelationId, Ct);
        var workflowNew = await store.GetWorkflowSpendAsync(root.CorrelationId, Ct);
        Assert.Equal(workflowOld, workflowNew);
        Assert.True(workflowNew.TokensSpent > 0 && workflowNew.RunsWithMeasuredUsage > 0 && workflowNew.RunsWithoutUsage > 0);

        var nudgeOld = await old.GetSpendSinceNudgeAsync(root.CorrelationId, Ct);
        var nudgeNew = await store.GetSpendSinceNudgeAsync(root.CorrelationId, Ct);
        Assert.Equal(nudgeOld, nudgeNew);
        Assert.NotEqual(workflowNew, nudgeNew);

        var sources = TriggerCost.SourcesOf("t1");
        var triggerOld = await old.GetTriggerSpendAsync(sources, since, Ct);
        var triggerNew = await store.GetTriggerSpendAsync(sources, since, Ct);
        Assert.Equal(triggerOld, triggerNew);

        // The fire's run, the Manager run its hand-back woke and the event arm's plugin run are
        // measured; the Manager run its completed row woke is not. The early fire is outside.
        Assert.Equal(new WorkflowSpend(2000 + 55, 3, 1), triggerNew);

        var cost = new TriggerCost(store, ledger, null!, Options.Create(new JsonOptions()));
        foreach (var member in new[] { Dev, Echo, Manager })
        {
            Assert.Equal(await OldRecentAsync(old, member), await cost.RecentAsync(member, "agent", Ct));
        }
    }

    /// <summary>`TriggerCost.RecentAsync` as it read the log before the ledger.</summary>
    private static async Task<MemberRecentCost> OldRecentAsync(LogSpend old, ContainerId member)
    {
        var runs = await old.ReadRecentRunsAsync(member.ToString(), TriggerCost.RecentRuns, Ct);
        var measured = runs.Select(run => TriggerCost.BillableOf(run.Payload)).OfType<long>().Order().ToArray();

        long? median = measured.Length == 0
            ? null
            : measured.Length % 2 == 1
                ? measured[measured.Length / 2]
                : (measured[(measured.Length / 2) - 1] + measured[measured.Length / 2]) / 2;

        return new MemberRecentCost(runs.Count, measured.Length, runs.Count - measured.Length, median, "agent");
    }

    private static NewMessage Instruction(ContainerId to, string text, string source, long? causation = null) =>
        new(MessageTypes.InstructionFor(to), JsonSerializer.Serialize(new { instruction = text }), source, causation);

    private static async Task<Message> RunAsync(
        SqliteMessageStore store, ContainerId member, long delivery, object usage, string type = MessageTypes.Completed)
    {
        await store.AppendAsync(new NewMessage(MessageTypes.Started, "{}", member.ToString(), delivery), Ct);
        return await store.AppendAsync(new NewMessage(type, JsonSerializer.Serialize(usage), member.ToString(), delivery), Ct);
    }
}
