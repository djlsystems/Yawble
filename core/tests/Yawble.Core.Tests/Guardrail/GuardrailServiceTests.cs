using System.Diagnostics;
using System.Text.Json.Nodes;
using Yawble.Core.Guardrail;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Agents;
using Xunit;

namespace Yawble.Core.Tests.Guardrail;

/// <summary>The check's IPC shape (guardrail.start / guardrail.get), its wiring, and the CLI.</summary>
[Collection(RealSandboxGuardrailCollection.Name)]
public class GuardrailServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeCliDetector Clis(GuardrailKit kit) => TestClis.StandIns(kit.Root, kit.Home);

    private static Func<string, string?> Env(GuardrailKit kit) => name => name == LaunchWiring.InstallDirVariable ? kit.Install : null;

    private static GuardrailService Service(GuardrailKit kit, IGuardrailSandbox sandbox) =>
        GuardrailService.Create(kit.Launcher(new GuardrailSandboxBackend(sandbox)), null, kit.ConnectionString, decoyListener: kit.DecoyListener);

    [Fact]
    public async Task ItIsOffWithAReasonWhenTheLayoutCannotBeHonoured()
    {
        await using var kit = GuardrailKit.Create();
        // The check rides on the launcher, so it is off whenever the launcher is, for the same reason.
        await using (var wrong = LaunchWiring.Create(kit.App, kit.ConnectionString, Clis(kit), null, new RunRegistry(), Env(kit), startKeeper: false))
            Assert.Contains("<app root>/data", GuardrailService.Create(wrong.Launcher, wrong.Problem, kit.ConnectionString).Problem);
        await using (var none = LaunchWiring.Create(kit.Data, kit.ConnectionString, Clis(kit), null, new RunRegistry(), _ => null, startKeeper: false))
            Assert.Contains("no install folder", GuardrailService.Create(none.Launcher, none.Problem, kit.ConnectionString).Problem);
        await using var wired = LaunchWiring.Create(kit.Data, kit.ConnectionString, Clis(kit), null, new RunRegistry(), Env(kit), startKeeper: false);
        Assert.Null(wired.Problem);
        Assert.Null(GuardrailService.Create(wired.Launcher, null, kit.ConnectionString).Problem);
        File.Delete(Path.Combine(kit.Install, "bin", OperatingSystem.IsWindows() ? "yawble-guardcheck.exe" : "yawble-guardcheck"));
        var noProbe = GuardrailService.Create(wired.Launcher, null, kit.ConnectionString);
        Assert.Contains("probe is not installed", noProbe.Problem);

        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], guardrail: noProbe);
        var e = await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsync("guardrail.start", null, Ct));
        Assert.Equal("unavailable", e.Code);
        var get = await dispatcher.DispatchAsync("guardrail.get", null, Ct);
        Assert.Equal("none", get!["state"]!.GetValue<string>());
        Assert.Contains("probe is not installed", get["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task StartReturnsAtOnceAndGetReturnsTheReportWhenDone()
    {
        await using var kit = GuardrailKit.Create();
        var release = new TaskCompletionSource();
        var sandbox = new GatedSandbox(release.Task);
        var service = Service(kit, sandbox);
        Assert.Null(service.Problem);
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], guardrail: service);
        var ada = new JsonObject { ["team"] = "payments", ["member"] = "ada" };

        Assert.Equal("none", (await dispatcher.DispatchAsync("guardrail.get", (JsonObject)ada.DeepClone(), Ct))!["state"]!.GetValue<string>());
        var started = await dispatcher.DispatchAsync("guardrail.start", (JsonObject)ada.DeepClone(), Ct);
        Assert.Equal("running", started!["state"]!.GetValue<string>());
        Assert.Equal("ada", started["subject"]!["member"]!.GetValue<string>());
        Assert.Equal("running", (await dispatcher.DispatchAsync("guardrail.get", (JsonObject)ada.DeepClone(), Ct))!["state"]!.GetValue<string>());
        // A second start while running joins the running check.
        await dispatcher.DispatchAsync("guardrail.start", (JsonObject)ada.DeepClone(), Ct);

        release.SetResult();
        JsonNode? done = null;
        await Bus.Wait.UntilAsync(async () =>
            (done = await dispatcher.DispatchAsync("guardrail.get", (JsonObject)ada.DeepClone(), Ct))!["state"]!.GetValue<string>() == "done", "the check to finish");
        var report = done!["report"]!;
        Assert.Equal("payments", report["subject"]!["team"]!.GetValue<string>());
        Assert.Equal("Bubblewrap: refused for the test", report["sandbox"]!["refusal"]!.GetValue<string>());
        Assert.False(report["passed"]!.GetValue<bool>());
        Assert.Equal(1, sandbox.Runs);

        // Saved: a new service (a restarted core) answers guardrail.get from disk.
        var again = Service(kit, sandbox);
        Assert.Equal("done", again.Get((JsonObject)ada.DeepClone())["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task BadParamsAndUnknownMembersAreReported()
    {
        await using var kit = GuardrailKit.Create();
        var service = Service(kit, new GatedSandbox(Task.CompletedTask));
        var e = Assert.Throws<RpcException>(() => service.Start(new JsonObject { ["team"] = "payments" }));
        Assert.Equal("bad_params", e.Code);

        var nobody = new JsonObject { ["team"] = "payments", ["member"] = "nobody" };
        service.Start((JsonObject)nobody.DeepClone());
        JsonNode? got = null;
        await Bus.Wait.UntilAsync(() => (got = service.Get((JsonObject)nobody.DeepClone()))["state"]!.GetValue<string>() == "failed", "the check to fail");
        Assert.StartsWith("unknown_member", got!["error"]!.GetValue<string>());
    }

    /// <summary>The window's Stop on a check that waits for a run slot: guardrail.get says queued with its position,
    /// guardrail.stop takes it out of the queue, and no sandbox ever starts.</summary>
    [Fact]
    public async Task StopWithdrawsACheckThatWaitsForARunSlot()
    {
        await using var kit = GuardrailKit.Create();
        await using var bus = new Yawble.Core.Bus.BusEngine(new Yawble.Core.Bus.BusStore(kit.ConnectionString),
            new Yawble.Core.Bus.FakeAgentRunner(Yawble.Core.Bus.FakeAgentRunner.Mode.Hang),
            new Yawble.Core.Bus.BusOptions { MaxConcurrentAgents = () => 1 });
        await bus.StartAsync(Ct);
        var hang = new HangingBackend();
        var service = GuardrailService.Create(kit.Launcher(hang), null, kit.ConnectionString, slot: GuardrailRunSlots.From(bus), decoyListener: kit.DecoyListener);
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], guardrail: service);
        var ada = new JsonObject { ["team"] = "payments", ["member"] = "ada" };

        var release = new TaskCompletionSource();
        var cy = await bus.RequestSessionAsync("docs", "cy", _ => release.Task, Ct);
        Assert.True(await cy.Admission);

        await dispatcher.DispatchAsync("guardrail.start", (JsonObject)ada.DeepClone(), Ct);
        JsonNode? got = null;
        await Bus.Wait.UntilAsync(async () =>
            (got = await dispatcher.DispatchAsync("guardrail.get", (JsonObject)ada.DeepClone(), Ct))!["position"] is not null, "the check to be queued");
        Assert.Equal("running", got!["state"]!.GetValue<string>());
        Assert.Equal("queued", got["phase"]!.GetValue<string>());
        Assert.Equal(1, got["position"]!.GetValue<int>());

        var stop = await dispatcher.DispatchAsync("guardrail.stop", (JsonObject)ada.DeepClone(), Ct);
        Assert.True(stop!["stopped"]!.GetValue<bool>());
        await Bus.Wait.UntilAsync(async () =>
            (got = await dispatcher.DispatchAsync("guardrail.get", (JsonObject)ada.DeepClone(), Ct))!["state"]!.GetValue<string>() != "running", "the check to end");
        Assert.Equal("failed", got!["state"]!.GetValue<string>());
        Assert.StartsWith("stopped", got["error"]!.GetValue<string>());
        Assert.Equal(0, hang.Spawned);

        // The slot it would have taken goes to the next in line, not to a ghost.
        release.SetResult();
        await cy.Completion;
        var next = await bus.RequestSessionAsync("payments", "ada", _ => Task.CompletedTask, Ct);
        Assert.True(await next.Admission.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False((await dispatcher.DispatchAsync("guardrail.stop", (JsonObject)ada.DeepClone(), Ct))!["stopped"]!.GetValue<bool>());
    }

    /// <summary>The window's Stop on a check whose sandbox is up: guardrail.get says running, guardrail.stop ends the
    /// sandbox tree through the run registry, and the saved report says the run was stopped.</summary>
    [Fact]
    public async Task StopEndsARunningChecksSandboxAndTheReportSaysStopped()
    {
        await using var kit = GuardrailKit.Create();
        await using var bus = new Yawble.Core.Bus.BusEngine(new Yawble.Core.Bus.BusStore(kit.ConnectionString),
            new Yawble.Core.Bus.FakeAgentRunner(Yawble.Core.Bus.FakeAgentRunner.Mode.Hang),
            new Yawble.Core.Bus.BusOptions { MaxConcurrentAgents = () => 1 });
        await bus.StartAsync(Ct);
        var hang = new HangingBackend();
        var service = GuardrailService.Create(kit.Launcher(hang), null, kit.ConnectionString, slot: GuardrailRunSlots.From(bus), decoyListener: kit.DecoyListener);
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], guardrail: service);
        var ada = new JsonObject { ["team"] = "payments", ["member"] = "ada" };

        await dispatcher.DispatchAsync("guardrail.start", (JsonObject)ada.DeepClone(), Ct);
        await Bus.Wait.UntilAsync(() => Task.FromResult(hang.Spawned == 1), "the probe's sandbox to start");
        var got = await dispatcher.DispatchAsync("guardrail.get", (JsonObject)ada.DeepClone(), Ct);
        Assert.Equal("running", got!["phase"]!.GetValue<string>());
        Assert.Null(got["position"]);

        var stop = await dispatcher.DispatchAsync("guardrail.stop", (JsonObject)ada.DeepClone(), Ct);
        Assert.True(stop!["stopped"]!.GetValue<bool>());
        Assert.True(hang.Killed);
        await Bus.Wait.UntilAsync(async () =>
            (got = await dispatcher.DispatchAsync("guardrail.get", (JsonObject)ada.DeepClone(), Ct))!["state"]!.GetValue<string>() == "done", "the report");
        var report = got!["report"]!;
        Assert.Equal("stopped", report["run"]!["end"]!.GetValue<string>());
        Assert.False(report["passed"]!.GetValue<bool>());
        Assert.Contains(report["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("stopped before it finished"));
    }

    /// <summary>A sandbox that runs until it is killed.</summary>
    private sealed class HangingBackend : ISandboxBackend
    {
        private int _spawned;
        public int Spawned => Volatile.Read(ref _spawned);
        public volatile bool Killed;
        public string Name => "hanging";

        public ISandboxProcess Spawn(string requestJson)
        {
            Interlocked.Increment(ref _spawned);
            return new Proc(this);
        }

        public ISandboxProcess SpawnWithPty(string requestJson, PtySize size) => throw new NotSupportedException();

        private sealed class Proc(HangingBackend owner) : ISandboxProcess
        {
            public int Id => 0;
            public Stream? Output { get; } = new MemoryStream();
            public Stream? Error { get; } = new MemoryStream();
            public Stream? Input => null;
            public SandboxExit? TryWait() => owner.Killed ? new SandboxExit(137, false) : null;
            public void Kill() => owner.Killed = true;
            public void KillForTimeout() => owner.Killed = true;
            public void Resize(PtySize size) { }
            public void Dispose() { }
        }
    }

    private sealed class GatedSandbox(Task gate) : IGuardrailSandbox
    {
        public int Runs;

        public async Task<SandboxOutcome> RunAsync(string requestJson, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            await gate;
            return new SandboxOutcome(12, -1, false, "", "", "Bubblewrap: refused for the test");
        }
    }

    // ------------------------------------------------------------- real sandbox through the real core process

    private static ProcessStartInfo Core(GuardrailKit kit, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "yawble-core.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[Yawble.Core.Launch.LaunchWiring.InstallDirVariable] = kit.Install;
        psi.Environment["HOME"] = kit.Home; // the fake home: the check never looks at this machine's real one
        CoreLaunch.IsolateCompanionHome(psi); // never the real %APPDATA%\yawble-companion
        return psi;
    }

    /// <summary>The CLI / test harness: one real check in the sandbox, the report as JSON, exit 0 when it passed.</summary>
    [Fact]
    public async Task TheCliRunsTheCheckInARealSandbox()
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        await using var kit = GuardrailKit.Create();
        using var p = Process.Start(Core(kit, "guardrail-check", "--data-dir", kit.Data, "--team", "payments", "--member", "ben", "--json"))!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(Ct);
        var stderr = p.StandardError.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(3), Ct);
        var json = JsonNode.Parse(await stdout)!;
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {await stderr}\n{json}");
        Assert.True(json["passed"]!.GetValue<bool>());
        Assert.Equal("grok", json["subject"]!["cli"]!.GetValue<string>());
        Assert.Contains(json["results"]!.AsArray(), r => r!["id"]!.GetValue<string>() == "gateway.connect-denied-host" && r["logged"]!.GetValue<bool>());
    }

    /// <summary>The core over IPC, as the window will call it: guardrail.start, then guardrail.get until done.</summary>
    [Fact]
    public async Task TheCoreRunsTheCheckOverIpcInARealSandbox()
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        await using var kit = GuardrailKit.Create();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var address = IpcEndpoint.NewAddress(kit.Data);
        var psi = Core(kit, "--ipc", address, "--data-dir", kit.Data);
        psi.Environment["YAWBLE_IPC_TOKEN"] = token;
        using var core = Process.Start(psi)!;
        try
        {
            var ready = await core.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal($"YAWBLE-READY {address}", ready);
            await using var client = await TestIpcClient.ConnectAsync(address, Ct);
            await client.AuthAsync(token, Ct);
            var ada = new JsonObject { ["team"] = "payments", ["member"] = "ada" };
            var started = (await client.CallAsync("guardrail.start", (JsonObject)ada.DeepClone(), Ct))!;
            Assert.True(started["result"] is not null, started.ToJsonString());
            JsonNode? state = null;
            await Bus.Wait.UntilAsync(async () =>
                (state = (await client.CallAsync("guardrail.get", (JsonObject)ada.DeepClone(), Ct))!["result"])!["state"]!.GetValue<string>() != "running",
                "the check to finish", TimeSpan.FromMinutes(3));
            Assert.Equal("done", state!["state"]!.GetValue<string>());
            var report = state["report"]!;
            Assert.True(report["passed"]!.GetValue<bool>(), report.ToJsonString());
            Assert.True(File.Exists(Path.Combine(kit.Data, "guardrail", "payments__ada.json")));
            // The running core's own launcher ran it (LaunchWiring). Host sign-in: the kit's fake home holds a dummy sign-in for
            // the CLI, so the member counts as signed in on this computer, and the check proves that dummy file is
            // readable (opened, never read) and not writable, while the rest of its config folder stays blocked.
            Assert.Equal("yawble-ffi", report["run"]!["backend"]!.GetValue<string>());
            Assert.True(report["run"]!["credentialConfigured"]!.GetValue<bool>());
            var results = report["results"]!.AsArray();
            string VerdictOf(string id) => results.Single(r => r!["id"]!.GetValue<string>() == id)!["verdict"]!.GetValue<string>();
            Assert.Equal("ALLOWED", VerdictOf("signin.read:0"));
            Assert.Equal("BLOCKED", VerdictOf("signin.write:0"));
            Assert.Equal("BLOCKED", VerdictOf("signin.folder-write:0"));
            Assert.Equal("BLOCKED", VerdictOf("cli.config-canary:0"));
        }
        finally
        {
            core.StandardInput.Close();
            if (!await Task.Run(() => core.WaitForExit(15_000), Ct)) core.Kill(entireProcessTree: true);
        }
    }
}
