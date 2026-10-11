using System.Diagnostics;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The test assembly's guard against a test Host starting a real, paid agent (see
/// <see cref="AgentCliIsolation"/>): every built-in preset's command resolves to the stub, and the
/// stub refuses to run.
/// </summary>
public sealed class AgentCliIsolationTests
{
    [Fact]
    public void Every_built_in_presets_command_resolves_to_the_stub_for_this_test_process()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stubs are POSIX shell scripts, and the suite runs on Linux.");

        var commands = AgentCliIsolation.Commands();

        // Read from the build, so an empty list would make the rest vacuous; and only the presets
        // that call a model, so the suite's own `bash` and `cat` still run.
        Assert.Contains("claude", commands);
        Assert.DoesNotContain("bash", commands);
        Assert.DoesNotContain("cat", commands);

        Assert.All(commands, command =>
            Assert.Equal(Path.Combine(AgentCliIsolation.Root, command), Harness.Pty.PathSearch.Find(command)));
    }

    [Fact]
    public async Task The_stub_says_why_and_fails_without_starting_anything()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stubs are POSIX shell scripts, and the suite runs on Linux.");

        var start = new ProcessStartInfo(Harness.Pty.PathSearch.Find("claude")!, ["-p", "hello"])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, process.ExitCode);
        Assert.Contains(AgentCliIsolation.StubMessage, error);
    }

    [Fact]
    public void Every_built_in_presets_update_program_resolves_to_the_update_stub_and_npms_prefix_is_scratch()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stubs are POSIX shell scripts, and the suite runs on Linux.");

        var programs = AgentCliIsolation.UpdatePrograms();

        // Read from the build: npm is what the built-in npm installs update with.
        Assert.Contains("npm", programs);
        Assert.All(programs, program => Assert.NotNull(Harness.Pty.PathSearch.Find(program)));
        Assert.Equal(Path.Combine(AgentCliIsolation.UpdateRoot, "npm"), Harness.Pty.PathSearch.Find("npm"));

        // npm reads either spelling; both name the scratch prefix, under this run's own folder.
        Assert.All(AgentCliIsolation.NpmPrefixVariables, variable =>
            Assert.Equal(AgentCliIsolation.NpmPrefix, Environment.GetEnvironmentVariable(variable)));
        Assert.StartsWith(AgentCliIsolation.Root + Path.DirectorySeparatorChar, AgentCliIsolation.NpmPrefix);
    }

    [Fact]
    public async Task The_update_stub_says_why_names_the_prefix_it_was_given_and_fails()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stubs are POSIX shell scripts, and the suite runs on Linux.");

        var start = new ProcessStartInfo(Harness.Pty.PathSearch.Find("npm")!, ["install", "-g", "@anthropic-ai/claude-code@latest"])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, process.ExitCode);
        Assert.Contains($"{AgentCliIsolation.UpdateStubMessage} (npm prefix: {AgentCliIsolation.NpmPrefix})", error);
    }

    [Fact]
    public void A_child_environment_a_test_builds_gets_the_update_stubs_first_and_the_scratch_prefix_whatever_it_set()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stubs are POSIX shell scripts, and the suite runs on Linux.");
        var real = Path.Combine(Path.GetTempPath(), "not-the-instance", "npm-global");

        var own = new Dictionary<string, string?> { ["PATH"] = "/usr/local/bin:/usr/bin:/bin", ["NPM_CONFIG_PREFIX"] = real, ["npm_config_prefix"] = real };
        AgentCliIsolation.Guard(own);
        Assert.Equal($"{AgentCliIsolation.UpdateRoot}:/usr/local/bin:/usr/bin:/bin", own["PATH"]);
        Assert.All(AgentCliIsolation.NpmPrefixVariables, variable => Assert.Equal(AgentCliIsolation.NpmPrefix, own[variable]));

        // Already guarded: PATH is left as it is.
        var inherited = new Dictionary<string, string?> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") };
        AgentCliIsolation.Guard(inherited);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), inherited["PATH"]);

        var cleared = new Dictionary<string, string?>();
        AgentCliIsolation.Guard(cleared);
        Assert.Equal(AgentCliIsolation.UpdateRoot, cleared["PATH"]);
    }

    /// <summary>
    /// A test that builds a child's environment from nothing loses what this process inherits, so it
    /// passes it through the guard; the beds that start Hosts and workers do so over whatever a test gave.
    /// </summary>
    [Fact]
    public void Every_test_that_clears_a_childs_environment_and_every_process_bed_applies_the_guard()
    {
        var tests = Path.Combine(RepoRoot(), "tests");
        var clearing = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("Environment.Clear()", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(clearing);
        Assert.All(clearing, file => Assert.Contains("AgentCliIsolation.Guard(", File.ReadAllText(file)));
        Assert.Contains("AgentCliIsolation.Guard(start.Environment)",
            File.ReadAllText(Path.Combine(tests, "Harness.Tests", "WorkerProcesses", "ProcessBed.cs")));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Harness.slnx) was not found above the test output.");
    }
}
