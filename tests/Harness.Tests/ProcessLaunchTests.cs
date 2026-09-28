using Harness.Contracts;
using Harness.Host;
using Harness.Pty;

namespace Harness.Tests;

public sealed class PathSearchTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("harness-pathsearch-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_bare_name_is_found_on_PATH()
    {
        var found = PathSearch.Find("bash");

        Assert.NotNull(found);
        Assert.True(Path.IsPathRooted(found));
        Assert.Equal("bash", Path.GetFileName(found));
    }

    [Fact]
    public void A_file_without_the_executable_bit_is_not_found()
    {
        var file = Path.Combine(_directory, "tool");
        File.WriteAllText(file, "#!/bin/sh\n");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Null(PathSearch.Find(file));

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal(file, PathSearch.Find(file));
    }

    [Fact]
    public void A_missing_command_is_null()
    {
        Assert.Null(PathSearch.Find("harness-no-such-command-anywhere"));
        Assert.Null(PathSearch.Find(Path.Combine(_directory, "absent")));
        Assert.Null(PathSearch.Find(" "));
    }
}

public sealed class TreeDeletionTests
{
    [Fact]
    public void A_symlink_inside_the_tree_is_unlinked_not_followed()
    {
        var outside = Directory.CreateTempSubdirectory("harness-outside-").FullName;
        var tree = Directory.CreateTempSubdirectory("harness-tree-").FullName;

        try
        {
            var precious = Path.Combine(outside, "precious.txt");
            File.WriteAllText(precious, "not the tree's");
            Directory.CreateDirectory(Path.Combine(tree, "nested"));
            Directory.CreateSymbolicLink(Path.Combine(tree, "nested", "link"), outside);
            File.CreateSymbolicLink(Path.Combine(tree, "file-link"), precious);

            Directory.Delete(tree, recursive: true);

            Assert.False(Directory.Exists(tree));
            Assert.True(File.Exists(precious));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
            if (Directory.Exists(tree)) Directory.Delete(tree, recursive: true);
        }
    }
}

public sealed class ProcessAgentRunnerLaunchTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("harness-runner-").FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_workspace);
        Directory.Delete(_workspace, recursive: true);
    }

    private Task<AgentResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string prompt = "hello")
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition(
                "probe",
                AgentMode.Headless,
                new AgentLaunch(fileName, arguments, LanguageModel: false)),
        ]);

        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat());

        return runner.RunAsync(new AgentInvocation(
            new ContainerId("alpha", "worker"),
            "You are a probe.",
            prompt,
            _workspace,
            new Dictionary<string, string>(),
            Agent: "probe"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_command_not_on_PATH_is_a_launch_failure()
    {
        var result = await RunAsync("harness-no-such-command-anywhere", []);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("not an executable file on PATH", result.LaunchError);
    }

    [Fact]
    public async Task The_child_leads_its_own_process_group()
    {
        var result = await RunAsync("bash", ["-c", "echo \"$$ $(cut -d' ' -f5 /proc/$$/stat)\""]);

        Assert.Equal(0, result.ExitCode);
        var parts = result.Output.Trim().Split(' ');
        Assert.Equal(parts[0], parts[1]);
    }

    [Fact]
    public async Task A_detached_grandchild_does_not_outlive_the_run()
    {
        var pidFile = Path.Combine(_workspace, "grandchild.pid");

        var result = await RunAsync(
            "bash",
            ["-c", $"sleep 300 </dev/null >/dev/null 2>&1 & echo $! > '{pidFile}'"]);

        Assert.Equal(0, result.ExitCode);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());

        // Killed, then reaped by whoever it was re-parented to; a zombie counts as gone.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (IsAlive(pid) && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(IsAlive(pid));
    }

    [Fact]
    public async Task A_prompt_over_the_argument_bound_is_handed_over_as_a_file()
    {
        var prompt = new string('x', ProcessAgentRunner.MaxArgumentBytes);

        var result = await RunAsync("bash", ["-c", "printf %s \"$1\"", "probe", "{userPrompt}"], prompt);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("YOUR TASK IS AT THE TOP OF THIS FILE", result.Output);
    }

    [Fact]
    public async Task A_prompt_under_the_argument_bound_goes_inline()
    {
        var prompt = new string('x', ProcessAgentRunner.MaxArgumentBytes - 1);

        var result = await RunAsync("bash", ["-c", "printf %s \"$1\"", "probe", "{userPrompt}"], prompt);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(prompt, result.Output);
    }

    private static bool IsAlive(int pid)
    {
        var stat = $"/proc/{pid}/stat";
        if (!File.Exists(stat)) return false;

        try
        {
            var text = File.ReadAllText(stat);
            return text[(text.LastIndexOf(')') + 2)] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }
}
