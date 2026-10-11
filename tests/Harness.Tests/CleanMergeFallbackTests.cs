using System.Diagnostics;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A team that resolved a conflicting rebase by MERGING origin/main into main: when origin/main
/// moves on by unrelated commits, a rebase drops the team's merge and replays its own commits into
/// the same conflicts, while a plain merge of origin/main is clean. The rebase falls back to that
/// clean merge.
/// </summary>
public sealed class CleanMergeFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-merge-{Guid.NewGuid():N}");

    [Fact]
    public async Task A_rebase_that_replays_a_resolved_conflict_falls_back_to_a_clean_merge()
    {
        var origin = Path.Combine(_root, "origin.git");
        var upstream = Path.Combine(_root, "upstream");
        var clone = Path.Combine(_root, "clone");
        var ct = TestContext.Current.CancellationToken;

        Git(_root, "init", "--bare", "-b", "main", origin);
        Git(_root, "clone", origin, upstream);
        Write(upstream, "shared.txt", "base\n");
        Commit(upstream, "base");
        Git(upstream, "push", "origin", "main");

        Git(_root, "clone", origin, clone);
        // The merge commit takes its identity from the clone's own config, as a team clone's does.
        // Without it the test passes only where a global identity exists (a developer machine)
        // and fails in the SDK container with "Please tell me who you are".
        Git(clone, "config", "user.name", "t");
        Git(clone, "config", "user.email", "t@example.invalid");

        // The team edits the shared file; upstream edits the same line: a real conflict.
        Write(clone, "shared.txt", "team\n");
        Commit(clone, "team edit");
        Write(upstream, "shared.txt", "upstream\n");
        Commit(upstream, "upstream edit");
        Git(upstream, "push", "origin", "main");

        // The team resolves it by merging origin/main, as the Ask instruction tells it to.
        Git(clone, "fetch", "origin");
        Git(clone, "merge", "--no-edit", "origin/main");
        Write(clone, "shared.txt", "team and upstream\n");
        Git(clone, "add", ".");
        Git(clone, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "--no-edit");

        // Then origin/main moves on, touching nothing the team touched.
        Write(upstream, "other.txt", "later\n");
        Commit(upstream, "unrelated");
        Git(upstream, "push", "origin", "main");
        Git(clone, "fetch", "origin");

        Assert.NotEqual(0, Git(clone, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "rebase", "origin/main").ExitCode);
        Git(clone, "rebase", "--abort");

        var merged = await CleanMergeFallback.TryAsync(new GitRunner(), clone, "main", ct);

        Assert.True(merged, "a clean merge was available and not taken");
        Assert.Equal(0, Git(clone, "merge-base", "--is-ancestor", "origin/main", "main").ExitCode);
        Assert.Equal("team and upstream\n", File.ReadAllText(Path.Combine(clone, "shared.txt")).Replace("\r\n", "\n"));
        Assert.Empty(Git(clone, "status", "--porcelain").Output.Trim());
    }

    [Fact]
    public async Task A_merge_that_would_conflict_changes_nothing()
    {
        var origin = Path.Combine(_root, "origin.git");
        var upstream = Path.Combine(_root, "upstream");
        var clone = Path.Combine(_root, "clone");
        var ct = TestContext.Current.CancellationToken;

        Git(_root, "init", "--bare", "-b", "main", origin);
        Git(_root, "clone", origin, upstream);
        Write(upstream, "shared.txt", "base\n");
        Commit(upstream, "base");
        Git(upstream, "push", "origin", "main");
        Git(_root, "clone", origin, clone);

        Write(clone, "shared.txt", "team\n");
        Commit(clone, "team edit");
        Write(upstream, "shared.txt", "upstream\n");
        Commit(upstream, "upstream edit");
        Git(upstream, "push", "origin", "main");
        Git(clone, "fetch", "origin");
        var before = Git(clone, "rev-parse", "main").Output.Trim();

        var merged = await CleanMergeFallback.TryAsync(new GitRunner(), clone, "main", ct);

        Assert.False(merged);
        Assert.Equal(before, Git(clone, "rev-parse", "main").Output.Trim());
        Assert.Empty(Git(clone, "status", "--porcelain").Output.Trim());
    }

    private static void Write(string repo, string name, string text) =>
        File.WriteAllText(Path.Combine(repo, name), text);

    private static void Commit(string repo, string message)
    {
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", message);
    }

    private static (int ExitCode, string Output) Git(string workingDirectory, params string[] args)
    {
        Directory.CreateDirectory(workingDirectory);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_AUTHOR_NAME"] = "t";
        start.Environment["GIT_AUTHOR_EMAIL"] = "t@example.invalid";
        start.Environment["GIT_COMMITTER_NAME"] = "t";
        start.Environment["GIT_COMMITTER_EMAIL"] = "t@example.invalid";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
