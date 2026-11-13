using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Agents;
using Yawble.Core.Agents.Profiles;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// agents.detect over the RPC, and the Agents dialog's Test: the CLI's TUI in a sandbox (real launcher, scripted
/// backend) as a stand-in under the global policy, on the agent-test:&lt;cli&gt; terminal channel.
/// </summary>
public class AgentTestSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedDetector(params DetectedCli[] clis) : ICliDetector
    {
        public int Calls;

        public Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<DetectedCli>>(clis);
        }
    }

    private static DetectedCli Found(string id) => new(id, true, $"/usr/bin/{id}", $"/opt/{id}/{id}", "1.2.3", "other",
        [$"/opt/{id}"], [$"/home/u/.{id}"], [], "install it");

    private static DetectedCli Missing(string id) => new(id, false, null, null, null, "other", [], [$"/home/u/.{id}"], [], $"install {id}");

    private static string Replay(TerminalHub hub, string channel) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(hub.Attach(channel)["replay"]!.GetValue<string>()));

    [Fact]
    public async Task AgentsDetectAnswersEveryCliFreshOnEachCall()
    {
        var detector = new FixedDetector(Found("claude"), Missing("grok"));
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], agents: detector);

        var answer = (await dispatcher.DispatchAsync("agents.detect", null, Ct))!.AsArray();
        await dispatcher.DispatchAsync("agents.detect", null, Ct);

        Assert.Equal(2, detector.Calls); // no cache
        var claude = answer[0]!.AsObject();
        Assert.Equal("claude", claude["cliId"]!.GetValue<string>());
        Assert.True(claude["found"]!.GetValue<bool>());
        Assert.Equal("/opt/claude/claude", claude["executablePath"]!.GetValue<string>());
        Assert.Equal("1.2.3", claude["version"]!.GetValue<string>());
        Assert.Equal("other", claude["installKind"]!.GetValue<string>());
        Assert.Equal("/opt/claude", claude["readOnlyFolders"]![0]!.GetValue<string>());
        Assert.Equal("/home/u/.claude", claude["configFolders"]![0]!.GetValue<string>());
        Assert.Equal("install it", claude["installHelp"]!.GetValue<string>());
        Assert.False(answer[1]!["found"]!.GetValue<bool>());
        Assert.Null(answer[1]!["launcherPath"]);
    }

    [Fact]
    public async Task TestOpensTheClisTuiInSandboxUnderTheGlobalPolicyWithTheDiagnostic()
    {
        await using var kit = await LaunchKit.StartAsync(limit: 1, wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        var members = new SqliteMemberDirectory(kit.Db.ConnectionString);
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (t, m) => members.Find(t, m)?.Id, AgentTestSessions.ChannelFor);
        kit.Backend.Stdout = "claude> ";
        kit.Backend.Script = (_, _) => Task.CompletedTask; // runs until stopped
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis); // the launcher's own detector, as in Program.cs
        var channel = AgentTestSessions.Channel("claude");

        var started = await tests.StartAsync("claude", diagnostic: true, Ct);

        Assert.Equal("agent-test:claude", started["channel"]!.GetValue<string>());
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Kind: "interactive", Ended: false }, "test session live");
        await Wait.UntilAsync(() => Replay(hub, channel).Contains("claude> "), "PTY output in the dialog's channel");
        var (json, kind) = kit.Backend.Requests.Single();
        Assert.Equal(RunKind.Interactive, kind);
        var request = JsonNode.Parse(json)!.AsObject();
        Assert.True(LaunchKit.IsFor(json, "claude"));
        Assert.Contains("_agent-test", request["process"]!["cwd"]!.GetValue<string>());
        var argv = request["process"]!["commandLine"]?.ToJsonString() ?? request["process"]!.ToJsonString();
        Assert.Contains("Yawble agent test", argv);
        // The Test runs the launcher's detection of the install (the same detector as the dialog in Program.cs):
        // its program, its folders read-only, its config folders never granted.
        var installed = (await kit.Clis.DetectAllAsync(Ct)).Single(c => c.CliId == "claude");
        Assert.Contains(HostPathCanonicalizer.Canonicalize(installed.ExecutablePath!).Replace("\\", "\\\\"), argv);
        var ro = request["filesystem"]!["readonlyPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var rw = request["filesystem"]!["readwritePaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.All(installed.ReadOnlyFolders, f => Assert.Contains(HostPathCanonicalizer.Canonicalize(f), ro));
        Assert.All(installed.ConfigFolders, f => Assert.DoesNotContain(HostPathCanonicalizer.Canonicalize(f), ro.Concat(rw)));
        Assert.Contains(kit.Host.Home.Replace("\\", "\\\\"), argv); // asks about the person's real home
        // It takes no run slot (like the synthetic guardrail check): a member's session still gets the only one.
        Assert.Equal(0, (await kit.Bus.SchedulerSnapshotAsync(Ct)).Running);

        Assert.True(hub.Input(channel, "hi\r"u8));
        await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("claude", false, Ct)); // busy

        Assert.True(await tests.StopAsync("claude"));
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: true }, "test session ended");
    }

    [Fact]
    public async Task WithoutTheDiagnosticTheTuiStartsWithNoPrompt()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis);

        await tests.StartAsync("grok", diagnostic: false, Ct);
        await Wait.UntilAsync(() => hub.SessionOf(AgentTestSessions.Channel("grok")) is { Ended: false }, "test session live");
        Assert.DoesNotContain("Yawble agent test", kit.Backend.Requests[0].Json);
        Assert.True(await tests.StopAsync("grok"));
    }

    [Fact]
    public async Task TestRefusesAMissingCliAnUnknownCliAndNoLauncher()
    {
        var hub = new TerminalHub(new AppEvents());
        var tests = new AgentTestSessions(null, "bwrap is not installed", hub, new FixedDetector(Missing("claude"), Found("grok")));

        var missing = await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("claude", true, Ct));
        Assert.Equal("not_found", missing.Code);
        var unknown = await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("nonesuch", true, Ct));
        Assert.Equal("bad_params", unknown.Code);
        var noLauncher = await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("grok", true, Ct));
        Assert.Equal("unavailable", noLauncher.Code);
        Assert.Contains("bwrap", noLauncher.Message);
    }

    private static (AgentProfileStore Store, AgentProfile Mine) CustomAgent(string cs, AgentCli baseCli = AgentCli.Claude)
    {
        AgentProfileStore? store = null;
        var log = new AgentChangeLog(cs, null, (a, m) => store!.ResolveModel(a, m));
        store = new AgentProfileStore(cs, log, new SqliteAgentUsage(cs));
        var p = store.Duplicate(baseCli, "Mine");
        p = store.Update(p.Id, p.Name, "haiku", ["--custom-before", "b"], ["--custom-after", "x"], []);
        return (store, p);
    }

    [Fact]
    public async Task ACustomAgentsTerminalRunsItsOwnArgvAndModel_AndTheFirstLineIsThatExactCommandLine()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        var (store, _) = CustomAgent(kit.Db.ConnectionString);
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis, agents: store);
        var channel = AgentTestSessions.Channel("claude");

        var started = await tests.StartAsync("claude", "mine", diagnostic: false, Ct);

        // The channel and folders stay the base CLI's (one session per CLI); the agent is named.
        Assert.Equal("agent-test:claude", started["channel"]!.GetValue<string>());
        Assert.Equal("Mine", started["agent"]!.GetValue<string>());
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "terminal session live");
        var json = kit.Backend.Requests.Single().Json;
        var argv = JsonNode.Parse(json)!["process"]!.ToJsonString();
        Assert.Contains("--custom-before", argv);
        Assert.Contains("--custom-after", argv);
        Assert.Contains("haiku", argv); // the agent's own default model
        Assert.DoesNotContain("Yawble agent test", json); // Terminal: no prompt

        // The banner is the described interactive line for that agent, with no prompt tail.
        var described = await kit.Launcher.DescribeAsync(AgentCli.Claude, "haiku", store.Resolve("Mine").Argv, Ct);
        var expected = string.Join(' ', described.Interactive.Argv.Where(a => a != AgentPresets.PromptPlaceholder));
        var commandLine = started["commandLine"]!.GetValue<string>();
        Assert.Equal(expected, commandLine);
        Assert.Contains("--custom-before b", commandLine);
        Assert.Contains("--model haiku", commandLine);
        Assert.DoesNotContain("<prompt>", commandLine);
        var replay = Replay(hub, channel);
        Assert.StartsWith("\r\n\x1b[2m" + expected + "\x1b[0m\r\n", replay);
        Assert.Contains("Mine terminal: starting", replay);
        Assert.True(await tests.StopAsync("claude"));
    }

    /// <summary>Holds every detection until the test opens the gate, then answers from <paramref name="inner"/>.</summary>
    private sealed class GatedDetector(ICliDetector inner) : ICliDetector
    {
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Gate.Task.WaitAsync(ct);
            return await inner.DetectAllAsync(ct);
        }
    }

    [Fact]
    public async Task TwoStartsForTheSameCliAtOnce_TheSecondIsBusyAndOnlyOneLaunches_ThenTheCliIsFreeAgain()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        var detector = new GatedDetector(kit.Clis);
        var tests = new AgentTestSessions(kit.Launcher, null, hub, detector);
        var channel = AgentTestSessions.Channel("claude");

        var first = tests.StartAsync("claude", diagnostic: false, Ct); // waits in detection: nothing is live yet
        await Wait.UntilAsync(() => Volatile.Read(ref detector.Calls) == 1, "the first start is detecting");
        var second = await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("claude", diagnostic: true, Ct));
        Assert.Equal("busy", second.Code);
        Assert.Equal(1, detector.Calls); // refused before detecting
        detector.Gate.SetResult();
        await first;
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "session live");
        Assert.Single(kit.Backend.Requests);
        Assert.Equal("busy", (await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("claude", diagnostic: false, Ct))).Code);

        // Stopped: once the run has ended, the CLI is free again.
        Assert.True(await tests.StopAsync("claude"));
        await Wait.UntilAsync(async () =>
        {
            try { await tests.StartAsync("claude", diagnostic: false, Ct); return true; }
            catch (RpcException e) when (e.Code == "busy") { return false; }
        }, "a new session after the stop");
        await Wait.UntilAsync(() => kit.Backend.Requests.Count == 2, "second launch");
        await Wait.UntilAsync(() => hub.SessionOf(channel) is { Ended: false }, "second session live");
        Assert.True(await tests.StopAsync("claude"));
    }

    [Fact]
    public async Task AStartThatIsRefusedBeforeLaunchingFreesTheCli()
    {
        var hub = new TerminalHub(new AppEvents());
        var tests = new AgentTestSessions(null, "no launcher", hub, new FixedDetector(Missing("claude"), Found("grok")));

        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("claude", false, Ct))).Code);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("claude", false, Ct))).Code); // not busy
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("grok", false, Ct))).Code);
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("grok", false, Ct))).Code);
    }

    [Fact]
    public async Task ACloudViewerIsNotShownAnAgentsTerminalSessionInTheRunList()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis);
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], runs: kit.Registry, agentTests: tests);

        await tests.StartAsync("claude", diagnostic: false, Ct);
        await Wait.UntilAsync(() => kit.Registry.All().Any(r => r.Team == AgentTestSessions.Team), "terminal session live");

        static IEnumerable<string> Teams(JsonNode? list) => list!.AsArray().Select(r => r!["team"]!.GetValue<string>());
        Assert.Contains(AgentTestSessions.Team, Teams(await dispatcher.DispatchAsAsync(Actor.Window, "runs.list", null, Ct)));
        Assert.DoesNotContain(AgentTestSessions.Team, Teams(await dispatcher.DispatchAsAsync(Actor.CloudViewer("ines"), "runs.list", null, Ct)));
        Assert.True(await tests.StopAsync("claude"));
    }

    [Fact]
    public async Task TheDiagnosticBannerEndsWithThePromptPlaceholder()
    {
        await using var kit = await LaunchKit.StartAsync(wallSec: 3600);
        var hub = new TerminalHub(new AppEvents());
        using var bridge = new RunTerminalBridge(kit.Registry, hub, (_, _) => null, AgentTestSessions.ChannelFor);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        var (store, _) = CustomAgent(kit.Db.ConnectionString);
        var tests = new AgentTestSessions(kit.Launcher, null, hub, kit.Clis, agents: store);

        var started = await tests.StartAsync("claude", "Mine", diagnostic: true, Ct);

        var commandLine = started["commandLine"]!.GetValue<string>();
        Assert.EndsWith(" --custom-after x <prompt>", commandLine);
        await Wait.UntilAsync(() => kit.Backend.Requests.Count == 1, "launched");
        Assert.Contains("Yawble agent test", kit.Backend.Requests[0].Json);
        Assert.StartsWith("\r\n\x1b[2m" + commandLine, Replay(hub, AgentTestSessions.Channel("claude")));
        await Wait.UntilAsync(() => hub.SessionOf(AgentTestSessions.Channel("claude")) is { Ended: false }, "test session live");
        Assert.True(await tests.StopAsync("claude"));
    }

    [Fact]
    public async Task AnAgentOfAnotherCliOrAnUnknownAgentIsBadParams()
    {
        using var db = new TempDb();
        var (store, _) = CustomAgent(db.ConnectionString, AgentCli.Grok);
        var hub = new TerminalHub(new AppEvents());
        var detector = new FixedDetector(Found("claude"), Found("grok"));
        var tests = new AgentTestSessions(null, "no launcher", hub, detector, agents: store);

        var mismatch = await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("claude", "Mine", true, Ct));
        Assert.Equal("bad_params", mismatch.Code);
        Assert.Contains("grok", mismatch.Message);
        var unknown = await Assert.ThrowsAsync<RpcException>(() => tests.StartAsync("grok", "nobody", false, Ct));
        Assert.Equal("bad_params", unknown.Code);
        Assert.Equal(0, detector.Calls); // refused before anything is detected or started
        Assert.False(hub.IsLive(AgentTestSessions.Channel("claude")));
    }

    [Fact]
    public async Task AgentsTestIsTheWindowsOnly_TheCloudLinkAndOtherActorsAreRefused()
    {
        var hub = new TerminalHub(new AppEvents());
        var detector = new FixedDetector(Found("claude"));
        var tests = new AgentTestSessions(null, "no launcher here", hub, detector);
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], agents: detector, agentTests: tests);
        var p = new JsonObject { ["cli"] = "claude", ["diagnostic"] = false };

        Assert.NotNull(Yawble.Core.Link.RemoteAccess.Refusal("agents.test", p, allowHostTerminals: true));
        Assert.NotNull(Yawble.Core.Link.RemoteAccess.Refusal("agents.stopTest", p, allowHostTerminals: true));
        foreach (var actor in new[] { Actor.CloudViewer("ines"), new Actor(Actor.Concierge, null), Actor.ExternalClient("claude-code") })
        {
            var refused = await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsAsync(actor, "agents.test", (JsonObject)p.DeepClone(), Ct));
            Assert.Equal("forbidden", refused.Code);
        }
        var noActor = await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsync("agents.test", (JsonObject)p.DeepClone(), Ct));
        Assert.Equal("forbidden", noActor.Code);
        Assert.Equal(0, detector.Calls);

        // The window reaches the session (here: refused for the missing launcher, after detection).
        var window = await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsAsync(Actor.Window, "agents.test", (JsonObject)p.DeepClone(), Ct));
        Assert.Equal("unavailable", window.Code);
        var badAgent = await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsAsync(Actor.Window, "agents.test",
            new JsonObject { ["cli"] = "claude", ["agent"] = 3, ["diagnostic"] = false }, Ct));
        Assert.Equal("bad_params", badAgent.Code);
    }

    [Fact]
    public void TheDiagnosticAsksAboutTheConfigFoldersAndTheRealHome()
    {
        var prompt = AgentTestSessions.DiagnosticPrompt(Found("grok"), "/home/u", "/home/u/.local/share/yawble");
        Assert.Contains("/home/u/.grok", prompt);
        Assert.Contains("/home/u;", prompt);
        Assert.Contains("--version", prompt);
        Assert.Contains("Do not read the contents of any credential file", prompt);
    }
}
