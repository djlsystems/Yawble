using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// A TOOL LISTING RUNS ON A WORKER. Each listing command is one request to the worker a run placed
/// now would go to, carrying what a member's spawn would take out of the environment and the run's
/// credential scope; what a CLI prints is cut at a mebibyte and says so; and with no worker every
/// preset is not measured, never clean.
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

        var run = await new WorkerListingRunner(asks, pathIsTheWorkers: false)
            .RunAsync("claude", ["mcp", "list"], new Dictionary<string, string> { ["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false" }, Ct);

        Assert.Equal((0, "listed on w2"), (run.ExitCode, run.Stdout));
        Assert.Empty(busy.Sent);
        var sent = Assert.IsType<RunAgentCommands>(Assert.Single(free.Sent));
        var command = Assert.Single(sent.Commands);
        Assert.Equal(("claude", true), (command.FileName, command.Scratch));
        Assert.Equal(["mcp", "list"], command.Arguments);
        Assert.Equal("false", command.Environment["ENABLE_CLAUDEAI_MCP_SERVERS"]);

        // Every provider key but claude's own is taken out on the worker, as at a member's spawn.
        Assert.Contains("OPENAI_API_KEY", command.RemovedEnvironment);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", command.RemovedEnvironment);
    }

    [Fact]
    public async Task Output_over_a_mebibyte_is_cut_and_says_so()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var cli = Path.Combine(_root, "loud-cli");
        await TestExecutable.WriteAsync(cli, "#!/bin/sh\nhead -c 2097152 /dev/zero | tr '\\0' a\necho short >&2\n");
        var worker = new WorkerAgentCli(new WorkerId("w1"), null);

        var ran = Assert.IsType<AgentCommandsRan>(await worker.AnswerAsync(
            new RunAgentCommands("r1", [new AgentCliRun(cli, [], new Dictionary<string, string>(), [], 30)]), Ct));

        var result = Assert.Single(ran.Results);
        Assert.Equal((true, 0, false, true), (result.Installed, result.ExitCode!.Value, result.TimedOut, result.Truncated));
        Assert.Equal(AgentCliRuns.MaxOutputBytes, result.Stdout.Length);
        Assert.Equal("short", result.Stderr.Trim());
    }

    [Fact]
    public async Task A_listing_carries_the_runs_credential_scope_to_the_worker()
    {
        var sent = Listing("w1");
        var asks = new WorkerAsks(() => sent);
        sent.Asks = asks;
        var scope = new CredentialScope(["XAI_API_KEY"]);

        await new WorkerListingRunner(asks, pathIsTheWorkers: false)
            .RunAsync("claude", ["mcp", "list"], new Dictionary<string, string>(), Ct, scope);

        var request = Assert.IsType<RunAgentCommands>(Assert.Single(sent.Sent));
        Assert.Same(scope, request.Scope);
    }

    [Fact]
    public async Task No_worker_makes_every_preset_not_measured()
    {
        var asks = new WorkerAsks(() => throw new InvalidOperationException("No worker is connected."));
        var runner = new WorkerListingRunner(asks, pathIsTheWorkers: false);
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
