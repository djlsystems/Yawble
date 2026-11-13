using Yawble.Core.Sandboxing;
using Xunit;

namespace Yawble.Core.Tests;

/// <summary>
/// The startup probe's Linux rules, against a faked /proc and PATH, so they run on every OS: Ubuntu 24.04's
/// AppArmor userns restriction is reported as a missing profile, with the fix, unless the core runs under it.
/// </summary>
public class HostChecksTests
{
    private const string Restrict = "/proc/sys/kernel/apparmor_restrict_unprivileged_userns";
    private const string Label = "/proc/self/attr/apparmor/current";

    private static IReadOnlyList<HostCheck> Run(Dictionary<string, string> files, string? script = "/opt/yawble/install-apparmor.sh", params string[] tools) =>
        new HostChecks(p => files.GetValueOrDefault(p), tools.Contains, script).RunLinux();

    [Fact]
    public void RestrictedHost_WithoutTheProfile_SaysTheProfileIsMissing_AndHowToInstallIt()
    {
        var checks = Run(new() { [Restrict] = "1\n", [Label] = "unconfined\n" }, tools: ["bwrap", "slirp4netns"]);
        var userns = Assert.Single(checks, c => c.Id == "apparmor-userns");
        Assert.False(userns.Ok);
        Assert.Contains("AppArmor profile is missing", userns.Message);
        Assert.Contains("'unconfined'", userns.Message);
        Assert.Equal("Run this once as root, then restart Yawble: sudo '/opt/yawble/install-apparmor.sh'", userns.Fix);
    }

    [Fact]
    public void RestrictedHost_UnderTheProfile_Passes()
    {
        var checks = Run(new() { [Restrict] = "1", [Label] = "yawble-core (unconfined)\0" }, tools: ["bwrap", "slirp4netns"]);
        Assert.All(checks, c => Assert.True(c.Ok, c.Message));
        Assert.Contains("yawble-core (unconfined)", checks.Single(c => c.Id == "apparmor-userns").Message);
    }

    [Fact]
    public void RestrictedHost_FallsBackToTheOlderLabelFile_AndToAGenericFixOutsideAnInstall()
    {
        var checks = Run(new() { [Restrict] = "1", ["/proc/self/attr/current"] = "snap.foo (enforce)" }, script: null, tools: ["bwrap", "slirp4netns"]);
        var userns = checks.Single(c => c.Id == "apparmor-userns");
        Assert.False(userns.Ok);
        Assert.Contains("'snap.foo (enforce)'", userns.Message);
        Assert.Contains("install-apparmor.sh once as root", userns.Fix);
    }

    [Theory]
    [InlineData(null)] // no AppArmor in this kernel (WSL, most containers)
    [InlineData("0")]
    public void UnrestrictedHost_NeedsNoProfile(string? restrict)
    {
        var files = restrict is null ? new Dictionary<string, string>() : new() { [Restrict] = restrict };
        Assert.True(Run(files, tools: ["bwrap", "slirp4netns"]).Single(c => c.Id == "apparmor-userns").Ok);
    }

    [Fact]
    public void MissingBwrapAndSlirp_AreEachNamedWithTheirPackage()
    {
        var checks = Run([]);
        Assert.Equal("Install it, e.g. sudo apt install bubblewrap", checks.Single(c => c.Id == "bwrap" && !c.Ok).Fix);
        Assert.Equal("Install it, e.g. sudo apt install slirp4netns", checks.Single(c => c.Id == "slirp4netns" && !c.Ok).Fix);
    }

    [Fact]
    public void OnlyLinuxHasChecks()
    {
        var checks = HostChecks.ForThisHost().Run();
        if (OperatingSystem.IsLinux()) Assert.Equal(["apparmor-userns", "bwrap", "slirp4netns"], checks.Select(c => c.Id));
        else Assert.Empty(checks);
    }
}
