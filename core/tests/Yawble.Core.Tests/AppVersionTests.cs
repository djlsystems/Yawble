using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Sandboxing;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests;

/// <summary>
/// The build's version (scripts/build-version.mjs at build time) as the core reports it: app.version for Settings >
/// System > About, the cloud's version from the Cloud Link's welcome, and the startup line. No socket, no network.
/// </summary>
public class AppVersionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // yyyy.mm.dd.N, or <release or 0.0.0>+dev.<7 hex>[.dirty], or the no-git fallback.
    private static readonly Regex Shape = new(@"^(\d{4}\.\d{2}\.\d{2}\.\d+|(\d{4}\.\d{2}\.\d{2}\.\d+|0\.0\.0)\+dev\.([0-9a-f]{7}(\.dirty)?|unknown))$");

    [Fact]
    public void TheVersionHasTheReleaseShape_AndAgreesWithItsFlags()
    {
        Assert.Matches(Shape, CoreInfo.Version);
        Assert.Equal(CoreInfo.Released, !CoreInfo.Version.Contains('+'));
        Assert.Equal(CoreInfo.Dirty, CoreInfo.Version.EndsWith(".dirty", StringComparison.Ordinal));
        if (CoreInfo.Version != CoreInfo.UnknownVersion)
            Assert.Matches("^[0-9a-f]{40}$", CoreInfo.Commit);
        // A dev build names its commit (the first 7 characters).
        if (!CoreInfo.Released && CoreInfo.Commit != "unknown")
            Assert.Contains($"+dev.{CoreInfo.Commit[..7]}", CoreInfo.Version);
    }

    [Fact]
    public async Task AppVersion_ReportsVersionCommitDirtyReleased_AndNoCloudWithoutALink()
    {
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => []);
        var v = (await dispatcher.DispatchAsync("app.version", null, Ct))!.AsObject();
        Assert.Equal(CoreInfo.Version, v["version"]!.GetValue<string>());
        Assert.Equal(CoreInfo.Commit, v["commit"]!.GetValue<string>());
        Assert.Equal(CoreInfo.Dirty, v["dirty"]!.GetValue<bool>());
        Assert.Equal(CoreInfo.Released, v["released"]!.GetValue<bool>());
        Assert.True(v.ContainsKey("cloudVersion"));
        Assert.Null(v["cloudVersion"]);
    }

    [Fact]
    public async Task AppVersion_WithALinkThatIsNotConnected_HasNoCloudVersion()
    {
        using var dir = new TempDb();
        await using var link = new CloudLink(dir.Dir, (_, _, _) => Task.FromResult<JsonNode?>(null), new AppEvents());
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], link: link);
        var v = (await dispatcher.DispatchAsync("app.version", null, Ct))!.AsObject();
        Assert.Null(v["cloudVersion"]);
        Assert.Null(link.Status()["cloudVersion"]);
    }

    [Fact]
    public void AppVersion_IsReadOnly_AndAvailableThroughTheCloudLink()
    {
        Assert.True(AuditLog.IsReadOnly("app.version"));
        Assert.Null(RemoteAccess.Refusal("app.version", null, allowHostTerminals: false));
    }

    [Theory]
    [InlineData("""{"type":"welcome","version":"2026.11.12.1"}""", "2026.11.12.1")]
    [InlineData("""{"type":"welcome","version":" 2026.11.12.1+dev.bf6ac38 "}""", "2026.11.12.1+dev.bf6ac38")]
    [InlineData("""{"type":"welcome"}""", null)]
    [InlineData("""{"type":"welcome","version":""}""", null)]
    [InlineData("""{"type":"welcome","version":42}""", null)]
    [InlineData("""{"type":"welcome","version":{"x":1}}""", null)]
    public void TheCloudsVersionComesFromItsWelcome(string message, string? expected) =>
        Assert.Equal(expected, CloudLink.WelcomeVersion(JsonNode.Parse(message)!.AsObject()));

    [Fact]
    public void ALongWelcomeVersionIsIgnored() =>
        Assert.Null(CloudLink.WelcomeVersion(new JsonObject { ["version"] = new string('9', 81) }));

    [Fact]
    public void TheStartupLineNamesTheVersionAndTheKindOfBuild()
    {
        var line = CoreInfo.StartupLine();
        Assert.StartsWith($"Yawble Desktop core {CoreInfo.Version} (", line);
        Assert.Contains(CoreInfo.Released ? "release" : "development build", line);
        Assert.Contains(CoreInfo.Commit, line);
    }
}
