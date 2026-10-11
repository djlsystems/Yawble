using System.Diagnostics;

namespace Harness.Tests.Host;

/// <summary>
/// AN INTERRUPTED NPM INSTALL DOES NOT KEEP A CLI AWAY FOR GOOD. A CLI replaced part-way through
/// updating itself leaves npm's moved-aside copy (e.g. `@anthropic-ai/.claude-code-c9HdIsHr`) and
/// part of the new one, with `claude` pointing at nothing; the start script's `npm install -g`
/// would then fail with ENOTEMPTY renaming onto the folder still there, on every start.
/// `scripts/npm-torn-install.sh` runs before each install.
/// </summary>
public sealed class NpmTornInstallTests : IDisposable
{
    private readonly string _prefix = Directory.CreateTempSubdirectory("harness-npm-prefix-").FullName;

    public void Dispose() => Directory.Delete(_prefix, recursive: true);

    [Fact]
    public void The_moved_aside_copy_and_the_partial_package_are_removed_and_nothing_else()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        var scope = Path.Combine(_prefix, "lib", "node_modules", "@anthropic-ai");
        var partial = Seed(scope, "claude-code");
        var movedAside = Seed(scope, ".claude-code-c9HdIsHr");
        var neighbour = Seed(scope, "sdk");
        var neighboursOwn = Seed(scope, ".sdk-Ab12Cd34");
        var otherScope = Seed(Path.Combine(_prefix, "lib", "node_modules", "@openai"), "codex");

        var (code, output) = Run("@anthropic-ai/claude-code");

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(movedAside), output);
        Assert.False(Directory.Exists(partial), output);
        Assert.True(Directory.Exists(neighbour), output);
        Assert.True(Directory.Exists(neighboursOwn), output);
        Assert.True(Directory.Exists(otherScope), output);
        Assert.Contains(".claude-code-c9HdIsHr", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_never_installed_is_nothing_to_do()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");

        var (code, output) = Run("@github/copilot");

        Assert.Equal(0, code);
        Assert.Equal("", output);
    }

    private static string Seed(string parent, string name)
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.WriteAllText(Path.Combine(dir, "package.json"), "{}");
        return dir;
    }

    private (int Code, string Output) Run(string package)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(RepoRoot(), "scripts", "npm-torn-install.sh"));
        start.ArgumentList.Add(_prefix);
        start.ArgumentList.Add(package);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No Harness.slnx above the test output.");
    }
}
