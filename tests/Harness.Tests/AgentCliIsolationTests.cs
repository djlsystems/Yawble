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
}
