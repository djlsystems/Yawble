using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The Host finds <c>git</c>, <c>setpriv</c> and <c>setsid</c> in root-owned system
/// directories, never on PATH. In the image the PATH starts with folders the agent owns, and a
/// program the Host starts without the agent prefix holds its capabilities. Each test puts a fake
/// of the command first on PATH, and the fake leaves a marker if anything runs it.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SystemCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-syscmd-").FullName;
    private string FakeBin => Path.Combine(_root, "bin");
    private string Marker => Path.Combine(_root, "fake-ran");

    public SystemCommandTests()
    {
        Directory.CreateDirectory(FakeBin);
        foreach (var name in new[] { "git", "setpriv", "setsid" })
        {
            var fake = Path.Combine(FakeBin, name);
            File.WriteAllText(fake, $"#!/bin/sh\necho {name} >> '{Marker}'\nexit 0\n");
            File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
        }
    }

    public void Dispose()
    {
        MemberTempCleanup.Remove(_root);
        Directory.Delete(_root, recursive: true);
    }

    private EnvironmentScope FakeFirstOnPath() =>
        new([new("PATH", FakeBin + ":" + Environment.GetEnvironmentVariable("PATH")), new("HOME", _root)]);

    [Theory]
    [InlineData("git")]
    [InlineData("setpriv")]
    [InlineData("setsid")]
    public void A_fake_earlier_on_path_is_not_what_the_host_resolves(string name)
    {
        using var scope = FakeFirstOnPath();

        // The fake really is what a PATH search finds, so the assertion below means something.
        Assert.Equal(Path.Combine(FakeBin, name), Harness.Pty.PathSearch.Find(name));

        var resolved = SystemCommand.Find(name);

        Assert.NotNull(resolved);
        Assert.Contains(Path.GetDirectoryName(resolved), SystemCommand.Directories);
    }

    [Fact]
    public void A_directory_that_is_not_roots_alone_is_skipped_even_when_listed()
    {
        // /tmp is world-writable, so nothing under it is trusted, whoever owns the file.
        Assert.Equal("/usr/bin/git", SystemCommand.Find("git", [FakeBin, "/usr/bin"]));
        Assert.False(SystemCommand.IsTrusted(FakeBin));
        Assert.True(SystemCommand.IsTrusted("/usr/bin"));
        Assert.DoesNotContain(FakeBin, SystemCommand.SafePath.Split(':'));
    }

    [Fact]
    public void The_setpriv_the_launch_user_resolves_is_the_system_one()
    {
        using var scope = FakeFirstOnPath();

        var runAs = AgentLaunchUser.Resolve("nobody");
        if (!runAs.Switches) Assert.Skip($"This process cannot switch users: {runAs.Reason}.");

        Assert.Contains(Path.GetDirectoryName(runAs.Prefix[0]), SystemCommand.Directories);
    }

    [Fact]
    public async Task A_probe_that_cannot_switch_to_an_existing_agent_does_not_run_the_agents_program()
    {
        var specs = new Dictionary<string, AgentAuthProbeSpec> { ["git"] = new(null, ["status"]) };
        var stranded = AgentLaunchUser.Decide("agent", (1001, 1001), 1000, 0, "/usr/bin/setpriv", _ => true);

        using var scope = FakeFirstOnPath();
        var (installed, authenticated, detail) = await AgentAuthProbe.ProbeCommandAsync(
            "git", specs, TestContext.Current.CancellationToken, stranded);

        Assert.True(installed);
        Assert.Null(authenticated);
        Assert.StartsWith("Not probed", detail);
        Assert.False(File.Exists(Marker), "the probe ran the agent's program as the Host");
    }

    [Fact]
    public async Task Git_run_by_the_host_is_not_the_fake_on_path()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        Git.Run(repo, "init", "-q", "-b", "main");

        using var scope = FakeFirstOnPath();
        var result = await new GitRunner().RunGitAsync(repo, ["rev-parse", "--is-inside-work-tree"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("true", result.Stdout.Trim());
        Assert.False(File.Exists(Marker), "the fake git ran: " + (File.Exists(Marker) ? File.ReadAllText(Marker) : ""));
    }

    [Fact]
    public async Task A_member_is_not_started_through_the_fake_setsid_on_path()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch("/bin/sh", ["-c", "echo started"], LanguageModel: false)),
        ]);

        using var scope = FakeFirstOnPath();
        var result = await new ProcessAgentRunner(catalog, new RunHeartbeat()).RunAsync(new AgentInvocation(
            new ContainerId("alpha", "worker"), "You are a probe.", "hello", workspace,
            new Dictionary<string, string>(), Agent: "probe"), TestContext.Current.CancellationToken);

        Assert.Contains("started", result.Output);
        Assert.False(File.Exists(Marker), "the fake setsid ran");
    }
}

