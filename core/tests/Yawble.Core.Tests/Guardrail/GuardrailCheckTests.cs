using System.Text.Json.Nodes;
using Yawble.Core.Guardrail;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Guardrail;

/// <summary>
/// The guardrail check's own logic, without the sandbox: the request it builds (the member's exact policy, only the command
/// line swapped), how it judges attempts on the host, and that it FAILS when nothing isolates the probe.
/// </summary>
public class GuardrailCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Line(string id, string outcome, string evidence = "x", string? extra = null) =>
        $"{{\"id\":\"{id}\",\"category\":\"t\",\"outcome\":\"{outcome}\",\"evidence\":\"{evidence}\"{(extra is null ? "" : "," + extra)}}}";

    /// <summary>Every planned attempt reported blocked (writes: created in the sandbox), positive controls allowed.</summary>
    private static string AllBlocked(JsonObject plan, Func<string, string?>? overrideLine = null)
    {
        var lines = new List<string>();
        foreach (var r in plan["reads"]!.AsArray()) lines.Add(Line(r!["id"]!.GetValue<string>(), "blocked", extra: r["mode"]!.GetValue<string>() == "list" ? "\"entries\":0" : null));
        foreach (var w in plan["writes"]!.AsArray())
        {
            var id = w!["id"]!.GetValue<string>();
            if (id == "write.clone") File.WriteAllText(w["path"]!.GetValue<string>(), plan["nonce"]!.GetValue<string>()); // reaches the host
            lines.Add(Line(id, "allowed", extra: "\"sandboxWrote\":true"));
        }
        foreach (var t in plan["tcp"]!.AsArray()) lines.Add(Line(t!["id"]!.GetValue<string>(), "blocked"));
        lines.Add(Line("net.dns", "blocked"));
        lines.Add(Line("gateway.reachable", "allowed"));
        foreach (var id in new[] { "proc.host-signal", "proc.host-environ", "proc.host-ptrace", "credstore.dbus-session", "credstore.dbus-env", "display.env", "display.x11-socket", "secrets.scan" })
            lines.Add(Line(id, "blocked"));
        lines.Add(Line("secrets.run-token", "info", "the run token is in: YAWBLE_RUN_TOKEN"));
        return string.Join('\n', lines.Select(l => overrideLine?.Invoke(l) ?? l));
    }

    /// <summary>
    /// On Windows the drive root is enumerable (microsoft/mxc#1676), so the home's and the
    /// CLI config folder's NAMES are listable there (expected ALLOWED, an accepted residual); every denied folder that
    /// exists on the host (the personal folders by default) must still refuse a listing, on every OS.
    /// </summary>
    [Fact]
    public async Task EveryDeniedFolderRefusesAListingAndWindowsAcceptsTheHomesNames()
    {
        await using var kit = GuardrailKit.Create();
        foreach (var name in new[] { "Documents", "Downloads", "OneDrive" }) Directory.CreateDirectory(Path.Combine(kit.Home, name));
        JsonObject? seen = null;
        var sandbox = new ScriptedSandbox((_, plan) => { seen = plan; return new SandboxOutcome(0, 0, false, AllBlocked(plan), "", null); });
        var report = await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        var reads = seen!["reads"]!.AsArray().Select(r => (Id: r!["id"]!.GetValue<string>(), Mode: r["mode"]!.GetValue<string>())).ToList();
        foreach (var name in new[] { "Documents", "Downloads", "OneDrive" })
        {
            Assert.Contains(("home.denied-list:" + name, "list"), reads);
            Assert.Equal(Verdicts.Blocked, report.Results.Single(r => r.Id == "home.denied-list:" + name).Expected);
        }
        // Not on the host: not attempted (a listing of a missing folder proves nothing).
        Assert.DoesNotContain(reads, r => r.Id == "home.denied-list:Pictures");
        var homeList = report.Results.Single(r => r.Id == "home.list");
        Assert.Equal(OperatingSystem.IsWindows() ? Verdicts.Allowed : Verdicts.Blocked, homeList.Expected);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task TheRequestIsTheMembersOwnWithOnlyTheProbeAsItsCommand()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the plan's OS-specific attempts are asserted for Linux here");
        await using var kit = GuardrailKit.Create();
        var sandbox = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false, AllBlocked(plan), "", null));
        await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        var request = JsonNode.Parse(sandbox.LastRequest!)!.AsObject();
        var layout = kit.Layout("payments", "ada", AgentCli.Claude);
        // The policy part is what the member's own run gets: compile it the launcher's way and compare everything but
        // the command line, the timeout, and the per-run token/port/ids.
        var fs = request["filesystem"]!.AsObject();
        Assert.Contains(HostPathCanonicalizer.Canonicalize(layout.ClonePath), fs["readwritePaths"]!.AsArray().Select(n => n!.GetValue<string>()));
        var denied = fs["deniedPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        foreach (var d in new[] { layout.DataDir, layout.DatabasePath, layout.HostGrokHome, Path.Combine(kit.App, "teams", "docs") })
            Assert.Contains(HostPathCanonicalizer.Canonicalize(d), denied);
        Assert.Equal("deny", request["network"]!["egress"]!["default"]!.GetValue<string>());
        Assert.StartsWith("http://127.0.0.1:", request["runtimeConfig"]!["networkProxy"]!.GetValue<string>());
        Assert.False(request["telemetry"]!["enabled"]!.GetValue<bool>());
        var commandLine = request["process"]!["commandLine"]!.GetValue<string>();
        Assert.StartsWith(HostPathCanonicalizer.Canonicalize(Path.Combine(kit.Install, "bin", "yawble-guardcheck")) + " --plan ", commandLine);
        Assert.True(request["process"]!["timeout"]!.GetValue<long>() <= 120_000);
        var env = request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains(env, e => e.StartsWith("CLAUDE_CODE_OAUTH_TOKEN=yawrt_", StringComparison.Ordinal));
        Assert.DoesNotContain(env, e => FakeSecrets.All.Any(s => e.Contains(s, StringComparison.Ordinal)));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task AWriteThatStaysInTheSandboxIsBlockedAndSaysSo()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the plan's OS-specific attempts are asserted for Linux here");
        await using var kit = GuardrailKit.Create();
        var sandbox = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false, AllBlocked(plan), "", null));
        var report = await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        var data = report.Results.Single(r => r.Id == "write.app-data");
        Assert.Equal(Verdicts.Blocked, data.Verdict);
        Assert.True(data.SandboxOnly);
        Assert.Contains("never reached the host", data.Evidence);
        Assert.Contains(GuardrailCheck.TmpfsNote, report.Notes);
        var clone = report.Results.Single(r => r.Id == "write.clone");
        Assert.Equal(Verdicts.Allowed, clone.Verdict);
        Assert.True(clone.AsExpected);
        Assert.Empty(Directory.EnumerateFiles(kit.Layout("payments", "ada", AgentCli.Claude).ClonePath, ".yawble-guardcheck-*"));
        // The gateway refusals were never made (no real probe), so they are MISSING and the check does not pass.
        Assert.Contains(report.Results, r => r.Id == "gateway.connect-denied-host" && r.Verdict == Verdicts.Missing);
        Assert.False(report.Passed);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task AWriteThatReachesTheHostIsAllowedEvenIfTheProbeSaysBlockedAndIsCleanedUp()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the plan's OS-specific attempts are asserted for Linux here");
        await using var kit = GuardrailKit.Create();
        string? leaked = null;
        var sandbox = new ScriptedSandbox((_, plan) =>
        {
            var w = plan["writes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "write.host-home")!;
            leaked = w["path"]!.GetValue<string>();
            File.WriteAllText(leaked, plan["nonce"]!.GetValue<string>());
            return new SandboxOutcome(0, 0, false, AllBlocked(plan, l => l.Contains("\"write.host-home\"") ? Line("write.host-home", "blocked") : null), "", null);
        });
        var report = await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        var r = report.Results.Single(x => x.Id == "write.host-home");
        Assert.Equal(Verdicts.Allowed, r.Verdict);
        Assert.False(r.AsExpected);
        Assert.Contains("reached the host", r.Evidence);
        Assert.False(File.Exists(leaked));
        Assert.False(report.Passed);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task AFolderOnTheWayToAGrantedPathInTheHomeListingIsAMountPointNotALeak()
    {
        // The Linux tarball installs to ~/.local/opt/yawble, and every run grants its bin/ (the probe) and roles
        // read-only, so bwrap creates ~/.local as a mount point and a listing of the home folder shows it. An earlier run
        // (42168545756) failed home.list on exactly that. A real host entry beside it must still count as a leak.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the plan's OS-specific attempts are asserted for Linux here");
        await using var kit = GuardrailKit.Create(installUnderHome: true);
        Directory.CreateDirectory(Path.Combine(kit.Home, "Documents"));
        string Listing(params string[] names) =>
            Line("home.list", "allowed", extra: $"\"entries\":{names.Length},\"names\":[{string.Join(',', names.Select(n => $"\"{n}\""))}]");

        var onlyTheWay = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false,
            AllBlocked(plan, l => l.Contains("\"home.list\"") ? Listing(".local") : null), "", null));
        var ok = (await kit.Check(onlyTheWay).RunAsync("payments", "ada", Ct)).Results.Single(r => r.Id == "home.list");
        Assert.Equal(Verdicts.Blocked, ok.Verdict);
        Assert.Contains(".local", ok.Evidence);

        var withHostEntry = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false,
            AllBlocked(plan, l => l.Contains("\"home.list\"") ? Listing(".local", "Documents") : null), "", null));
        var leak = (await kit.Check(withHostEntry).RunAsync("payments", "ada", Ct)).Results.Single(r => r.Id == "home.list");
        Assert.Equal(Verdicts.Allowed, leak.Verdict);
        Assert.False(leak.AsExpected);
        Assert.Contains("(1)", leak.Evidence);
    }

    [Fact]
    public async Task ASandboxRefusalIsTheReportNotAnException()
    {
        await using var kit = GuardrailKit.Create();
        var sandbox = new ScriptedSandbox((_, _) => new SandboxOutcome(12, -1, false, "", "", "Bubblewrap: slirp4netns startup failed"));
        var report = await kit.Check(sandbox).RunAsync(null, null, Ct);

        Assert.False(report.Passed);
        Assert.Equal("Bubblewrap: slirp4netns startup failed", report.Sandbox.Refusal);
        Assert.Empty(report.Results);
        Assert.True(report.Subject.Synthetic);
        if (OperatingSystem.IsLinux()) Assert.Contains(report.Notes, n => n.Contains("AppArmor"));
        // Saved, and guardrail.last returns it.
        Assert.Equal(report.Id, kit.Check(sandbox).Last(null, null)!.Id);
        // Planted canaries and decoy folders are gone again.
        Assert.Empty(Directory.EnumerateFiles(kit.Data, ".yawble-guardcheck-*"));
        Assert.False(Directory.Exists(Path.Combine(kit.App, "teams", GuardrailCheck.Decoy)));
    }

    /// <summary>The sandbox cannot bind a missing grant and blames slirp4netns; the check names the missing path instead.</summary>
    [Fact]
    public async Task AMissingGrantedPathIsNamedAndSandboxIsNotCalled()
    {
        await using var kit = GuardrailKit.Create();
        // The detector still names grok's install folder, but it is gone from the disk (uninstalled during the run).
        var grok = Path.Combine(kit.Root, "host-clis", "grok");
        Directory.Delete(grok, recursive: true);
        // The real backend: its preflight refuses before the sandbox is called, so this needs no sandbox on the host.
        var report = await kit.Check(new NativeSandboxBackend()).RunAsync("payments", "ben", Ct);

        Assert.False(report.Passed);
        Assert.Contains("granted path(s) missing", report.Sandbox.Refusal);
        Assert.Contains(HostPathCanonicalizer.Canonicalize(grok), report.Sandbox.Refusal);
        Assert.Equal(-2, report.Sandbox.Status);
        Assert.Equal("start_failed", report.Run!.End);
        Assert.DoesNotContain(report.Notes, n => n.Contains("AppArmor"));
    }

    /// <summary>
    /// A run the launcher refuses before the sandbox is called (here: an install folder inside the app data root, as in a
    /// careless dev layout) is reported with the launcher's reason, and without the AppArmor hint, which is only about
    /// The sandbox refusing namespaces.
    /// </summary>
    [Fact]
    public async Task ARunRefusedBeforeSandboxNamesTheReasonWithoutTheAppArmorHint()
    {
        await using var kit = GuardrailKit.Create();
        var inside = new LaunchHost(TargetOsExtensions.Host, kit.Home, kit.App, Path.Combine(kit.App, "install"), kit.Host.Clis,
            HostPathCanonicalizer.Canonicalize);
        var sandbox = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false, AllBlocked(plan), "", null));
        var launcher = new SandboxLauncher(inside, new SqliteMemberDirectory(kit.ConnectionString),
            new PolicyStore(kit.ConnectionString, canonicalize: HostPathCanonicalizer.Canonicalize), new FakeCredentialModes(),
            new GatewayRunFactory(kit.Gateways), new GuardrailSandboxBackend(sandbox), new RunRecordStore(kit.ConnectionString), new RunRegistry());
        var report = await new GuardrailCheck(launcher, kit.ConnectionString) { DecoyListener = kit.DecoyListener }.RunAsync("payments", "ben", Ct);

        Assert.False(report.Passed);
        Assert.Null(sandbox.LastRequest);
        Assert.Equal(-1, report.Sandbox.Status);
        Assert.Contains("is inside the app data root", report.Sandbox.Refusal);
        Assert.DoesNotContain(report.Notes, n => n.Contains("AppArmor"));
    }

    /// <summary>
    /// The check is the member's real launch with the probe as its command: the launcher records it in run_records,
    /// its per-run gateway issues and then revokes the run token, the decoy folders are denied like members, and the
    /// gateway forwards nothing to a model vhost. With no model credential set up it still runs, and says so.
    /// </summary>
    [Fact]
    public async Task TheCheckIsTheLaunchersRunOfTheMember()
    {
        await using var kit = GuardrailKit.Create();
        var sandbox = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false, AllBlocked(plan), "", null));
        var report = await kit.Check(sandbox, new FakeCredentialModes(claude: null, grok: null)).RunAsync("payments", "ada", Ct);

        Assert.Equal(0, report.Sandbox.Status); // the scripted probe omits the gateway lines; the real-sandbox tests have them
        var run = report.Run!;
        Assert.Equal("completed", run.End);
        Assert.Equal("scripted", run.Backend);
        Assert.False(run.CredentialConfigured);
        Assert.Equal(nameof(CredentialMode.ClaudeSubscription), run.CredentialMode);
        Assert.Contains(report.Notes, n => n.Contains("No model credential is set up for claude"));
        Assert.Contains(report.Results, r => r.Id == "check.no-model-call" && r.Verdict == Verdicts.Blocked && r.AsExpected);

        var record = kit.Records.Get(run.RunKey)!;
        Assert.Equal("ada", record.Member);
        Assert.Equal("headless", record.Kind);
        Assert.Equal("completed", record.EndClass);
        Assert.Null(record.BusRunId);

        var request = JsonNode.Parse(sandbox.LastRequest!)!.AsObject();
        var denied = request["filesystem"]!["deniedPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains(HostPathCanonicalizer.Canonicalize(Path.Combine(kit.App, "teams", "payments", "members", GuardrailCheck.Decoy)), denied);
        var token = request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>())
            .Single(e => e.StartsWith("CLAUDE_CODE_OAUTH_TOKEN=", StringComparison.Ordinal)).Split('=', 2)[1];
        Assert.Equal(TokenCheck.Revoked, kit.Gateways.Tokens.Check(run.RunNumber!.Value, token)); // revoked when the launch ended
    }

    [Fact]
    public async Task AnUnknownMemberIsRefused()
    {
        await using var kit = GuardrailKit.Create();
        var e = await Assert.ThrowsAsync<GuardrailException>(() =>
            kit.Check(new ScriptedSandbox((_, _) => throw new InvalidOperationException())).RunAsync("payments", "nobody", Ct));
        Assert.Equal("unknown_member", e.Code);
    }

    /// <summary>
    /// The real probe with NO sandbox around it: the check must FAIL, with the escapes it can see marked ALLOWED. This is
    /// the negative control for every real-sandbox pass: the verdicts come from what happened on the host.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task WithoutASandboxTheCheckFails()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the unsandboxed run uses the POSIX probe wrapper");
        await using var kit = GuardrailKit.Create();
        var report = await kit.Check(new UnsandboxedRunner()).RunAsync("payments", "ada", Ct);

        Assert.False(report.Passed);
        string V(string id) => report.Results.Single(r => r.Id == id).Verdict;
        Assert.Equal(Verdicts.Allowed, V("app.data"));
        Assert.Equal(Verdicts.Allowed, V("app.grok-home"));
        Assert.Equal(Verdicts.Allowed, V("member.other-team"));
        Assert.Equal(Verdicts.Allowed, V("home.ssh"));
        Assert.Equal(Verdicts.Allowed, V("home.claude-credentials"));
        Assert.Equal(Verdicts.Allowed, V("write.host-home"));
        Assert.Equal(Verdicts.Allowed, V("net.host-loopback"));
        // The gateway is real, so its refusals hold even without a sandbox, and are logged.
        Assert.Equal(Verdicts.Allowed, V("gateway.reachable"));
        var connect = report.Results.Single(r => r.Id == "gateway.connect-denied-host");
        Assert.Equal(Verdicts.Blocked, connect.Verdict);
        Assert.True(connect.Logged);
        Assert.True(report.Results.Single(r => r.Id == "gateway.connect-vendor:api.anthropic.com").Logged);
        Assert.Equal(Verdicts.Blocked, V("gateway.guessed-token"));
        Assert.Contains(kit.GatewayEvents(), e => e.Kind == "connect-denied" && e.Target!.StartsWith("example.com", StringComparison.Ordinal) && e.RunId < 0);
        // Nothing the probe wrote is left on the host.
        Assert.False(File.Exists(Path.Combine(kit.Home, ".yawble-guardcheck-" + report.Id + "-w")));
        // The secret scan only looks inside the member's own folders, where no secret is.
        Assert.Equal(Verdicts.Blocked, V("secrets.scan"));
    }

    /// <summary>
    /// A member's check is one of its runs: it waits for a global run slot in the bus's queue (limit 1 here, held by a
    /// session of docs/cy) and runs once that slot is free; while the member already holds a slot it is refused.
    /// </summary>
    [Fact]
    public async Task AMembersCheckWaitsForARunSlotAndIsRefusedWhileTheMemberHoldsOne()
    {
        await using var kit = GuardrailKit.Create();
        await using var bus = new Yawble.Core.Bus.BusEngine(new Yawble.Core.Bus.BusStore(kit.ConnectionString),
            new Yawble.Core.Bus.FakeAgentRunner(Yawble.Core.Bus.FakeAgentRunner.Mode.Hang),
            new Yawble.Core.Bus.BusOptions { MaxConcurrentAgents = () => 1 });
        await bus.StartAsync(Ct);
        var sandbox = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false, AllBlocked(plan), "", null));
        var check = new GuardrailCheck(kit.Launcher(new GuardrailSandboxBackend(sandbox)), kit.ConnectionString,
            slot: GuardrailRunSlots.From(bus)) { DecoyListener = kit.DecoyListener };

        var release = new TaskCompletionSource();
        var cy = await bus.RequestSessionAsync("docs", "cy", _ => release.Task, Ct);
        Assert.True(await cy.Admission);

        var running = check.RunAsync("payments", "ada", Ct);
        await Task.Delay(500, Ct);
        Assert.False(running.IsCompleted);
        Assert.Null(sandbox.LastRequest); // still waiting for the slot

        release.SetResult();
        var report = await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal("completed", report.Run!.End);
        Assert.NotNull(sandbox.LastRequest);

        var hold = new TaskCompletionSource();
        var ada = await bus.RequestSessionAsync("payments", "ada", _ => hold.Task, Ct);
        Assert.True(await ada.Admission);
        var busy = await Assert.ThrowsAsync<GuardrailException>(() => check.RunAsync("payments", "ada", Ct));
        Assert.Equal("busy", busy.Code);
        hold.SetResult();
        await ada.Completion;
    }

    /// <summary>
    /// A member's check is an entry in the ONE global FIFO (run_queue), beside agent runs of other teams. It
    /// gets no jump: queued between two runs it is admitted after the first and before the second, and the scheduler
    /// shows it with its position while it waits. Limit 1, held first by a terminal session of docs/cy.
    /// </summary>
    [Fact]
    public async Task AMembersCheckWaitsInTheSameFifoAsAgentRuns()
    {
        await using var kit = GuardrailKit.Create();
        await using var bus = new Yawble.Core.Bus.BusEngine(new Yawble.Core.Bus.BusStore(kit.ConnectionString),
            new Yawble.Core.Bus.FakeAgentRunner(Yawble.Core.Bus.FakeAgentRunner.Mode.Hang),
            new Yawble.Core.Bus.BusOptions { MaxConcurrentAgents = () => 1, SafetyPoll = TimeSpan.FromMilliseconds(100) });
        await bus.CreateTeamAsync("ops", ct: Ct);
        await bus.AddMemberAsync("ops", "dan", ct: Ct);
        await bus.AddMemberAsync("ops", "eve", ct: Ct);
        await bus.StartAsync(Ct);
        var sandbox = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false, AllBlocked(plan), "", null));
        var check = new GuardrailCheck(kit.Launcher(new GuardrailSandboxBackend(sandbox)), kit.ConnectionString,
            slot: GuardrailRunSlots.From(bus)) { DecoyListener = kit.DecoyListener };
        async Task<IReadOnlyList<(string, string, string)>> Queue() =>
            (await bus.SchedulerSnapshotAsync(Ct)).Queue.Select(q => (q.Team, q.Member, q.Kind)).ToArray();

        var release = new TaskCompletionSource();
        var cy = await bus.RequestSessionAsync("docs", "cy", _ => release.Task, Ct);
        Assert.True(await cy.Admission);

        // Oldest first: dan's run, then ada's check, then eve's run.
        await bus.PersonTellAsync("ops", "dan", "job dan", ct: Ct);
        await Yawble.Core.Tests.Bus.Wait.UntilAsync(async () => (await Queue()).Count == 1, "dan queued");
        var progress = new GuardrailProgress();
        var running = check.RunAsync("payments", "ada", Ct, progress);
        await Yawble.Core.Tests.Bus.Wait.UntilAsync(async () => (await Queue()).Count == 2, "ada's check queued");
        await bus.PersonTellAsync("ops", "eve", "job eve", ct: Ct);
        await Yawble.Core.Tests.Bus.Wait.UntilAsync(async () => (await Queue()).Count == 3, "eve queued");
        Assert.Equal([("ops", "dan", "run"), ("payments", "ada", "interactive"), ("ops", "eve", "run")], await Queue());
        Assert.Equal(2, await progress.PositionAsync());
        Assert.Equal(1, (await bus.SchedulerSnapshotAsync(Ct)).Running);

        // cy's slot goes to dan, not to the check.
        release.SetResult();
        await cy.Completion;
        await Yawble.Core.Tests.Bus.Wait.UntilAsync(async () => (await Queue()).Count == 2, "dan admitted");
        Assert.Equal([("payments", "ada", "interactive"), ("ops", "eve", "run")], await Queue());
        await Task.Delay(300, Ct);
        Assert.Null(sandbox.LastRequest);
        Assert.False(running.IsCompleted);

        // dan stops: the check takes the slot before eve, runs, and gives the slot back to eve.
        Assert.True(await bus.StopMemberAsync("ops", "dan", Ct));
        var report = await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal("completed", report.Run!.End);
        Assert.NotNull(sandbox.LastRequest);
        await Yawble.Core.Tests.Bus.Wait.UntilAsync(async () =>
            (await bus.SchedulerSnapshotAsync(Ct)).Live.Any(l => l.Member == "eve"), "eve admitted after the check");
        Assert.Equal(1, (await bus.SchedulerSnapshotAsync(Ct)).Running);
    }
}
