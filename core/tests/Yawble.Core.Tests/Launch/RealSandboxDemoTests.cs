using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Guardrail;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Guardrail;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// An end-to-end walk through the agent-run features under a REAL sandbox
/// on Linux, without the UI: two teams under a global run limit of 3, each agent in its own sandbox through its
/// per-run gateway and the real bridge, Managers telling members and completing workflows over the bus, a guardrail
/// check that takes a run slot while the teams work, an interactive terminal on the sandbox's PTY, and Stop. The agent CLIs
/// are the stand-in (Launch/RealSandbox/standin-cli.py) and the model upstream is the fake; every secret is a fake.
/// </summary>
[Collection(RealSandboxCollection.Name)]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class RealSandboxDemoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Call(string tool, object args) => new { tool, args };

    private static object Manager(params string[] members) => new
    {
        say = "manager",
        model = true,
        busByRun = new object[]
        {
            members.Select(m => Call("tell", new { member = m, instruction = $"{m}: take your part of the task" })).ToArray(),
            new[] { Call("workflow_complete", new { delivered = "the team's task" }) },
        },
    };

    private static object Member(string name) => new
    {
        say = name,
        model = true,
        sleepSec = 3, // real seconds of "work", so the teams contend for the three slots
        bus = new[] { Call("progress", new { status = name + ": working" }), Call("handback", new { delivered = name + ": done" }) },
    };

    [Fact]
    public async Task TwoTeamsWorkUnderALimitOfThreeEachAgentInItsOwnSandboxSandbox()
    {
        RealSandbox.SkipUnlessAvailable();
        var marker = Procs.NewMarker();
        var scenario = new Dictionary<string, object>
        {
            ["payments-refactor/Manager"] = Manager("ada", "ben"),
            ["docs-site/Manager"] = Manager("cy", "dee"),
            ["ada"] = Member("ada"),
            ["ben"] = Member("ben"),
            ["cy"] = Member("cy"),
            ["dee"] = Member("dee"),
            ["alpha/dev1"] = new { say = "a task that never ends", tree = marker },
        };
        // Real time (speed 1): the guardrail probe's own bound is 150 s of launcher time, and no watchdog is under test here.
        await using var kit = await RealSandboxLauncherTests.Kit.StartAsync(scenario, limit: 3, speed: 1);
        var bus = kit.Launch.Bus;
        await bus.CreateTeamAsync("payments-refactor", ct: Ct);
        await bus.AddMemberAsync("payments-refactor", "ada", "claude", Ct);
        await bus.AddMemberAsync("payments-refactor", "ben", "grok", Ct);
        await bus.CreateTeamAsync("docs-site", ct: Ct);
        await bus.AddMemberAsync("docs-site", "cy", "claude", Ct);
        await bus.AddMemberAsync("docs-site", "dee", "claude", Ct);
        // The docs-site Manager is on Grok; the bus's CreateTeam makes every Manager Claude.
        using (var c = new SqliteConnection(kit.Gateway.Db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE members SET cli = 'grok' WHERE name = 'Manager' AND team_id = (SELECT id FROM teams WHERE name = 'docs-site');";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        GuardrailKit.InstallProbe(Path.Combine(kit.Install, "bin"));

        // Sample what runs: never more than 3 sandboxes, and agents queue while all 3 are taken.
        var maxLive = 0;
        var maxQueued = 0;
        var sawPids = new HashSet<int>();
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                var live = kit.Launch.Registry.All();
                maxLive = Math.Max(maxLive, live.Count);
                foreach (var r in live) sawPids.Add(r.SandboxPid);
                var queued = 0;
                foreach (var team in new[] { "payments-refactor", "docs-site", "alpha" })
                    queued += (await bus.TeamStatusAsync(team, Ct)).Count(m => m.State == "queued");
                maxQueued = Math.Max(maxQueued, queued);
                await Task.Delay(25, Ct);
            }
        }, Ct);

        // Step 1: tell both Managers at the same moment.
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(
            bus.PersonTellAsync("payments-refactor", "Manager", "refactor the payments module", ct: Ct),
            bus.PersonTellAsync("docs-site", "Manager", "update the docs site", ct: Ct));

        // Step 5 while the teams work: the guardrail check for an idle member takes a run slot like any run.
        await Task.Delay(1500, Ct);
        var check = new GuardrailCheck(kit.Launch.Launcher, kit.Gateway.Db.ConnectionString, slot: GuardrailRunSlots.From(bus));
        var checkTask = check.RunAsync("alpha", "dev2", Ct);

        // Steps 2-3: both workflows complete.
        IReadOnlyList<Message> All() => kit.Launch.Messages();
        await Wait.UntilAsync(() => All().Count(m => m.Type == MessageTypes.WorkflowCompleted) == 2, "both workflows to complete", TimeSpan.FromMinutes(3));
        var workflowsMs = sw.ElapsedMilliseconds;
        var report = await checkTask.WaitAsync(TimeSpan.FromMinutes(2), Ct);
        await Wait.UntilAsync(() => bus.IsQuiescentAsync(Ct), "the bus to settle", TimeSpan.FromMinutes(1));
        sampling.Cancel();
        await sampler;

        var messages = All();
        var outcomes = messages.Where(m => m.Type is MessageTypes.Completed or MessageTypes.Failed).ToList();
        var failed = outcomes.Where(m => m.Type == MessageTypes.Failed).Select(m => $"{m.Source}: {m.Payload}").ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed));
        foreach (var who in new[] { "payments-refactor/ada", "payments-refactor/ben", "docs-site/cy", "docs-site/dee" })
            Assert.Contains(messages, m => m.Type == MessageTypes.Handback && m.Source == who);
        // No message crosses teams.
        foreach (var m in messages.Where(m => m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal) && m.Source != MessageTypes.PersonSource))
            Assert.Equal(m.Source.Split('/')[0], m.Type[MessageTypes.InstructionPrefix.Length..].Split('/')[0], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, maxLive);
        Assert.True(maxQueued > 0, "nothing ever queued behind the 3 slots");
        var records = kit.Launch.Records;
        var sandboxes = outcomes.Count + 1; // + the guardrail check
        Assert.True(sawPids.Count >= 8, $"saw {sawPids.Count} sandbox pids");
        Assert.True(report.Passed, report.ToText());
        Assert.Equal("yawble-ffi", report.Run!.Backend);
        // Each agent ran in its own sandbox with its own gateway; each run token is revoked now.
        Assert.All(kit.Started, s => Assert.Equal(Yawble.Core.Gateway.TokenCheck.Revoked, kit.Gateway.Tokens.Check(s.Run.RunNumber, s.Gateway.RunToken)));
        RealSandbox.Report($"end to end: 2 workflows complete in {workflowsMs} ms; {sandboxes} sandboxes ({outcomes.Count} agent runs + 1 guardrail check); " +
                       $"max {maxLive} live at once (limit 3), max {maxQueued} queued; guardrail check {(report.Passed ? "PASSED" : "FAILED")} " +
                       $"{report.Counts.Blocked} blocked / {report.Counts.Allowed} allowed in {report.DurationMs} ms");

        // Step 4: the terminal icon. Ben's interactive CLI on the sandbox's PTY, in a fresh sandbox, through a run slot.
        var ended = new TaskCompletionSource<LaunchOutcome>();
        var ticket = await kit.Launch.Sessions.OpenAsync("payments-refactor", "ben", new PtySize(30, 100), o => ended.TrySetResult(o), Ct);
        Assert.True(await ticket.Admission);
        LiveRun? session = null;
        await Wait.UntilAsync(() => (session = kit.Launch.Registry.ForMember("payments-refactor", "ben")) is not null, "ben's session");
        string Screen() => Encoding.UTF8.GetString(session!.Output.Snapshot());
        await Wait.UntilAsync(() => Screen().Contains("> ", StringComparison.Ordinal), "the interactive prompt", TimeSpan.FromSeconds(30));
        Assert.Contains("grok stand-in, interactive, for payments-refactor/ben", Screen());
        session!.WriteInput("hello from the terminal\r"u8);
        var sessionOutcome = await ended.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Contains("you said: hello from the terminal", Screen());
        Assert.Equal(RunEnd.Completed, sessionOutcome.End);
        Assert.Equal("interactive", records.Get(sessionOutcome.RunKey)!.Kind);
        await ticket.Completion;
        RealSandbox.Report("end to end: ben's interactive terminal ran on the sandbox's PTY: banner, input echoed, exit 0");

        // Step 8: Stop on a running agent ends its whole tree within 5 s.
        await bus.PersonTellAsync("alpha", "dev1", "a task that never ends", ct: Ct);
        await Wait.UntilAsync(() => Procs.WithMarker("detached-" + marker).Count > 0 && Procs.WithMarker("child-" + marker).Count > 0,
            "dev1's tree inside the sandbox", TimeSpan.FromSeconds(30));
        var stop = Stopwatch.StartNew();
        Assert.True(kit.Launch.Registry.Stop("alpha", "dev1"));
        var stopped = await kit.Launch.OutcomeAsync("dev1");
        var stopToOutcome = stop.ElapsedMilliseconds;
        Assert.Equal(FailureClasses.Stopped, Payload.Field(stopped, "failureClass"));
        var latency = JsonDocument.Parse(stopped.Payload).RootElement.GetProperty("run").GetProperty("killLatencyMs").GetInt64();
        var gone = Stopwatch.StartNew();
        while (Procs.WithMarker(marker).Count > 0 && gone.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, Ct);
        Assert.Empty(Procs.WithMarker(marker));
        Assert.True(stopToOutcome < 5000, $"Stop to outcome {stopToOutcome} ms");
        RealSandbox.Report($"end to end: Stop on alpha/dev1: killLatencyMs={latency}, Stop to outcome {stopToOutcome} ms, 0 processes left");
    }
}
