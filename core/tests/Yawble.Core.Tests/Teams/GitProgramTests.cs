using System.Diagnostics;
using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Ops;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The git requirement: finding git, its version, a clear message when it is missing or too old, and installing it.
/// Every program below is a fake (<see cref="FakeGitHost"/>): no test installs anything, runs winget or changes PATH.
/// </summary>
public class GitProgramTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("git version 2.45.1.windows.1", "2.45.1")]
    [InlineData("git version 2.38.0", "2.38.0")]
    [InlineData("git version 2.39.5 (Apple Git-154)", "2.39.5")]
    [InlineData("git version 3.0", "3.0.0")]
    public void ParsesTheVersionGitPrints(string output, string expected) =>
        Assert.Equal(Version.Parse(expected), GitProgram.ParseVersion(output));

    [Theory]
    [InlineData("")]
    [InlineData("not git at all")]
    public void SomethingElseIsNoVersion(string output) => Assert.Null(GitProgram.ParseVersion(output));

    [Fact]
    public void FindsTheFirstFolderOnPathThatHasIt()
    {
        var sep = Path.PathSeparator;
        var have = new HashSet<string> { Path.Combine("b", "git.exe"), Path.Combine("c", "git.exe") };
        Assert.Equal(Path.Combine("b", "git.exe"), GitProgram.FindOnPath($"a{sep}\"b\"{sep}c", "git.exe", have.Contains));
        Assert.Null(GitProgram.FindOnPath($"a{sep}{sep}d", "git.exe", have.Contains));
        Assert.Null(GitProgram.FindOnPath(null, "git.exe", have.Contains));
    }

    [Fact]
    public void WindowsLooksInThePersonsAndMachinesPath_ThenGitForWindowsOwnFolders()
    {
        var env = new Dictionary<string, string?> { ["LOCALAPPDATA"] = @"C:\Users\ada\AppData\Local", ["ProgramFiles"] = @"C:\Program Files" };
        var folders = GitProgram.KnownFolders(true, k => env.GetValueOrDefault(k), () => @"C:\tools\bin;", () => @"C:\Windows\system32");
        Assert.Equal(
            [@"C:\tools\bin", @"C:\Windows\system32", Path.Combine(@"C:\Users\ada\AppData\Local", "Programs", "Git", "cmd"), Path.Combine(@"C:\Program Files", "Git", "cmd")],
            folders);
    }

    [Fact]
    public void MacAndLinuxLookInTheSystemAndHomebrewFolders() =>
        Assert.Equal(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"], GitProgram.KnownFolders(false, _ => null));

    [Fact]
    public void AGitThatCannotBeFoundIsOneClearMessage()
    {
        var psi = new ProcessStartInfo("yawble-test-no-such-program-" + Guid.NewGuid().ToString("N")) { UseShellExecute = false };
        var e = Assert.Throws<GitMissingException>(() => GitProgram.Start(psi));
        Assert.Contains("git is not installed", e.Message);
        Assert.Contains("2.38", e.Message);
        Assert.Contains(GitProgram.DownloadUrl, e.Message);
        Assert.IsAssignableFrom<GitException>(e);
    }

    [Theory]
    [InlineData("error: unknown option `write-tree'")]
    [InlineData("usage: git merge-tree [--write-tree] [<options>] <branch1> <branch2>")]
    public void AnOldGitsRefusalIsRecognised(string stderr)
    {
        Assert.True(GitProgram.LooksTooOld(stderr));
        Assert.Contains("too old", GitProgram.TooOldMessage("git merge-tree"));
    }

    [Fact]
    public void AnOrdinaryFailureIsNotCalledOld() => Assert.False(GitProgram.LooksTooOld("fatal: not a git repository"));

    // ---------------------------------------------------------------- the check

    [Fact]
    public async Task Missing_SaysSo_AndOffersAnInstallWhereWingetIs()
    {
        var host = new FakeGitHost { Git = null };
        var s = await new GitService(host).CheckAsync(Ct);
        Assert.Equal("missing", s.State);
        Assert.Equal("2.38", s.Minimum);
        Assert.True(s.CanInstall);

        host.Winget = null;
        Assert.False((await new GitService(host).CheckAsync(Ct)).CanInstall);
        host.IsWindows = false;
        host.Winget = @"C:\fake\winget.exe";
        Assert.False((await new GitService(host).CheckAsync(Ct)).CanInstall); // only Windows installs from the app
    }

    [Theory]
    [InlineData("git version 2.45.1.windows.1", "ok", "2.45.1")]
    [InlineData("git version 2.38.0", "ok", "2.38.0")]
    [InlineData("git version 2.37.9", "old", "2.37.9")]
    [InlineData("git version 2.29.2.windows.1", "old", "2.29.2")]
    public async Task TheVersionDecides(string output, string state, string version)
    {
        var host = new FakeGitHost { VersionOutput = output };
        var s = await new GitService(host).CheckAsync(Ct);
        Assert.Equal(state, s.State);
        Assert.Equal(version, s.Version);
        Assert.Equal(host.Git, s.Path);
        Assert.Equal(["--version"], host.Calls.Single().Args);
    }

    [Fact]
    public async Task AGitThatDoesNotAnswerIsBroken()
    {
        var host = new FakeGitHost { VersionExit = 1, VersionOutput = "", VersionError = "fatal: something is wrong" };
        var s = await new GitService(host).CheckAsync(Ct);
        Assert.Equal("broken", s.State);
        Assert.Contains("something is wrong", s.Detail);
    }

    // ---------------------------------------------------------------- install

    [Fact]
    public async Task Install_RunsWinget_ThenChecksAgain()
    {
        var host = new FakeGitHost { Git = null };
        host.OnWinget = () => host.Git = @"C:\Users\ada\AppData\Local\Programs\Git\cmd\git.exe";
        var r = await new GitService(host).InstallAsync(Ct);
        Assert.True(r.Done, r.Message);
        Assert.Equal("ok", r.Status.State);
        var winget = host.Calls.Single(c => c.Exe == host.Winget);
        Assert.Equal(["install", "--id", "Git.Git", "--exact", "--source", "winget", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"], winget.Args);
        Assert.Equal(GitService.InstallLimit, winget.Limit);
    }

    [Fact]
    public async Task AnOldGitIsUpdated()
    {
        var host = new FakeGitHost { VersionOutput = "git version 2.30.1" };
        host.OnWinget = () => host.VersionOutput = "git version 2.56.0";
        var r = await new GitService(host).InstallAsync(Ct);
        Assert.True(r.Done);
        var args = host.Calls.Single(c => c.Exe == host.Winget).Args;
        Assert.Equal("upgrade", args[0]);
    }

    [Fact]
    public async Task AFailedInstallSaysWhatWingetSaid_AndWhereToGetGit()
    {
        var host = new FakeGitHost { Git = null, WingetExit = -1978335216, WingetOutput = "  -\n  \\\nNo applicable installer found; see logs for more details." };
        var r = await new GitService(host).InstallAsync(Ct);
        Assert.False(r.Done);
        Assert.Contains("No applicable installer found", r.Message);
        Assert.Contains("-1978335216", r.Message);
        Assert.Contains(GitProgram.DownloadUrl, r.Message);
        Assert.Equal("missing", r.Status.State);
    }

    [Fact]
    public async Task NothingRunsWhenGitIsFine_OrWithoutWinget_OrOffWindows()
    {
        var fine = new FakeGitHost();
        Assert.True((await new GitService(fine).InstallAsync(Ct)).Done);
        Assert.DoesNotContain(fine.Calls, c => c.Exe == fine.Winget);

        var noWinget = new FakeGitHost { Git = null, Winget = null };
        var r = await new GitService(noWinget).InstallAsync(Ct);
        Assert.False(r.Done);
        Assert.Contains("winget is not available", r.Message);

        var mac = new FakeGitHost { Git = null, IsWindows = false };
        var m = await new GitService(mac).InstallAsync(Ct);
        Assert.False(m.Done);
        Assert.Contains("package manager", m.Message);
        Assert.DoesNotContain(mac.Calls, c => c.Exe == mac.Winget);
    }

    [Fact]
    public async Task OneInstallAtATime()
    {
        var gate = new TaskCompletionSource();
        var host = new FakeGitHost { Git = null, WingetWait = gate.Task };
        var git = new GitService(host);
        var first = git.InstallAsync(Ct);
        await host.WingetStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = await git.InstallAsync(Ct);
        Assert.False(second.Done);
        Assert.Contains("being installed already", second.Message);
        gate.SetResult();
        await first;
        Assert.Single(host.Calls, c => c.Exe == host.Winget);
    }

    // ---------------------------------------------------------------- calls, the doctor, the Cloud Link

    [Fact]
    public async Task OnlyThePersonsWindowInstalls()
    {
        var host = new FakeGitHost { Git = null };
        var rpc = new GitRpc(new GitService(host));
        var check = (JsonObject)(await rpc.DispatchAsync("git.check", null, Ct))!;
        Assert.Equal("missing", check["state"]!.GetValue<string>());
        Assert.Equal(GitProgram.DownloadUrl, check["downloadUrl"]!.GetValue<string>());

        foreach (var actor in new[] { new Actor(Actor.Cloud, null), new Actor(Actor.Concierge, null), new Actor(Actor.External, "claude-code") })
        {
            using (Actor.Use(actor))
            {
                var e = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("git.install", null, Ct));
                Assert.Equal("forbidden", e.Code);
            }
        }
        Assert.DoesNotContain(host.Calls, c => c.Exe == host.Winget);

        using (Actor.Use(Actor.Window))
        {
            var r = (JsonObject)(await rpc.DispatchAsync("git.install", null, Ct))!;
            Assert.NotNull(r["status"]);
        }
        Assert.Single(host.Calls, c => c.Exe == host.Winget);
    }

    [Fact]
    public void ACloudViewerSeesTheCheck_NeverTheInstall()
    {
        Assert.Null(RemoteAccess.Refusal("git.check", null, allowHostTerminals: false));
        Assert.Equal(GitRpc.InstallRefusal, RemoteAccess.Refusal("git.install", null, allowHostTerminals: true));
    }

    [Fact]
    public async Task TheDoctorFailsWithoutGit_AndItsFixAsksFirst()
    {
        var host = new FakeGitHost { Git = null };
        var doctor = new DoctorService(new NoSystem(), null, null, null, null, null, git: new GitService(host));
        var check = (await doctor.RunAsync(Ct)).Single(c => c.Id == "git");
        Assert.Equal(DoctorStatus.Fail, check.Status);
        Assert.Contains("not installed", check.Detail);
        Assert.Equal(new DoctorFix(DoctorService.InstallGitFix, "Install Git", "run", Confirm: true), check.Fix);

        var ask = await doctor.RunFixAsync(DoctorService.InstallGitFix, confirmed: false, Ct);
        Assert.True(ask.NeedsConfirm);
        Assert.Contains("Windows asks for permission", ask.Message);
        Assert.DoesNotContain(host.Calls, c => c.Exe == host.Winget);

        using (Actor.Use(new Actor(Actor.Concierge, null)))
        {
            var refused = await doctor.RunFixAsync(DoctorService.InstallGitFix, confirmed: true, Ct);
            Assert.False(refused.Done);
            Assert.Equal(GitRpc.InstallRefusal, refused.Message);
        }
        Assert.DoesNotContain(host.Calls, c => c.Exe == host.Winget);

        host.OnWinget = () => host.Git = @"C:\Git\cmd\git.exe";
        using (Actor.Use(Actor.Window))
        {
            var done = await doctor.RunFixAsync(DoctorService.InstallGitFix, confirmed: true, Ct);
            Assert.True(done.Done, done.Message);
        }
        Assert.Equal(DoctorStatus.Ok, (await doctor.RunAsync(Ct)).Single(c => c.Id == "git").Status);
    }

    [Fact]
    public async Task TheDoctorNamesAnOldGitsVersion_AndOffersAnUpdate()
    {
        var host = new FakeGitHost { VersionOutput = "git version 2.30.0" };
        var check = (await new DoctorService(new NoSystem(), null, null, null, null, null, git: new GitService(host)).RunAsync(Ct)).Single(c => c.Id == "git");
        Assert.Equal(DoctorStatus.Fail, check.Status);
        Assert.Contains("2.30.0 is older than 2.38", check.Detail);
        Assert.Equal("Update Git", check.Fix!.Label);
    }

    private sealed class NoSystem : IDoctorSystem
    {
        public string QuickCheck() => "ok";
        public IReadOnlyList<string> AppliedMigrations() => [];
        public IReadOnlyList<string> ShippedMigrations() => [];
        public long FreeBytesOnDataDrive() => long.MaxValue;
        public IReadOnlyDictionary<string, long> TeamFolderSizes() => new Dictionary<string, long>();
        public long LogBytes() => 0;
        public SandboxState Sandbox() => new(null, "done", null, null);
        public LinkState Link() => new("off", null);
    }
}

/// <summary>A computer as the git check sees it: where git and winget are, and what they print. Runs nothing.</summary>
public sealed class FakeGitHost : IGitHost
{
    public bool IsWindows { get; set; } = true;
    public string? Git { get; set; } = @"C:\Program Files\Git\cmd\git.exe";
    public string? Winget { get; set; } = @"C:\Users\ada\AppData\Local\Microsoft\WindowsApps\winget.exe";
    public string VersionOutput { get; set; } = "git version 2.45.1.windows.1";
    public int VersionExit { get; set; }
    public string VersionError { get; set; } = "";
    public int WingetExit { get; set; }
    public string WingetOutput { get; set; } = "Successfully installed";
    public Action? OnWinget { get; set; }
    public Task? WingetWait { get; set; }
    public TaskCompletionSource WingetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(string Exe, IReadOnlyList<string> Args, TimeSpan Limit)> Calls { get; } = [];

    public string? FindGit() => Git;
    public string? FindWinget() => Winget;

    public async Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct)
    {
        lock (Calls) Calls.Add((exe, args, limit));
        if (exe == Winget)
        {
            WingetStarted.TrySetResult();
            if (WingetWait is not null) await WingetWait.WaitAsync(ct);
            OnWinget?.Invoke();
            return new GitResult(WingetExit, WingetOutput, "");
        }
        if (exe == Git) return new GitResult(VersionExit, VersionOutput, VersionError);
        throw new InvalidOperationException($"the test did not expect {exe}");
    }
}
