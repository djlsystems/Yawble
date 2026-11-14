using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Agents;
using Yawble.Core.Tests.Agents;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// The whole launcher over a real bus and database, with the gateway and the credential modes faked and a
/// scripted backend in place of the sandbox. Time runs 60x faster, so the policy's 60 s floors are 1 s here.
/// </summary>
internal sealed class LaunchKit : IAsyncDisposable
{
    public const double Speed = 60;

    private LaunchKit(TempDb db, string root, BusEngine bus, SandboxLauncher launcher, ScriptedBackend backend,
        FakeGatewayFactory gateways, RunRecordStore records, LaunchHost host)
    {
        Db = db;
        Root = root;
        Bus = bus;
        Launcher = launcher;
        Backend = backend;
        Gateways = gateways;
        Records = records;
        Host = host;
        Sessions = new InteractiveSessions(bus, launcher);
    }

    public TempDb Db { get; }
    public string Root { get; }
    public BusEngine Bus { get; }
    public SandboxLauncher Launcher { get; }
    public ScriptedBackend Backend { get; }
    public FakeGatewayFactory Gateways { get; }
    public RunRecordStore Records { get; }
    public LaunchHost Host { get; }
    public InteractiveSessions Sessions { get; }
    public AgentDefaultsStore Defaults { get; private init; } = null!;
    public RunRegistry Registry => Launcher.Registry;

    private bool _ownsDb = true;

    /// <param name="db">A database another fixture owns (the gateway fixture): not disposed by the kit.</param>
    /// <param name="gateways">The real gateway factory; the fake when null (then <see cref="Gateways"/> is it).</param>
    public static async Task<LaunchKit> StartAsync(int limit = 4, int idleSec = 60, int wallSec = 120, ICredentialModes? credentials = null,
        TempDb? db = null, IRunGatewayFactory? gateways = null, IRunTranscripts? transcripts = null, ISandboxBackend? realBackend = null,
        double speed = Speed, IReadOnlyList<string>? allowHosts = null, Func<IAgentRunner, LaunchHost, string, IAgentRunner>? wrapRunner = null,
        ICliDetector? clis = null)
    {
        var ownsDb = db is null;
        db ??= new TempDb();
        // Canonical from the start, so the kit's own paths are the ones the launcher compiles (a symlinked temp root).
        var root = HostPathCanonicalizer.Canonicalize(Path.Combine(db.Dir, "machine"));
        var clock = new ScaledTimeProvider(speed);
        // The agent CLIs are stand-ins "installed on the host" outside the home and the app root (<root>/host-clis/<cli>/).
        Directory.CreateDirectory(Path.Combine(root, "home"));
        clis ??= TestClis.StandIns(root, Path.Combine(root, "home"));
        var host = new LaunchHost(TargetOsExtensions.Host, Path.Combine(root, "home"), Path.Combine(root, "app"), Path.Combine(root, "install"),
            clis, HostPathCanonicalizer.Canonicalize);
        // As LaunchWiring wires the real core: canonical paths, so a symlinked temp root (macOS /var) behaves as in the app.
        var policies = new PolicyStore(db.ConnectionString, canonicalize: HostPathCanonicalizer.Canonicalize);
        policies.SaveGlobal(GlobalPolicy.Default with
        {
            Network = new NetworkPolicy(allowHosts ?? []),
            Compute = new ComputePolicy(idleSec, wallSec),
            App = new AppPolicy(limit, "default"),
        });
        var backend = new ScriptedBackend();
        var fakeGateways = new FakeGatewayFactory();
        var records = new RunRecordStore(db.ConnectionString);
        var defaults = new AgentDefaultsStore(db.ConnectionString);
        var launcher = new SandboxLauncher(host, new SqliteMemberDirectory(db.ConnectionString), policies,
            credentials ?? new FakeCredentialModes(), gateways ?? fakeGateways, realBackend ?? backend, records, new RunRegistry(), clock,
            transcripts: transcripts, agentDefaults: defaults);
        // The app wraps the launcher's runner (TeamRepoRunner: the member's clone of the team repository).
        IAgentRunner runner = new SandboxAgentRunner(launcher);
        if (wrapRunner is not null) runner = wrapRunner(runner, host, db.ConnectionString);
        var bus = new BusEngine(new BusStore(db.ConnectionString), runner,
            new BusOptions { MaxConcurrentAgents = () => limit, SafetyPoll = TimeSpan.FromMilliseconds(50) });
        await bus.StartAsync();
        await bus.CreateTeamAsync("alpha");
        await bus.AddMemberAsync("alpha", "dev1", "claude");
        await bus.AddMemberAsync("alpha", "dev2", "grok");
        return new LaunchKit(db, root, bus, launcher, backend, fakeGateways, records, host) { _ownsDb = ownsDb, Defaults = defaults };
    }

