using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Guardrail;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// A sandbox must outlive the thread that spawned it. The sandbox forks bwrap with --die-with-parent on the CALLER's
/// thread, and Linux's PR_SET_PDEATHSIG fires when that thread exits: a run spawned from a .NET thread-pool thread was
/// SIGKILLed (exit -1, no output) once the pool retired the thread - seen when RealSandboxHostUpdateTests ran next to the
/// Launch tests. NativeSandboxBackend spawns from a process-lifetime thread instead (upstream's fix, microsoft/mxc 1cc2dbd0, is in no
/// release). This spawns from a thread that ends at once, as a retired pool thread does, and waits for the workload.
/// </summary>
[Collection(RealSandboxGuardrailCollection.Name)]
public class RealSandboxSpawnThreadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSandboxOutlivesTheThreadThatSpawnedIt(bool pty)
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var request = new JsonObject
        {
            ["version"] = SandboxPin.RequestContract,
            ["containment"] = "bubblewrap",
            ["process"] = new JsonObject
            {
                ["commandLine"] = CommandLine.Join(TargetOs.Linux, ["/bin/sh", "-c", "sleep 2; echo survived-the-spawning-thread"]),
                ["cwd"] = "/",
                ["env"] = new JsonArray("PATH=/usr/bin:/bin"),
                ["timeout"] = 60_000,
            },
            ["filesystem"] = new JsonObject { ["readonlyPaths"] = new JsonArray(), ["readwritePaths"] = new JsonArray(), ["deniedPaths"] = new JsonArray() },
            ["network"] = new JsonObject { ["egress"] = new JsonObject { ["default"] = "deny" }, ["ingress"] = new JsonObject { ["default"] = "deny", ["hostLoopback"] = "deny" } },
            ["runtimeConfig"] = new JsonObject { ["networkProxy"] = $"http://127.0.0.1:{port}" },
            ["telemetry"] = new JsonObject { ["enabled"] = false },
        }.ToJsonString();

        ISandboxProcess? process = null;
        Exception? failed = null;
        var spawner = new Thread(() =>
        {
            try { process = pty ? new NativeSandboxBackend().SpawnWithPty(request, PtySize.Default) : new NativeSandboxBackend().Spawn(request); }
            catch (Exception e) { failed = e; }
        });
        spawner.Start();
        spawner.Join(); // the spawning thread is gone from here on
        Assert.Null(failed);
        using var p = process!;

        var read = Task.Run(async () =>
        {
            using var buffer = new MemoryStream();
            await p.Output!.CopyToAsync(buffer, TestContext.Current.CancellationToken);
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }, TestContext.Current.CancellationToken);
        SandboxExit? exit = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((exit = p.TryWait()) is null && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.NotNull(exit);
        var stdout = "";
        try { stdout = await read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        catch (Exception e) when (e is IOException or TimeoutException) { }
        Assert.True(exit!.Value.ExitCode == 0, $"the sandbox ended with {exit.Value.ExitCode} after its spawning thread exited (stdout: {stdout})");
        Assert.Contains("survived-the-spawning-thread", stdout);
    }
}
