using Yawble.Bridge;
using Xunit;

namespace Yawble.Bridge.Tests;

public class GatewayAddressTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Key == name).Value;

    [Fact]
    public void UsesTheProxySandboxInjected_NotLoopback()
    {
        // Linux rewrites a 127.0.0.1 proxy to the slirp gateway; the bridge must follow the env.
        var gateway = GatewayAddress.FromEnvironment(
            Env(("HTTP_PROXY", "http://10.0.2.2:41234"), ("YAWBLE_RUN_TOKEN", "run-token")), out var error);

        Assert.Null(error);
        Assert.NotNull(gateway);
        Assert.Equal(new Uri("http://10.0.2.2:41234"), gateway.Proxy);
        Assert.Equal(new Uri("http://bus.yawble.internal/mcp"), gateway.BusEndpoint);
        Assert.Equal("run-token", gateway.RunToken);
    }

    [Fact]
    public void RefusesToRunOutsideASandbox()
    {
        Assert.Null(GatewayAddress.FromEnvironment(Env(("YAWBLE_RUN_TOKEN", "t")), out var error));
        Assert.Contains("HTTP_PROXY", error);
    }

    [Fact]
    public void RequiresTheRunToken()
    {
        Assert.Null(GatewayAddress.FromEnvironment(Env(("HTTP_PROXY", "http://127.0.0.1:5000")), out var error));
        Assert.Contains("YAWBLE_RUN_TOKEN", error);
    }
}
