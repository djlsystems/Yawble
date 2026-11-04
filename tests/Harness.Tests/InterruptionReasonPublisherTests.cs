using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// EACH INTERRUPTION ROW CARRIES THE CODE OF WHAT CAUSED IT, chosen by the platform where it knows the
/// cause: a member's needs_decision is <c>decision_required</c>, its own blocked call the documented
/// default <c>failure</c>, a Host block for a missing connection <c>auth</c>, a full queue
/// <c>admission</c>, and a workflow over its budget <c>workflow_budget</c> on both its rejection and its
/// pause. The free text is never read for one. The trigger skips are in <see cref="TriggerRunNowTests"/>
/// and <c>TeamArchiveTests</c>.
/// </summary>
public sealed class InterruptionReasonPublisherTests
{
    private static readonly ContainerId Dev = new("alpha", "dev");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_members_decision_its_own_block_and_a_hosts_needs_person_block_each_carry_their_code_and_the_run_ends_with_the_newest()
    {
        await using var bed = new ContainerTestBed();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        bed.Agent.Behaviour = async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return new AgentResult(0, "done");
        };

        await bed.AddAsync(Dev);
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"one"}""", "console"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => started.Task.IsCompleted));

        var reports = new MemberReports(bed.Host, bed.Store, new RunHeartbeat());

        // The words name a token cap and a decision; the codes come from which call was made.
        await reports.NeedsDecisionAsync(Dev, "the token cap is near - carry on?", Ct);
        await reports.BlockedAsync(Dev, "I need a decision about the budget", ct: Ct);
        await reports.NeedsPersonAsync(Dev, "Bind the mailbox connection.", Ct);

        Assert.Equal(
            [InterruptionReasons.DecisionRequired],
            [.. (await bed.OfTypeAsync(MessageTypes.NeedsDecision)).Select(Code)]);
        Assert.Equal(
            [InterruptionReasons.Failure, InterruptionReasons.Auth],
            [.. (await bed.OfTypeAsync(MessageTypes.Blocked)).OrderBy(row => row.Seq).Select(Code)]);

        release.SetResult();
        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Completed)).Count > 0));

        var terminal = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));
        Assert.Equal(["blocked", InterruptionReasons.Auth], await LedgerAsync(bed, terminal.Seq));
    }

    [Fact]
    public async Task A_full_queue_rejects_the_work_with_admission()
    {
        await using var bed = new ContainerTestBed();
        await using var member = new MemberRuntime(
            ContainerTestBed.Definition(Dev) with { Ceiling = 0 }, bed.Runner, bed.Store);

        var instruction = await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console"), Ct);

        Assert.False(await member.OfferAsync(instruction, Ct));

        var rejected = Assert.Single(await bed.OfTypeAsync(MessageTypes.Rejected));
        Assert.Equal(InterruptionReasons.Admission, Code(rejected));
        Assert.Equal("queue is at its ceiling", Field(rejected, PayloadFields.Reason));
    }

    [Fact]
    public async Task A_workflow_over_its_budget_is_rejected_and_paused_with_workflow_budget()
    {
        await using var bed = new ContainerTestBed(workflowSpendLimit: 10);
        await bed.AddAsync(Dev);

        // The workflow has already spent 150 measured tokens against a bound of 10.
        var root = await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console"), Ct);
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.Completed, """{"tokensIn":100,"tokensOut":50,"tokensSource":"claude"}""", "alpha/other", root.Seq), Ct);

        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.WorkflowPaused)).Count > 0));

        var rejected = Assert.Single(await bed.OfTypeAsync(MessageTypes.Rejected));
        var paused = Assert.Single(await bed.OfTypeAsync(MessageTypes.WorkflowPaused));
        Assert.Equal(InterruptionReasons.WorkflowBudget, Code(rejected));
        Assert.Equal(InterruptionReasons.WorkflowBudget, Code(paused));
        Assert.Equal(0, bed.Agent.RunsFor(Dev));
    }

    private static string? Code(Message row) => Field(row, PayloadFields.ReasonCode);

    private static string? Field(Message row, string name) =>
        JsonDocument.Parse(row.Payload).RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static async Task<object?[]> LedgerAsync(ContainerTestBed bed, long runSeq)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = bed.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT run_outcome, reason_code FROM usage_ledger WHERE run_seq = {runSeq}";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return [reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)];
    }
}
