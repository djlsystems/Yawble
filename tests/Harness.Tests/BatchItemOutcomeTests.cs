using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;

namespace Harness.Tests;

/// <summary>
/// A BATCHED RUN NEVER COMPLETES AN INSTRUCTION IT DID NOT DO. A run that carried several
/// items says, on its terminal rows, what became of each - answered, blocked or deferred - and a
/// deferred item is not closed: it is delivered again as its own next run, in the same workflow,
/// under its own causation, saying which run it was deferred from.
/// </summary>
public sealed class BatchItemOutcomeTests
{
    private static readonly ContainerId Manager = new("Alpha", "Manager");
    private static readonly ContainerId Dev = new("Alpha", "DeveloperTobias");
    private static readonly string[] ManagerTypes = [MessageTypes.Completed, MessageTypes.Failed, MessageTypes.Handback];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A busy member is sent "do X", then a duplicate of X, then
    /// "do Y after X". The batch that carries the duplicate and Y ends with Y deferred and delivered
    /// as its own next run, not completed; the Manager is woken once per finished run.
    /// </summary>
    [Fact]
    public async Task A_deferred_item_is_delivered_again_as_its_own_next_run_and_the_manager_wakes_once_per_run()
    {
        await using var bed = new ContainerTestBed(pending: true);
        var reports = new MemberReports(bed.Host, bed.Store, new RunHeartbeat());
        await bed.AddAsync(Manager, ManagerTypes);
        await bed.AddAsync(Dev);

        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = new ConcurrentQueue<string>();
        var runs = 0;
        MemberReportOutcome? deferral = null;

        bed.Agent.Behaviour = async invocation =>
        {
            if (invocation.Container != Dev) return new AgentResult(0, "noted");

            prompts.Enqueue(invocation.Prompt);

            switch (Interlocked.Increment(ref runs))
            {
                case 1:
                    await firstRun.Task;
                    return new AgentResult(0, "X is done and handed back.");
                case 2:
                    deferral = await reports.DeferAsync(Dev, 2, "Waiting until X is accepted.", Ct);
                    return new AgentResult(0, "X was already handed back.");
                default:
                    await thirdRun.Task;
                    return new AgentResult(0, "Y is done.");
            }
        };

        var x = await bed.Store.AppendAsync(Instruction("do X"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 1));

        var duplicate = await bed.Store.AppendAsync(Instruction("do X", x.Seq), Ct);
        var y = await bed.Store.AppendAsync(Instruction("do Y after X", x.Seq), Ct);
        await bed.SettleAsync();
        firstRun.SetResult();

        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 3));

        // While Y's own run is going, its pending row names the run it was deferred from.
        var batchStarted = (await bed.OfTypeAsync(MessageTypes.Started)).Where(r => r.Source == Dev.ToString()).ElementAt(1);
        var pendingY = Assert.Single(await bed.Pending!.ForAsync(Dev, Ct), row => row.Seq == y.Seq);
        Assert.Equal(batchStarted.Seq, pendingY.DeferredFromRun);

        thirdRun.SetResult();
        Assert.True(await bed.PumpUntilAsync(async () => (await DevRowsAsync(bed)).Count == 3));
        await bed.SettleAsync();

        Assert.True(deferral!.Accepted);

        // The batch was told the rule, and the redelivered run was told where it came from.
        var said = prompts.ToArray();
        Assert.Contains(MessageText.BatchRule, said[1]);
        Assert.Contains($"You deferred this from run {batchStarted.Seq}", said[2]);
        Assert.Contains("do Y after X", said[2]);
        Assert.DoesNotContain("new messages", said[2]);

        var rows = await DevRowsAsync(bed);

        // The batch closed the duplicate, and said Y was deferred rather than completing it.
        var batchRow = Assert.Single(rows, r => r.CausationSeq == duplicate.Seq);
        using (var payload = JsonDocument.Parse(batchRow.Payload))
        {
            Assert.Equal(1, payload.RootElement.GetProperty(PayloadFields.Item).GetInt32());
            Assert.Equal(ItemOutcomes.Answered, payload.RootElement.GetProperty(PayloadFields.ItemOutcome).GetString());
            var items = payload.RootElement.GetProperty(PayloadFields.Items).EnumerateArray().ToList();
            Assert.Equal([ItemOutcomes.Answered, ItemOutcomes.Deferred], items.Select(i => i.GetProperty("outcome").GetString()));
            Assert.Equal(y.Seq, items[1].GetProperty("seq").GetInt64());
            Assert.Equal("Waiting until X is accepted.", items[1].GetProperty("reason").GetString());
        }

        // Y's own row: its own causation and correlation, and the run it was deferred from.
        var yRow = Assert.Single(rows, r => r.CausationSeq == y.Seq);
        Assert.Equal(x.CorrelationId, yRow.CorrelationId);
        using (var payload = JsonDocument.Parse(yRow.Payload))
        {
            Assert.Equal(batchStarted.Seq, payload.RootElement.GetProperty(PayloadFields.DeferredFromRun).GetInt64());
            Assert.False(payload.RootElement.TryGetProperty(PayloadFields.Items, out _));
        }

        var yStarted = (await bed.OfTypeAsync(MessageTypes.Started)).Where(r => r.Source == Dev.ToString()).ElementAt(2);
        Assert.Equal(y.Seq, yStarted.CausationSeq);
        Assert.Equal(x.CorrelationId, yStarted.CorrelationId);

        // One Manager wake per finished run: three runs, three rows handed to the Manager.
        Assert.Equal(3, HandedToManager(bed));
        Assert.Empty(await bed.Pending!.ForAsync(Dev, Ct));
    }

    /// <summary>
    /// A mid-card update batched into a run answering another instruction ends with
    /// its OWN outcome on its own terminal row, and the Manager is woken once for the run.
    /// </summary>
    [Fact]
    public async Task A_mid_card_update_batched_into_another_run_ends_with_its_own_outcome_row()
    {
        await using var bed = new ContainerTestBed();
        await bed.AddAsync(Manager, ManagerTypes);
        await bed.AddAsync(Dev);

        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        bed.Agent.Behaviour = async invocation =>
        {
            if (invocation.Container != Dev) return new AgentResult(0, "noted");
            if (Interlocked.Increment(ref runs) == 1) await firstRun.Task;
            return new AgentResult(0, "Merged and green.");
        };

        var root = await bed.Store.AppendAsync(Instruction("build the deep link"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 1));

        var answer = await bed.Store.AppendAsync(Instruction("answer the review of the deep link", root.Seq), Ct);
        var update = await bed.Store.AppendAsync(Instruction("merge the team branch at 88d207d and report the green SHA", root.Seq), Ct);
        await bed.SettleAsync();
        firstRun.SetResult();

        Assert.True(await bed.PumpUntilAsync(async () => (await DevRowsAsync(bed)).Count == 3));
        await bed.SettleAsync();

        var rows = await DevRowsAsync(bed);
        var answerRow = JsonDocument.Parse(Assert.Single(rows, r => r.CausationSeq == answer.Seq).Payload).RootElement;
        var updateRow = JsonDocument.Parse(Assert.Single(rows, r => r.CausationSeq == update.Seq).Payload).RootElement;

        // Each row names ITS item and that item's outcome - not the other item's `completed`.
        Assert.Equal(1, answerRow.GetProperty(PayloadFields.Item).GetInt32());
        Assert.Equal(2, updateRow.GetProperty(PayloadFields.Item).GetInt32());
        Assert.Equal(ItemOutcomes.Answered, updateRow.GetProperty(PayloadFields.ItemOutcome).GetString());
        Assert.Equal(
            [(answer.Seq, ItemOutcomes.Answered), (update.Seq, ItemOutcomes.Answered)],
            updateRow.GetProperty(PayloadFields.Items).EnumerateArray()
                .Select(i => (i.GetProperty("seq").GetInt64(), i.GetProperty("outcome").GetString())));

        // Billed once, and the Manager woken once per run: two runs, two rows handed to it - the
        // batch's second row is never delivered.
        Assert.Equal(answer.Seq, updateRow.GetProperty(PayloadFields.UsageCountedOn).GetInt64());
        Assert.Equal(2, HandedToManager(bed));
    }

    [Fact]
    public async Task The_terminal_rows_name_each_items_outcome_answered_blocked_and_deferred()
    {
        await using var bed = new ContainerTestBed();
        var reports = new MemberReports(bed.Host, bed.Store, new RunHeartbeat());
        await bed.AddAsync(Dev);

        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        bed.Agent.Behaviour = async _ =>
        {
            switch (Interlocked.Increment(ref runs))
            {
                case 1:
                    await firstRun.Task;
                    break;
                case 2:
                    Assert.True((await reports.BlockedAsync(Dev, "No key for it.", item: 2, ct: Ct)).Accepted);
                    Assert.True((await reports.DeferAsync(Dev, 3, "After the first is merged.", Ct)).Accepted);

                    // One item, one outcome.
                    Assert.False((await reports.DeferAsync(Dev, 2, "again", Ct)).Accepted);
                    Assert.False((await reports.BlockedAsync(Dev, "again", item: 3, ct: Ct)).Accepted);
                    break;
            }

            return new AgentResult(0, "done");
        };

        var root = await bed.Store.AppendAsync(Instruction("first"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 1));

        var one = await bed.Store.AppendAsync(Instruction("one", root.Seq), Ct);
        var two = await bed.Store.AppendAsync(Instruction("two", root.Seq), Ct);
        var three = await bed.Store.AppendAsync(Instruction("three", root.Seq), Ct);
        await bed.SettleAsync();
        firstRun.SetResult();

        Assert.True(await bed.PumpUntilAsync(async () => (await DevRowsAsync(bed)).Count == 3));

        var rows = await DevRowsAsync(bed);
        Assert.DoesNotContain(rows, r => r.CausationSeq == two.Seq);

        var row = JsonDocument.Parse(Assert.Single(rows, r => r.CausationSeq == one.Seq).Payload).RootElement;
        Assert.Equal(
            [(1, ItemOutcomes.Answered), (2, ItemOutcomes.Blocked), (3, ItemOutcomes.Deferred)],
            row.GetProperty(PayloadFields.Items).EnumerateArray()
                .Select(i => (i.GetProperty("item").GetInt32(), i.GetProperty("outcome").GetString())));

        // The deferred item's own run closed it afterwards.
        Assert.Single(rows, r => r.CausationSeq == three.Seq);
        Assert.Equal(3, bed.Agent.RunsFor(Dev));
    }

    [Fact]
    public async Task Deferring_the_only_item_of_a_run_is_refused_with_a_sentence()
    {
        await using var bed = new ContainerTestBed();
        var reports = new MemberReports(bed.Host, bed.Store, new RunHeartbeat());
        await bed.AddAsync(Dev);

        MemberReportOutcome? outcome = null;
        bed.Agent.Behaviour = async _ =>
        {
            outcome = await reports.DeferAsync(Dev, 1, "later", Ct);
            return new AgentResult(0, "done");
        };

        var only = await bed.Store.AppendAsync(Instruction("only"), Ct);
        Assert.True(await bed.PumpUntilAsync(async () => (await DevRowsAsync(bed)).Count == 1));
        await bed.SettleAsync();

        Assert.False(outcome!.Accepted);
        Assert.Equal(400, outcome.Status);
        Assert.Contains("only one item", outcome.Refusal);

        // Closed as it always was, and not delivered again.
        Assert.Equal(only.Seq, Assert.Single(await DevRowsAsync(bed)).CausationSeq);
        Assert.Equal(1, bed.Agent.RunsFor(Dev));
    }

    [Fact]
    public async Task Deferring_every_item_of_a_run_is_refused_for_the_last()
    {
        await using var bed = new ContainerTestBed();
        var reports = new MemberReports(bed.Host, bed.Store, new RunHeartbeat());
        await bed.AddAsync(Dev);

        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        MemberReportOutcome? second = null;

        bed.Agent.Behaviour = async _ =>
        {
            var run = Interlocked.Increment(ref runs);
            if (run == 1) await firstRun.Task;
            if (run == 2)
            {
                Assert.True((await reports.DeferAsync(Dev, 1, "later", Ct)).Accepted);
                second = await reports.DeferAsync(Dev, 2, "later too", Ct);
            }

            return new AgentResult(0, "done");
        };

        var root = await bed.Store.AppendAsync(Instruction("first"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 1));
        await bed.Store.AppendAsync(Instruction("a", root.Seq), Ct);
        await bed.Store.AppendAsync(Instruction("b", root.Seq), Ct);
        await bed.SettleAsync();
        firstRun.SetResult();

        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 3));

        Assert.False(second!.Accepted);
        Assert.Contains("Every other item of this run is already deferred", second.Refusal);
    }

    [Fact]
    public async Task A_run_that_fails_closes_a_deferred_item_as_failed_and_does_not_deliver_it_again()
    {
        await using var bed = new ContainerTestBed();
        var reports = new MemberReports(bed.Host, bed.Store, new RunHeartbeat());
        await bed.AddAsync(Dev);

        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        bed.Agent.Behaviour = async _ =>
        {
            var run = Interlocked.Increment(ref runs);
            if (run == 1) await firstRun.Task;
            if (run == 2)
            {
                Assert.True((await reports.DeferAsync(Dev, 2, "later", Ct)).Accepted);
                return new AgentResult(1, "it broke");
            }

            return new AgentResult(0, "done");
        };

        var root = await bed.Store.AppendAsync(Instruction("first"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 1));
        await bed.Store.AppendAsync(Instruction("a", root.Seq), Ct);
        var b = await bed.Store.AppendAsync(Instruction("b", root.Seq), Ct);
        await bed.SettleAsync();
        firstRun.SetResult();

        Assert.True(await bed.PumpUntilAsync(async () => (await bed.OfTypeAsync(MessageTypes.Failed)).Count == 2));
        await bed.SettleAsync();

        var failed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Failed), r => r.CausationSeq == b.Seq);
        Assert.Equal(ItemOutcomes.Failed, JsonDocument.Parse(failed.Payload).RootElement.GetProperty(PayloadFields.ItemOutcome).GetString());
        Assert.Equal(2, bed.Agent.RunsFor(Dev));
    }

    /// <summary>
    /// A deferred item waiting on its pending row when the Host starts is re-offered as the run it
    /// was promised: its own, with its note - not batched, not failed as interrupted.
    /// </summary>
    [Fact]
    public async Task A_restart_delivers_a_deferred_item_again_as_its_own_run()
    {
        await using var bed = new ContainerTestBed(pending: true);
        await bed.AddAsync(Dev);

        var root = await bed.Store.AppendAsync(Instruction("first"), Ct);
        var deferred = await bed.Store.AppendAsync(Instruction("do Y after X", root.Seq), Ct);
        await bed.Pending!.DeferAsync(Dev, deferred.Seq, fromRun: 4242, Ct);

        var report = await bed.Host.ResumePendingAsync(Ct);

        Assert.Equal(1, report.Reoffered);
        Assert.True(await bed.PumpUntilAsync(async () => (await DevRowsAsync(bed)).Count == 1));

        var invocation = Assert.Single(bed.Agent.Invocations);
        Assert.Contains("You deferred this from run 4242", invocation.Prompt);

        var row = Assert.Single(await DevRowsAsync(bed));
        Assert.Equal(deferred.Seq, row.CausationSeq);
        Assert.Equal(4242, JsonDocument.Parse(row.Payload).RootElement.GetProperty(PayloadFields.DeferredFromRun).GetInt64());
    }

    [Fact]
    public async Task A_deferred_pending_delivery_names_its_run_for_the_member_and_the_team()
    {
        await using var bed = new ContainerTestBed(pending: true);

        await bed.Pending!.AddAsync(Dev, 10, Ct);
        await bed.Pending.StartAsync(Dev, 10, Ct);
        await bed.Pending.AddAsync(Dev, 11, Ct);
        await bed.Pending.DeferAsync(Dev, 10, fromRun: 9, Ct);

        Assert.Equal(
            [new PendingDelivery(10, false, 9), new PendingDelivery(11, false)],
            await bed.Pending.ForAsync(Dev, Ct));
        Assert.Equal(
            [new TeamPendingDelivery(Dev.ToString(), 10, false, 9), new TeamPendingDelivery(Dev.ToString(), 11, false)],
            await bed.Pending.ForTeamAsync("Alpha", Ct));
    }

    [Fact]
    public void A_batch_prompt_says_every_item_is_the_agents_now_and_a_single_one_does_not()
    {
        Message Item(long seq, string text) => new(
            seq, MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }),
            "Alpha/Manager", 1, 1, 1, DateTimeOffset.UnixEpoch);

        var batch = MessageText.Of([Item(1, "one"), Item(2, "two")]);
        Assert.EndsWith(MessageText.BatchRule, batch);
        Assert.Contains("there is no later run for it", MessageText.BatchRule);
        Assert.Contains("defer", MessageText.BatchRule);
        Assert.Contains("block", MessageText.BatchRule);

        Assert.DoesNotContain(MessageText.BatchRule, MessageText.Of([Item(1, "one")]));
    }

    [Fact]
    public async Task The_blocked_tool_defers_an_item_through_the_blocked_route()
    {
        var handler = new BodyRecorder();
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = "the-members-key";
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(Dev.ToString(), PrincipalKind.Container, new HashSet<string>()), "test");

        var tools = new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context }, new MintingPrincipals(),
            new AgentCatalog([]), new OneClient(handler));

        await tools.Blocked("After X is accepted.", item: 2, defer: true, cancellationToken: Ct);

        var (uri, body) = Assert.Single(handler.Requests);
        Assert.EndsWith($"/api/teams/{Dev.Team}/containers/{Dev.Name}/blocked", uri);
        using var sent = JsonDocument.Parse(body);
        Assert.Equal(2, sent.RootElement.GetProperty("item").GetInt32());
        Assert.True(sent.RootElement.GetProperty("defer").GetBoolean());
        Assert.Equal("After X is accepted.", sent.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void The_runs_route_reads_each_items_outcome_off_the_terminal_row()
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [PayloadFields.Items] = new object[]
            {
                new { item = 1, seq = 5, outcome = "answered" },
                new { item = 2, seq = 6, outcome = "deferred", reason = "after X" },
            },
        });

        Assert.Equal(
            """[{"item":1,"seq":5,"outcome":"answered","reason":null},{"item":2,"seq":6,"outcome":"deferred","reason":"after X"}]""",
            JsonSerializer.Serialize(LiveViewEndpoints.Items(payload)));
        Assert.Null(LiveViewEndpoints.Items("""{"output":"done"}"""));
    }

    /// <summary>
    /// How many rows the Manager has been handed, over all its runs. Counted rather than its runs:
    /// two runs' rows landing in one pump pass are one Manager run, which is fewer wakes, not more.
    /// </summary>
    private static int HandedToManager(ContainerTestBed bed) =>
        bed.Agent.Invocations.Where(i => i.Container == Manager).Sum(i =>
            System.Text.RegularExpressions.Regex.Match(i.Prompt, @"^You have (\d+) new messages") is { Success: true } many
                ? int.Parse(many.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 1);

    private static async Task<List<Message>> DevRowsAsync(ContainerTestBed bed) =>
        (await bed.OfTypeAsync(MessageTypes.Completed)).Where(r => r.Source == Dev.ToString()).ToList();

    private static NewMessage Instruction(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }), "Alpha/Manager", causation);

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:8123") };
    }

    private sealed class BodyRecorder : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
