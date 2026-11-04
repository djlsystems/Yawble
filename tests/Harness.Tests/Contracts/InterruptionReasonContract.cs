using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// INTERRUPTION REASONS COME FROM THE CLOSED SET, as each provider's store writes them: every
/// <c>blocked</c>, <c>needsDecision</c>, <c>rejected</c>, <c>workflow.paused</c> and <c>scheduleSkipped</c>
/// row holds exactly one code from <see cref="InterruptionReasons"/>, a code outside it is never written,
/// and a run that ends interrupted carries its code onto <c>usage_ledger.reason_code</c>
/// (<c>outcome-015</c>), where a completed run's is NULL.
/// </summary>
public abstract class InterruptionReasonContract : ProviderContract
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    [Fact]
    public async Task Every_interruption_kind_is_stored_with_exactly_one_code_from_the_set()
    {
        var stores = await StoresAsync();

        var rows = new (string Type, string Payload, string Expected)[]
        {
            (MessageTypes.Blocked, """{"reason":"stuck"}""", InterruptionReasons.Failure),
            (MessageTypes.Blocked, """{"reason":"stuck","reasonCode":"auth"}""", InterruptionReasons.Auth),
            (MessageTypes.NeedsDecision, """{"question":"which?"}""", InterruptionReasons.DecisionRequired),
            (MessageTypes.Rejected, """{"reason":"queue is at its ceiling","reasonCode":"admission"}""", InterruptionReasons.Admission),
            (MessageTypes.WorkflowPaused, """{"reason":"over","limitSource":"team"}""", InterruptionReasons.WorkflowBudget),
            (MessageTypes.ScheduleSkipped, """{"reason":"team archived","reasonCode":"team_archived"}""", InterruptionReasons.TeamArchived),
            (MessageTypes.ScheduleSkipped, "", InterruptionReasons.Failure),
        };

        foreach (var (type, payload, expected) in rows)
        {
            var appended = await stores.Log.AppendAsync(new NewMessage(type, payload, Dev.ToString()), Ct);
            var stored = await PayloadAsync(stores, appended.Seq);

            Assert.Equal([expected], Codes(stored));
            Assert.Equal(stored, appended.Payload);
            Assert.Contains(expected, InterruptionReasons.All);
        }
    }

    [Fact]
    public async Task A_code_outside_the_set_a_second_code_and_a_code_that_is_not_text_are_never_written()
    {
        var stores = await StoresAsync();

        foreach (var payload in new[]
        {
            """{"reason":"stuck","reasonCode":"because"}""",
            """{"reason":"stuck","reasonCode":"Failure"}""",
            """{"reason":"stuck","reasonCode":"failure","reasonCode":"auth"}""",
            """{"reason":"stuck","reasonCode":7}""",
            """{"reason":"stuck","reasonCode":null}""",
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                stores.Log.AppendAsync(new NewMessage(MessageTypes.Blocked, payload, Dev.ToString()), Ct));
        }

        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public async Task A_row_that_names_no_code_gets_its_types_default_from_its_structured_fields_only()
    {
        var stores = await StoresAsync();

        // The free text names a token cap; the code still comes from the type and its fields.
        var blocked = await stores.Log.AppendAsync(
            new NewMessage(MessageTypes.Blocked, """{"reason":"daily token cap reached"}""", Dev.ToString()), Ct);
        var needsPerson = await stores.Log.AppendAsync(
            new NewMessage(MessageTypes.Blocked, """{"reason":"bind it","failureClass":"needs-person"}""", Dev.ToString()), Ct);

        Assert.Equal([InterruptionReasons.Failure], Codes(await PayloadAsync(stores, blocked.Seq)));
        Assert.Equal([InterruptionReasons.Auth], Codes(await PayloadAsync(stores, needsPerson.Seq)));

        // Every byte the publisher wrote is kept; the code is added in front.
        Assert.Equal(
            """{"reasonCode":"failure","reason":"daily token cap reached"}""",
            await PayloadAsync(stores, blocked.Seq));
    }

    [Fact]
    public async Task A_resume_and_a_failure_carry_no_code_field()
    {
        var stores = await StoresAsync();

        var resumed = await stores.Log.AppendAsync(new NewMessage(MessageTypes.WorkflowResumed, """{"team":"Alpha"}""", "user-1"), Ct);
        var failed = await stores.Log.AppendAsync(
            new NewMessage(MessageTypes.Failed, """{"failureClass":"needs-person"}""", Dev.ToString()), Ct);

        Assert.Empty(Codes(await PayloadAsync(stores, resumed.Seq)));
        Assert.Empty(Codes(await PayloadAsync(stores, failed.Seq)));
    }

    [Fact]
    public async Task A_runs_ledger_row_carries_its_reason_code_and_a_completed_runs_is_null()
    {
        var stores = await StoresAsync();
        await RosterAsync(stores);

        var failed = await RunAsync(stores, MessageTypes.Failed, """{"failureClass":"needs-person"}""");
        var crashed = await RunAsync(stores, MessageTypes.Failed, """{"failureClass":"crashed"}""");
        var unclassified = await RunAsync(stores, MessageTypes.Failed, """{"output":"boom"}""");
        var blocked = await RunAsync(stores, MessageTypes.Completed, "{}", (MessageTypes.Blocked, """{"reason":"stuck"}"""));
        var asked = await RunAsync(stores, MessageTypes.Completed, "{}", (MessageTypes.NeedsDecision, """{"question":"which?"}"""));
        var waiting = await RunAsync(
            stores, MessageTypes.Completed, "{}", (MessageTypes.Blocked, """{"reason":"bind it","failureClass":"needs-person"}"""));
        var completed = await RunAsync(stores, MessageTypes.Completed, "{}");
        var quiet = await RunAsync(stores, MessageTypes.Completed, """{"quiet":true}""");

        // An item's own blocked row closes only that item: the run itself completed.
        var itemOnly = await RunAsync(stores, MessageTypes.Completed, "{}", (MessageTypes.Blocked, """{"reason":"one","item":2}"""));

        Assert.Equal(["failed", InterruptionReasons.Auth], await LedgerAsync(stores, failed));
        Assert.Equal(["failed", InterruptionReasons.Failure], await LedgerAsync(stores, crashed));
        Assert.Equal(["failed", InterruptionReasons.Failure], await LedgerAsync(stores, unclassified));
        Assert.Equal(["blocked", InterruptionReasons.Failure], await LedgerAsync(stores, blocked));
        Assert.Equal(["blocked", InterruptionReasons.DecisionRequired], await LedgerAsync(stores, asked));
        Assert.Equal(["blocked", InterruptionReasons.Auth], await LedgerAsync(stores, waiting));
        Assert.Equal(["completed", null], await LedgerAsync(stores, completed));
        Assert.Equal(["quiet", null], await LedgerAsync(stores, quiet));
        Assert.Equal(["completed", null], await LedgerAsync(stores, itemOnly));

        // The copy outlives the log's purge.
        await stores.Log.DeleteAsync([.. (await stores.RowsAsync("SELECT seq FROM messages")).Select(row => Convert.ToInt64(row[0]))], Ct);
        Assert.Equal(["failed", InterruptionReasons.Auth], await LedgerAsync(stores, failed));
    }

    [Fact]
    public async Task A_blocked_row_from_before_codes_existed_is_read_as_its_types_default_on_the_ledger()
    {
        var stores = await StoresAsync();
        await RosterAsync(stores);

        var root = await stores.Log.AppendAsync(Instruction(), Ct);
        var started = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", Dev.ToString(), root.Seq), Ct);
        await stores.InsertRawMessageAsync(MessageTypes.NeedsDecision, """{"question":"old"}""", Dev.ToString(), started.Seq);
        var terminal = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", Dev.ToString(), root.Seq), Ct);

        Assert.Equal(["blocked", InterruptionReasons.DecisionRequired], await LedgerAsync(stores, terminal.Seq));
    }

    // ---- fixtures ----

    private static async Task RosterAsync(ContractStores stores)
    {
        await stores.ExecuteAsync(
            "INSERT INTO teams (id, name, created_utc) VALUES ('Alpha', 'Alpha Squad', '2026-10-01T00:00:00.0000000+00:00')");
        await stores.ExecuteAsync(
            "INSERT INTO team_members (team, name, agent, subscribes, permits, floor_seq, created_utc) "
            + "VALUES ('Alpha', 'Dev', 'claude', '[]', '[]', 0, '2026-10-01T00:00:00.0000000+00:00')");
    }

    /// <summary>One run: an instruction, its start, optionally one row the run published, and its terminal row.</summary>
    private static async Task<long> RunAsync(
        ContractStores stores, string terminalType, string terminalPayload, (string Type, string Payload)? during = null)
    {
        var root = await stores.Log.AppendAsync(Instruction(), Ct);
        var started = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", Dev.ToString(), root.Seq), Ct);
        if (during is { } row)
        {
            await stores.Log.AppendAsync(new NewMessage(row.Type, row.Payload, Dev.ToString(), started.Seq), Ct);
        }

        var terminal = await stores.Log.AppendAsync(new NewMessage(terminalType, terminalPayload, Dev.ToString(), root.Seq), Ct);
        return terminal.Seq;
    }

    private static NewMessage Instruction() =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "go" }), "user-1");

    private static async Task<object?[]> LedgerAsync(ContractStores stores, long runSeq) =>
        Assert.Single(await stores.RowsAsync($"SELECT run_outcome, reason_code FROM usage_ledger WHERE run_seq = {runSeq}"));

    private static async Task<string> PayloadAsync(ContractStores stores, long seq) =>
        (string)Assert.Single(Assert.Single(await stores.RowsAsync($"SELECT payload FROM messages WHERE seq = {seq}")))!;

    /// <summary>Every <c>reasonCode</c> property the payload holds, duplicates included.</summary>
    private static string?[] Codes(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return [.. document.RootElement.EnumerateObject()
            .Where(property => property.NameEquals(PayloadFields.ReasonCode))
            .Select(property => property.Value.GetString())];
    }
}

public sealed class SqliteInterruptionReasonTests : InterruptionReasonContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerInterruptionReasonTests : InterruptionReasonContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