    public IReadOnlyList<Message> Messages()
    {
        using var store = Db.OpenStore();
        return store.AllMessages();
    }

    public Message Outcome(string member) =>
        Messages().Last(m => m.Source == "alpha/" + member && m.Type is MessageTypes.Completed or MessageTypes.Failed);

    public async Task<Message> OutcomeAsync(string member)
    {
        await Wait.UntilAsync(() => Messages().Any(m => m.Source == "alpha/" + member && m.Type is MessageTypes.Completed or MessageTypes.Failed),
            $"{member}'s run to end");
        return Outcome(member);
    }

    public RunLayout Layout(string member, AgentCli cli) =>
        RunLayout.Standard(Host.Os, Host.Home, Host.AppDataRoot, Host.InstallDir,
            new Yawble.Core.Policy.MemberRef("alpha", member), cli, Launcher.DetectCliAsync(cli, false, CancellationToken.None).GetAwaiter().GetResult(), []);

    /// <summary>The kit's detector (stand-ins unless the test passed its own).</summary>
    public ICliDetector Clis => Host.Clis;

    /// <summary>
    /// Whether a compiled request is <paramref name="member"/>'s: its cwd (the member's clone) has the path
    /// segments members/&lt;member&gt;, with either separator (Windows requests use backslashes).
    /// </summary>
    public static bool IsFor(string requestJson, string member)
    {
        var cwd = JsonNode.Parse(requestJson)!["process"]!["cwd"]!.GetValue<string>();
        var parts = cwd.Split('/', '\\');
        for (var i = 0; i + 1 < parts.Length; i++)
            if (parts[i] == "members" && string.Equals(parts[i + 1], member, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static Dictionary<string, string> Env(JsonObject request) =>
        request["process"]!["env"]!.AsArray().Select(e => e!.GetValue<string>())
            .ToDictionary(e => e[..e.IndexOf('=')], e => e[(e.IndexOf('=') + 1)..]);

    public async ValueTask DisposeAsync()
    {
        await Bus.DisposeAsync();
        if (_ownsDb) Db.Dispose();
    }
}

public class LauncherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Run(Message m) => JsonDocument.Parse(m.Payload).RootElement.GetProperty("run");

    [Fact]
    public async Task AClaudeMemberRunsHeadlessWithOnlyThePlaceholderAndTheSeed()
    {
        await using var kit = await LaunchKit.StartAsync();
        JsonObject? seen = null;
        string? claudeJson = null, settingsJson = null;
        var layout = kit.Layout("dev1", AgentCli.Claude);
        kit.Backend.Script = async (req, p) =>
        {
            if (LaunchKit.Env(req).GetValueOrDefault("CLAUDE_CONFIG_DIR") is { } dir && dir.Contains("dev1"))
            {
                seen = req;
                claudeJson = await File.ReadAllTextAsync(Path.Combine(dir, ".claude.json"));
                settingsJson = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
            }
            p.Exit(0);
        };
        await kit.Bus.PersonTellAsync("alpha", "dev1", "write hello.txt", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev1");

        Assert.Equal(MessageTypes.Completed, outcome.Type);
        Assert.Equal("completed", Run(outcome).GetProperty("end").GetString());
        Assert.NotNull(seen);
        var env = LaunchKit.Env(seen!);
        var gateway = kit.Gateways.Started.Single(g => g.Run.Member == "dev1");
        // The sandbox's only credential is the run token, which is also its bus identity.
        Assert.Equal(gateway.RunToken, env["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal(gateway.RunToken, env["YAWBLE_RUN_TOKEN"]);
        Assert.Equal($"http://127.0.0.1:{gateway.Port}", seen!["runtimeConfig"]!["networkProxy"]!.GetValue<string>());
        Assert.Equal(120_000, seen["process"]!["timeout"]!.GetValue<long>());
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"]);
        Assert.Equal("1", env["DISABLE_AUTOUPDATER"]);
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.Contains("write hello.txt", seen["process"]!["commandLine"]!.GetValue<string>());
        Assert.NotNull(gateway.Plan);
        Assert.True(gateway.Disposed);

        // Claude's config seed, on disk: 4 keys, the clone trusted.
        var state = JsonNode.Parse(claudeJson!)!;
        Assert.True(state["hasCompletedOnboarding"]!.GetValue<bool>());
        Assert.True(state["hasSeenAutoDefaultNudge"]!.GetValue<bool>());
        Assert.True(state["projects"]![AgentPresets.ClaudeProjectKey(layout)]!["hasTrustDialogAccepted"]!.GetValue<bool>());
        var settings = JsonNode.Parse(settingsJson!)!;
        Assert.True(settings["skipDangerousModePermissionPrompt"]!.GetValue<bool>());
        // No AI attribution in the agent's commits or pull requests.
        Assert.Equal("", settings["attribution"]!["commit"]!.GetValue<string>());
        Assert.Equal("", settings["attribution"]!["pr"]!.GetValue<string>());
        Assert.False(settings["includeCoAuthoredBy"]!.GetValue<bool>());
        Assert.DoesNotContain(gateway.RunToken, claudeJson);

        var record = kit.Records.All().Single(r => r.Member == "dev1");
        Assert.Equal("completed", record.EndClass);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal("headless", record.Kind);
        Assert.Contains(record.RunKey, outcome.Payload);
        Assert.NotNull(record.SandboxPid);
        Assert.Equal(JsonDocument.Parse(outcome.Payload).RootElement.GetProperty("runId").GetInt64(), record.BusRunId);
    }

    [Fact]
    public async Task AGrokRunThatExitsZeroOnARevokedCredentialIsReportedFromTheGateway()
    {
        await using var kit = await LaunchKit.StartAsync();
        kit.Gateways.RejectCredential = true;
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };
        await kit.Bus.PersonTellAsync("alpha", "dev2", "anything", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev2");
        Assert.Equal(MessageTypes.Failed, outcome.Type);
        Assert.Equal(FailureClasses.Credential, Payload.Field(outcome, "failureClass"));
        Assert.Equal(GatewayVerdict.CredentialReason, Payload.Field(outcome, "reason"));
        Assert.Equal(0, Run(outcome).GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task GrokPlaceholderOnlyAuthJsonIsCleanAndARealOneIsDeleted()
    {
        await using var kit = await LaunchKit.StartAsync();
        var grokHome = AgentPresets.GrokHome(kit.Layout("dev2", AgentCli.Grok));
        var real = false;
        kit.Backend.Script = async (req, p) =>
        {
            var env = LaunchKit.Env(req);
            if (env.ContainsKey("GROK_HOME"))
            {
                // grok 1.0.70 in login mode writes exactly this; the second run plants a "real" login.
                var token = env["YAWBLE_RUN_TOKEN"];
                var json = real
                    ? """{"key":"fake-real-session-not-a-credential","auth_mode":"oauth","refresh_token":"fake"}"""
                    : $$"""{"key":"{{token}}","auth_mode":"external"}""";
                await File.WriteAllTextAsync(Path.Combine(env["GROK_HOME"], "auth.json"), json);
            }
            p.Exit(0);
        };
        await kit.Bus.PersonTellAsync("alpha", "dev2", "first", ct: Ct);
        var first = await kit.OutcomeAsync("dev2");
        Assert.Equal(MessageTypes.Completed, first.Type);
        Assert.Equal(0, Run(first).GetProperty("guardrails").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(grokHome, "auth.json")));

        real = true;
        await Wait.UntilAsync(() => kit.Bus.IsQuiescentAsync(Ct), "quiet");
        await kit.Bus.PersonTellAsync("alpha", "dev2", "second", ct: Ct);
        await Wait.UntilAsync(() => kit.Messages().Count(m => m.Source == "alpha/dev2" && m.Type == MessageTypes.Completed) == 2, "second run");
        var second = kit.Outcome("dev2");
        var g = Run(second).GetProperty("guardrails");
        Assert.Equal(1, g.GetArrayLength());
        Assert.Equal(PostRunChecks.GrokAuthFile, g[0].GetProperty("kind").GetString());
        Assert.Equal("deleted", g[0].GetProperty("action").GetString());
        Assert.False(File.Exists(Path.Combine(grokHome, "auth.json")));
    }

    [Fact]
    public async Task AClaudeCredentialsFileFromALoginIsDeletedAndRecorded()
    {
        await using var kit = await LaunchKit.StartAsync();
        kit.Backend.Script = async (req, p) =>
        {
            if (LaunchKit.Env(req).GetValueOrDefault("CLAUDE_CONFIG_DIR") is { } dir && dir.Contains("dev1"))
                await File.WriteAllTextAsync(Path.Combine(dir, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"fake"}}""");
            p.Exit(0);
        };
        await kit.Bus.PersonTellAsync("alpha", "dev1", "type /login", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev1");
        var g = Run(outcome).GetProperty("guardrails");
        Assert.Equal(PostRunChecks.ClaudeCredentialsFile, g[0].GetProperty("kind").GetString());
        Assert.False(File.Exists(Path.Combine(AgentPresets.ClaudeConfigDir(kit.Layout("dev1", AgentCli.Claude)), ".credentials.json")));
        Assert.Contains(PostRunChecks.ClaudeCredentialsFile, kit.Records.All().Single(r => r.Member == "dev1").Guardrails);
    }

    [Fact]
    public async Task ARunPastItsWallClockLimitEndsWithTimeoutAndFreesItsSlot()
    {
        await using var kit = await LaunchKit.StartAsync(limit: 1, idleSec: 3600, wallSec: 60);
        kit.Backend.Script = (req, p) =>
        {
            if (!LaunchKit.Env(req).GetValueOrDefault("CLAUDE_CONFIG_DIR", "").Contains("dev1")) p.Exit(0);
            return Task.CompletedTask; // dev1 loops forever
        };
        await kit.Bus.PersonTellAsync("alpha", "dev1", "loop forever", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev1");
        Assert.Equal(FailureClasses.Timeout, Payload.Field(outcome, "failureClass"));
        Assert.StartsWith("timeout", Payload.Field(outcome, "reason"));
        Assert.True(Run(outcome).GetProperty("killLatencyMs").GetInt64() < 5000);
        Assert.Equal(1, kit.Backend.Spawned[0].TimeoutKillCalls);
        var record = kit.Records.All().First(r => r.Member == "dev1");
        Assert.Equal("timeout", record.EndClass);
        Assert.False(record.TreeSurvived);
        // The slot is free again: the Manager (woken by agent.failed) gets to run.
        await Wait.UntilAsync(() => kit.Records.All().Any(r => r.Member == "Manager" && r.EndClass == "completed"), "the Manager's run");
    }

    [Fact]
    public async Task ARunThatStopsCallingProgressEndsWithIdle()
    {
        await using var kit = await LaunchKit.StartAsync(idleSec: 60, wallSec: 3600);
        kit.Backend.Script = (_, _) => Task.CompletedTask; // never exits, never calls progress
        await kit.Bus.PersonTellAsync("alpha", "dev2", "go quiet", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev2");
        Assert.Equal(FailureClasses.Idle, Payload.Field(outcome, "failureClass"));
        Assert.Equal("idle: no progress for 60 s", Payload.Field(outcome, "reason"));
    }

    [Fact]
    public async Task ProgressThroughTheGatewayKeepsAnIdleRunAlive()
    {
        await using var kit = await LaunchKit.StartAsync(idleSec: 60, wallSec: 3600);
        kit.Gateways.ProgressEvery = TimeSpan.FromMilliseconds(100); // 6 s in run time, against a 60 s limit
        kit.Backend.Script = async (_, p) =>
        {
            await Task.Delay(2500); // 150 s in run time: well past the idle limit
            p.Exit(0);
        };
        await kit.Bus.PersonTellAsync("alpha", "dev1", "busy but reporting", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev1");
        Assert.Equal(MessageTypes.Completed, outcome.Type);
    }

    [Fact]
    public async Task StopOnTheCardEndsTheRunAndTheNextQueuedMemberGetsTheSlot()
    {
        await using var kit = await LaunchKit.StartAsync(limit: 1, idleSec: 3600, wallSec: 3600);
        kit.Backend.Script = (req, p) =>
        {
            if (!LaunchKit.Env(req).GetValueOrDefault("CLAUDE_CONFIG_DIR", "").Contains("dev1")) p.Exit(0);
            return Task.CompletedTask;
        };
        await kit.Bus.PersonTellAsync("alpha", "dev1", "hang", ct: Ct);
        await Wait.UntilAsync(() => kit.Registry.ForMember("alpha", "dev1") is not null, "dev1 running");
        await kit.Bus.PersonTellAsync("alpha", "dev2", "wait your turn", ct: Ct);
        await Wait.UntilAsync(async () => (await kit.Bus.TeamStatusAsync("alpha", Ct)).Single(m => m.Name == "dev2").State == "queued", "dev2 queued");
        Assert.Single(kit.Backend.Spawned);

        // The card's Stop, through the shell's RPC.
        var rpc = new RpcDispatcher(new SandboxPlatformInfo(), () => [], kit.Registry);
        var listed = (await rpc.DispatchAsync("runs.list", null, Ct))!.AsArray();
        Assert.Equal("dev1", listed.Single()!["member"]!.GetValue<string>());
        var stopped = await rpc.DispatchAsync("runs.stop", new JsonObject { ["team"] = "alpha", ["member"] = "dev1" }, Ct);
        Assert.True(stopped!["stopped"]!.GetValue<bool>());

        var outcome = await kit.OutcomeAsync("dev1");
        Assert.Equal(FailureClasses.Stopped, Payload.Field(outcome, "failureClass"));
        Assert.Equal("stopped by the person", Payload.Field(outcome, "reason"));
        Assert.Equal(MessageTypes.Completed, (await kit.OutcomeAsync("dev2")).Type);
    }

    [Theory]
    [InlineData("/home/u/.yawble/teams/alpha/members/dev1/clone", "dev1", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Yawble\teams\alpha\members\dev1\clone", "dev1", true)]
    [InlineData(@"C:\Users\u\teams\alpha\members\dev10\clone", "dev1", false)]
    [InlineData("/home/u/teams/alpha/members/Manager/clone", "dev1", false)]
    public void ARequestIsMatchedToItsMemberWithEitherPathSeparator(string cwd, string member, bool expected)
    {
        var json = new JsonObject { ["process"] = new JsonObject { ["cwd"] = cwd } }.ToJsonString();
        Assert.Equal(expected, LaunchKit.IsFor(json, member));
    }

    [Fact]
    public async Task NoCredentialSetUpFailsAtOnceWithAReason()
    {
        await using var kit = await LaunchKit.StartAsync(credentials: new FakeCredentialModes(claude: CredentialMode.ClaudeSubscription, grok: null));
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };
        await kit.Bus.PersonTellAsync("alpha", "dev2", "anything", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev2");
        Assert.Equal(FailureClasses.StartFailed, Payload.Field(outcome, "failureClass"));
        Assert.Contains("no model credential is set up for grok", Payload.Field(outcome, "reason"));
        // dev2's agent.failed wakes the team's Manager (a claude member), so a sandbox does start: the Manager's.
        // Wait for it, so the assertion below does not race it on any OS; only dev2 must have had none.
        Assert.Equal(MessageTypes.Completed, (await kit.OutcomeAsync("Manager")).Type);
        lock (kit.Backend.Requests) Assert.DoesNotContain(kit.Backend.Requests, r => LaunchKit.IsFor(r.Json, "dev2"));
        lock (kit.Gateways.Started) Assert.DoesNotContain(kit.Gateways.Started, g => g.Run.Member == "dev2");
    }

    [Fact]
    public async Task ASandboxRefusalFailsTheRunWithSandboxMessage()
    {
        await using var kit = await LaunchKit.StartAsync();
        kit.Backend.Refuse = new SandboxStartException(4, "the sandbox refused the request (status 4): Bubblewrap (bwrap) is not installed");
        await kit.Bus.PersonTellAsync("alpha", "dev1", "anything", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev1");
        Assert.Equal(FailureClasses.StartFailed, Payload.Field(outcome, "failureClass"));
        Assert.Contains("bwrap", Payload.Field(outcome, "reason"));
        Assert.True(kit.Gateways.Started.Single(g => g.Run.Member == "dev1").Disposed);
    }

    [Fact]
    public async Task ACoreStopKillsTheSandboxAndLeavesTheRunToBeReportedInterrupted()
    {
        var kit = await LaunchKit.StartAsync();
        try
        {
            kit.Backend.Script = (_, _) => Task.CompletedTask;
            await kit.Bus.PersonTellAsync("alpha", "dev1", "hang", ct: Ct);
            await Wait.UntilAsync(() => kit.Registry.ForMember("alpha", "dev1") is not null, "dev1 running");
            await kit.Bus.DisposeAsync();
            Assert.Equal(1, kit.Backend.Spawned.Single().KillCalls);
            Assert.Equal("interrupted", kit.Records.All().Single(r => r.Member == "dev1").EndClass);
            Assert.DoesNotContain(kit.Messages(), m => m.Source == "alpha/dev1" && m.Type == MessageTypes.Failed);
        }
        finally
        {
            kit.Db.Dispose();
        }
    }

    [Fact]
    public async Task AnInteractiveSessionTakesASlotHoldsTheMembersWorkAndTakesInput()
    {
        await using var kit = await LaunchKit.StartAsync(limit: 1, idleSec: 60, wallSec: 3600);
        kit.Backend.Script = (_, p) => Task.CompletedTask;
        var ended = new TaskCompletionSource<LaunchOutcome>();
        var ticket = await kit.Sessions.OpenAsync("alpha", "dev1", new PtySize(30, 100), o => ended.TrySetResult(o), Ct);
        Assert.True(await ticket.Admission);
        await Wait.UntilAsync(() => kit.Registry.ForMember("alpha", "dev1") is not null, "session running");
        var live = kit.Registry.ForMember("alpha", "dev1")!;
        Assert.Equal(RunKind.Interactive, live.Kind);
        Assert.Equal(RunKind.Interactive, kit.Backend.Requests.Single().Kind);
        Assert.Equal("session", (await kit.Bus.TeamStatusAsync("alpha", Ct)).Single(m => m.Name == "dev1").State);

        live.WriteInput("hello\r"u8);
        live.Resize(new PtySize(50, 160));
        var process = kit.Backend.Spawned.Single();
        Assert.Equal("hello\r", System.Text.Encoding.UTF8.GetString(process.InputBytes));
        Assert.Equal(new PtySize(50, 160), process.Resizes.Single());

        // No idle watchdog for a session: 2 s here is 120 s of run time against a 60 s idle limit.
        await Task.Delay(2000, Ct);
        Assert.False(ended.Task.IsCompleted);

        // While the session holds the only slot, work for dev1 and dev2 waits.
        await kit.Bus.PersonTellAsync("alpha", "dev1", "after the session", ct: Ct);
        await kit.Bus.PersonTellAsync("alpha", "dev2", "behind the session", ct: Ct);
        await Task.Delay(300, Ct);
        Assert.Single(kit.Backend.Spawned);
        await Assert.ThrowsAsync<BusRefusal>(() => kit.Sessions.OpenAsync("alpha", "dev1", PtySize.Default, ct: Ct));

        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };
        Assert.True(kit.Registry.Stop("alpha", "dev1"));
        var outcome = await ended.Task;
        Assert.Equal(RunEnd.Stopped, outcome.End);
        await ticket.Completion;
        Assert.Equal("interactive", kit.Records.Get(outcome.RunKey)!.Kind);
        await kit.OutcomeAsync("dev1");
        await kit.OutcomeAsync("dev2");
    }

    [Fact]
    public async Task ASessionWaitsForASlotInFifoOrder()
    {
        await using var kit = await LaunchKit.StartAsync(limit: 1, idleSec: 3600, wallSec: 3600);
        kit.Backend.Script = (_, _) => Task.CompletedTask;
        await kit.Bus.PersonTellAsync("alpha", "dev2", "hold the slot", ct: Ct);
        await Wait.UntilAsync(() => kit.Registry.ForMember("alpha", "dev2") is not null, "dev2 running");
        var ticket = await kit.Sessions.OpenAsync("alpha", "dev1", PtySize.Default, ct: Ct);
        Assert.Equal(1, await ticket.PositionAsync());
        Assert.False(ticket.Admission.IsCompleted);
        await Assert.ThrowsAsync<BusRefusal>(() => kit.Sessions.OpenAsync("alpha", "dev2", PtySize.Default, ct: Ct));

        kit.Registry.Stop("alpha", "dev2");
        Assert.True(await ticket.Admission);
        await Wait.UntilAsync(() => kit.Registry.ForMember("alpha", "dev1") is not null, "session running");
        kit.Registry.Stop("alpha", "dev1");
        await ticket.Completion;
    }
}
