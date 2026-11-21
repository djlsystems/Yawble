using Yawble.Core.Agents;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// B118 Part B: a desktop launch on macOS/Linux gives the core a short PATH without ~/.local/bin, so the "not on your
/// PATH" warning is judged against the person's login-shell PATH, read once per detection through a fake shell (no
/// real shell ever runs here). A shell that cannot be read gives no warning rather than a false one. Windows unchanged.
/// </summary>
public class LoginShellPathTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NotOnPath = "not on your PATH";

    /// <summary>The official installer's ~/.local/bin/claude, with the core's (desktop launch) PATH missing ~/.local/bin.</summary>
    private static FakeHost DesktopLaunch(TargetOs os, string home)
    {
        var f = new FakeHost(os, home)
            .File($"{home}/.local/share/claude/versions/2.1.318", os == TargetOs.MacOS ? FakeHost.MachO : FakeHost.Elf)
            .Link($"{home}/.local/bin/claude", $"{home}/.local/share/claude/versions/2.1.318")
            .OnPath("/usr/bin", "/bin");
        f.VersionOutput = "2.1.318 (Claude Code)";
        return f;
    }

    private static Task<DetectedCli> Claude(FakeHost f) => new ClaudeDetector().DetectAsync(f.Host(), Ct);

    [Theory]
    [InlineData(TargetOs.Linux, "/home/u")]
    [InlineData(TargetOs.MacOS, "/Users/u")]
    public async Task NoWarningWhenTheLoginShellPathHasLocalBin(TargetOs os, string home)
    {
        var f = DesktopLaunch(os, home);
        f.ShellPath = [$"{home}/.local/bin", "/usr/bin", "/bin"];

        var c = await Claude(f);

        Assert.True(c.Found);
        Assert.Equal($"{home}/.local/bin/claude", c.LauncherPath);
        Assert.DoesNotContain(c.Problems, p => p.Contains(NotOnPath));
        var run = Assert.Single(f.ShellRuns);
        Assert.Equal("/bin/zsh", run.FileName);
        Assert.Equal("-ilc", run.Arguments[0]);
        Assert.Contains("printf", run.Arguments[1]);
        Assert.Contains("\"$PATH\"", run.Arguments[1]);
        Assert.Equal(DetectionHost.LoginShellTimeout, run.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3), run.Timeout);
    }

    [Fact]
    public async Task ABannerFromTheProfileIsNotPartOfThePath()
    {
        var f = DesktopLaunch(TargetOs.Linux, "/home/u");
        var m = DetectionHost.LoginShellMarker;
        f.ShellRespond = _ => new CliProbeResult(0, $"Welcome to host: a:b\n{m}/home/u/.local/bin:/usr/bin{m}\nbye", "", false);

        Assert.DoesNotContain((await Claude(f)).Problems, p => p.Contains(NotOnPath));
    }

    [Fact]
    public async Task StillWarnsWhenTheLoginShellPathMissesTheFolder()
    {
        var f = DesktopLaunch(TargetOs.Linux, "/home/u");
        f.ShellPath = ["/usr/local/bin", "/usr/bin", "/bin"];

        var c = await Claude(f);

        Assert.Contains(c.Problems, p => p.Contains(NotOnPath) && p.Contains("/home/u/.local/bin"));
    }

    public static TheoryData<string, Func<CliProbeCommand, CliProbeResult>> Unreadable => new()
    {
        { "timed out", _ => new CliProbeResult(null, "", "", true) },
        { "non-zero exit", _ => new CliProbeResult(1, "", "zsh: bad option", false) },
        { "did not start", _ => new CliProbeResult(null, "", "", false, "No such file or directory") },
        { "empty output", _ => new CliProbeResult(0, "", "", false) },
        { "empty PATH", _ => new CliProbeResult(0, DetectionHost.LoginShellMarker + DetectionHost.LoginShellMarker, "", false) },
        { "no markers", _ => new CliProbeResult(0, "/usr/bin:/bin", "", false) },
        { "runner threw", _ => throw new InvalidOperationException("boom") },
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task NoWarningWhenTheLoginShellCannotBeRead(string why, Func<CliProbeCommand, CliProbeResult> respond)
    {
        var f = DesktopLaunch(TargetOs.Linux, "/home/u");
        f.ShellRespond = respond;

        var c = await Claude(f);

        Assert.True(c.Found, why);
        Assert.DoesNotContain(c.Problems, p => p.Contains(NotOnPath));
        Assert.Single(f.ShellRuns);
    }

    [Fact]
    public async Task NoWarningAndNoShellRunWhenShellIsUnset()
    {
        var f = DesktopLaunch(TargetOs.MacOS, "/Users/u");
        f.Shell = null;

        var c = await Claude(f);

        Assert.True(c.Found);
        Assert.DoesNotContain(c.Problems, p => p.Contains(NotOnPath));
        Assert.Empty(f.ShellRuns);
    }

    [Fact]
    public async Task NoShellRunWhenTheLauncherIsOnTheCorePath()
    {
        var f = DesktopLaunch(TargetOs.Linux, "/home/u").OnPath("/home/u/.local/bin");

        var c = await Claude(f);

        Assert.Empty(c.Problems);
        Assert.Empty(f.ShellRuns);
    }

    [Fact]
    public async Task TheShellIsReadOncePerDetectionForEveryCli()
    {
        var f = DesktopLaunch(TargetOs.Linux, "/home/u")
            .File("/home/u/.grok/bin/grok", FakeHost.Elf);
        f.ShellPath = ["/home/u/.local/bin", "/usr/bin"];
        var registry = new CliDetectorRegistry([new ClaudeDetector(), new GrokDetector()], f.Host);

        var all = await registry.DetectAllAsync(Ct);

        Assert.Single(f.ShellRuns);
        Assert.DoesNotContain(all.Single(d => d.CliId == "claude").Problems, p => p.Contains(NotOnPath));
        // grok's ~/.grok/bin is not on the login shell's PATH either: that warning is a true one.
        Assert.Contains(all.Single(d => d.CliId == "grok").Problems, p => p.Contains(NotOnPath));

        await registry.DetectAllAsync(Ct);
        Assert.Equal(2, f.ShellRuns.Count); // a fresh read on the next detection
    }

    [Fact]
    public async Task FishJoinsItsPathListWithColons()
    {
        var f = DesktopLaunch(TargetOs.Linux, "/home/u");
        f.Shell = "/usr/bin/fish";
        f.ShellPath = ["/home/u/.local/bin", "/usr/bin"];

        Assert.DoesNotContain((await Claude(f)).Problems, p => p.Contains(NotOnPath));
        Assert.Contains("(string join : $PATH)", Assert.Single(f.ShellRuns).Arguments[1]);
    }

    [Fact]
    public async Task WindowsStillWarnsAndNeverRunsAShell()
    {
        var f = new FakeHost(TargetOs.Windows, @"C:\Users\u").File(@"C:\Users\u\.local\bin\claude.exe", FakeHost.Pe);
        f.Vars["SHELL"] = "/bin/bash";
        f.ShellPath = [@"C:\Users\u\.local\bin"];

        var c = await new ClaudeDetector().DetectAsync(f.Host(), Ct);

        Assert.Contains(c.Problems, p => p.Contains(NotOnPath));
        Assert.Empty(f.ShellRuns);
    }
}
