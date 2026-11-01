using System.Collections.Concurrent;
using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE AGENT CLIS ARE INSTALLED AND UPDATED AFTER THE WORKER CONNECTS, through the one update gate
/// (<see cref="AgentInstallsAtStart"/>). From the Host's start each installable command is held, so a run
/// queued for one waits rather than meeting a worker without it; when a worker joins each is measured and,
/// on its own and in parallel, installed when missing, updated when its interval has passed, or let go.
/// Every install and update here is a task the test completes through the real gate: nothing runs npm.
/// </summary>
public sealed class AgentInstallsAtStartTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly string[] Commands = ["claude", "codex", "copilot", "grok"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateTempSubdirectory("agent-installs-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A gate, and an install and an update per command that each wait for the test to let them finish.</summary>
    private sealed class Bed
    {
        public readonly AgentUpdateGate Gate = new();
        public readonly ConcurrentDictionary<string, bool?> Installed = new();
        public readonly ConcurrentDictionary<string, TaskCompletionSource> Started = new();
        public readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> Release = new();
        public readonly ConcurrentBag<string> Installs = [];
        public readonly ConcurrentBag<string> Updates = [];
        public readonly ConcurrentBag<bool> Reinstalls = [];
        public int Measured;

        public TaskCompletionSource StartedOf(string command) =>
            Started.GetOrAdd(command, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public TaskCompletionSource<bool> ReleaseOf(string command) =>
            Release.GetOrAdd(command, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        private bool Ask(string command, string kind, Func<AgentUpdateState, Task> finished) =>
            Gate.Request(command, command, null, async ct =>
            {
                StartedOf(command).TrySetResult();
                var ok = await ReleaseOf(command).Task.WaitAsync(ct);
                return new AgentUpdateResult(
                    command, command, ok, ok ? 0 : 1, null, ok ? "1.0" : null, DateTimeOffset.UtcNow,
                    ok ? $"{command} installed" : $"`npm install -g {command}` exited 1: npm ERR! network unreachable");
            }, finished, kind).Started;

        public AgentInstallsAtStart Over(string root, bool updates = true, bool reinstall = false, TimeSpan? interval = null, bool onThisMachine = false) =>
            new(
                Gate,
                () => [.. Commands.Select(c => new AgentInstallsAtStart.Command(c, c))],
                (command, _) =>
                {
                    Interlocked.Increment(ref Measured);
                    return Task.FromResult(Installed.GetValueOrDefault(command, false));
                },
                (agent, again, finished) =>
                {
                    Installs.Add(agent);
                    Reinstalls.Add(again);
                    return Ask(agent, AgentUpdateKinds.Install, finished);
                },
                updates ? (agent, finished) =>
                {
                    Updates.Add(agent);
                    return Ask(agent, AgentUpdateKinds.Update, finished);
                } : null,
                () => interval ?? TimeSpan.FromDays(1),
                reinstall,
                root,
                onThisMachine: onThisMachine);
    }

    private void CheckedAgo(TimeSpan ago, params string[] commands) =>
        AgentInstallsAtStart.WriteChecks(_root, commands.ToDictionary(c => c, _ => DateTimeOffset.UtcNow - ago));

    private static async Task<bool> EntersSoon(Task<IDisposable> run)
    {
        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMilliseconds(300), Ct));
        if (done != run) return false;
        (await run).Dispose();
        return true;
    }

    [Fact]
    public async Task A_restart_with_every_cli_installed_inside_the_window_measures_in_seconds_and_runs_no_install_or_update()
    {
        var bed = new Bed();
        foreach (var command in Commands) bed.Installed[command] = true;
        CheckedAgo(TimeSpan.FromHours(3), Commands);
        using var installs = bed.Over(_root);

        installs.Hold();
        var queued = bed.Gate.EnterRunAsync("claude", null, Ct);
        Assert.False(await EntersSoon(queued));
        Assert.Equal(AgentUpdateKinds.Check, bed.Gate.Holding("claude")!.Kind);

        var watch = Stopwatch.StartNew();
        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Empty(bed.Installs);
        Assert.Empty(bed.Updates);
        Assert.Equal(4, bed.Measured);
        (await queued.WaitAsync(Bound, Ct)).Dispose();
        foreach (var command in Commands) Assert.Null(bed.Gate.Holding(command));

        var record = AgentInstallsRecord.Read(_root)!;
        Assert.Equal("worker-1", record.Worker);
        Assert.All(record.Items, item => Assert.Equal(AgentInstallWords.Ready, item.State));
    }

    [Fact]
    public async Task A_second_start_does_nothing_more()
    {
        var bed = new Bed();
        using var installs = bed.Over(_root);
        installs.Hold();
        await installs.StartAsync("worker-1", Ct);
        await installs.StartAsync("worker-2", Ct);

        Assert.Equal(4, bed.Installs.Count);
        foreach (var command in Commands) bed.ReleaseOf(command).SetResult(true);
    }

    [Fact]
    public async Task A_fresh_volume_installs_every_cli_at_the_same_time_and_holds_only_the_agent_being_installed()
    {
        var bed = new Bed();
        using var installs = bed.Over(_root);
        installs.Hold();

        // Queued before any worker joined: held, never sent to a worker without the CLI.
        var claudeRun = bed.Gate.EnterRunAsync("claude", null, Ct);
        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);

        // All four started, and none has been let finish: they run at once.
        await Task.WhenAll(Commands.Select(c => bed.StartedOf(c).Task)).WaitAsync(Bound, Ct);
        Assert.Equal(Commands.Order(), bed.Installs.Order());
        Assert.All(bed.Reinstalls, again => Assert.False(again));
        var hold = bed.Gate.Holding("claude")!;
        Assert.Equal(AgentUpdateKinds.Install, hold.Kind);
        Assert.StartsWith("Installing claude", UpdatingOn.Text(hold), StringComparison.Ordinal);
        Assert.Contains("to be installed", RunLauncher.HeldText("claude", hold), StringComparison.Ordinal);
        Assert.Contains(AgentUpdateGate.InstallBoundText, RunLauncher.HeldText("claude", hold), StringComparison.Ordinal);
        installs.Record();
        Assert.All(AgentInstallsRecord.Read(_root)!.Items, item => Assert.Equal(AgentInstallWords.Installing, item.State));

        // Codex done first: a codex member runs while claude still installs, and claude's waits.
        bed.ReleaseOf("codex").SetResult(true);
        Assert.True(await EntersSoon(WhenFree(bed.Gate, "codex")));
        Assert.False(claudeRun.IsCompleted);

        bed.ReleaseOf("claude").SetResult(true);
        (await claudeRun.WaitAsync(Bound, Ct)).Dispose();
        bed.ReleaseOf("copilot").SetResult(true);
        bed.ReleaseOf("grok").SetResult(true);
        await WaitUntil(() => installs.Now().Items.All(item => item.State == AgentInstallWords.Ready));
        installs.Record();
        Assert.All(AgentInstallsRecord.Read(_root)!.Items, item => Assert.Equal(AgentInstallWords.Ready, item.State));

        // A fresh install counts as this interval's check: the next start inside it updates nothing.
        Assert.False(installs.Due("claude"));
    }

    [Fact]
    public async Task After_the_window_the_update_runs_in_the_background_another_agent_runs_and_the_updating_agent_waits_then_runs()
    {
        var bed = new Bed();
        foreach (var command in Commands) bed.Installed[command] = true;
        AgentInstallsAtStart.WriteChecks(_root, new Dictionary<string, DateTimeOffset>
        {
            ["claude"] = DateTimeOffset.UtcNow - TimeSpan.FromDays(2),
            ["codex"] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
            ["copilot"] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
            ["grok"] = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
        });
        using var installs = bed.Over(_root);
        installs.Hold();

        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);
        await bed.StartedOf("claude").Task.WaitAsync(Bound, Ct);

        Assert.Equal(["claude"], bed.Updates);
        Assert.Empty(bed.Installs);
        Assert.Equal(AgentUpdateKinds.Update, bed.Gate.Holding("claude")!.Kind);
        Assert.True(await EntersSoon(bed.Gate.EnterRunAsync("codex", null, Ct)));

        var claudeRun = bed.Gate.EnterRunAsync("claude", null, Ct);
        Assert.False(await EntersSoon(claudeRun));
        bed.ReleaseOf("claude").SetResult(true);
        (await claudeRun.WaitAsync(Bound, Ct)).Dispose();

        await WaitUntil(() => !installs.Due("claude"));
    }

    [Fact]
    public async Task A_due_update_waits_for_a_member_already_running_from_that_cli()
    {
        var bed = new Bed();
        foreach (var command in Commands) bed.Installed[command] = true;
        using var installs = bed.Over(_root);

        // In flight before anything asked: the update waits for it, as the Agents screen's does.
        var running = await bed.Gate.EnterRunAsync("claude", null, Ct);
        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);
        Assert.Equal(4, bed.Updates.Count);
        await Task.Delay(200, Ct);
        Assert.False(bed.StartedOf("claude").Task.IsCompleted);

        running.Dispose();
        await bed.StartedOf("claude").Task.WaitAsync(Bound, Ct);
        foreach (var command in Commands) bed.ReleaseOf(command).TrySetResult(true);
    }

    [Fact]
    public async Task An_unreachable_vendor_fails_that_cli_only_and_the_others_install()
    {
        var bed = new Bed();
        using var installs = bed.Over(_root);
        installs.Hold();
        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);
        await Task.WhenAll(Commands.Select(c => bed.StartedOf(c).Task)).WaitAsync(Bound, Ct);

        bed.ReleaseOf("codex").SetResult(false);
        foreach (var command in new[] { "claude", "copilot", "grok" }) bed.ReleaseOf(command).SetResult(true);
        await WaitUntil(() => installs.Now().Items.All(item => item.State is AgentInstallWords.Ready or AgentInstallWords.Failed));

        installs.Record();
        var record = AgentInstallsRecord.Read(_root)!;
        var codex = Assert.Single(record.Items, i => i.Command == "codex");
        Assert.Equal(AgentInstallWords.Failed, codex.State);
        Assert.Contains("network unreachable", codex.Detail, StringComparison.Ordinal);
        Assert.All(record.Items.Where(i => i.Command != "codex"), i => Assert.Equal(AgentInstallWords.Ready, i.State));
        // A launch of the failed one is let go (and finds no program); the others were never held by it.
        Assert.True(await EntersSoon(bed.Gate.EnterRunAsync("codex", null, Ct)));
        Assert.True(installs.Due("codex"));
    }

    [Fact]
    public async Task With_updates_off_a_missing_cli_is_still_installed_and_an_installed_one_is_not_updated()
    {
        var bed = new Bed();
        bed.Installed["claude"] = false;
        foreach (var command in new[] { "codex", "copilot", "grok" }) bed.Installed[command] = true;
        using var installs = bed.Over(_root, updates: false);
        installs.Hold();

        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);
        await installs.CheckDueAsync(Ct);

        Assert.Equal(["claude"], bed.Installs);
        Assert.Empty(bed.Updates);
        bed.ReleaseOf("claude").SetResult(true);
    }

    [Fact]
    public async Task Reinstall_installs_every_cli_again_without_measuring()
    {
        var bed = new Bed();
        foreach (var command in Commands) bed.Installed[command] = true;
        CheckedAgo(TimeSpan.FromMinutes(5), Commands);
        using var installs = bed.Over(_root, reinstall: true);

        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);

        Assert.Equal(Commands.Order(), bed.Installs.Order());
        Assert.All(bed.Reinstalls, Assert.True);
        Assert.Equal(0, bed.Measured);
        foreach (var command in Commands) bed.ReleaseOf(command).SetResult(true);
    }

    [Fact]
    public async Task The_interval_is_the_tenant_setting_and_the_hourly_check_updates_only_what_is_due()
    {
        var bed = new Bed();
        foreach (var command in Commands) bed.Installed[command] = true;
        CheckedAgo(TimeSpan.FromHours(5), Commands);
        using var daily = bed.Over(_root, interval: TimeSpan.FromDays(1));

        await daily.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);
        await daily.CheckDueAsync(Ct);
        Assert.Empty(bed.Updates);

        // The same checks against a shorter interval are due.
        using var shorter = bed.Over(_root, interval: TimeSpan.FromHours(4));
        await shorter.CheckDueAsync(Ct);
        Assert.Equal(4, bed.Updates.Count);
        foreach (var command in Commands) bed.ReleaseOf(command).SetResult(true);
    }

    [Fact]
    public async Task A_cli_that_cannot_be_measured_is_handed_to_the_installer_which_installs_only_what_is_missing()
    {
        var bed = new Bed();
        foreach (var command in Commands) bed.Installed[command] = null;
        using var installs = bed.Over(_root);

        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);

        Assert.Equal(4, bed.Installs.Count);
        foreach (var command in Commands) bed.ReleaseOf(command).SetResult(true);
    }

    [Fact]
    public async Task An_update_a_person_already_asked_for_stands_and_the_record_does_not_stay_in_progress()
    {
        var bed = new Bed();
        foreach (var command in Commands) bed.Installed[command] = true;
        using var installs = bed.Over(_root);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bed.Gate.Request("claude", "claude", "person@example.test", async _ =>
        {
            await release.Task;
            return new AgentUpdateResult("claude", "claude", true, 0, "1", "2", DateTimeOffset.UtcNow, "updated");
        });

        await installs.StartAsync("worker-1", Ct).WaitAsync(Bound, Ct);
        Assert.Equal(AgentInstallWords.Updating, Assert.Single(installs.Now().Items, i => i.Command == "claude").State);

        release.SetResult(true);
        await WaitUntil(() => bed.Gate.Holding("claude") is null);
        Assert.Equal(AgentInstallWords.Ready, Assert.Single(installs.Now().Items, i => i.Command == "claude").State);
        foreach (var command in Commands.Where(c => c != "claude")) bed.ReleaseOf(command).SetResult(true);
    }

    [Fact]
    public async Task In_a_host_that_runs_its_runs_itself_the_record_says_this_machine_never_its_own_worker()
    {
        var bed = new Bed();
        using var installs = bed.Over(_root, onThisMachine: true);
        await installs.StartAsync(null, Ct).WaitAsync(Bound, Ct);
        await bed.StartedOf("claude").Task.WaitAsync(Bound, Ct);
        bed.Gate.RunsOn("claude", "local");

        var claude = Assert.Single(installs.Now().Items, i => i.Command == "claude");
        Assert.Equal("Installing claude on this machine; it is measured again when the install ends.", claude.Detail);
        Assert.Null(claude.Worker);
        foreach (var command in Commands) bed.ReleaseOf(command).SetResult(true);
    }

    [Fact]
    public void Only_commands_the_built_ins_declare_an_update_for_are_installable()
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("claude-headless", AgentMode.Headless, new AgentLaunch("claude", ["-p"])),
            new AgentDefinition("claude", AgentMode.Interactive, new AgentLaunch("claude", [])),
            new AgentDefinition("grok-headless", AgentMode.Headless, new AgentLaunch("grok", [])),
            new AgentDefinition("sh-headless", AgentMode.Headless, new AgentLaunch("sh", [])),
        ]);

        Assert.Equal(
            [new AgentInstallsAtStart.Command("claude-headless", "claude"), new AgentInstallsAtStart.Command("grok-headless", "grok")],
            AgentInstallsAtStart.Installable(catalog));
    }

    [Fact]
    public async Task A_pending_check_holds_new_launches_without_waiting_for_the_runs_in_flight()
    {
        var gate = new AgentUpdateGate();
        using var inFlight = await gate.EnterRunAsync("claude", null, Ct);

        Assert.True(gate.BeginPending("claude"));
        Assert.False(gate.BeginPending("claude"));
        var held = gate.EnterRunAsync("claude", null, Ct);
        Assert.False(await EntersSoon(held));
        Assert.Equal(1, gate.Holding("claude")!.Runs);

        gate.EndPending("claude");
        (await held.WaitAsync(Bound, Ct)).Dispose();
        Assert.Null(gate.Holding("claude"));
    }

    [Fact]
    public async Task An_install_asked_while_pending_holds_on_with_no_gap()
    {
        var gate = new AgentUpdateGate();
        gate.BeginPending("claude");
        var held = gate.EnterRunAsync("claude", null, Ct);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        gate.Request("claude", "claude", null, async _ =>
        {
            await release.Task;
            return new AgentUpdateResult("claude", "claude", true, 0, null, "1", DateTimeOffset.UtcNow, "installed");
        }, kind: AgentUpdateKinds.Install);
        gate.EndPending("claude");

        Assert.False(await EntersSoon(held));
        Assert.Equal(AgentUpdateKinds.Install, gate.Holding("claude")!.Kind);
        Assert.Equal(AgentUpdateKinds.Install, gate.StateOf("claude").Kind);

        release.SetResult(true);
        (await held.WaitAsync(Bound, Ct)).Dispose();
    }

    [Fact]
    public void A_cli_being_installed_reads_installing_and_never_not_installed()
    {
        var gate = new AgentUpdateGate();
        var installsOn = new WorkerInstalls(() => [new WorkerId("worker-1")]);
        installsOn.Record(new WorkerId("worker-1"), [("no-such-cli-7f3a9c", false)]);
        var probe = new AgentInstallProbe(workers: installsOn, gate: gate);
        var preset = new AgentDefinition("x-headless", AgentMode.Headless, new AgentLaunch("no-such-cli-7f3a9c", []));
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        gate.Request("no-such-cli-7f3a9c", "x-headless", null, async _ =>
        {
            await release.Task;
            return new AgentUpdateResult("x-headless", "no-such-cli-7f3a9c", true, 0, null, "1", DateTimeOffset.UtcNow, "installed");
        }, kind: AgentUpdateKinds.Install);
        gate.RunsOn("no-such-cli-7f3a9c", "worker-1");

        var held = probe.Probe(preset);
        Assert.Equal(AgentInstallStates.Installing, held.State);
        Assert.Equal("Installing no-such-cli-7f3a9c on worker-1; it is measured again when the install ends.", held.Message);
        Assert.Equal(AgentUpdateKinds.Install, held.Updating!.Kind);
        Assert.DoesNotContain("not installed", held.Message, StringComparison.OrdinalIgnoreCase);

        gate.BeginPending("other");
        Assert.StartsWith("Getting other ready", UpdatingOn.Text(gate.Holding("other")!), StringComparison.Ordinal);
        release.SetResult(true);
    }

    [Fact]
    public async Task The_concierge_on_a_fresh_volume_picks_the_agent_being_installed_and_says_installing()
    {
        var gate = new AgentUpdateGate();
        var catalog = new AgentCatalog(
            [new AgentDefinition("claude", AgentMode.Interactive, new AgentLaunch("no-such-claude-7f3a9c", [], LanguageModel: true))]);
        var probe = new AgentAuthProbe(catalog, gate: gate);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Request("no-such-claude-7f3a9c", "claude", null, async _ =>
        {
            await release.Task;
            return new AgentUpdateResult("claude", "no-such-claude-7f3a9c", true, 0, null, "1", DateTimeOffset.UtcNow, "installed");
        }, kind: AgentUpdateKinds.Install);

        var effective = await ConciergeAgentDefault.EffectiveAsync(null, catalog, probe, Ct);

        Assert.Equal("claude", effective.Agent);
        Assert.Equal(AgentUpdateKinds.Install, effective.Auth.HeldBy);
        Assert.False(effective.Auth.Installed);
        release.SetResult(true);
    }

    [Fact]
    public void The_chrome_status_line_is_read_with_its_percent()
    {
        var chrome = Directory.CreateDirectory(Path.Combine(_root, "google-chrome")).FullName;
        Assert.Null(GoogleChromeStatus.Read(chrome));

        File.WriteAllText(Path.Combine(chrome, GoogleChromeStatus.FileName), "installing 40\n");
        Assert.Equal(new GoogleChromeStatus("installing", 40, null), GoogleChromeStatus.Read(chrome));

        File.WriteAllText(Path.Combine(chrome, GoogleChromeStatus.FileName), "ready 131.0.6778.85\n");
        Assert.Equal(new GoogleChromeStatus("ready", null, "131.0.6778.85"), GoogleChromeStatus.Read(chrome));

        File.WriteAllText(Path.Combine(chrome, GoogleChromeStatus.FileName), "failed could not reach dl.google.com to install it\n");
        Assert.Equal(new GoogleChromeStatus("failed", null, "could not reach dl.google.com to install it"), GoogleChromeStatus.Read(chrome));
    }

    private static async Task<IDisposable> WhenFree(AgentUpdateGate gate, string command)
    {
        await WaitUntil(() => gate.Holding(command) is null);
        return await gate.EnterRunAsync(command, null, Ct);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition did not hold in time");
            await Task.Delay(20, Ct);
        }
    }
}
