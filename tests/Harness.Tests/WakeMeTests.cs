using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// `wake_me`: A MEMBER ASKS TO BE WOKEN ONCE, LATER, IN THE WORKFLOW IT IS WORKING. On the real Host,
/// with a fake agent for every agent member and the wakes' clock injected (<see cref="WakeClock"/>),
/// so a test moves a wake's time and nothing else's.
/// </summary>
public sealed class WakeMeTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string OfferText = "This workflow is open and nobody is working it";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-wake-me-{Guid.NewGuid():N}");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 16, 20, 0, 0, TimeSpan.Zero));
    private FakeAgent _agents = new();
    private bool _wokenDeclares = true;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private IWakeStore Wakes => Services.GetRequiredService<IWakeStore>();

    private ContainerHost Host => Services.GetRequiredService<ContainerHost>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Dev2 => new(_team, "Dev2");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = Start();

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Waking", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = await PersonAsync();

        foreach (var name in new[] { "Dev", "Dev2" })
        {
            var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name, agent = "claude-headless" }, Ct);
            Assert.Equal(HttpStatusCode.OK, hired.StatusCode);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // --- the wake, end to end ---------------------------------------------------------------------

    [Fact]
    public async Task A_wake_asked_in_a_run_is_a_row_and_at_its_time_one_instruction_in_the_same_workflow_runs_the_member_once()
    {
        var asked = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agents.Behaviour = async invocation =>
        {
            if (await CommonAsync(invocation) is { } common) return common;

            if (invocation.Container == Dev && invocation.Prompt.Contains("check the record", StringComparison.Ordinal))
            {
                var (status, body) = await AskAsync(invocation, "2m", "look at the DNS record for example.test again");
                Assert.Equal(HttpStatusCode.OK, status);
                asked.TrySetResult(body);
            }

            return new AgentResult(0, "done");
        };

        var root = await PersonTellsAsync(Dev, "check the record");
        var answer = await asked.Task.WaitAsync(Patience, Ct);
        await SettleAsync();

        // THE ASK: its row, in the workflow, caused by the row the run was handling; the answer names
        // the wake, its time in UTC and on the Host's clock, and how to cancel it.
        var id = answer.GetProperty("wake").GetInt64();
        Assert.Equal(root.CorrelationId, answer.GetProperty("workflow").GetInt64());
        Assert.Equal(_time.Now.AddMinutes(2), answer.GetProperty("dueAt").GetDateTimeOffset());
        Assert.Contains("UTC", answer.GetProperty("dueLocal").GetString());
        Assert.Contains($"cancel {id}", answer.GetProperty("notice").GetString());

        var askedRow = Assert.Single(await RowsAsync(root.CorrelationId, MessageTypes.WakeAsked));
        Assert.Equal(Dev.ToString(), askedRow.Source);
        Assert.Equal(root.Seq, askedRow.CausationSeq);
        Assert.Equal(id, Field(askedRow, PayloadFields.Wake).GetInt64());
        Assert.Equal("look at the DNS record for example.test again", Field(askedRow, PayloadFields.Note).GetString());

        // PENDING: stored, on the member's snapshot, on the team's list, its card and its workflow row.
        var pending = Assert.Single(await Wakes.ForTeamAsync(_team, Ct));
        Assert.Equal((id, askedRow.Seq, "Dev"), (pending.Id, pending.AskedSeq, pending.Member));
        Assert.Equal(id, Assert.Single(Host.Find(Dev)!.Snapshot().Wakes!).Id);
        Assert.Equal(id, (await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/wakes", Ct))
            .GetProperty("wakes")[0].GetProperty("id").GetInt64());
        var board = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/kanban/board", Ct);
        Assert.Contains(board.GetProperty("cards").EnumerateArray(), card =>
            card.TryGetProperty("wakes", out var wakes) && wakes[0].GetProperty("id").GetInt64() == id);
        var workflows = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/workflows", Ct);
        Assert.Contains(workflows.GetProperty("workflows").EnumerateArray(), row =>
            row.TryGetProperty("wakes", out var wakes) && wakes[0].GetProperty("note").GetString() == pending.Note);

        // NOTHING BEFORE ITS TIME.
        await MoveClockAsync(TimeSpan.FromSeconds(119));
        await SettleAsync();
        Assert.Equal(1, _agents.RunsFor(Dev));
        Assert.Empty(await RowsAsync(root.CorrelationId, MessageTypes.WakeFired));

        // AT ITS TIME: one instruction to the member, source `wake:<id>`, caused by the asking row,
        // in the same workflow, carrying the note and when it was asked; and the member runs once.
        await MoveClockAsync(TimeSpan.FromSeconds(1));
        await UntilAsync(() => _agents.RunsFor(Dev) == 2, "Dev woken");
        await SettleAsync();

        var fire = Assert.Single(await RowsAsync(root.CorrelationId, MessageTypes.InstructionFor(Dev)), m => m.Source == MessageTypes.WakeSource(id));
        Assert.Equal(askedRow.Seq, fire.CausationSeq);
        Assert.Equal(root.CorrelationId, fire.CorrelationId);
        Assert.Equal("look at the DNS record for example.test again", Field(fire, PayloadFields.Note).GetString());
        Assert.Equal(pending.AskedAt, Field(fire, PayloadFields.AskedAt).GetDateTimeOffset());
        Assert.False(JsonDocument.Parse(fire.Payload).RootElement.TryGetProperty(PayloadFields.LateSeconds, out _));
        Assert.Contains("look at the DNS record for example.test again", _agents.Invocations.Last(i => i.Container == Dev).Prompt);

        var fired = Assert.Single(await RowsAsync(root.CorrelationId, MessageTypes.WakeFired));
        Assert.Equal((Dev.ToString(), askedRow.Seq), (fired.Source, fired.CausationSeq));

        // ONCE: gone from the store and the card, and nothing more however long the clock runs on.
        Assert.Empty(await Wakes.AllAsync(Ct));
        Assert.Null(Host.Find(Dev)!.Snapshot().Wakes);
        await MoveClockAsync(TimeSpan.FromHours(3));
        await SettleAsync();
        Assert.Equal(2, _agents.RunsFor(Dev));
        Assert.Single(await RowsAsync(root.CorrelationId, MessageTypes.WakeFired));
    }

    // --- the refusals -------------------------------------------------------------------------------

    [Fact]
    public async Task A_wake_is_refused_for_another_member_past_the_maximum_delay_past_three_pending_or_a_second_in_one_workflow_each_with_its_sentence()
    {
        var answers = new ConcurrentDictionary<string, (HttpStatusCode Status, string Error)>();

        _agents.Behaviour = async invocation =>
        {
            if (await CommonAsync(invocation) is { } common) return common;
            if (invocation.Container != Dev) return new AgentResult(0, "done");

            var prompt = invocation.Prompt;
            if (prompt.Contains("job-first", StringComparison.Ordinal))
            {
                answers["first"] = Error(await AskAsync(invocation, "10m", "the first thing"));
                answers["second in one workflow"] = Error(await AskAsync(invocation, "5m", "the same thing again"));
                answers["past the maximum"] = Error(await AskAsync(invocation, "25h", "tomorrow"));
                answers["too soon"] = Error(await AskAsync(invocation, "10s", "in a moment"));
                answers["not a delay"] = Error(await AskAsync(invocation, "soon", "whenever"));
                answers["another member"] = Error(await PostAsync(invocation, $"/api/teams/{_team}/containers/Dev2/wake", new { @in = "10m", note = "for Dev2" }));
            }
            else if (prompt.Contains("job-second", StringComparison.Ordinal))
            {
                answers["second"] = Error(await AskAsync(invocation, "20m", "the second thing"));
            }
            else if (prompt.Contains("job-third", StringComparison.Ordinal))
            {
                answers["third"] = Error(await AskAsync(invocation, "30m", "the third thing"));
            }
            else if (prompt.Contains("job-fourth", StringComparison.Ordinal))
            {
                answers["fourth"] = Error(await AskAsync(invocation, "40m", "the fourth thing"));
            }

            return new AgentResult(0, "done");
        };

        foreach (var which in new[] { "first", "second", "third", "fourth" })
        {
            await PersonTellsAsync(Dev, $"the job-{which}");
            await UntilAsync(() => answers.ContainsKey(which), $"the {which} ask");
        }

        await SettleAsync();

        foreach (var which in new[] { "first", "second", "third" }) Assert.Equal(HttpStatusCode.OK, answers[which].Status);
        var held = await Wakes.ForTeamAsync(_team, Ct);
        Assert.Equal(3, held.Count);
        var first = held.Single(w => w.Note == "the first thing");

        var (status, error) = answers["second in one workflow"];
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("already have a wake pending in this workflow", error);
        Assert.Contains($"wake {first.Id}", error);
        Assert.Contains($"cancel {first.Id}", error);

        (status, error) = answers["past the maximum"];
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("at most 24 hours", error);
        Assert.Contains("wake.maxDelay", error);
        Assert.Contains("trigger", error);

        (status, error) = answers["too soon"];
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("at least 30 seconds", error);

        (status, error) = answers["not a delay"];
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("90s, 10m, 2h", error);

        (status, error) = answers["another member"];
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("addressed only to the member asking", error);

        (status, error) = answers["fourth"];
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("3 pending wakes", error);
        foreach (var wake in held) Assert.Contains($"wake {wake.Id}", error);
        Assert.Contains("wake_me and cancel", error);

        // Only the three that were set are rows; Dev2 has none, and a person is refused the member's route.
        Assert.Equal(3, (await Log.ReadAfterAsync(0, [MessageTypes.WakeAsked], int.MaxValue, Ct)).Count);
        Assert.DoesNotContain(held, w => w.Member == "Dev2");
        var personAsks = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/Dev/wake", new { @in = "10m", note = "for Dev" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, personAsks.StatusCode);
    }

    [Fact]
    public async Task The_maximum_delay_is_the_tenant_setting_read_at_every_ask()
    {
        var answers = new ConcurrentQueue<(HttpStatusCode, string)>();
        _agents.Behaviour = async invocation =>
        {
            if (await CommonAsync(invocation) is { } common) return common;
            if (invocation.Container == Dev) answers.Enqueue(Error(await AskAsync(invocation, "3h", "later")));
            return new AgentResult(0, "done");
        };

        var put = await _person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { ["wake.maxDelay"] = "02:00:00" }, Ct);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(Ct));

        await PersonTellsAsync(Dev, "one");
        await UntilAsync(() => answers.Count == 1, "the first ask");
        var (status, error) = answers.Single();
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("at most 2 hours", error);

        put = await _person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { ["wake.maxDelay"] = "04:00:00" }, Ct);
        Assert.True(put.IsSuccessStatusCode);

        await PersonTellsAsync(Dev, "two");
        await UntilAsync(() => answers.Count == 2, "the second ask");
        Assert.Equal(HttpStatusCode.OK, answers.Last().Item1);
    }

    // --- paused, archived, deleted ----------------------------------------------------------------

    [Fact]
    public async Task A_paused_teams_wake_is_delivered_once_at_resume()
    {
        var root = await DevAsksAsync("1m", "is the certificate issued yet");

        Assert.Equal(HttpStatusCode.NoContent, (await _person.PostAsync($"/api/teams/{_team}/pause", null, Ct)).StatusCode);
        await MoveClockAsync(TimeSpan.FromMinutes(1));

        // FIRED, AND HELD: its instruction is on the log, and the paused team runs nothing.
        await UntilAsync(() => RowsAsync(root, MessageTypes.WakeFired).Result.Count == 1, "the wake fired");
        await Task.Delay(500, Ct);
        Assert.Equal(1, _agents.RunsFor(Dev));

        Assert.Equal(HttpStatusCode.NoContent, (await _person.PostAsync($"/api/teams/{_team}/resume", null, Ct)).StatusCode);
        await UntilAsync(() => _agents.RunsFor(Dev) == 2, "Dev woken at Resume");
        await SettleAsync();

        Assert.Equal(2, _agents.RunsFor(Dev));
        Assert.Single(await RowsAsync(root, MessageTypes.InstructionFor(Dev)), m => m.Source.StartsWith("wake:", StringComparison.Ordinal));
        Assert.Single(await RowsAsync(root, MessageTypes.WakeFired));
    }

    [Fact]
    public async Task An_archived_teams_wake_is_skipped_with_its_row()
    {
        var root = await DevAsksAsync("1m", "is the deployment settled");
        var wake = Assert.Single(await Wakes.AllAsync(Ct));

        var archived = await _person.PostAsync($"/api/teams/{_team}/archive", null, Ct);
        Assert.True(archived.IsSuccessStatusCode, await archived.Content.ReadAsStringAsync(Ct));

        await MoveClockAsync(TimeSpan.FromMinutes(1));
        await UntilAsync(() => RowsAsync(root, MessageTypes.WakeSkipped).Result.Count == 1, "the wake skipped");

        var skipped = Assert.Single(await RowsAsync(root, MessageTypes.WakeSkipped));
        Assert.Equal((Dev.ToString(), wake.AskedSeq, wake.Id), (skipped.Source, skipped.CausationSeq, Field(skipped, PayloadFields.Wake).GetInt64()));
        Assert.Contains("archived", Field(skipped, PayloadFields.Reason).GetString());
        Assert.Empty(await RowsAsync(root, MessageTypes.WakeFired));
        Assert.DoesNotContain(await RowsAsync(root, MessageTypes.InstructionFor(Dev)), m => m.Source.StartsWith("wake:", StringComparison.Ordinal));
        Assert.Empty(await Wakes.AllAsync(Ct));
        Assert.Equal(1, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task A_deleted_teams_wakes_go_with_it()
    {
        var root = await DevAsksAsync("1m", "has the rate limit passed");
        Assert.Single(await Wakes.AllAsync(Ct));

        var deleted = await _person.DeleteAsync($"/api/teams/{_team}", Ct);
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.Empty(await Wakes.AllAsync(Ct));

        await MoveClockAsync(TimeSpan.FromMinutes(5));
        await Task.Delay(1000, Ct);

        Assert.Empty(await RowsAsync(root, MessageTypes.WakeFired));
        Assert.Empty(await RowsAsync(root, MessageTypes.WakeSkipped));
        Assert.DoesNotContain(await Log.ReadCorrelationAsync(root, Ct), m => m.Source.StartsWith("wake:", StringComparison.Ordinal));
    }

    // --- a restart ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_restart_keeps_a_pending_wake_and_one_that_came_due_while_it_was_down_fires_once_at_start_saying_how_late()
    {
        var late = await DevAsksAsync("5m", "look at the build again");
        var kept = await MemberAsksAsync(Dev2, "2h", "look at the nightly run");
        var dueBefore = (await Wakes.AllAsync(Ct)).OrderBy(w => w.DueAt).ToList();

        // DOWN, AND THE FIRST WAKE COMES DUE WHILE IT IS: the Host starts again ten minutes after it.
        await _factory.DisposeAsync();
        _person.Dispose();
        _time.Now += TimeSpan.FromMinutes(15);
        _agents = new FakeAgent { Behaviour = DefaultAsync };
        _factory = Start();
        _person = await PersonAsync();

        await UntilAsync(() => _agents.RunsFor(Dev) == 1, "Dev woken at start");
        await SettleAsync();

        var fire = Assert.Single(await RowsAsync(late, MessageTypes.InstructionFor(Dev)), m => m.Source.StartsWith("wake:", StringComparison.Ordinal));
        Assert.Equal(600, Field(fire, PayloadFields.LateSeconds).GetInt64());
        Assert.Contains("10 minutes late", Field(fire, PayloadFields.Instruction).GetString());
        Assert.Equal(600, Field(Assert.Single(await RowsAsync(late, MessageTypes.WakeFired)), PayloadFields.LateSeconds).GetInt64());
        Assert.Equal(1, _agents.RunsFor(Dev));
        Assert.Equal(0, _agents.RunsFor(Dev2));

        // THE OTHER IS KEPT, as it was, and still on Dev2's card.
        var still = Assert.Single(await Wakes.AllAsync(Ct));
        Assert.Equal(dueBefore[1], still);
        Assert.Equal(kept, still.Workflow);
        await UntilAsync(() => Host.Find(Dev2)!.Snapshot().Wakes is { Count: 1 }, "Dev2's card showing its wake");

        // ONCE: another start fires nothing more.
        await _factory.DisposeAsync();
        _person.Dispose();
        _agents = new FakeAgent { Behaviour = DefaultAsync };
        _factory = Start();
        _person = await PersonAsync();
        await Task.Delay(1000, Ct);
        await SettleAsync();

        Assert.Equal(0, _agents.RunsFor(Dev));
        Assert.Single(await RowsAsync(late, MessageTypes.WakeFired));
    }

    // --- work in the workflow, and its end ----------------------------------------------------------

    [Fact]
    public async Task A_pending_wake_keeps_the_idle_offer_from_being_made_until_it_has_fired()
    {
        var root = await DevAsksAsync("1m", "is the domain resolving");
        await SettleAsync();

        // Dev owns the workflow, ended its run without declaring, and nothing is queued: only the
        // pending wake stands between it and the offer.
        Assert.DoesNotContain(await Log.ReadCorrelationAsync(root, Ct), IdleWorkflowOffer.IsOffer);
        Assert.Equal(1, _agents.RunsFor(Dev));

        // THE CONTROL: once the wake has fired and its run ends without declaring, the offer comes.
        _wokenDeclares = false;
        await MoveClockAsync(TimeSpan.FromMinutes(1));
        await UntilAsync(() => _agents.Invocations.Any(i => i.Container == Dev && i.Prompt.Contains(OfferText, StringComparison.Ordinal)), "the offer after the wake");
        await SettleAsync();

        Assert.Single(await Log.ReadCorrelationAsync(root, Ct), IdleWorkflowOffer.IsOffer);
    }

    [Fact]
    public async Task A_persons_close_cancels_the_workflows_wake_with_a_row()
    {
        var root = await DevAsksAsync("1m", "is the queue drained");
        var wake = Assert.Single(await Wakes.AllAsync(Ct));

        var closed = await _person.PostAsJsonAsync($"/api/teams/{_team}/workflows/{root}/close", new { reason = "not needed" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);

        var cancelled = Assert.Single(await RowsAsync(root, MessageTypes.WakeCancelled));
        Assert.Equal((Dev.ToString(), wake.AskedSeq), (cancelled.Source, cancelled.CausationSeq));
        Assert.Equal(Email, Field(cancelled, PayloadFields.By).GetString());
        Assert.Contains("closed", Field(cancelled, PayloadFields.Reason).GetString());
        Assert.Empty(await Wakes.AllAsync(Ct));
        Assert.True(cancelled.Seq < Assert.Single(await RowsAsync(root, MessageTypes.WorkflowClosed)).Seq);

        await MoveClockAsync(TimeSpan.FromMinutes(5));
        await Task.Delay(500, Ct);
        Assert.Empty(await RowsAsync(root, MessageTypes.WakeFired));
        Assert.Equal(1, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task The_owners_declaration_cancels_its_wake_with_a_row_and_says_so_in_its_answer()
    {
        var declared = new TaskCompletionSource<(HttpStatusCode, JsonElement)>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agents.Behaviour = async invocation =>
        {
            if (await CommonAsync(invocation) is { } common) return common;

            if (invocation.Container == Dev)
            {
                Assert.Equal(HttpStatusCode.OK, (await AskAsync(invocation, "10m", "was the release published")).Status);
                var response = await ContainerClient(invocation).PostAsJsonAsync(
                    $"/api/teams/{_team}/containers/Dev/workflow-complete", new { delivered = "published" }, Ct);
                declared.TrySetResult((response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(Ct)));
            }

            return new AgentResult(0, "done");
        };

        var root = (await PersonTellsAsync(Dev, "publish the release")).CorrelationId;
        var (status, body) = await declared.Task.WaitAsync(Patience, Ct);
        await SettleAsync();

        var wakeId = Field(Assert.Single(await RowsAsync(root, MessageTypes.WakeAsked)), PayloadFields.Wake).GetInt64();
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains($"wake {wakeId}", body.GetProperty("notice").GetString());
        Assert.Contains("cancelled", body.GetProperty("notice").GetString());
        Assert.Equal(wakeId, body.GetProperty("wakesCancelled")[0].GetProperty("id").GetInt64());

        var cancelled = Assert.Single(await RowsAsync(root, MessageTypes.WakeCancelled));
        Assert.Equal(Dev.ToString(), Field(cancelled, PayloadFields.By).GetString());
        Assert.Contains("declared complete", Field(cancelled, PayloadFields.Reason).GetString());
        Assert.Single(await RowsAsync(root, MessageTypes.WorkflowCompleted));
        Assert.Empty(await Wakes.AllAsync(Ct));
    }

    [Fact]
    public async Task A_person_cancels_a_wake_through_the_route_and_a_machine_principal_is_refused()
    {
        var root = await DevAsksAsync("1m", "is the mirror in sync");
        var wake = Assert.Single(await Wakes.AllAsync(Ct));

        // A MACHINE PRINCIPAL HOLDING EVERY PERMIT is refused.
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            $"{_team}/Probe", PrincipalKind.Container, _team, Permits.All, ct: Ct);
        using var machine = _factory.CreateClient();
        machine.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        var refused = await machine.PostAsync($"/api/teams/{_team}/wakes/{wake.Id}/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Single(await Wakes.AllAsync(Ct));

        var cancelled = await _person.PostAsync($"/api/teams/{_team}/wakes/{wake.Id}/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Empty(await Wakes.AllAsync(Ct));

        var row = Assert.Single(await RowsAsync(root, MessageTypes.WakeCancelled));
        Assert.Equal(Email, Field(row, PayloadFields.By).GetString());
        Assert.Null(Host.Find(Dev)!.Snapshot().Wakes);

        // Twice is a missing wake, and the clock reaching its time fires nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await _person.PostAsync($"/api/teams/{_team}/wakes/{wake.Id}/cancel", null, Ct)).StatusCode);
        await MoveClockAsync(TimeSpan.FromMinutes(2));
        await Task.Delay(500, Ct);
        Assert.Empty(await RowsAsync(root, MessageTypes.WakeFired));
    }

    [Fact]
    public async Task A_member_cancels_its_own_wake_and_not_another_members()
    {
        var own = new TaskCompletionSource<(HttpStatusCode, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var others = new TaskCompletionSource<(HttpStatusCode, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await MemberAsksAsync(Dev2, "1h", "Dev2's own");
        var dev2s = Assert.Single(await Wakes.AllAsync(Ct));

        _agents.Behaviour = async invocation =>
        {
            if (await CommonAsync(invocation) is { } common) return common;

            if (invocation.Container == Dev)
            {
                var (_, asked) = await AskAsync(invocation, "1h", "Dev's own");
                var id = asked.GetProperty("wake").GetInt64();
                others.TrySetResult(Error(await PostAsync(invocation, $"/api/teams/{_team}/containers/Dev/wake/{dev2s.Id}/cancel", null)));
                own.TrySetResult(Error(await PostAsync(invocation, $"/api/teams/{_team}/containers/Dev/wake/{id}/cancel", null)));
            }

            return new AgentResult(0, "done");
        };

        var root = (await PersonTellsAsync(Dev, "set and cancel")).CorrelationId;
        Assert.Equal(HttpStatusCode.NotFound, (await others.Task.WaitAsync(Patience, Ct)).Item1);
        Assert.Equal(HttpStatusCode.OK, (await own.Task.WaitAsync(Patience, Ct)).Item1);
        await SettleAsync();

        Assert.Equal(dev2s, Assert.Single(await Wakes.AllAsync(Ct)));
        Assert.Equal(Dev.ToString(), Field(Assert.Single(await RowsAsync(root, MessageTypes.WakeCancelled)), PayloadFields.By).GetString());
    }

    // --- the workflow's bounds ----------------------------------------------------------------------

    [Fact]
    public async Task The_fire_counts_one_step_toward_the_chain_limit_and_one_past_it_is_refused_at_the_ask_and_skipped_at_the_fire()
    {
        // ONE STEP: the fire's instruction is the asking row's depth plus one.
        var first = await DevAsksAsync("1m", "the first look");
        var askedFirst = Assert.Single(await RowsAsync(first, MessageTypes.WakeAsked));
        await MoveClockAsync(TimeSpan.FromMinutes(1));
        await UntilAsync(() => _agents.RunsFor(Dev) == 2, "the first wake");
        await SettleAsync();
        var fire = Assert.Single(await RowsAsync(first, MessageTypes.InstructionFor(Dev)), m => m.Source.StartsWith("wake:", StringComparison.Ordinal));
        Assert.Equal(askedFirst.Depth + 1, fire.Depth);

        // SKIPPED AT THE FIRE when the limit has come down to the asking row's depth since.
        var second = await DevAsksAsync("1m", "the second look");
        var askedSecond = Assert.Single(await RowsAsync(second, MessageTypes.WakeAsked));
        await SetAsync("causation.depthLimit", askedSecond.Depth);
        await MoveClockAsync(TimeSpan.FromMinutes(1));
        await UntilAsync(() => RowsAsync(second, MessageTypes.WakeSkipped).Result.Count == 1, "the second wake skipped");
        var skipped = Assert.Single(await RowsAsync(second, MessageTypes.WakeSkipped));
        Assert.Equal(WorkflowBounds.DepthRefusal(askedSecond.Depth + 1), Field(skipped, PayloadFields.Reason).GetString());
        Assert.Empty(await RowsAsync(second, MessageTypes.WakeFired));

        // REFUSED AT THE ASK when its instruction would already be past it.
        var refusal = new TaskCompletionSource<(HttpStatusCode, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _agents.Behaviour = async invocation =>
        {
            if (await CommonAsync(invocation) is { } common) return common;
            if (invocation.Container == Dev) refusal.TrySetResult(Error(await AskAsync(invocation, "1m", "a third look")));
            return new AgentResult(0, "done");
        };
        await SetAsync("causation.depthLimit", 1);
        await PersonTellsAsync(Dev, "a third job");
        var (status, error) = await refusal.Task.WaitAsync(Patience, Ct);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(WorkflowBounds.DepthRefusal(2), error);
    }

    [Fact]
    public async Task The_fires_run_is_inside_the_workflow_budget_and_a_wake_over_it_is_refused_at_the_ask_and_skipped_at_the_fire()
    {
        var registry = Services.GetRequiredService<TeamRegistry>();
        var refusal = new TaskCompletionSource<(HttpStatusCode, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

        _agents.Behaviour = async invocation =>
        {
            if (await CommonAsync(invocation) is { } common) return common;
            if (invocation.Container != Dev) return new AgentResult(0, "done", Usage: new InvocationUsage(10, 10, "test"));

            if (invocation.Prompt.Contains("lower the budget", StringComparison.Ordinal))
            {
                await registry.SetBudgetAsync(_team, 1000, Ct);
                refusal.TrySetResult(Error(await AskAsync(invocation, "1m", "over the budget")));
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, (await AskAsync(invocation, "1m", "look again")).Status);
            }

            return new AgentResult(0, "done", Usage: new InvocationUsage(1000, 500, "test"));
        };

        // INSIDE THE BUDGET: the woken run is the workflow's, billed to it as any run is.
        await registry.SetBudgetAsync(_team, 1_000_000, Ct);
        var inside = (await PersonTellsAsync(Dev, "the first job")).CorrelationId;
        await SettleAsync();
        var before = await Log.GetSpendSinceNudgeAsync(inside, Ct);
        await MoveClockAsync(TimeSpan.FromMinutes(1));
        await UntilAsync(() => _agents.RunsFor(Dev) == 2, "the woken run");
        await SettleAsync();

        var fire = Assert.Single(await RowsAsync(inside, MessageTypes.InstructionFor(Dev)), m => m.Source.StartsWith("wake:", StringComparison.Ordinal));
        var wokenRun = Assert.Single(await RowsAsync(inside, MessageTypes.Completed), m => m.CausationSeq == fire.Seq);
        Assert.Equal(inside, wokenRun.CorrelationId);
        var after = await Log.GetSpendSinceNudgeAsync(inside, Ct);
        Assert.Equal(before.RunsWithMeasuredUsage + 1, after.RunsWithMeasuredUsage);
        Assert.Equal(before.TokensSpent + 1500, after.TokensSpent);

        // SKIPPED AT THE FIRE: the workflow went over its budget while the wake waited.
        var over = (await PersonTellsAsync(Dev, "the second job")).CorrelationId;
        await SettleAsync();
        await registry.SetBudgetAsync(_team, 1000, Ct);
        await MoveClockAsync(TimeSpan.FromMinutes(1));
        await UntilAsync(() => RowsAsync(over, MessageTypes.WakeSkipped).Result.Count == 1, "the wake skipped over budget");

        var overSpend = await Log.GetSpendSinceNudgeAsync(over, Ct);
        var skipped = Assert.Single(await RowsAsync(over, MessageTypes.WakeSkipped));
        Assert.Equal(WorkflowBounds.BudgetRefusal(overSpend.TokensSpent, 1000), Field(skipped, PayloadFields.Reason).GetString());
        Assert.Empty(await RowsAsync(over, MessageTypes.WakeFired));
        Assert.Equal(3, _agents.RunsFor(Dev));

        // REFUSED AT THE ASK: a run already in a workflow over its budget asks.
        await registry.SetBudgetAsync(_team, 1_000_000, Ct);
        var third = (await PersonTellsAsync(Dev, "the third job")).CorrelationId;
        await SettleAsync();
        await PersonTellsAsync(Dev, "lower the budget", (await Log.ReadCorrelationAsync(third, Ct)).Max(m => m.Seq));
        var (status, error) = await refusal.Task.WaitAsync(Patience, Ct);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.StartsWith("This workflow has spent ", error);
        Assert.Contains($"which exceeds the limit of {1000:N0}", error);
        Assert.Single(await Wakes.ForWorkflowAsync(third, Ct));
    }

    // --- helpers --------------------------------------------------------------------------------------

    /// <summary>
    /// What every scripted member does before its test's own script: a run answering the idle offer
    /// does nothing, and a run a wake started declares its workflow complete, as a member that found
    /// what it was waiting for does - so a test counts the runs it caused and no offer after them.
    /// Null hands the run to the test's script.
    /// </summary>
    private async Task<AgentResult?> CommonAsync(AgentInvocation invocation)
    {
        if (invocation.Prompt.Contains(OfferText, StringComparison.Ordinal)) return new AgentResult(0, "nothing to add");

        if (invocation.Prompt.Contains("You asked to be woken", StringComparison.Ordinal))
        {
            if (_wokenDeclares)
            {
                using var client = ContainerClient(invocation);
                await client.PostAsJsonAsync(
                    $"/api/teams/{_team}/containers/{invocation.Container.Name}/workflow-complete",
                    new { delivered = "looked again" }, Ct);
            }

            return new AgentResult(0, "looked again", Usage: new InvocationUsage(1000, 500, "test"));
        }

        return null;
    }

    private async Task<AgentResult> DefaultAsync(AgentInvocation invocation) =>
        await CommonAsync(invocation) ?? new AgentResult(0, "done");

    private WebApplicationFactory<Program> Start() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(_agents);
                services.AddSingleton(new WakeClock(_time));
            }));

    private async Task<HttpClient> PersonAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A person tells Dev something whose run asks for a wake; answers the workflow once
    /// the member has settled.</summary>
    private Task<long> DevAsksAsync(string delay, string note) => MemberAsksAsync(Dev, delay, note);

    private async Task<long> MemberAsksAsync(ContainerId who, string delay, string note)
    {
        var marker = $"ask for a wake: {Guid.NewGuid():N}";

        _agents.Behaviour = async invocation =>
        {
            if (await CommonAsync(invocation) is { } common) return common;

            if (invocation.Container == who && invocation.Prompt.Contains(marker, StringComparison.Ordinal))
            {
                var (status, body) = await AskAsync(invocation, delay, note);
                Assert.True(status == HttpStatusCode.OK, body.ToString());
            }

            return new AgentResult(0, "done");
        };

        var root = (await PersonTellsAsync(who, marker)).CorrelationId;
        await UntilAsync(() => Wakes.AllAsync(Ct).Result.Any(w => w.Workflow == root), "the wake stored");
        await SettleAsync();
        return root;
    }

    private async Task<Message> PersonTellsAsync(ContainerId who, string instruction, long? causation = null)
    {
        var after = await Log.HighestSeqAsync(Ct);
        var told = await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{who.Name}/tell",
            new { instruction, causation = causation?.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        return (await Log.ReadAfterAsync(after, [MessageTypes.InstructionFor(who)], int.MaxValue, Ct)).First();
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> AskAsync(AgentInvocation invocation, string delay, string note) =>
        PostAsync(invocation, $"/api/teams/{_team}/containers/{invocation.Container.Name}/wake", new { @in = delay, note });

    private async Task<(HttpStatusCode, JsonElement)> PostAsync(AgentInvocation invocation, string path, object? body)
    {
        using var client = ContainerClient(invocation);
        var response = body is null ? await client.PostAsync(path, null, Ct) : await client.PostAsJsonAsync(path, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static (HttpStatusCode, string) Error((HttpStatusCode Status, JsonElement Body) answer) =>
        (answer.Status, answer.Body.ValueKind == JsonValueKind.Object && answer.Body.TryGetProperty("error", out var error)
            ? error.GetString() ?? ""
            : "");

    private HttpClient ContainerClient(AgentInvocation invocation)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, invocation.Environment["HARNESS_KEY"]);
        return client;
    }

    private async Task SetAsync(string setting, long value)
    {
        var put = await _person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { [setting] = value }, Ct);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(Ct));
    }

    private async Task MoveClockAsync(TimeSpan by)
    {
        _time.Now += by;
        Services.GetRequiredService<WakeSignal>().Signal();
        await Task.Delay(100, Ct);
    }

    private async Task<IReadOnlyList<Message>> RowsAsync(long correlation, string type) =>
        [.. (await Log.ReadCorrelationAsync(correlation, Ct)).Where(m => m.Type == type)];

    private static JsonElement Field(Message row, string name) =>
        JsonDocument.Parse(row.Payload).RootElement.GetProperty(name).Clone();

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Never saw {what}.");
            await Task.Delay(25);
        }
    }

    /// <summary>Every member idle and nothing pending, several passes in a row.</summary>
    private async Task SettleAsync()
    {
        var pending = Services.GetRequiredService<IPendingDeliveries>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        for (var quietPasses = 0; quietPasses < 10 && DateTime.UtcNow < deadline;)
        {
            var read = (await pending.ForTeamAsync(_team, Ct)).Count == 0;
            var idle = new[] { Manager, Dev, Dev2 }.All(id => Host.Find(id) is not { } found || found.State == ContainerState.Idle);
            quietPasses = read && idle ? quietPasses + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
