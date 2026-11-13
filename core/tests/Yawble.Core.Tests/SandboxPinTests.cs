using Yawble.Core.Sandboxing;
using Yawble.SandboxCheck;
using Xunit;

namespace Yawble.Core.Tests;

public class SandboxPinTests
{
    [Fact]
    public void ProbeRequestIsContract100WithTelemetryOff()
    {
        var r = SandboxPin.ProbeRequest();
        Assert.Equal("1.0.0", r["version"]!.GetValue<string>());
        Assert.False(r["telemetry"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("deny", r["network"]!["egress"]!["default"]!.GetValue<string>());
        Assert.True(r["ui"]!["disable"]!.GetValue<bool>());
    }

    [Fact]
    public void TheTypedSdkEmitsContract100()
    {
        Assert.Equal("1.0.0", SandboxContractProbe.SdkContract());
    }

    [Theory]
    [InlineData("1.0.1", false)]
    [InlineData("1.0.1-rc1", true)]
    [InlineData("1.1.0-alpha", true)]
    public void PrereleaseVersionsAreRecognised(string version, bool prerelease) =>
        Assert.Equal(prerelease, ReleaseGate.IsPrerelease(version));

    [Fact]
    public void ProbeFailsWhenThePinnedRequestIsRejected()
    {
        var verdict = SandboxContractProbe.Check(_ => new NativeSandbox.Outcome(NativeSandbox.StatusMalformedRequest, 0, "unsupported"), () => 0);
        Assert.False(verdict.Ok);
        Assert.Contains("no longer accepts", verdict.Summary);
    }

    [Fact]
    public void ProbeFailsWhenItCannotDiscriminate()
    {
        var verdict = SandboxContractProbe.Check(_ => new NativeSandbox.Outcome(NativeSandbox.StatusSuccess, 0, null), () => 0);
        Assert.False(verdict.Ok);
        Assert.Contains("cannot discriminate", verdict.Summary);
    }

    [Fact]
    public void RealNativeParserAcceptsThePinAndRejectsTheControls()
    {
        // Calls libmxc_ffi from the restored package. On a host without a usable backend the pinned request
        // is parsed and then fails with a backend error, which still counts as accepted.
        var verdict = SandboxContractProbe.Check(NativeSandbox.RunJson);
        Assert.True(verdict.Ok, verdict.Summary);
    }

    [Fact]
    public async Task OfflineGatePassesForTheCommittedPins()
    {
        var root = ReleaseGate.FindRepoRoot(AppContext.BaseDirectory)!;
        var report = await ReleaseGate.RunAsync(root, offline: true, new HttpClient());
        Assert.False(report.Failed, string.Join("\n", report.Lines.Select(l => $"{l.Level} {l.Text}")));
    }
}
