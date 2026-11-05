using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A MEMBER'S REMOVAL IS NOT A WORKFLOW, on the real Host. Removing a member appends one
/// <c>agentContainer.removed</c> row with no causation, so it sits alone in a correlation of its own
/// that nothing ever declares; read as a workflow it is open forever, and the team's tile reads NO
/// RESULT over work that completed. The team's workflow reads (<c>GET /api/teams/{team}/workflow</c>,
/// <c>/workflows</c> and the rollup) leave it out by <c>NotAWorkflowSql</c> - for a removal made
/// now and for a row already on the log - while the row itself stays as written and still unassigns
/// the member's cards.
/// </summary>
public sealed class MemberRemovalWorkflowTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string Dev = "Dev";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-removal-workflow-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";
    private readonly FakeAgent _agent = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMessageLog Log => _factory.Services.GetRequiredService<IMessageLog>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agent)));

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        _team = (await registry.CreateAsync("Removals", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        await registry.AddContainerAsync(_team, Dev, "claude-headless", "", [], permits: new HashSet<string>(Permits.All), ct: Ct);

        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_member_removed_after_the_teams_one_workflow_completed_is_no_workflow()
    {
        var workflow = await CompletedWorkflowAsync();

        var removed = await _person.DeleteAsync($"/api/teams/{_team}/containers/{Dev}", Ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        // THE ROW IS STILL WRITTEN AS IT WAS: its type, an empty payload, the member as its source, and
        // no causation - the root of a correlation of its own.
        var row = Assert.Single(await Log.ReadAfterAsync(0, [MessageTypes.ContainerRemoved], 10, Ct));
        Assert.Equal("{}", row.Payload);
        Assert.Equal(new ContainerId(_team, Dev).ToString(), row.Source);
        Assert.Null(row.CausationSeq);
        Assert.Equal(row.Seq, row.CorrelationId);

        await AssertOnlyAsync(workflow);
    }

    [Fact]
    public async Task A_removal_row_already_on_the_log_is_no_workflow()
    {
        // WRITTEN AS `MemberDeletion` WRITES IT TODAY, before any read rule existed: a member long gone.
        var old = await Log.AppendAsync(new NewMessage(MessageTypes.ContainerRemoved, "{}", new ContainerId(_team, "Gone").ToString()), Ct);
        Assert.Equal(old.Seq, old.CorrelationId);

        var workflow = await CompletedWorkflowAsync();

        // AND ONE MORE AFTER THE WORK, so it is the team's most recently active correlation too.
        var later = await Log.AppendAsync(new NewMessage(MessageTypes.ContainerRemoved, "{}", new ContainerId(_team, "Left").ToString()), Ct);
        Assert.Equal(later.Seq, later.CorrelationId);

        await AssertOnlyAsync(workflow);
    }

    [Fact]
    public async Task The_removal_row_still_unassigns_the_removed_members_cards()
    {
        await CompletedWorkflowAsync();

        Assert.Contains(await CardsAsync(), card => card.GetProperty("member").GetString() == Dev);

        (await _person.DeleteAsync($"/api/teams/{_team}/containers/{Dev}", Ct)).EnsureSuccessStatusCode();

        var cards = await CardsAsync();
        Assert.NotEmpty(cards);
        Assert.DoesNotContain(cards, card => card.GetProperty("member").GetString() == Dev);
    }

    /// <summary>
    /// The team runs one workflow as a team does: a person tells the Manager, the Manager's run puts
    /// Dev on it, Dev runs, and Dev's completion wakes the Manager, whose run declares it through the
    /// real <c>workflow-complete</c> route. Answers the workflow's correlation once that declaring run
    /// has ended, so nothing the platform does at a run's end can land after the declaration.
    /// </summary>
    /// <remarks>
    /// DECLARED FROM INSIDE THE MANAGER'S RUN, AS PRODUCTION DOES. A declaration appended from outside
    /// while the Manager's re-wake run was still in flight let that run's end offer the workflow
    /// another turn (<c>IdleWorkflowOffer</c>), an instruction after the declaration that reopens it.
    ///
    /// BY A RUN THAT WAS HANDED DEV'S COMPLETION, WITH NOTHING STILL COMING TO THE MANAGER. Reading
    /// Dev's completion on the log is not the same as the Manager having been handed it: a run woken
    /// by something earlier can see the row while its delivery is still queued, and
    /// `workflow-complete` refuses that ("Manager has 1 pending delivery in this workflow"). So a run
    /// declares only when it was handed Dev's completion and no unstarted delivery of the workflow
    /// is queued for the Manager; otherwise it ends, and the queued run declares.
    ///
    /// HANDED MEANS IN THIS RUN'S BATCH, NOT ONLY ITS WAKING ROW. The pump runs every queued row of
    /// one workflow as one batch: one <c>agentContainer.started</c>, caused by the FIRST row, which is
    /// all <c>HARNESS_CAUSATION</c> names, and one terminal row per row handed. An idle offer queued
    /// just before Dev's completion makes the batch [offer, completion]: woken by the offer, handed
    /// both. Asking the causation alone made that run decline and wait for a run that never came
    /// (the thread then ends started, completed, completed). Every row of the batch is marked Started
    /// before the agent runs and removed only after its terminal row, so the run asks its own
    /// started deliveries (<see cref="HandedAsync"/>).
    ///
    /// AND ONCE DEV'S RUN HAS FINISHED ENDING. Dev appends its completion before it drops its own
    /// pending row and leaves Running, so a quick Manager run can be handed that row while
    /// `workflow-complete` still refuses it ("Dev is Running; Dev has 1 pending delivery"), and the
    /// same busy state refuses the idle offer at that run's end - nothing would wake the Manager again.
    /// The run waits for that tail, a condition (<see cref="DevSettledAsync"/>), before declaring.
    ///
    /// NOT BY THE MANAGER'S CURSOR. The pump moves a cursor only after its whole batch, so the run
    /// handed Dev's completion can start and end with the cursor still below it. Asking the cursor
    /// made that run decline, left the declaration to a re-wake only <c>IdleWorkflowOffer</c> might
    /// give, and timed out when it gave none.
    ///
    /// THE WAIT FOLLOWS THE CONDITION, NOT A CLOCK: it ends when a run declares, and fails at once,
    /// naming every Manager run and what it did, when no declaration has come and nothing more is
    /// coming to the Manager (<see cref="NothingComingToAsync"/>) - no run, nothing queued, nothing
    /// on the thread its cursor has still to pass. The outer bound only catches a hang.
    /// </remarks>
    private async Task<long> CompletedWorkflowAsync()
    {
        var root = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var declared = new TaskCompletionSource<(HttpStatusCode, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var manager = new ContainerId(_team, TeamRegistry.DefaultManagerName);
        var dev = new ContainerId(_team, Dev);

        _agent.Behaviour = async invocation =>
        {
            if (invocation.Container == manager)
            {
                var correlation = await root.Task;
                var cause = long.Parse(invocation.Environment["HARNESS_CAUSATION"]);
                var thread = await Log.ReadCorrelationAsync(correlation, Ct);

                if (thread.FirstOrDefault(m => m.Type == MessageTypes.Completed && m.Source == dev.ToString()) is { } done)
                {
                    if (cause < done.Seq && !await HandedAsync(manager, done.Seq))
                    {
                        runs.Enqueue($"run woken by {cause}: saw Dev's completion {done.Seq} before being handed it; left it to that run");
                    }
                    else if (await QueuedForAsync(manager, correlation) is { } queued)
                    {
                        runs.Enqueue($"run woken by {cause}: delivery {queued} still queued for the Manager; left it to that run");
                    }
                    else if (!await DevSettledAsync(dev, correlation))
                    {
                        runs.Enqueue($"run woken by {cause}: Dev's run never finished ending");
                    }
                    else
                    {
                        var (status, body) = await DeclareAsync(invocation, "Widget built.");
                        runs.Enqueue($"run woken by {cause}: declared, {(int)status} {body}");
                        declared.TrySetResult((status, body));
                    }
                }
                else if (!thread.Any(m => m.Type == MessageTypes.InstructionFor(dev)))
                {
                    await Log.AppendAsync(new NewMessage(
                        MessageTypes.InstructionFor(dev),
                        """{"instruction":"Build the widget","subject":"Build the widget"}""",
                        manager.ToString(),
                        cause), Ct);
                    runs.Enqueue($"run woken by {cause}: told Dev");
                }
                else
                {
                    runs.Enqueue($"run woken by {cause}: Dev has not completed yet");
                }
            }

            return new AgentResult(0, "done");
        };

        var told = await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{TeamRegistry.DefaultManagerName}/tell",
            new { instruction = "Build the widget", subject = "Build the widget" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        var workflow = JsonDocument.Parse(await told.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("correlationId").GetInt64();
        root.SetResult(workflow);

        // Until a run declares, or until nothing more can come to the Manager to declare with: then
        // say why, at once. Quiet is asked to hold for a second, so a row in flight between two
        // stores (appended, not yet offered) is not read as an end.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var quietSince = (TimeSpan?)null;
        while (!declared.Task.IsCompleted)
        {
            if (await NothingComingToAsync(manager, workflow))
            {
                quietSince ??= watch.Elapsed;
                if (watch.Elapsed - quietSince > TimeSpan.FromSeconds(1))
                {
                    Assert.Fail($"No Manager run declared and nothing more is coming to the Manager - {await StoryAsync(workflow, runs)}");
                }
            }
            else
            {
                quietSince = null;
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(90)) Assert.Fail($"Still not declared after 90 s - {await StoryAsync(workflow, runs)}");
            await Task.WhenAny(declared.Task, Task.Delay(50, Ct));
        }

        var (status, body) = await declared.Task;
        Assert.True(status == HttpStatusCode.NoContent, $"{status}: {body} - {string.Join("; ", runs)}");

        // THE DECLARING RUN HAS ENDED: its own `agentContainer.completed` follows the declaration.
        for (var tries = 0; ; tries++)
        {
            var thread = await Log.ReadCorrelationAsync(workflow, Ct);
            var declaration = thread.Single(m => m.Type == MessageTypes.WorkflowCompleted);
            if (thread.Any(m => m.Type == MessageTypes.Completed && m.Source == manager.ToString() && m.Seq > declaration.Seq)) break;
            Assert.True(tries < 200, $"The Manager's declaring run never ended in workflow {workflow}.");
            await Task.Delay(50, Ct);
        }

        return workflow;
    }

    /// <summary>
    /// Whether <paramref name="dev"/>'s run has finished ending under <paramref name="workflow"/>:
    /// not Running in it and no pending delivery of it left. Waited for from inside the Manager's
    /// run; false only if that never happens, which the wait outside then reports.
    /// </summary>
    private async Task<bool> DevSettledAsync(ContainerId dev, long workflow)
    {
        var host = _factory.Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var pending = _factory.Services.GetRequiredService<IPendingDeliveries>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            var running = host.Snapshots().Any(s => string.Equals(s.Team, dev.Team, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Id, dev.Name, StringComparison.OrdinalIgnoreCase)
                && s.State == ContainerState.Running && s.CurrentCorrelation == workflow);
            var queued = false;
            foreach (var row in await pending.ForTeamAsync(_team, Ct))
            {
                if (string.Equals(row.Subscriber, dev.ToString(), StringComparison.OrdinalIgnoreCase)
                    && (await Log.FindAsync(row.Seq, Ct))?.CorrelationId == workflow)
                {
                    queued = true;
                }
            }

            if (!running && !queued) return true;
            await Task.Delay(20, Ct);
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="seq"/> is one of the deliveries handed to the run of
    /// <paramref name="member"/> that is asking: its pending row is Started. The pump marks every row
    /// of a batch Started before the agent runs and removes each only after that row's terminal
    /// row, so inside the run this is exactly its batch.
    /// </summary>
    private async Task<bool> HandedAsync(ContainerId member, long seq) =>
        (await _factory.Services.GetRequiredService<IPendingDeliveries>().ForTeamAsync(_team, Ct)).Any(row =>
            row.Started && row.Seq == seq && string.Equals(row.Subscriber, member.ToString(), StringComparison.OrdinalIgnoreCase));

    private async Task<string> StoryAsync(long workflow, IEnumerable<string> runs) =>
        $"workflow {workflow}: {string.Join("; ", runs)}. Thread: "
        + string.Join(", ", (await Log.ReadCorrelationAsync(workflow, Ct))
            .Select(m => $"{m.Seq} {m.Type} from {m.Source}{(m.CausationSeq is { } c ? $" caused by {c}" : "")}"));

    /// <summary>
    /// The seq of an unstarted delivery of <paramref name="workflow"/> queued for
    /// <paramref name="member"/>, or null when there is none. Its own started row - the run asking -
    /// is not waiting.
    /// </summary>
    private async Task<long?> QueuedForAsync(ContainerId member, long workflow)
    {
        foreach (var row in await _factory.Services.GetRequiredService<IPendingDeliveries>().ForTeamAsync(_team, Ct))
        {
            if (row.Started || !string.Equals(row.Subscriber, member.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            if ((await Log.FindAsync(row.Seq, Ct))?.CorrelationId == workflow) return row.Seq;
        }

        return null;
    }

    /// <summary>
    /// Whether nothing more of <paramref name="workflow"/> can reach <paramref name="member"/>: it is
    /// not running, no delivery of any kind is pending for it, and no row of the thread it subscribes
    /// to, from someone else, lies beyond its cursor (the pump's own delivery test).
    /// </summary>
    private async Task<bool> NothingComingToAsync(ContainerId member, long workflow)
    {
        var host = _factory.Services.GetRequiredService<Harness.Containers.ContainerHost>();
        if (host.Snapshots().Any(s => string.Equals(s.Team, member.Team, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Id, member.Name, StringComparison.OrdinalIgnoreCase)
                && (s.State == ContainerState.Running || s.QueueDepth > 0)))
        {
            return false;
        }

        if ((await _factory.Services.GetRequiredService<IPendingDeliveries>().ForTeamAsync(_team, Ct))
            .Any(row => string.Equals(row.Subscriber, member.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var types = new HashSet<string>(await _factory.Services.GetRequiredService<ISubscriptions>().ForAsync(member, Ct), StringComparer.Ordinal);
        var position = await _factory.Services.GetRequiredService<ICursors>().PositionAsync(member, Ct);
        return !(await Log.ReadCorrelationAsync(workflow, Ct)).Any(m =>
            m.Seq > position && types.Contains(m.Type) && !string.Equals(m.Source, member.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(HttpStatusCode, string)> DeclareAsync(AgentInvocation invocation, string delivered)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, invocation.Environment["HARNESS_KEY"]);
        var response = await client.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{invocation.Container.Name}/workflow-complete", new { delivered }, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The team's workflow reads answer <paramref name="workflow"/>, Completed, and nothing open.</summary>
    private async Task AssertOnlyAsync(long workflow)
    {
        var listed = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_team}/workflows", Ct)).RootElement;
        var only = Assert.Single(listed.GetProperty("workflows").EnumerateArray());
        Assert.Equal(workflow, only.GetProperty("correlation").GetInt64());
        Assert.Equal("Completed", only.GetProperty("state").GetString());
        Assert.Equal(0, listed.GetProperty("openCount").GetInt32());
        Assert.Equal(1, listed.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("earliestStartedAt").ValueKind);

        var single = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_team}/workflow", Ct)).RootElement;
        Assert.Equal(workflow, single.GetProperty("correlation").GetInt64());
        Assert.Equal("Completed", single.GetProperty("state").GetString());

        var rolled = JsonDocument.Parse(await _person.GetStringAsync("/api/teams/rollup", Ct)).RootElement
            .GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == _team);
        Assert.Equal(workflow, rolled.GetProperty("workflow").GetProperty("correlation").GetInt64());
        Assert.Equal("Completed", rolled.GetProperty("workflow").GetProperty("state").GetString());
        Assert.Equal(0, rolled.GetProperty("openWorkflows").GetProperty("openCount").GetInt32());
        Assert.Equal(workflow, Assert.Single(rolled.GetProperty("openWorkflows").GetProperty("workflows").EnumerateArray())
            .GetProperty("correlation").GetInt64());
    }

    private async Task<IReadOnlyList<JsonElement>> CardsAsync() =>
        JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_team}/kanban/board", Ct)).RootElement
            .GetProperty("cards").EnumerateArray().Select(card => card.Clone()).ToList();
}
