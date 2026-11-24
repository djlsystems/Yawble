using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Bus;
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
/// Card 360 (verify B066, cards 358 + 359): gaps the developers' ConciergeHostTaskTests leave. host_task_list's approval and
/// state lines as they change (Always, a changed script, a waiting request, the last run); the real Read-Host / PromptForChoice /
/// ReadKey / pause end at once with stdin closed; the Update goes only to the chat that asked; a timed-out, a stopped and a
/// refused-at-start run end as failed / blocked Updates; host_task_status by team and task while it runs; Save of a
/// description-only edit removes Always as docs/host-tasks-release.md says; where the Concierge tells the person tasks are
/// defined.
/// </summary>
public sealed class HostTaskGapTests : IAsyncLifetime
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

    private ConciergeTools Tools(string conversation = Conversation)
    {
        var turn = new ConciergeTurn(conversation, true, false);
        var empty = new Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        var actions = new ConciergeActions(_bus, empty, empty, new ConciergeGit(_f.Git, new PersonGitPush()),
            (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([]), new ConfirmationBook(), turn: turn) { HostTasks = _hostTasks };
        return new ConciergeTools(_bus, _store, empty, () => null, conversation, null, actions, false, turn, null);
    }

    private async Task<string> Call(string tool, JsonObject args, string conversation = Conversation)
    {
        var (text, isError) = await Tools(conversation).CallToolAsync(tool, args, Ct);
        Assert.False(isError, text);
        return text;
    }

    private static JsonObject RunArgs() => new() { ["team"] = "Alpha", ["task"] = "deploy", ["reason"] = "the person asked to deploy" };
    private static JsonObject ListArgs() => new() { ["team"] = "Alpha" };

    private Task<HostTask> SaveDeploy(string? script = null, int timeout = 60, string description = "Deploys the cloud") =>
        _f.SaveAsync(_f.Def("deploy", script: script, timeout: timeout) with { Description = description, CheckAfter = "https://cloud.example.test/health" });

    private TaskWake LastWake()
    {
        lock (_f.Wakes) return _f.Wakes[^1];
    }

    private async Task<TaskRequestResult> Approve(bool always = false)
    {
        var ask = Assert.Single(_f.Svc.Store.PendingAsks(_f.TeamId));
        using (Actor.Use(Actor.Window)) return await _f.Svc.ApproveAsync(ask.Id, always, Ct);
    }

    private IReadOnlyList<ConciergeMessageRow> Updates(string conversation = Conversation) =>
        _store.Messages(conversation).Where(m => m.Role == "update").ToList();

    private string Script(string name, string ps, string sh)
    {
        _f.Write($"scripts/{name}.ps1", ps);
        _f.Write($"scripts/{name}.sh", sh);
        _f.Commit(name);
        return TaskFixture.IsWin ? $"scripts/{name}.ps1" : $"scripts/{name}.sh";
    }

    // ------------------------------------------------------------------ point 1: host_task_list's approval and state lines

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

        _f.Write(_f.ScriptPath, TaskFixture.IsWin ? "Write-Output 'changed'" : "echo changed");
        _f.Commit("script changed");
        Assert.Contains("approval: asks (the standing approval no longer matches the code)", await Call(ConciergeHostTasks.ListTool, ListArgs()));
        Assert.StartsWith("asked:", await Call(ConciergeHostTasks.RunTool, RunArgs()));
    }

    /// <summary>docs/host-tasks-release.md: "Editing the task (pressing Save on it) always removes an Always approval", even when only the description changed.</summary>
    [Fact]
    public async Task SavingADescriptionOnlyEdit_RemovesAlways_AsTheDocSays()
    {
        await SaveDeploy();
        await Call(ConciergeHostTasks.RunTool, RunArgs());
        await _f.Finish(await Approve(always: true));
        Assert.Contains("always (standing approval)", await Call(ConciergeHostTasks.ListTool, ListArgs()));

        await SaveDeploy(description: "Deploys the cloud (reworded)");
        Assert.Contains("approval: asks the person each time", await Call(ConciergeHostTasks.ListTool, ListArgs()));
        Assert.StartsWith("asked:", await Call(ConciergeHostTasks.RunTool, RunArgs()));
    }

    // ------------------------------------------------------------------ point 2: status by team and task while it runs

    [Fact]
    public async Task Status_ByTeamAndTask_ShowsTheLiveTailWhileRunning_ThenTheExitCodeDurationCommitAndCheckAfter()
    {
        var flag = Path.Combine(_f.Db.Dir, "go.flag");
        var script = Script("wait",
            $"Write-Output 'step one'; while (-not (Test-Path '{flag}')) {{ Start-Sleep -Milliseconds 100 }}; Write-Output 'step two'; exit 2",
            $"echo 'step one'; while [ ! -e '{flag}' ]; do sleep 0.1; done; echo 'step two'; exit 2");
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

    // ------------------------------------------------------------------ point 4: Updates for every way a run ends

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
        var script = Script("hang", "Write-Output 'started'; Start-Sleep -Seconds 120", "echo started; sleep 120");
        await SaveDeploy(script, timeout: 10);
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
        var script = Script("long", "Write-Output 'started'; Start-Sleep -Seconds 120", "echo started; sleep 120");
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

    // ------------------------------------------------------------------ point 5: real prompts with stdin closed

    [Theory]
    [InlineData("read-host", "$a = Read-Host 'Are you sure? [y/N]'; if ($a -ne 'y') { Write-Output 'not confirmed'; exit 6 }; Write-Output 'confirmed'")]
    [InlineData("prompt-for-choice", "$c = $Host.UI.PromptForChoice('Deploy', 'Are you sure?', @('&Yes', '&No'), 1); Write-Output \"choice [$c]\"; exit 6")]
    [InlineData("read-key", "[Console]::ReadKey() | Out-Null; Write-Output 'after the key'; exit 6")]
    [InlineData("pause", "pause; Write-Output 'after pause'; exit 6")]
    public async Task AWindowsPowerShellPrompt_EndsAtOnce_NotAtTheTimeLimit(string name, string ps)
    {
        Assert.SkipUnless(TaskFixture.IsWin, "Windows PowerShell prompts");
        var script = Script(name, ps, "exit 6");
        await SaveDeploy(script, timeout: 120);
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