/// <summary>
/// The agent owns the team clones, so it can write their hooks and <c>.git/config</c>.
/// Nothing it writes there runs when the Host merges, pushes, fetches, reads status or removes a
/// worktree. Each hook and the fsmonitor command leave a marker; the test ends by pushing with
/// plain git to show they were live.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class GitRunnerAgentConfigTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-gitcfg-").FullName;
    private string Remote => Path.Combine(_root, "remote.git");
    private string Clone => Path.Combine(_root, "clone");
    private string Markers => Path.Combine(_root, "markers");

    public GitRunnerAgentConfigTests()
    {
        Directory.CreateDirectory(Markers);
        Git.Run(_root, "init", "-q", "--bare", "-b", "main", Remote);
        Git.Run(_root, "clone", "-q", Remote, Clone);
        Git.Run(Clone, "config", "user.email", "t@example.invalid");
        Git.Run(Clone, "config", "user.name", "t");
        Git.Run(Clone, "commit", "-q", "--allow-empty", "-m", "first");
        Git.Run(Clone, "push", "-q", "origin", "main");
        Git.Run(Clone, "branch", "feature");
        Git.Run(Clone, "checkout", "-q", "feature");
        Git.Run(Clone, "commit", "-q", "--allow-empty", "-m", "second");
        Git.Run(Clone, "checkout", "-q", "main");
        Git.Run(Clone, "worktree", "add", "-q", Path.Combine(_root, "wt"), "-b", "wt");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>What an agent can write in its clone: three hooks and an fsmonitor command.</summary>
    private void ArmAgentConfig()
    {
        var hooks = Path.Combine(Clone, ".git", "hooks");
        foreach (var hook in new[] { "pre-push", "post-merge", "reference-transaction" })
        {
            WriteScript(Path.Combine(hooks, hook), hook);
        }

        var fsmonitor = Path.Combine(_root, "fsmonitor.sh");
        WriteScript(fsmonitor, "fsmonitor");
        Git.Run(Clone, "config", "core.fsmonitor", fsmonitor);
    }

    private void WriteScript(string path, string name)
    {
        File.WriteAllText(path, $"#!/bin/sh\ntouch '{Path.Combine(Markers, name)}'\ncat >/dev/null\nexit 0\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    private string[] Ran() => [.. Directory.EnumerateFiles(Markers).Select(Path.GetFileName).Order()!];

    [Fact]
    public async Task Hooks_and_fsmonitor_the_agent_wrote_do_not_run_when_the_host_uses_the_clone()
    {
        using var home = new EnvironmentScope([new("HOME", _root)]);
        ArmAgentConfig();
        var git = new GitRunner();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(0, (await git.MergeFastForwardAsync(Clone, "feature", ct)).ExitCode);
        Assert.Equal(0, (await git.PushAsync(Clone, "main", ct)).ExitCode);
        Assert.Equal(0, (await git.PushRefspecAsync(Clone, "feature", "refs/heads/feature", ct)).ExitCode);
        Assert.Equal(0, (await git.FetchAsync(Clone, ct)).ExitCode);
        Assert.Equal(0, (await git.FetchRefspecAsync(Clone, "main", "refs/heads/copy", ct)).ExitCode);
        await git.StatusAsync(Clone, "main", ct: ct);
        Assert.Equal(0, (await git.RemoveWorktreeAsync(Clone, Path.Combine(_root, "wt"), ct)).ExitCode);

        Assert.Empty(Ran());

        // Control: the same clone with plain git runs them, so the ones above were live.
        Git.Run(Clone, "commit", "-q", "--allow-empty", "-m", "third");
        Git.Run(Clone, "push", "-q", "origin", "main");
        Assert.Contains("pre-push", Ran());
        Assert.Contains("reference-transaction", Ran());
    }
}

/// <summary>
/// When the Host switches, its git runs as the agent: a file git writes in the clone
/// belongs to the agent's uid, not to the Host.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class GitRunnerRunsAsAgentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-gitagent-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Host_side_git_runs_as_the_agent_when_the_host_switches()
    {
        var runAs = AgentLaunchUser.Resolve("nobody");
        if (!runAs.Switches) Assert.Skip($"This process cannot switch users: {runAs.Reason}.");

        var remote = Path.Combine(_root, "remote.git");
        var clone = Path.Combine(_root, "clone");
        Git.Run(_root, "init", "-q", "--bare", "-b", "main", remote);
        Git.Run(_root, "clone", "-q", remote, clone);
        Git.Run(clone, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "first");
        Git.Run(clone, "push", "-q", "origin", "main");

        // The clone is the agent's in the image; here, nobody's.
        File.SetUnixFileMode(_root, (UnixFileMode)0b111_101_101);
        Git.Chown(runAs.Uid, runAs.Gid, remote, clone);

        using var home = new EnvironmentScope([new("HOME", _root)]);
        var result = await new GitRunner(runAs: runAs).FetchAsync(clone, TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(runAs.Uid.ToString(), Git.Owner(Path.Combine(clone, ".git", "FETCH_HEAD")));
    }
}

/// <summary>Plain git for arranging a test, run as the test process.</summary>
internal static class Git
{
    public static void Run(string directory, params string[] args) => Exec("git", directory, args);

    public static void Chown(int uid, int gid, params string[] paths) =>
        Exec("chown", "/", ["-R", $"{uid}:{gid}", .. paths]);

    public static string Owner(string path) => Exec("stat", "/", ["-c", "%u", path]).Trim();

    private static string Exec(string file, string directory, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} {string.Join(' ', args)}: {error}");
        return output;
    }
}
