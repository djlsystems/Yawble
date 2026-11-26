using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Provenance;
using Yawble.Core.Tasks;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Tests.Tasks;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The Concierge (and the companion) list, run and check a team's host tasks. A run is asked for exactly as an agent's
/// (the person's Run once / Always, pinned to the files); it is refused on review turns and over the Cloud Link like
/// deliverable_close; its end is one Update in the chat it was asked from, whichever way the run ends (exit code, time
/// limit, stopped, refused at the start). host_task_list's approval and state lines follow Always, a changed script,
/// a waiting request and the last run; a task that waits for input ends at once, since its input is closed.
/// </summary>
[Trait("Speed", "Slow")]
public sealed class ConciergeHostTaskTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TaskFixture _f = new();
    private BusEngine _bus = null!;
    private ConciergeStore _store = null!;
    private ConciergeService _service = null!;
    private ConciergeHostTasks _hostTasks = null!;
    private const string Conversation = "conv-1", Other = "conv-2";

    public async ValueTask InitializeAsync()
    {
        _bus = new BusEngine(_f.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await _bus.StartAsync(Ct);
        _store = new ConciergeStore(_f.Db.ConnectionString);
        _store.Create(Conversation, "Release", "claude", _f.TeamId);
        _store.Create(Other, "Something else", "claude", _f.TeamId);
        _service = new ConciergeService(_store, _bus, (_, _) => throw new InvalidOperationException("no turn runs here"),
            new Yawble.Core.Launch.RunRegistry(), new AppEvents(), (_, _, _) => Task.FromResult<JsonNode?>(null), _ => Task.FromResult("claude"));
        _hostTasks = new ConciergeHostTasks(new HostTaskRpc(_f.Svc, _ => ["main"]));
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _f.Dispose();
    }

    private ConciergeTools Tools(bool byPerson = true, bool remote = false, Actor? actingFor = null, string conversation = Conversation)
    {
        var turn = new ConciergeTurn(conversation, byPerson, remote);
        var empty = new Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        var actions = new ConciergeActions(_bus, empty, empty, new ConciergeGit(_f.Git, new PersonGitPush()),
            (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([]), new ConfirmationBook(), turn: turn) { HostTasks = _hostTasks };
        return new ConciergeTools(_bus, _store, empty, () => null, conversation, null, actions, remote, turn, actingFor);
    }

    private async Task<string> Call(ConciergeTools tools, string tool, JsonObject args) => (await tools.CallToolAsync(tool, args, Ct)).Text;

    /// <summary>A call by the person in a chat that must not be an error.</summary>
    private async Task<string> Call(string tool, JsonObject args, string conversation = Conversation)
    {
        var (text, isError) = await Tools(conversation: conversation).CallToolAsync(tool, args, Ct);
        Assert.False(isError, text);
        return text;
    }

    private static JsonObject RunArgs(string task = "deploy") => new() { ["team"] = "Alpha", ["task"] = task, ["reason"] = "the person asked to deploy" };
    private static JsonObject ListArgs() => new() { ["team"] = "Alpha" };

    private Task<HostTask> SaveDeploy(string? script = null, int timeout = 60, string description = "Builds the remote-UI image and deploys the cloud service") =>
        _f.SaveAsync(_f.Def("deploy", script: script, timeout: timeout) with { Description = description, CheckAfter = "https://cloud.example.test/health" });

    /// <summary>Commits a file of stand-in steps (<see cref="StandInTask"/>) and gives its path in the checkout.</summary>
    private string Script(string name, params string[] steps)
    {
        _f.Write($"scripts/{name}.task", string.Join('\n', steps));
        _f.Commit(name);
        return $"scripts/{name}.task";
    }

    private TaskWake LastWake()
    {
        lock (_f.Wakes) return _f.Wakes[^1];
    }

    private async Task<TaskRequestResult> Approve(bool always = false)
    {
        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        using (Actor.Use(Actor.Window)) return await _f.Svc.ApproveAsync(ask.Id, always, Ct);
    }

    private async Task<TaskRunRow> ApproveAndFinish(bool always = false)
    {
        var started = await Approve(always);
        Assert.Equal(RequestKind.Started, started.Kind);
        return await _f.Finish(started);
    }

    private IReadOnlyList<ConciergeMessageRow> Updates(string conversation = Conversation) =>
        _store.Messages(conversation).Where(m => m.Role == "update").ToList();

    // ------------------------------------------------------------------ list

    [Fact]
    public async Task List_ShowsEachTasksDescriptionProgramCheckAfterAndApproval()
    {
        await SaveDeploy();
        await _f.SaveAsync(_f.Def("e2e"));
        var text = await Call(Tools(), ConciergeHostTasks.ListTool, new JsonObject { ["team"] = "Alpha" });
        Assert.Contains("- deploy: Builds the remote-UI image and deploys the cloud service", text);
        Assert.Contains("check after: https://cloud.example.test/health", text);
        Assert.Contains("- e2e: (no description)", text);
        Assert.Contains("approval: asks the person each time", text);
        Assert.Contains(_f.ScriptPath, text);
        Assert.Contains("repo main", text);
    }

    [Fact]
    public async Task List_AndStatus_AreReads_AllowedOnReviewTurnsAndRemote()
    {
        await SaveDeploy();
        foreach (var tools in new[] { Tools(byPerson: false), Tools(remote: true) })
        {
            var (text, isError) = await tools.CallToolAsync(ConciergeHostTasks.ListTool, new JsonObject { ["team"] = "Alpha" }, Ct);
            Assert.False(isError, text);
            Assert.Contains("deploy", text);
            (text, isError) = await tools.CallToolAsync(ConciergeHostTasks.StatusTool, new JsonObject { ["team"] = "Alpha", ["task"] = "deploy" }, Ct);
            Assert.False(isError, text);
            Assert.Contains("has not run yet", text);
        }
    }

    [Fact]
    public async Task List_SaysSoWhenTheTeamHasNoTasks()
    {
        var text = await Call(Tools(), ConciergeHostTasks.ListTool, new JsonObject { ["team"] = "Alpha" });
        Assert.Contains("Alpha has no host tasks", text);
        Assert.Contains("write a script", text);
    }

    // ------------------------------------------------------------------ run: the same approval as an agent's

    [Fact]
    public async Task Run_AsksThePersonLikeAnAgentsRequest_AndTheApprovedRunEndsAsAnUpdateInTheChat()
    {
        await SaveDeploy();
        var text = await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        Assert.StartsWith("asked: task deploy of Alpha waits for the person's Run once / Always", text);
        Assert.Contains("posts an Update in this chat", text);
        Assert.Contains("Check after: https://cloud.example.test/health", text);

        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        Assert.Equal($"{ConciergeService.Team}/{Conversation}", ask.Member);
        Assert.Equal("the person asked to deploy", ask.Reason);

        var run = await ApproveAndFinish();
        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"{ConciergeService.Team}/{Conversation}", run.RequestedBy);

        var wake = LastWake();
        Assert.NotNull(wake.Run);
        Assert.True(_service.TryPostTaskUpdate(wake, "Alpha"));
        var update = Assert.Single(Updates());
        Assert.Equal("done", update.Kind);
        Assert.Equal("Alpha", update.Team);
        Assert.StartsWith("**Alpha** · task deploy: exit 0 in ", update.Text);
        Assert.Contains("hello", update.Text);                                   // the log tail
        Assert.Contains("Check after: https://cloud.example.test/health", update.Text);
        Assert.Contains("Full log: ", update.Text);
        Assert.Equal(1, _store.Get(Conversation)!.Unread);
    }

    [Fact]
    public async Task Run_ANonZeroExit_IsAFailedUpdate_WithTheExitCodeAndTail()
    {
        _f.Write("scripts/fail.task", "print deploy broke\nexit 4");
        _f.Commit("fail");
        await SaveDeploy("scripts/fail.task");
        await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        var run = await ApproveAndFinish();
        Assert.Equal(4, run.ExitCode);

        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        var update = Assert.Single(Updates());
        Assert.Equal("failed", update.Kind);
        Assert.Contains("exit 4", update.Text);
        Assert.Contains("deploy broke", update.Text);
    }

    [Fact]
    public async Task Run_Declined_IsABlockedUpdate_AndNothingRan()
    {
        await SaveDeploy();
        await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        using (Actor.Use(Actor.Window)) await _f.Svc.DeclineAsync(ask.Id, "not today", Ct);
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        var update = Assert.Single(Updates());
        Assert.Equal("blocked", update.Kind);
        Assert.Contains("declined", update.Text);
        Assert.Contains("not today", update.Text);
        Assert.Empty(_f.Svc.Store.RecentRuns(_f.TeamId, 10));
    }

    [Fact]
    public async Task Run_AlwaysIsPinnedToTheFiles_AsForAnAgent()
    {
        await SaveDeploy();
        await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        await ApproveAndFinish(always: true);

        // The standing approval covers the next request: it starts at once.
        var text = await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        Assert.StartsWith("started: run ", text);
        Assert.DoesNotContain("You will be woken", text);
        var runId = long.Parse(System.Text.RegularExpressions.Regex.Match(text, @"run (\d+)").Groups[1].Value);
        await _f.Svc.WaitForRunAsync(runId);

        // A pinned file changes: the approval no longer covers it, so it asks again.
        _f.Write(_f.ScriptPath, "print changed");
        _f.Commit("script changed");
        text = await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        Assert.StartsWith("asked:", text);
    }

    [Fact]
    public async Task Run_IsRefusedOnAReviewTurn_AndOverTheCloudLink_LikeDeliverableClose()
    {
        await SaveDeploy();
        var (review, reviewError) = await Tools(byPerson: false).CallToolAsync(ConciergeHostTasks.RunTool, RunArgs(), Ct);
        Assert.True(reviewError);
        Assert.Equal(ConciergeActions.ReviewRefusal(ConciergeHostTasks.RunTool), review);
        var (remote, remoteError) = await Tools(remote: true).CallToolAsync(ConciergeHostTasks.RunTool, RunArgs(), Ct);
        Assert.True(remoteError);
        Assert.Equal(ConciergeActions.RemoteRefusal(ConciergeHostTasks.RunTool), remote);
        // deliverable_close is refused the same way, by the same lists
        Assert.Contains("deliverable_close", ConciergeTools.Acting);
        Assert.Contains(ConciergeHostTasks.RunTool, ConciergeTools.Acting);
        Assert.Contains("deliverable_close", ConciergeTools.LocalOnly);
        Assert.Contains(ConciergeHostTasks.RunTool, ConciergeTools.LocalOnly);

        Assert.Empty(_f.Svc.Store.PendingAsks(_f.TeamId));
        Assert.Empty(_f.Svc.Store.RecentRuns(_f.TeamId, 10));
    }

    [Fact]
    public async Task Run_TakesNoArgumentsOrEnvironment()
    {
        await SaveDeploy();
        var args = RunArgs();
        args["args"] = "--force";
        var (text, isError) = await Tools().CallToolAsync(ConciergeHostTasks.RunTool, args, Ct);
        Assert.True(isError);
        Assert.Contains("no host task declares any", text);
        args = RunArgs();
        args["env"] = "X=1";
        (text, isError) = await Tools().CallToolAsync(ConciergeHostTasks.RunTool, args, Ct);
        Assert.True(isError);
        Assert.Contains("takes only 'team', 'task' and 'reason'", text);
        Assert.Empty(_f.Svc.Store.PendingAsks(_f.TeamId));
    }

    [Fact]
    public async Task Run_AnUnknownTask_IsRefusedWithTheTeamsTasks()
    {
        await SaveDeploy();
        var text = await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs("release"));
        Assert.StartsWith("refused: there is no task named 'release'", text);
        Assert.Contains("deploy", text);
    }

    // ------------------------------------------------------------------ status

    [Fact]
    public async Task Status_ShowsTheRunsStateExitCodeTailAndCheckAfter()
    {
        await SaveDeploy();
        await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        Assert.Contains("waiting for the person to approve request",
            await Call(Tools(), ConciergeHostTasks.StatusTool, new JsonObject { ["team"] = "Alpha", ["task"] = "deploy" }));
        var run = await ApproveAndFinish();

        var text = await Call(Tools(), ConciergeHostTasks.StatusTool, new JsonObject { ["run"] = run.Id });
        Assert.StartsWith($"run {run.Id} of task deploy (Alpha): exit 0", text);
        Assert.Contains("hello", text);
        Assert.Contains("Check after: https://cloud.example.test/health", text);
        Assert.Equal(text, await Call(Tools(), ConciergeHostTasks.StatusTool, new JsonObject { ["team"] = "Alpha", ["task"] = "deploy" }));
        Assert.Contains("there is no task run 999", await Call(Tools(), ConciergeHostTasks.StatusTool, new JsonObject { ["run"] = 999 }));
    }

    [Fact]
    public async Task Status_ShowsTheLiveLogWhileTheRunIsUnderWay()
    {
        _f.Write("scripts/slow.task", "print step one\nwait 20\nprint step two");
        _f.Commit("slow");
        await SaveDeploy("scripts/slow.task");
        await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        TaskRequestResult started;
        using (Actor.Use(Actor.Window)) started = await _f.Svc.ApproveAsync(ask.Id, false, Ct);
        var runId = started.RunId!.Value;
        try
        {
            string text = "";
            await Wait.UntilAsync(async () => (text = await Call(Tools(), ConciergeHostTasks.StatusTool, new JsonObject { ["run"] = runId })).Contains("step one"),
                "the live tail");
            Assert.Contains(": running;", text);
            Assert.Contains("live", text);
            Assert.DoesNotContain("Check after", text);
            Assert.Empty(Updates()); // no progress Updates while it runs
        }
        finally
        {
            _f.Svc.Cancel(runId);
            await _f.Svc.WaitForRunAsync(runId);
        }
    }

    // ------------------------------------------------------------------ fail fast on input

    [Fact]
    public async Task AScriptThatPromptsGetsEndOfInput_AndEndsAtOnce()
    {
        _f.Write("scripts/ask.task", "read-line 3");
        _f.Commit("prompting script");
        await SaveDeploy("scripts/ask.task");
        await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        var watch = Stopwatch.StartNew();
        var run = await ApproveAndFinish();
        watch.Stop();
        Assert.Equal(3, run.ExitCode);
        Assert.False(run.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), $"took {watch.Elapsed}");
        Assert.Contains("no input", LastWake().Tail);
    }

    // ------------------------------------------------------------------ who is told

    [Fact]
    public void AnAgentsRunIsNotTheConciergesToPost()
    {
        var wake = new TaskWake(_f.TeamId, "Alpha/Ada", 3, 7, "task build: exit 0 in 1s at abc");
        Assert.False(_service.TryPostTaskUpdate(wake, "Alpha"));
        Assert.Empty(Updates());
    }

    [Fact]
    public async Task ACompanionClientsRun_IsRecordedAsTheClient_AndPostsNoUpdate()
    {
        await SaveDeploy();
        var text = await Call(Tools(conversation: "external:claude-code:r1", actingFor: Actor.ExternalClient("claude-code")), ConciergeHostTasks.RunTool, RunArgs());
        Assert.Contains("host_task_status", text);
        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        Assert.Equal("_companion/claude-code", ask.Member);
        using (Actor.Use(Actor.Window)) await _f.Svc.DeclineAsync(ask.Id, null, Ct);
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        Assert.Empty(Updates());
    }

    [Fact]
    public void TheCompanionOffersListAndStatusAsReads_AndRunAsDestructive()
    {
        Assert.Equal(CompanionLevel.Read, CompanionTools.LevelOf(ConciergeHostTasks.ListTool, []));
        Assert.Equal(CompanionLevel.Read, CompanionTools.LevelOf(ConciergeHostTasks.StatusTool, []));
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf(ConciergeHostTasks.RunTool, []));
        var act = CompanionTools.Definitions(CompanionLevel.Act).OfType<JsonObject>().Select(d => d["name"]!.GetValue<string>()).ToList();
        Assert.Contains(ConciergeHostTasks.ListTool, act);
        Assert.DoesNotContain(ConciergeHostTasks.RunTool, act);
        var run = CompanionTools.Definitions(CompanionLevel.Destructive).OfType<JsonObject>().Single(d => d["name"]!.GetValue<string>() == ConciergeHostTasks.RunTool);
        var description = run["description"]!.GetValue<string>();
        Assert.DoesNotContain("this chat", description);
        Assert.Contains("host_task_status", description);
        Assert.Contains("approval in the Yawble window", description);
        Assert.Contains("Ask to run host task deploy of team Alpha", CompanionTools.Describe(ConciergeHostTasks.RunTool, RunArgs()));
    }

    [Fact]
    public void TheConciergeOffersTheThreeTools_AndThePromptHasTheReleaseRecipe()
    {
        var names = ConciergeTools.Definitions(true).OfType<JsonObject>().Select(d => d["name"]!.GetValue<string>()).ToList();
        foreach (var n in ConciergeHostTasks.Names) Assert.Contains(n, names);
        var run = ConciergeTools.Definitions(true).OfType<JsonObject>().Single(d => d["name"]!.GetValue<string>() == ConciergeHostTasks.RunTool);
        var props = run["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).Order().ToList();
        Assert.Equal(["reason", "task", "team"], props);
        Assert.Contains("Release / deploy", ConciergePrompt.Role);
        Assert.Contains("Never run a release or deploy task unless the person asked", ConciergePrompt.Role);
        Assert.Contains("If no task fits, say so", ConciergePrompt.Role);
    }

    // ------------------------------------------------------------------ description and checkAfter

    [Fact]
    public async Task TheTaskRpcs_SaveAndReturnDescriptionAndCheckAfter_WhichAreNotPinned()
    {
        var rpc = new HostTaskRpc(_f.Svc, _ => ["main"]);
        var def = _f.Def("deploy");
        var json = new JsonObject
        {
            ["name"] = def.Name, ["program"] = def.Program, ["args"] = new JsonArray(def.Args.Select(a => (JsonNode?)a).ToArray()),
            ["description"] = "  Deploys the service  ", ["checkAfter"] = "https://x.example.test/health",
        };
        using (Actor.Use(Actor.Window))
        {
            await rpc.DispatchAsync("tasks.save", new JsonObject { ["teamId"] = _f.TeamId, ["def"] = json.DeepClone() }, Ct);
            var listed = (await rpc.DispatchAsync("tasks.list", new JsonObject { ["teamId"] = _f.TeamId }, Ct))!["tasks"]![0]!["def"]!;
            Assert.Equal("Deploys the service", listed["description"]!.GetValue<string>());
            Assert.Equal("https://x.example.test/health", listed["checkAfter"]!.GetValue<string>());

            json["description"] = "   ";
            json.Remove("checkAfter");
            await rpc.DispatchAsync("tasks.save", new JsonObject { ["teamId"] = _f.TeamId, ["def"] = json.DeepClone() }, Ct);
            listed = (await rpc.DispatchAsync("tasks.list", new JsonObject { ["teamId"] = _f.TeamId }, Ct))!["tasks"]![0]!["def"]!;
            Assert.Null(listed["description"]);
            Assert.Null(listed["checkAfter"]);

            json["description"] = new string('x', HostTaskRules.MaxTextLength + 1);
            var e = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => rpc.DispatchAsync("tasks.save", new JsonObject { ["teamId"] = _f.TeamId, ["def"] = json.DeepClone() }, Ct));
            Assert.Contains("description", e.Message);
        }

        // Neither is part of what an approval is pinned to.
        var p = await _f.Svc.PreviewAsync(_f.TeamId, def, Ct);
        Assert.Equal(TaskPinning.Definition(_f.TeamId, def, p.ProgramResolved!).Hash,
            TaskPinning.Definition(_f.TeamId, def with { Description = "d", CheckAfter = "c" }, p.ProgramResolved!).Hash);
    }

    [Fact]
    public async Task AnAgentsWake_NamesTheCheckAfter()
    {
        await SaveDeploy();
        var run = await _f.Finish(await RequestAndApprove());
        Assert.Equal(0, run.ExitCode);
        var wake = LastWake();
        Assert.Equal("Alpha/Ada", wake.Member);
        Assert.Contains("Check after (the person's note on how to check the result): https://cloud.example.test/health", wake.Text);

        async Task<TaskRequestResult> RequestAndApprove()
        {
            var asked = await _f.Request("deploy");
            Assert.Equal(RequestKind.Asked, asked.Kind);
            using (Actor.Use(Actor.Window)) return await _f.Svc.ApproveAsync(asked.AskId!.Value, false, Ct);
        }
    }

    // ------------------------------------------------------------------ list: the approval and state lines as they change

    [Fact]
    public async Task List_ApprovalAndStateFollowAlways_AChangedScript_AWaitingRequestAndTheLastRun()
    {
        await SaveDeploy();
        Assert.Contains("approval: asks the person each time; state: idle", await Call(ConciergeHostTasks.ListTool, ListArgs()));

        await Call(ConciergeHostTasks.RunTool, RunArgs());
        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        Assert.Contains($"state: waiting for the person (request {ask.Id})", await Call(ConciergeHostTasks.ListTool, ListArgs()));

        var run = await _f.Finish(await Approve(always: true));
        var text = await Call(ConciergeHostTasks.ListTool, ListArgs());
        Assert.Contains("approval: always (standing approval); state: idle", text);
        Assert.Contains($"last run {run.Id}: exit 0", text);

        _f.Write(_f.ScriptPath, "print changed");
        _f.Commit("script changed");
        Assert.Contains("approval: asks (the standing approval no longer matches the code)", await Call(ConciergeHostTasks.ListTool, ListArgs()));
        Assert.StartsWith("asked:", await Call(ConciergeHostTasks.RunTool, RunArgs()));
    }

    /// <summary>Saving a task removes its Always approval even when only the description changed (docs/host-tasks-release.md).</summary>
    [Fact]
    public async Task SavingADescriptionOnlyEdit_RemovesAlways()
    {
        await SaveDeploy();
        await Call(ConciergeHostTasks.RunTool, RunArgs());
        await _f.Finish(await Approve(always: true));
        Assert.Contains("always (standing approval)", await Call(ConciergeHostTasks.ListTool, ListArgs()));

        await SaveDeploy(description: "Deploys the cloud (reworded)");
        Assert.Contains("approval: asks the person each time", await Call(ConciergeHostTasks.ListTool, ListArgs()));
        Assert.StartsWith("asked:", await Call(ConciergeHostTasks.RunTool, RunArgs()));
    }

    // ------------------------------------------------------------------ status by team and task while it runs

    [Fact]
    public async Task Status_ByTeamAndTask_ShowsTheLiveTailWhileRunning_ThenTheExitCodeDurationCommitAndCheckAfter()
    {
        var flag = Path.Combine(_f.Db.Dir, "go.flag");
        var script = Script("wait", "print step one", $"wait-for {flag} 60", "print step two", "exit 2");
        await SaveDeploy(script);
        await Call(ConciergeHostTasks.RunTool, RunArgs());
        var started = await Approve();
        var byTask = new JsonObject { ["team"] = "alpha", ["task"] = "deploy" }; // the team name in any case
        string text = "";
        try
        {
            await Wait.UntilAsync(async () => (text = await Call(ConciergeHostTasks.StatusTool, byTask.DeepClone().AsObject())).Contains("step one"), "the live tail by team and task");
            Assert.StartsWith($"run {started.RunId} of task deploy (Alpha): running", text);
            Assert.Contains("live", text);
            Assert.DoesNotContain("step two", text);
            Assert.Contains("Full log: ", text); // the live log is named while it runs (registered before the start)
        }
        finally
        {
            File.WriteAllText(flag, "");
            await _f.Svc.WaitForRunAsync(started.RunId!.Value);
        }

        text = await Call(ConciergeHostTasks.StatusTool, byTask.DeepClone().AsObject());
        Assert.StartsWith($"run {started.RunId} of task deploy (Alpha): exit 2", text);
        Assert.Matches(@"ended [^;\n]+ \(\d+s\)", text);
        Assert.Contains($"; at {TaskPinning.Short(_f.G("rev-parse", "HEAD").Trim())}", text);
        Assert.Contains("step two", text);
        Assert.DoesNotContain("live,", text);
        Assert.Contains("Check after: https://cloud.example.test/health", text);
    }

    // ------------------------------------------------------------------ Updates for every way a run ends

    [Fact]
    public async Task TheUpdate_GoesOnlyToTheChatThatAsked()
    {
        await SaveDeploy();
        await Call(ConciergeHostTasks.RunTool, RunArgs(), Other);
        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        Assert.Equal($"{ConciergeService.Team}/{Other}", ask.Member);
        await _f.Finish(await Approve());
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        Assert.Equal("done", Assert.Single(Updates(Other)).Kind);
        Assert.Empty(Updates(Conversation));
    }

    [Fact]
    public async Task TheUpdate_ForADeletedChat_IsDropped_AndTheAgentIsNotWokenInstead()
    {
        await SaveDeploy();
        await Call(ConciergeHostTasks.RunTool, RunArgs(), Other);
        await _f.Finish(await Approve());
        _store.Delete(Other);
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha")); // true: handled, the app must not PersonTell "_concierge"
        Assert.Empty(Updates(Conversation));
    }

    [Fact]
    public async Task ATimedOutRun_IsAFailedUpdate()
    {
        var script = Script("hang", "print started", "wait 60");
        await SaveDeploy(script, timeout: 10);
        _f.Svc.SecondLength = TimeSpan.FromMilliseconds(300); // the 10 s limit runs out after 3 real seconds (the task is up and has said "started")
        await Call(ConciergeHostTasks.RunTool, RunArgs());
        var run = await _f.Finish(await Approve());
        Assert.True(run.TimedOut);
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        var update = Assert.Single(Updates());
        Assert.Equal("failed", update.Kind);
        Assert.Contains("started", update.Text);
        Assert.Contains("Check after:", update.Text);
        Assert.Contains("timed out", await Call(ConciergeHostTasks.StatusTool, new JsonObject { ["run"] = run.Id }));
    }

    [Fact]
    public async Task ARunThePersonStopped_IsAFailedUpdate()
    {
        var script = Script("long", "print started", "wait 60");
        await SaveDeploy(script);
        await Call(ConciergeHostTasks.RunTool, RunArgs());
        var started = await Approve();
        await Wait.UntilAsync(async () => (await Call(ConciergeHostTasks.StatusTool, new JsonObject { ["run"] = started.RunId })).Contains("started"), "the run to start");
        Assert.True(_f.Svc.Cancel(started.RunId!.Value));
        await _f.Svc.WaitForRunAsync(started.RunId.Value);
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        Assert.Equal("failed", Assert.Single(Updates()).Kind);
        Assert.Contains("stopped by the person", await Call(ConciergeHostTasks.StatusTool, new JsonObject { ["run"] = started.RunId }));
    }

    [Fact]
    public async Task ARunThatCannotStart_ADirtyCheckout_IsABlockedUpdate_AndNothingRan()
    {
        await SaveDeploy();
        await Call(ConciergeHostTasks.RunTool, RunArgs());
        _f.Write("untracked.txt", "the person left a file here");
        var result = await Approve();
        Assert.NotEqual(RequestKind.Started, result.Kind);
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        var update = Assert.Single(Updates());
        Assert.Equal("blocked", update.Kind);
        Assert.Contains("nothing ran", update.Text);
    }

    // ------------------------------------------------------------------ prompts with the input closed

    /// <summary>A task's input is closed, so a program that waits for a key gets the end of input at once, and its non-zero exit is a failed Update.</summary>
    [Fact]
    public async Task ATaskThatWaitsForAKey_EndsAtOnce_NotAtTheTimeLimit_AndIsAFailedUpdate()
    {
        var script = Script("read-key", "read-key 6", "print after the input");
        await SaveDeploy(script, timeout: 120);
        await Call(ConciergeHostTasks.RunTool, RunArgs());
        var watch = Stopwatch.StartNew();
        var run = await _f.Finish(await Approve());
        watch.Stop();
        Assert.False(run.TimedOut);
        Assert.Equal(6, run.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), $"took {watch.Elapsed}");
        Assert.DoesNotContain("after the input", LastWake().Tail);
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        Assert.Equal("failed", Assert.Single(Updates()).Kind);
    }

    /// <summary>
    /// Windows PowerShell's own prompts (Read-Host, PromptForChoice, ReadKey, pause) with the input closed. This is
    /// the one test that starts PowerShell, since what it checks is PowerShell's behaviour; it runs only with
    /// YAWBLE_REAL_POWERSHELL=1. The script only prompts and prints.
    /// </summary>
    [Theory]
    [InlineData("read-host", "$a = Read-Host 'Are you sure? [y/N]'; if ($a -ne 'y') { Write-Output 'not confirmed'; exit 6 }; Write-Output 'confirmed'")]
    [InlineData("prompt-for-choice", "$c = $Host.UI.PromptForChoice('Deploy', 'Are you sure?', @('&Yes', '&No'), 1); Write-Output \"choice [$c]\"; exit 6")]
    [InlineData("read-key", "[Console]::ReadKey() | Out-Null; Write-Output 'after the key'; exit 6")]
    [InlineData("pause", "pause; Write-Output 'after pause'; exit 6")]
    public async Task AWindowsPowerShellPrompt_EndsAtOnce_NotAtTheTimeLimit(string name, string ps)
    {
        Assert.SkipUnless(TaskFixture.IsWin, "Windows PowerShell prompts");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("YAWBLE_REAL_POWERSHELL") == "1", "starts Windows PowerShell: set YAWBLE_REAL_POWERSHELL=1 to run it");
        _f.Write($"scripts/{name}.ps1", ps);
        _f.Commit(name);
        await _f.SaveAsync(HostTaskDef.Make("deploy", "powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $"scripts/{name}.ps1"], timeoutSeconds: 120)
            with { Description = "Deploys the cloud", CheckAfter = "https://cloud.example.test/health" });
        await Call(ConciergeHostTasks.RunTool, RunArgs());
        var watch = Stopwatch.StartNew();
        var run = await _f.Finish(await Approve());
        watch.Stop();
        Assert.False(run.TimedOut);
        Assert.Equal(6, run.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), $"took {watch.Elapsed}");
        Assert.True(_service.TryPostTaskUpdate(LastWake(), "Alpha"));
        Assert.Equal("failed", Assert.Single(Updates()).Kind);
    }

    // ------------------------------------------------------------------ where the person defines a task

    /// <summary>
    /// TasksPanel is the "Host tasks" section of Settings > Integrations, below the tabs (IntegrationsPanel.vue; docs/host-tasks-release.md).
    /// No team has a Tasks tab, so what the Concierge says must not send the person to one.
    /// </summary>
    [Fact]
    public async Task TheConcierge_DoesNotSendThePersonToATasksTabThatDoesNotExist()
    {
        var empty = await Call(ConciergeHostTasks.ListTool, ListArgs());
        Assert.Contains("Alpha has no host tasks", empty);
        var texts = new Dictionary<string, string> { ["host_task_list (no tasks)"] = empty, ["ConciergePrompt.Role"] = ConciergePrompt.Role };
        await SaveDeploy();
        var args = RunArgs();
        args["args"] = "--force";
        texts["host_task_run with args"] = (await Tools().CallToolAsync(ConciergeHostTasks.RunTool, args, Ct)).Text;
        var wrong = texts.Where(t => Regex.IsMatch(t.Value, @"Tasks tab")).Select(t => t.Key).ToList();
        Assert.True(wrong.Count == 0, "these say \"Tasks tab\" (the place is Settings > Integrations, Host tasks): " + string.Join(", ", wrong));
    }
}
