using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// A TOOL LISTING RUNS ON A WORKER. Each listing command is one request to the worker a run placed
/// now would go to, carrying what a member's spawn would take out of the environment; an issued
/// preset's listing runs in a home the worker makes and removes; what a CLI prints is cut at a
/// mebibyte and says so; the credential never crosses in the clear; and with no worker every preset
/// is not measured, never clean.
/// </summary>
public sealed class AgentCommandsOnWorkerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-commands-worker-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_listing_is_one_request_to_the_worker_a_run_placed_now_would_go_to()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, clock), clock: clock);
        var busy = Listing("w1");
        var free = Listing("w2");
        pool.Join(busy, new WorkerInfo(busy.Id, null, 4, null, clock.GetUtcNow()));
        pool.Join(free, new WorkerInfo(free.Id, null, 4, null, clock.GetUtcNow()));
        pool.Placed = id => id == busy.Id ? 1 : 0;

        var asks = new WorkerAsks(() => pool.Worker(null));
        busy.Asks = free.Asks = asks;

        var run = await new WorkerListingRunner(asks, _root, pathIsTheWorkers: false)
            .RunAsync("claude", ["mcp", "list"], new Dictionary<string, string> { ["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false" }, Ct);

        Assert.Equal((0, "listed on w2"), (run.ExitCode, run.Stdout));
        Assert.Empty(busy.Sent);
        var sent = Assert.IsType<RunAgentCommands>(Assert.Single(free.Sent));
        var command = Assert.Single(sent.Commands);
        Assert.Equal(("claude", true, (string?)null), (command.FileName, command.Scratch, command.HomesIn));
        Assert.Equal(["mcp", "list"], command.Arguments);
        Assert.Equal("false", command.Environment["ENABLE_CLAUDEAI_MCP_SERVERS"]);

        // Every provider key but claude's own is taken out on the worker, as at a member's spawn.
        Assert.Contains("OPENAI_API_KEY", command.RemovedEnvironment);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", command.RemovedEnvironment);
    }

    [Fact]
    public async Task An_issued_listing_runs_in_a_home_the_worker_makes_and_removes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var homes = Directory.CreateDirectory(Path.Combine(_root, "homes")).FullName;
        var cli = Path.Combine(_root, "fake-cli");
        await TestExecutable.WriteAsync(cli, "#!/bin/sh\necho \"$HOME|$XDG_CACHE_HOME|$FAKE_ISSUED\"\n");
        var issued = new RunCredential(
            CredentialSource.Issued, new Dictionary<string, string> { ["FAKE_ISSUED"] = "fake-issued-value" }, [], [], true, null);

        var runner = new WorkerListingRunner(WorkerAsks.InProcess(null), homes, pathIsTheWorkers: true);
        var home = (await runner.MakeHomeAsync(Ct))!;
        var run = await runner.RunAsync(
            cli, [], new Dictionary<string, string> { ["HOME"] = home, ["XDG_CACHE_HOME"] = RunHome.CacheBeside(home) }, Ct, issued);

        var seen = run.Stdout.Trim().Split('|');
        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith(homes + Path.DirectorySeparatorChar + RunHome.Prefix, seen[0]);
        Assert.NotEqual(home, seen[0]);
        Assert.Equal(Path.Combine(homes, RunHome.CacheFolder), seen[1]);
        Assert.Equal("fake-issued-value", seen[2]);

        // The worker removed the home it made; the cache beside it is kept.
        Assert.False(Directory.Exists(seen[0]));
        Assert.Empty(Directory.EnumerateDirectories(homes, RunHome.Prefix + "*"));
        Assert.True(Directory.Exists(seen[1]));
    }

    [Fact]
    public async Task Output_over_a_mebibyte_is_cut_and_says_so()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var cli = Path.Combine(_root, "loud-cli");
        await TestExecutable.WriteAsync(cli, "#!/bin/sh\nhead -c 2097152 /dev/zero | tr '\\0' a\necho short >&2\n");
        var worker = new WorkerAgentCli(new WorkerId("w1"), null, new RunHomes(null, RunHomeRemoval.For(null)));

        var ran = Assert.IsType<AgentCommandsRan>(await worker.AnswerAsync(
            new RunAgentCommands("r1", [new AgentCliRun(cli, [], new Dictionary<string, string>(), [], 30)]), Ct));

        var result = Assert.Single(ran.Results);
        Assert.Equal((true, 0, false, true), (result.Installed, result.ExitCode!.Value, result.TimedOut, result.Truncated));
        Assert.Equal(AgentCliRuns.MaxOutputBytes, result.Stdout.Length);
        Assert.Equal("short", result.Stderr.Trim());
    }

    [Fact]
    public async Task A_credential_on_a_command_never_crosses_in_clear()
    {
        const string Value = "fake-issued-value-Zq7w";
        var sent = Listing("w1");
        var asks = new WorkerAsks(() => sent);
        sent.Asks = asks;
        var issued = new RunCredential(
            CredentialSource.Issued, new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = Value }, [], [], true, null);

        await new WorkerListingRunner(asks, _root, pathIsTheWorkers: false)
            .RunAsync("claude", ["mcp", "list"], new Dictionary<string, string>(), Ct, issued);

        var request = Assert.IsType<RunAgentCommands>(Assert.Single(sent.Sent));
        Assert.Same(issued, request.Credential);
        Assert.DoesNotContain(Value, string.Join(" ", request.Commands.SelectMany(c => c.Environment.Values)));

        var key = WorkerFrameCodec.Key("the-worker-key", "control-nonce", "worker-nonce");
        var text = new WorkerFrameCodec(key).Write(new CommandFrame(1, request));
        Assert.DoesNotContain("Zq7w", text, StringComparison.Ordinal);
        Assert.Equal(Value, ((RunAgentCommands)((CommandFrame)new WorkerFrameCodec(key).Read(text)).Message).Credential!.Environment["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public async Task No_worker_makes_every_preset_not_measured()
    {
        var asks = new WorkerAsks(() => throw new InvalidOperationException("No worker is connected."));
        var runner = new WorkerListingRunner(asks, _root, pathIsTheWorkers: false);
        IReadOnlyList<AgentDefinition> presets =
        [
            .. AgentCatalogFile.BuiltIns().Where(d => d.Mode == AgentMode.Headless && d.Launch.LanguageModel
                && AgentToolListers.Listed.Contains(d.Launch.FileName)),
        ];
        Assert.NotEmpty(presets);

        var reports = await AgentToolPreflight.ReportsAsync(presets, runner, Ct);

        Assert.Equal(presets.Count, reports.Count);
        Assert.All(reports, report =>
        {
            Assert.NotEqual(ToolVerdicts.Isolated, report.Verdict);
            Assert.Contains(report.Verdict, new[] { ToolVerdicts.NotMeasured, ToolVerdicts.NotVerified });
            Assert.Contains(WorkerListingRunner.NoWorkerText, report.Detail);
        });
    }

    private static ScriptedCliWorker Listing(string id) =>
        new(id, message => message is RunAgentCommands run
            ? new AgentCommandsRan(run.Request, [.. run.Commands.Select(_ => new AgentCliRunResult(true, 0, false, $"listed on {id}", "", false, null))])
            : null);
}
