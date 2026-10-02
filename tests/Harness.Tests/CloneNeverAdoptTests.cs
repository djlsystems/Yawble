using System.Diagnostics;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// ONLY A CLONE THAT HOLDS NOTHING IS MADE AGAIN. The platform's clone may replace a clone that came
/// out empty, but a directory at the clone's path that holds anything a person could lose - a commit,
/// a file in its working tree, or no repository at all - is still left alone and answered
/// <see cref="RepoCloneResult.AlreadyThere"/>, untouched.
/// </summary>
public sealed class CloneNeverAdoptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-never-adopt-").FullName;
    private readonly string _origin;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CloneNeverAdoptTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "main", _origin);
        Git(_root, "clone", _origin, seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "base");
        Git(seed, "push", "origin", "main");
    }

    [Fact]
    public async Task A_clone_with_a_commit_of_its_own_is_left_alone()
    {
        var path = Target();
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "work");
        Git(path, "remote", "add", "origin", _origin);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "mine\n");
        Git(path, "add", ".");
        Git(path, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "unpushed");

        await AssertLeftAloneAsync(path, "notes.txt");
    }

    [Fact]
    public async Task A_repository_with_no_commit_but_a_file_in_its_working_tree_is_left_alone()
    {
        var path = Target();
        Directory.CreateDirectory(path);
        Git(path, "init", "-q");
        Git(path, "remote", "add", "origin", _origin);
        File.WriteAllText(Path.Combine(path, "draft.txt"), "not yet committed\n");

        await AssertLeftAloneAsync(path, "draft.txt");
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_left_alone()
    {
        var path = Target();
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "unrelated.txt"), "somebody's work\n");

        await AssertLeftAloneAsync(path, "unrelated.txt");
    }

    private string Target() => Path.Combine(_root, "clones", "Widget", "main");

    private async Task AssertLeftAloneAsync(string path, string file)
    {
        var before = File.ReadAllText(Path.Combine(path, file));

        var outcome = await new RepoClone(new GitRunner()).EnsureAsync(_origin, path, Ct);

        Assert.Equal(RepoCloneResult.AlreadyThere, outcome.Result);
        Assert.Equal(before, File.ReadAllText(Path.Combine(path, file)));
        Assert.False(File.Exists(Path.Combine(path, "README.md")), "the origin was cloned over what was there");
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetDirectoryName(path)!), d => d != path);
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
