using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Mxc.Sdk.V1;
using Yawble.Core.Ipc;
using Xunit;

namespace Yawble.Core.Tests;

/// <summary>
/// The core as the shell starts it, minus the window: start the real core executable as the shell does, read its
/// READY line, connect over the socket or pipe, and get the version and getAvailableBackends().
/// </summary>
public class CoreProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SidecarServesVersionAndBackendsOverIpc_AndExitsWhenStdinCloses()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var dataDir = Directory.CreateTempSubdirectory("yawble-data-").FullName;
        var address = IpcEndpoint.NewAddress(dataDir);
        var coreDll = Path.Combine(AppContext.BaseDirectory, "yawble-core.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { coreDll, "--ipc", address, "--data-dir", dataDir },
            Environment = { ["YAWBLE_IPC_TOKEN"] = token },
        };
        CoreLaunch.IsolateCompanionHome(start); // never the real %APPDATA%\yawble-companion
        using var core = Process.Start(start)!;
        try
        {
            var ready = await core.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal($"YAWBLE-READY {address}", ready);

            await using var client = await TestIpcClient.ConnectAsync(address, Ct);
            await client.AuthAsync(token, Ct);
            var info = (await client.CallAsync("core.getInfo", null, Ct))!["result"]!;
            var backends = (await client.CallAsync("sandbox.getAvailableBackends", null, Ct))!["result"]!;

            Assert.Equal(CoreInfo.Version, info["version"]!.GetValue<string>());
            // The startup probe reaches the shell: on Linux the AppArmor/bwrap checks, elsewhere none.
            var checks = info["hostChecks"]!.AsArray().Select(c => c!["id"]!.GetValue<string>());
            Assert.Equal(Yawble.Core.Sandboxing.HostChecks.ForThisHost().Run().Select(c => c.Id), checks);
            var expected = JsonSerializer.SerializeToNode(MxcPlatform.GetAvailableBackends(), RpcDispatcher.Json);
            Assert.True(JsonNode.DeepEquals(expected, backends), $"got {backends}, expected {expected}");
            TestContext.Current.SendDiagnosticMessage($"getAvailableBackends() over IPC: {backends.ToJsonString()}");
            Assert.True(File.Exists(Path.Combine(dataDir, "yawble.db")));

            core.StandardInput.Close(); // the shell went away
            await core.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(0, core.ExitCode);
        }
        finally
        {
            if (!core.HasExited) core.Kill(entireProcessTree: true);
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheCoreWritesControlJsonUnderYawbleHomeAndRemovesItOnExit()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var dataDir = Directory.CreateTempSubdirectory("yawble-data-").FullName;
        var home = Path.Combine(Directory.CreateTempSubdirectory("yawble-yhome-").FullName, "yawble");
        var address = IpcEndpoint.NewAddress(dataDir);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "yawble-core.dll"), "--ipc", address, "--data-dir", dataDir },
            Environment = { ["YAWBLE_IPC_TOKEN"] = token, [Yawble.Core.Companion.ControlDiscovery.HomeVariable] = home },
        };
        Assert.Equal(home, CoreLaunch.IsolateCompanionHome(start)); // its own temp home is kept
        using var core = Process.Start(start)!;
        try
        {
            var ready = await core.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal($"YAWBLE-READY {address}", ready);

            // Written before READY: a client started as soon as the window is up finds it.
            var control = Yawble.Core.Companion.ControlDiscovery.Read(home);
            Assert.NotNull(control);
            Assert.Equal(core.Id, control.Pid);
            Assert.Equal(CoreInfo.Version, control.CoreVersion);
            Assert.NotEqual(address, control.Address); // its own endpoint, never the window's
            if (OperatingSystem.IsWindows()) Assert.StartsWith(@"\\.\pipe\yawble-ctl-", control.Address);
            Assert.True(Yawble.Core.Companion.UserOnlyFile.IsUserOnly(home));
            Assert.True(Yawble.Core.Companion.UserOnlyFile.IsUserOnly(Yawble.Core.Companion.ControlDiscovery.FilePath(home)));

            // The window sees where it listens; nothing about it is a key.
            await using (var client = await TestIpcClient.ConnectAsync(address, Ct))
            {
                await client.AuthAsync(token, Ct);
                var status = (await client.CallAsync("companion.status", null, Ct))!["result"]!;
                Assert.Equal(control.Address, status["endpoint"]!.GetValue<string>());
                Assert.Null(status["problem"]);
            }

            core.StandardInput.Close();
            await core.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(0, core.ExitCode);
            Assert.False(File.Exists(Yawble.Core.Companion.ControlDiscovery.FilePath(home)));
        }
        finally
        {
            if (!core.HasExited) core.Kill(entireProcessTree: true);
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
            try { TestFiles.DeleteTree(Path.GetDirectoryName(home)!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task RefusesToStartWithoutAToken()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "yawble-core.dll"), "--ipc", "/tmp/x.sock", "--data-dir", "/tmp/x" },
        };
        start.Environment.Remove("YAWBLE_IPC_TOKEN");
        CoreLaunch.IsolateCompanionHome(start);
        using var core = Process.Start(start)!;
        await core.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(2, core.ExitCode);
    }
}
