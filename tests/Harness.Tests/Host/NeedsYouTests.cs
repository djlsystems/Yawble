using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Solutions;
using Harness.Kanban;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// WHAT IS WAITING ON A PERSON, IN ONE LIST. `GET /api/needs-you` answers one row per fact the
/// platform already holds - a decision, a blocked or failed member, a solution's missing input, an
/// item whose completed work is pushed and not merged, a stranded dispatch, an unfinished removal -
/// newest first, in the platform's own words, and to a person only. It never runs git: the Host here
/// is handed a git that records every call and fails, and the landed cache is filled by the test.
/// Each test has a Host of its own, and every team is paused so nothing written here starts a run.
/// </summary>
public sealed class NeedsYouTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-needs-you-").FullName;
    private readonly BacklogLandedCache _landed = new();
    private readonly AsyncLocal<ConcurrentQueue<string>?> _recording = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient? _person;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string GitCalls => Path.Combine(_root, "git-calls");

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private IBacklogStore Backlog => Services.GetRequiredService<IBacklogStore>();

    public ValueTask InitializeAsync()
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);

        // A GIT THAT FAILS, AND SAYS IT WAS ASKED: a fetch, or any other git, during the read is a
        // line in GitCalls and an exit 1.
        var git = Path.Combine(_root, "git.sh");
        File.WriteAllText(git, $"#!/bin/sh\necho \"$@\" >> '{GitCalls}'\nexit 1\n");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new HeldAgent());
                services.AddSingleton(new GitRunner(gitExecutable: git));
                services.AddSingleton(_landed);
                services.AddSingleton<IRepoClone>(new FolderClone());

                // THE LOG AND THE BACKLOG, EACH CALL RECORDED by the read that makes it: the Host
                // registers both as instances, and every reader here gets these around them.
                var log = (IMessageLog)services.Last(d => d.ServiceType == typeof(IMessageLog)).ImplementationInstance!;
                services.AddSingleton(Recorded<IMessageLog>.Around(log, _recording));
                var backlog = (IBacklogStore)services.Last(d => d.ServiceType == typeof(IBacklogStore)).ImplementationInstance!;
                services.AddSingleton(Recorded<IBacklogStore>.Around(backlog, _recording));
            }));

        // The read runs in the test's execution context, so it sees the recorder set around it.
        _factory.Server.PreserveExecutionContext = true;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _person?.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_quiet_instance_answers_an_empty_list()
    {
        foreach (var part in new[] { "", "?part=live", "?part=stored" })
        {
            var items = (await ReadAsync(part)).GetProperty("items");
            Assert.Equal(JsonValueKind.Array, items.ValueKind);
            Assert.Empty(items.EnumerateArray());
        }
    }

    [Fact]
    public async Task A_machine_principal_is_refused()
    {
        var team = await TeamAsync("Refused");
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, team, Permits.All, ct: Ct);

        using var member = _factory.CreateClient();
        member.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);

        var response = await member.GetAsync("/api/needs-you", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_decision_is_listed_with_its_question_its_row_and_the_item_its_workflow_was_dispatched_for()
    {
        var team = await TeamAsync("Decides");
        var (workflow, item) = await DispatchAsync(team, "Pick a store");
        var manager = Manager(team);

        var row = await Log.AppendAsync(new NewMessage(
            MessageTypes.NeedsDecision, """{"question":"SQLite or Postgres?"}""", manager.Id.ToString(), workflow), Ct);
        manager.MarkNeedsDecision("SQLite or Postgres?", row.Seq, row.CorrelationId);
        manager.NoteMarkRow(row);

        var listed = Assert.Single((await ReadAsync("?part=live")).GetProperty("items").EnumerateArray());

        Assert.Equal("decision", listed.GetProperty("kind").GetString());
        Assert.Equal(team, listed.GetProperty("team").GetProperty("id").GetString());
        Assert.Equal("Decides", listed.GetProperty("team").GetProperty("name").GetString());
        Assert.Equal(TeamRegistry.DefaultManagerName, listed.GetProperty("member").GetString());
        Assert.Equal(workflow, listed.GetProperty("workflow").GetInt64());
        Assert.Equal(item, listed.GetProperty("item").GetProperty("id").GetInt64());
        Assert.Equal("Pick a store", listed.GetProperty("item").GetProperty("title").GetString());
        Assert.Equal("SQLite or Postgres?", listed.GetProperty("words").GetString());
        Assert.Equal(row.Seq, listed.GetProperty("needsDecisionSeq").GetInt64());
        Assert.Equal(row.OccurredAt, listed.GetProperty("since").GetDateTimeOffset());
        Assert.False(listed.TryGetProperty("failureClass", out _));
        Assert.Equal("workflow", listed.GetProperty("where").GetProperty("view").GetString());
        Assert.Equal(workflow, listed.GetProperty("where").GetProperty("workflow").GetInt64());
    }

    [Fact]
    public async Task A_blocked_member_is_listed_with_its_reason()
    {
        var team = await TeamAsync("Blocks");
        var manager = Manager(team);

        var row = await Log.AppendAsync(new NewMessage(
            MessageTypes.Blocked, """{"reason":"The clone is missing."}""", manager.Id.ToString()), Ct);
        manager.MarkBlocked("The clone is missing.");
        manager.NoteMarkRow(row);

        var listed = Assert.Single((await ReadAsync()).GetProperty("items").EnumerateArray());

        Assert.Equal("blocked", listed.GetProperty("kind").GetString());
        Assert.Equal("The clone is missing.", listed.GetProperty("words").GetString());
        Assert.Equal(row.CorrelationId, listed.GetProperty("workflow").GetInt64());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("item").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("card").ValueKind);
        Assert.Equal(row.OccurredAt, listed.GetProperty("since").GetDateTimeOffset());
        Assert.False(listed.TryGetProperty("needsDecisionSeq", out _));
    }

    [Fact]
    public async Task A_failed_member_is_listed_with_its_failure_and_class_and_outranks_its_blocked()
    {
        var team = await TeamAsync("Fails");
        var manager = Manager(team);

        var blocked = await Log.AppendAsync(new NewMessage(
            MessageTypes.Blocked, """{"reason":"Giving up."}""", manager.Id.ToString()), Ct);
        manager.MarkBlocked("Giving up.");
        manager.NoteMarkRow(blocked);

        var failed = await Log.AppendAsync(new NewMessage(
            MessageTypes.Failed, """{"output":"connection reset","failureClass":"transport"}""", manager.Id.ToString(),
            blocked.Seq), Ct);
        manager.MarkFailed("connection reset", FailureClasses.Transport);
        manager.NoteMarkRow(failed);

        var listed = Assert.Single((await ReadAsync("?part=live")).GetProperty("items").EnumerateArray());

        Assert.Equal("failed", listed.GetProperty("kind").GetString());
        Assert.Equal("connection reset", listed.GetProperty("words").GetString());
        Assert.Equal(FailureClasses.Transport, listed.GetProperty("failureClass").GetString());
        Assert.Equal(failed.OccurredAt, listed.GetProperty("since").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_solution_team_missing_an_input_is_listed_in_the_waits_own_sentence()
    {
        var team = await TeamAsync("Waits");
        await SolutionMissingResumeAsync(team);

        var listed = Assert.Single((await ReadAsync("?part=live")).GetProperty("items").EnumerateArray());

        Assert.Equal("waiting-input", listed.GetProperty("kind").GetString());
        Assert.Equal("waiting for a file in Resume/", listed.GetProperty("words").GetString());
        Assert.Equal("solution", listed.GetProperty("where").GetProperty("view").GetString());
        Assert.Equal(team, listed.GetProperty("where").GetProperty("team").GetString());
        Assert.Equal(
            (await Services.GetRequiredService<ITeamSolutionStore>().FindAsync(team, Ct))!.InstalledAt,
            listed.GetProperty("since").GetDateTimeOffset());
        Assert.Empty((await ReadAsync("?part=stored")).GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// THE READ RECORDS NOTHING IN THE WAIT. A wait nobody has seen yet is dated from the package's
    /// install and stays unseen after the read; once the wait's own writer has seen it, the read
    /// answers that sighting and leaves it as it was.
    /// </summary>
    [Fact]
    public async Task A_read_leaves_the_solution_wait_as_it_found_it()
    {
        var team = await TeamAsync("Unseen");
        await SolutionMissingResumeAsync(team);
        var wait = Services.GetRequiredService<SolutionWait>();
        var installed = (await Services.GetRequiredService<ITeamSolutionStore>().FindAsync(team, Ct))!.InstalledAt;

        Assert.Null(wait.WaitingSince(team));
        var unseen = Assert.Single((await ReadAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(installed, unseen.GetProperty("since").GetDateTimeOffset());
        Assert.Null(wait.WaitingSince(team));

        Assert.Equal("waiting for a file in Resume/", await wait.WaitingForAsync(team, Ct));
        var seen = wait.WaitingSince(team);
        Assert.NotNull(seen);

        var listed = Assert.Single((await ReadAsync("?part=live")).GetProperty("items").EnumerateArray());
        Assert.Equal(seen, listed.GetProperty("since").GetDateTimeOffset());
        Assert.Equal(seen, wait.WaitingSince(team));
    }

    /// <summary>
    /// ONE QUERY PER STORE, HOWEVER MANY ITEMS. A stored read with one item in flight and one with
    /// five - open, blocked, completed and pushed - asks the log and the backlog exactly the same
    /// calls: one standing query for every workflow, one read of every dispatch with its item. A
    /// live read naming a mark's item reads the backlog once.
    /// </summary>
    [Fact]
    public async Task A_read_asks_each_store_once_however_many_items_are_in_flight()
    {
        _person ??= await PersonAsync();
        var team = await TeamAsync("Counts");
        await DispatchAsync(team, "One");

        var (one, oneBacklog) = await RecordAsync("?part=stored");

        var (blocked, _) = await DispatchAsync(team, "Blocked");
        await BlockAsync(team, blocked);
        var (completed, _) = await DispatchAsync(team, "Completed");
        await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", "console", completed), Ct);
        _landed.Put(team, new BacklogLanded(BacklogLandedStates.Pushed, team, "On origin."));
        await DispatchAsync(team, "Three");
        await DispatchAsync(team, "Four");

        var (five, fiveBacklog) = await RecordAsync("?part=stored");

        Assert.Equal([nameof(IMessageLog.WorkflowStandingsAmongAsync)], one);
        Assert.Equal(one, five);
        Assert.Equal([nameof(IBacklogStore.CurrentDispatchesAsync)], oneBacklog);
        Assert.Equal(oneBacklog, fiveBacklog);

        var manager = Manager(team);
        var row = await Log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"held"}""", manager.Id.ToString(), blocked), Ct);
        manager.MarkBlocked("held");
        manager.NoteMarkRow(row);

        var (liveLog, liveBacklog) = await RecordAsync("?part=live");
        Assert.Empty(liveLog);
        Assert.Equal([nameof(IBacklogStore.CurrentDispatchesAsync)], liveBacklog);
    }

    [Fact]
    public async Task A_completed_dispatch_whose_work_is_pushed_is_merge_ready_and_no_git_runs()
    {
        var team = await TeamAsync("Pushes");
        var (workflow, item) = await DispatchAsync(team, "Ship the tracker");
        var completed = await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", "console", workflow), Ct);
        _landed.Put(team, new BacklogLanded(BacklogLandedStates.Pushed, team, "team/x is on origin and not on origin/main."));

        // A SECOND TEAM, COMPLETED TOO, WHOSE LANDED NOBODY HAS READ: the cache has nothing, and this
        // read does not go and measure it. It has a clone and a known default branch, so measuring it
        // would run git in that clone.
        var uncached = await TeamAsync("Uncached");
        var registry = Services.GetRequiredService<TeamRegistry>();
        await registry.SetReposAsync(uncached, ["https://github.com/owner/Widget.git"], Ct);
        Assert.True(Directory.Exists(Path.Combine(Services.GetRequiredService<TeamPaths>().ReposFor(uncached), "Widget", "main", ".git")));
        Assert.Equal("main", registry.DefaultBranchFor(uncached, "Widget").Branch);
        var (other, _) = await DispatchAsync(uncached, "Not measured");
        await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", "console", other), Ct);

        File.Delete(GitCalls);
        var listed = Assert.Single((await ReadAsync("?part=stored")).GetProperty("items").EnumerateArray());
        var all = (await ReadAsync()).GetProperty("items").EnumerateArray().ToList();

        Assert.False(File.Exists(GitCalls), File.Exists(GitCalls) ? File.ReadAllText(GitCalls) : "");
        Assert.Equal("merge-ready", listed.GetProperty("kind").GetString());
        Assert.Equal("Pushes", listed.GetProperty("team").GetProperty("name").GetString());
        Assert.Equal(workflow, listed.GetProperty("workflow").GetInt64());
        Assert.Equal(item, listed.GetProperty("item").GetProperty("id").GetInt64());
        Assert.Equal("Ship the tracker", listed.GetProperty("item").GetProperty("title").GetString());
        Assert.Equal("team/x is on origin and not on origin/main.", listed.GetProperty("words").GetString());
        Assert.Equal(completed.OccurredAt, listed.GetProperty("since").GetDateTimeOffset());
        var where = listed.GetProperty("where");
        Assert.Equal("merge", where.GetProperty("view").GetString());
        Assert.Equal(team, where.GetProperty("team").GetString());
        Assert.Equal(item, where.GetProperty("item").GetInt64());
        Assert.Single(all);
    }

    [Theory]
    [InlineData(BacklogLandedStates.Local)]
    [InlineData(BacklogLandedStates.Unknown)]
    [InlineData(BacklogLandedStates.InReview)]
    public async Task A_completed_dispatch_whose_landed_is_not_pushed_is_not_merge_ready(string state)
    {
        var team = await TeamAsync("Not Pushed");
        var (workflow, _) = await DispatchAsync(team, "Still local");
        await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", "console", workflow), Ct);
        _landed.Put(team, new BacklogLanded(state, team, "Not on a remote."));

        Assert.Empty((await ReadAsync()).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_pushed_dispatch_whose_workflow_was_closed_not_completed_is_not_merge_ready()
    {
        var team = await TeamAsync("Closed");
        var (workflow, _) = await DispatchAsync(team, "Closed by a person");
        await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowClosed, """{"reason":"dropped"}""", "console", workflow), Ct);
        _landed.Put(team, new BacklogLanded(BacklogLandedStates.Pushed, team, "On origin."));

        Assert.Empty((await ReadAsync()).GetProperty("items").EnumerateArray());

        // AND ONE STILL OPEN IS NOT EITHER.
        var open = await TeamAsync("Open");
        await DispatchAsync(open, "Still running");
        _landed.Put(open, new BacklogLanded(BacklogLandedStates.Pushed, open, "On origin."));

        Assert.Empty((await ReadAsync("?part=stored")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_stranded_dispatch_is_listed_with_the_backlogs_notice()
    {
        var team = await TeamAsync("Strands");
        var (workflow, item) = await DispatchAsync(team, "Job tracker");
        var card = await BlockAsync(team, workflow);
        var later = await FinishInALaterWorkflowAsync(team, card);

        var listed = Assert.Single((await ReadAsync("?part=stored")).GetProperty("items").EnumerateArray());
        var done = await Services.GetRequiredService<KanbanStore>().GetCardAsync(card, TestContext.Current.CancellationToken);

        Assert.Equal("stranded", listed.GetProperty("kind").GetString());
        Assert.Equal($"The work continued in workflow {later}.", listed.GetProperty("words").GetString());
        Assert.Equal(workflow, listed.GetProperty("workflow").GetInt64());
        Assert.Equal(item, listed.GetProperty("item").GetProperty("id").GetInt64());
        Assert.Equal(new DateTimeOffset(done!.UpdatedAt), listed.GetProperty("since").GetDateTimeOffset());
        Assert.Equal("backlog", listed.GetProperty("where").GetProperty("view").GetString());
        Assert.Equal(item, listed.GetProperty("where").GetProperty("item").GetInt64());

        // THE SET QUERY'S STATE IS THE ONE-WORKFLOW PROJECTION'S.
        var timing = await Log.WorkflowForTeamAsync(team, workflow, 0, Ct);
        Assert.Equal(timing.State, (await Log.WorkflowStandingsAmongAsync(new Dictionary<long, string> { [workflow] = team }, Ct))[workflow].Stuck);

        // READING IT CHANGED NOTHING: the workflow is still open, nothing closed or declared.
        Assert.Contains(workflow, await Log.OpenWorkflowsAmongAsync([workflow], Ct));
    }

    [Fact]
    public async Task An_unfinished_removal_is_listed_with_what_it_left()
    {
        var team = await TeamAsync("Removes");
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await Services.GetRequiredService<IUnfinishedRemovals>().RecordAsync(
            new UnfinishedRemoval("/data/teams/x/workspaces/Dev", "member", team, "Dev", ["/data/teams/x/workspaces/Dev/locked"], at, 1), Ct);

        var listed = Assert.Single((await ReadAsync("?part=stored")).GetProperty("items").EnumerateArray());

        Assert.Equal("removal", listed.GetProperty("kind").GetString());
        Assert.Equal("Removes", listed.GetProperty("team").GetProperty("name").GetString());
        Assert.Equal("Dev", listed.GetProperty("member").GetString());
        Assert.Equal("1 path(s) remain: /data/teams/x/workspaces/Dev/locked", listed.GetProperty("words").GetString());
        Assert.Equal(at, listed.GetProperty("since").GetDateTimeOffset());
        Assert.Equal("removals", listed.GetProperty("where").GetProperty("view").GetString());
        Assert.Empty((await ReadAsync("?part=live")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_list_is_newest_first_across_both_parts()
    {
        var team = await TeamAsync("Orders");
        var removals = Services.GetRequiredService<IUnfinishedRemovals>();
        var older = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        await removals.RecordAsync(new UnfinishedRemoval("/data/old", "member", team, "A", ["/data/old"], older, 1), Ct);
        await removals.RecordAsync(new UnfinishedRemoval("/data/new", "member", team, "B", ["/data/new"], newer, 1), Ct);

        var manager = Manager(team);
        var row = await Log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"now"}""", manager.Id.ToString()), Ct);
        manager.MarkBlocked("now");
        manager.NoteMarkRow(row);

        var items = (await ReadAsync()).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(["blocked", "removal", "removal"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(["Manager", "B", "A"], items.Select(i => i.GetProperty("member").GetString()));
        var since = items.Select(i => i.GetProperty("since").GetDateTimeOffset()).ToList();
        Assert.Equal(since.OrderByDescending(s => s), since);
    }

    [Fact]
    public async Task An_unknown_part_is_refused()
    {
        using var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await person.GetAsync("/api/needs-you?part=all", Ct)).StatusCode);
    }

    /// <summary>A member's mark set in a run names the run's card: the instruction's own <c>card</c>.</summary>
    [Fact]
    public async Task A_mark_set_in_a_run_names_the_runs_card_and_its_row()
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

        var dev = new ContainerId("alpha", "dev");
        await bed.AddAsync(dev);
        var told = await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(dev), """{"instruction":"one","card":"4242"}""", "console"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => started.Task.IsCompleted));

        await new MemberReports(bed.Host, bed.Store, new RunHeartbeat()).BlockedAsync(dev, "stuck", ct: Ct);

        var blocked = (await bed.OfTypeAsync(MessageTypes.Blocked)).Single();
        var mark = bed.Host.Find(dev)!.Marks().Blocked;
        Assert.Equal(new MarkRow(blocked.Seq, told.CorrelationId, blocked.OccurredAt, "4242"), mark);
        release.SetResult();
    }

    /// <summary>The log's and the backlog's calls made by one read, by method name, in order.</summary>
    private async Task<(List<string> Log, List<string> Backlog)> RecordAsync(string query)
    {
        var calls = new ConcurrentQueue<string>();
        _recording.Value = calls;
        try { await ReadAsync(query); }
        finally { _recording.Value = null; }

        return (
            [.. calls.Where(c => c.StartsWith("log:", StringComparison.Ordinal)).Select(c => c[4..])],
            [.. calls.Where(c => c.StartsWith("backlog:", StringComparison.Ordinal)).Select(c => c[8..])]);
    }

    private async Task<JsonElement> ReadAsync(string query = "")
    {
        _person ??= await PersonAsync();
        var response = await _person.GetAsync($"/api/needs-you{query}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
    }

    private async Task<HttpClient> PersonAsync()
    {
        var users = Services.GetRequiredService<IUserStore>();
        if (await users.FindAsync(Email, Ct) is null) await users.CreateAsync(Email, Password);

        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A team of its own, paused so nothing written to the log here starts a run.</summary>
    private async Task<string> TeamAsync(string name)
    {
        var registry = Services.GetRequiredService<TeamRegistry>();
        var agent = Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetPausedAsync(team, true, Ct);
        return team;
    }

    private MemberRuntime Manager(string team) =>
        Services.GetRequiredService<ContainerHost>().Find(new ContainerId(team, TeamRegistry.DefaultManagerName))!;

    /// <summary>A backlog item dispatched to <paramref name="team"/>, rooted as a real dispatch is.</summary>
    private async Task<(long Workflow, long Item)> DispatchAsync(string team, string title)
    {
        var item = await Backlog.CreateAsync(null, title, "Build it.", Email, Ct);
        var root = await Log.AppendAsync(new NewMessage(
            MessageTypes.BacklogItemDispatched,
            JsonSerializer.Serialize(new { item = item.Id, team, title }), Email), Ct);
        await Backlog.AddDispatchAsync(item.Id, team, team, root.CorrelationId, Email, Ct);
        return (root.CorrelationId, item.Id);
    }

    /// <summary>The Developer is sent a card in <paramref name="workflow"/> and blocks; so does the
    /// Manager. Returns the card's id. As <c>StrandedWorkflowTests</c> does.</summary>
    private async Task<string> BlockAsync(string team, long workflow)
    {
        var manager = new ContainerId(team, TeamRegistry.DefaultManagerName).ToString();
        var developer = new ContainerId(team, "Developer");

        await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(developer), """{"instruction":"Re-check the build."}""", manager, workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"The tests cannot start."}""", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", manager, workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"The re-check cannot start."}""", manager, workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", manager, workflow), Ct);

        return $"{workflow}_Developer";
    }

    /// <summary>The same card sent again as a NEW workflow, and done there. Returns that workflow.</summary>
    private async Task<long> FinishInALaterWorkflowAsync(string team, string card)
    {
        var developer = new ContainerId(team, "Developer");
        var resent = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(developer),
            JsonSerializer.Serialize(new { instruction = "Re-check the build.", card }),
            new ContainerId(team, TeamRegistry.DefaultManagerName).ToString()), Ct);
        var later = resent.CorrelationId;

        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", developer.ToString(), later), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Handback, """{"delivered":"Re-checked."}""", developer.ToString(), later), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", developer.ToString(), later), Ct);

        return later;
    }

    /// <summary>The team's solution row, as <c>SolutionWaitTests</c> writes it: a required Resume folder.</summary>
    private async Task SolutionMissingResumeAsync(string team)
    {
        var manifest = new JsonObject
        {
            ["format"] = 1,
            ["id"] = "waits",
            ["name"] = "Waits",
            ["version"] = "1.0.0",
            ["description"] = "A team that needs a resume.",
            ["team"] = new JsonObject { ["name"] = "Waits", ["instructions"] = "Write letters." },
            ["members"] = new JsonArray(new JsonObject { ["name"] = "Dev", ["role"] = "member", ["instructions"] = "Write." }),
            ["inputs"] = new JsonObject
            {
                ["documents"] = new JsonArray(new JsonObject
                {
                    ["folder"] = "Resume",
                    ["description"] = "Your reference resume.",
                    ["required"] = true,
                }),
            },
        }.ToJsonString();

        await Services.GetRequiredService<ITeamSolutionStore>().SaveAsync(
            new TeamSolutionRow(
                team, "waits", "Waits", "1.0.0", _root, DateTimeOffset.UtcNow, Email, null, manifest,
                new Dictionary<string, string>(), new Dictionary<string, string> { ["Dev"] = "Dev" },
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            new TriggerAudit("test", null, "solution.installed", team, "Waits", null),
            Ct);
    }

    /// <summary>A clone that is a folder with a <c>.git</c> in it and origin's HEAD naming main: no git.</summary>
    private sealed class FolderClone : IRepoClone
    {
        public Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
            IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
        {
            foreach (var (_, path) in repos) Directory.CreateDirectory(Path.Combine(path, ".git"));

            return Task.FromResult<IReadOnlyList<RepoCloneOutcome>>(
                [.. repos.Select(r => new RepoCloneOutcome(r.Url, r.Path, RepoCloneResult.Cloned, DefaultBranch: "main"))]);
        }
    }

    /// <summary>The real store, with every call recorded by name before it runs, into the recorder of
    /// the async flow that makes it, if that flow has one - as <c>BoardOutcomeQueryTests</c> does.</summary>
    public class Recorded<T> : DispatchProxy where T : class
    {
        private T _real = null!;
        private AsyncLocal<ConcurrentQueue<string>?> _recording = null!;

        public static T Around(T real, AsyncLocal<ConcurrentQueue<string>?> recording)
        {
            var proxy = Create<T, Recorded<T>>();
            var recorded = (Recorded<T>)(object)proxy;
            recorded._real = real;
            recorded._recording = recording;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            _recording.Value?.Enqueue((typeof(T) == typeof(IMessageLog) ? "log:" : "backlog:") + method.Name);
            try { return method.Invoke(_real, args); }
            catch (TargetInvocationException thrown) when (thrown.InnerException is not null)
            {
                throw thrown.InnerException;
            }
        }
    }

    /// <summary>A run that never ends on its own: nothing here should run, and nothing does.</summary>
    private sealed class HeldAgent : IAgentRunner
    {
        public async Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentResult(0, "done");
        }
    }
}
