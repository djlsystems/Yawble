using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// A READ THAT FAILS IS LOGGED AND SKIPPED, NEVER A RUN'S FAILURE. A pass whose read throws answers
/// normally with a warning logged, keeps what it recorded, and the next pass carries on; a tree git
/// cannot read is skipped without throwing.
/// </summary>
public sealed class WorkspaceFileReadFailureTests : IDisposable
{
    private const string Team = "Alpha";
    private const string Member = "Dev";
    private const string Repo = "Widget";

    private static readonly WorkspaceRun Run = new(Team, Member, 3);

    private readonly string _root = Directory.CreateTempSubdirectory("harness-workspace-read-failure-").FullName;
    private readonly TeamPaths _paths;
    private readonly WorkspaceFileChangeStore _record;
    private readonly string _tree;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WorkspaceFileReadFailureTests()
    {
        _paths = new TeamPaths(Path.Combine(_root, "data"));
        _paths.Register(Team, null);

        var clone = Path.Combine(_paths.ReposFor(Team), Repo, "main");
        Directory.CreateDirectory(clone);
        Git(clone, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(clone, "README.md"), "readme");
        Git(clone, "add", "-A");
        Git(clone, "commit", "-q", "-m", "Initial");
        _tree = Path.Combine(_paths.ReposFor(Team), Repo, Worktrees.DirectoryName(Member, "1"));
        Git(clone, "worktree", "add", "-q", _tree, "-b", "dev/1");

        var database = Path.Combine(_root, "messages.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _record = new WorkspaceFileChangeStore(database);
    }

    [Fact]
    public async Task A_read_that_throws_is_logged_and_skipped_and_the_next_pass_carries_on()
    {
        var logger = new Captured();
        var fail = true;
        var watch = new WorkspaceFileWatch(new GitRunner(), _paths, (_, _) =>
            fail ? throw new IOException("the hub is gone") : Task.CompletedTask,
            record: _record, logger: logger);
        watch.Join("person");

        await watch.PassAsync([Run], readAll: false, Ct);
        File.WriteAllText(Path.Combine(_tree, "first.txt"), "first");

        Assert.Equal(0, await watch.PassAsync([Run], readAll: true, Ct));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains($"{Team}/{Member}", warning.Message);

        fail = false;
        File.WriteAllText(Path.Combine(_tree, "second.txt"), "second");
        Assert.Equal(1, await watch.PassAsync([Run], readAll: true, Ct));

        var rows = await _record.ReadAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10,
            new Dictionary<string, DateTimeOffset> { [Team] = DateTimeOffset.MinValue }, Ct);
        Assert.Equal(["first.txt", "second.txt"], rows.SelectMany(r => r.Files).Select(f => f.Path));
        Assert.All(rows, r => Assert.Equal(3L, r.Correlation));
    }

    [Fact]
    public async Task A_tree_git_cannot_read_is_skipped_without_throwing()
    {
        var watch = new WorkspaceFileWatch(new GitRunner(), _paths, (_, _) => Task.CompletedTask, record: _record);

        await watch.PassAsync([Run], readAll: false, Ct);
        File.Move(Path.Combine(_tree, ".git"), Path.Combine(_tree, ".git.moved"));

        Assert.Equal(0, await watch.PassAsync([Run], readAll: true, Ct));
        Assert.Equal(0, await watch.PassAsync([], readAll: true, Ct));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false" })
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
    }

    private sealed class Captured : ILogger<WorkspaceFileWatch>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
