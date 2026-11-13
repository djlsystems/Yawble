using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Mxc.Sdk.V1;
using Yawble.Core.Ipc;
using Yawble.Core.Sandboxing;
using Xunit;

namespace Yawble.Core.Tests;

public class IpcServerTests
{
    private const string Token = "0123456789abcdef0123456789abcdef-test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IpcServer> StartAsync(ISandboxPlatformInfo sandbox)
    {
        var dispatcher = new RpcDispatcher(sandbox, () => ["0001_baseline"]);
        await Task.Yield();
        return IpcServer.Start(IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath())), Token, dispatcher);
    }

    [Fact]
    public async Task ReturnsTheRealSandboxBackendProbe_OverTheSocket()
    {
        await using var server = await StartAsync(new SandboxPlatformInfo());
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);

        Assert.True((await client.AuthAsync(Token, Ct))!["result"]!["authenticated"]!.GetValue<bool>());
        var response = await client.CallAsync("sandbox.getAvailableBackends", null, Ct);

        Assert.Null(response!["error"]);
        var expected = JsonSerializer.SerializeToNode(MxcPlatform.GetAvailableBackends(), RpcDispatcher.Json);
        Assert.True(JsonNode.DeepEquals(expected, response["result"]), $"got {response["result"]}, expected {expected}");
    }

    /// <summary>Shutdown with a shell still connected and idle must finish, not wait on the accept or the read.</summary>
    [Fact]
    public async Task DisposeFinishes_WithAnIdleClientConnected()
    {
        var server = await StartAsync(new SandboxPlatformInfo());
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        Assert.True((await client.AuthAsync(Token, Ct))!["result"]!["authenticated"]!.GetValue<bool>());

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task ReportsCoreVersionAndPinnedContract()
    {
        await using var server = await StartAsync(new SandboxPlatformInfo());
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        await client.AuthAsync(Token, Ct);

        var info = (await client.CallAsync("core.getInfo", null, Ct))!["result"]!;

        Assert.Equal(CoreInfo.Version, info["version"]!.GetValue<string>());
        Assert.Equal("1.0.1", info["sandbox"]!["sdkVersion"]!.GetValue<string>());
        Assert.Equal("1.0.0", info["sandbox"]!["requestContract"]!.GetValue<string>());
        Assert.False(info["sandbox"]!["telemetryEnabled"]!.GetValue<bool>());
        Assert.Equal("0001_baseline", info["schema"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task SerializesBackendsWithCamelCaseNames()
    {
        var fake = new FakeSandbox([new AvailableBackend
        {
            Backend = ContainmentBackend.ProcessContainer,
            Tier = IsolationTier.BaseContainer,
            Capabilities = [BackendCapability.ProxyEnforcement],
            Warnings = ["w"],
        }]);
        await using var server = await StartAsync(fake);
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        await client.AuthAsync(Token, Ct);

        var result = (await client.CallAsync("sandbox.getAvailableBackends", null, Ct))!["result"]!.AsArray();

        var only = Assert.Single(result)!;
        Assert.Equal("processContainer", only["backend"]!.GetValue<string>());
        Assert.Equal("baseContainer", only["tier"]!.GetValue<string>());
        Assert.Equal("proxyEnforcement", only["capabilities"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task RefusesAConnectionWithoutTheLaunchToken()
    {
        await using var server = await StartAsync(new SandboxPlatformInfo());
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);

        var denied = await client.CallAsync("core.getInfo", null, Ct);

        Assert.Equal("unauthorized", denied!["error"]!["code"]!.GetValue<string>());
        // The core closed the connection: the next call reads end-of-stream or fails to write.
        JsonObject? after;
        try { after = await client.CallAsync("core.getInfo", null, Ct); }
        catch (IOException) { after = null; }
        Assert.Null(after);
    }

    [Fact]
    public async Task UnknownMethodIsAnErrorNotADisconnect()
    {
        await using var server = await StartAsync(new SandboxPlatformInfo());
        await using var client = await TestIpcClient.ConnectAsync(server.Address, Ct);
        await client.AuthAsync(Token, Ct);

        Assert.Equal("unknown_method", (await client.CallAsync("nope", null, Ct))!["error"]!["code"]!.GetValue<string>());
        Assert.NotNull((await client.CallAsync("core.getInfo", null, Ct))!["result"]);
    }

    [Fact]
    public async Task UnixSocketIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return; // the pipe uses PipeOptions.CurrentUserOnly instead
        var dir = Path.Combine(Path.GetTempPath(), "yawble-test-" + Guid.NewGuid().ToString("N")[..8]);
        var path = Path.Combine(dir, "core.sock");
        var endpoint = IpcEndpoint.Create(path);
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            const UnixFileMode groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(dir) & groupOrOther);
        }
        finally
        {
            await endpoint.DisposeAsync();
            Directory.Delete(dir, true);
        }
    }

    private sealed class FakeSandbox(IReadOnlyList<AvailableBackend> backends) : ISandboxPlatformInfo
    {
        public string SdkVersion => "1.0.1";
        public string NativeVersion => "fake";
        public IReadOnlyList<AvailableBackend> GetAvailableBackends() => backends;
        public PlatformSupport GetPlatformSupport() => new() { IsSupported = false, Reason = "fake" };
    }
}
