using System.Diagnostics;
using System.Text.Json.Nodes;
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
/// workflow_close; its end is one Update in the chat it was asked from.
/// </summary>
public sealed class ConciergeHostTaskTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TaskFixture _f = new();
    private BusEngine _bus = null!;
    private ConciergeStore _store = null!;
    private ConciergeService _service = null!;
    private ConciergeHostTasks _hostTasks = null!;
    private const string Conversation = "conv-1";

    public async ValueTask InitializeAsync()
    {
        _bus = new BusEngine(_f.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await _bus.StartAsync(Ct);
        _store = new ConciergeStore(_f.Db.ConnectionString);
        _store.Create(Conversation, "Release", "claude", _f.TeamId);
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

    private static JsonObject RunArgs(string task = "deploy") => new() { ["team"] = "Alpha", ["task"] = task, ["reason"] = "the person asked to deploy" };

    private Task<HostTask> SaveDeploy(string? script = null) => _f.SaveAsync(_f.Def("deploy", script: script) with
    {
        Description = "Builds the remote-UI image and deploys the cloud service", CheckAfter = "https://cloud.example.test/health",
    });

    private TaskWake LastWake()
    {
        lock (_f.Wakes) return _f.Wakes[^1];
    }

    private async Task<TaskRunRow> ApproveAndFinish(bool always = false)
    {
        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        TaskRequestResult started;
        using (Actor.Use(Actor.Window)) started = await _f.Svc.ApproveAsync(ask.Id, always, Ct);
        Assert.Equal(RequestKind.Started, started.Kind);
        return await _f.Finish(started);
    }

    private IReadOnlyList<ConciergeMessageRow> Updates() => _store.Messages(Conversation).Where(m => m.Role == "update").ToList();

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
        _f.Write("scripts/fail.ps1", "Write-Output 'deploy broke'; exit 4");
        _f.Write("scripts/fail.sh", "echo 'deploy broke'; exit 4");
        _f.Commit("fail");
        await SaveDeploy(TaskFixture.IsWin ? "scripts/fail.ps1" : "scripts/fail.sh");
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
        _f.Write(_f.ScriptPath, TaskFixture.IsWin ? "Write-Output 'changed'" : "echo changed");
        _f.Commit("script changed");
        text = await Call(Tools(), ConciergeHostTasks.RunTool, RunArgs());
        Assert.StartsWith("asked:", text);
    }

    [Fact]
    public async Task Run_IsRefusedOnAReviewTurn_AndOverTheCloudLink_LikeWorkflowClose()
    {
        await SaveDeploy();
        var (review, reviewError) = await Tools(byPerson: false).CallToolAsync(ConciergeHostTasks.RunTool, RunArgs(), Ct);
        Assert.True(reviewError);
        Assert.Equal(ConciergeActions.ReviewRefusal(ConciergeHostTasks.RunTool), review);
        var (remote, remoteError) = await Tools(remote: true).CallToolAsync(ConciergeHostTasks.RunTool, RunArgs(), Ct);
        Assert.True(remoteError);
        Assert.Equal(ConciergeActions.RemoteRefusal(ConciergeHostTasks.RunTool), remote);
        // workflow_close is refused the same way, by the same lists
        Assert.Contains("workflow_close", ConciergeTools.Acting);
        Assert.Contains(ConciergeHostTasks.RunTool, ConciergeTools.Acting);
        Assert.Contains("workflow_close", ConciergeTools.LocalOnly);
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
        _f.Write("scripts/slow.ps1", "Write-Output 'step one'; Start-Sleep -Seconds 20; Write-Output 'step two'");
        _f.Write("scripts/slow.sh", "echo 'step one'; sleep 20; echo 'step two'");
        _f.Commit("slow");
        await SaveDeploy(TaskFixture.IsWin ? "scripts/slow.ps1" : "scripts/slow.sh");
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
        _f.Write("scripts/ask.ps1", "$answer = [Console]::In.ReadLine(); if ($null -eq $answer) { Write-Output 'no input'; exit 3 }; Write-Output \"got $answer\"");
        _f.Write("scripts/ask.sh", "if read answer; then echo \"got $answer\"; else echo 'no input'; exit 3; fi");
        _f.Commit("prompting script");
        await SaveDeploy(TaskFixture.IsWin ? "scripts/ask.ps1" : "scripts/ask.sh");
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
}
