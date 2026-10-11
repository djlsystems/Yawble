using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// An agent may amend a commit the platform has already pushed, and origin then refuses that branch
/// as non-fast-forward. The publisher must not stop there: every branch sorting after it would go
/// unpushed on every completion, each row saying "safe to try again" when retrying can never
/// succeed.
/// </summary>
public sealed class PublisherRejectionTests : IAsyncDisposable
{
    private readonly ContainerTestBed _bed = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-pub-{Guid.NewGuid():N}");

    [Fact]
    public async Task A_rewritten_branch_does_not_hold_back_the_branches_after_it()
    {
        var paths = new TeamPaths(_root);
        paths.Register("t1", null);

        var origin = Path.Combine(_root, "origin.git");
        var clone = Path.Combine(paths.ReposFor("t1"), "demo", "main");

        Git(_root, "init", "--bare", "-b", "main", origin);
        Git(_root, "clone", origin, clone);
        Commit(clone, "base");
        Git(clone, "push", "origin", "main");

        // Pushed, then rewritten locally: origin now holds a commit the clone does not.
        Git(clone, "checkout", "-b", "a-rewritten");
        Commit(clone, "wip");
        Git(clone, "push", "origin", "a-rewritten");
        Git(clone, "commit", "--amend", "-m", "wip, reworded");

        // Sorts after the rejected branch, and has never been pushed.
        Git(clone, "checkout", "-b", "b-fresh", "main");
        Commit(clone, "fresh");

        var publisher = new TeamPublisher(paths, new GitRunner(), _bed.Store);
        var report = await publisher.PublishAsync(
            "t1", [$"https://example.invalid/org/demo.git"], new ContainerId("t1", "Dev"), null, TestContext.Current.CancellationToken);

        Assert.Equal(0, Git(clone, "ls-remote", "--exit-code", origin, "refs/heads/b-fresh").ExitCode);

        var rows = await _bed.Store.ReadAfterAsync(
            0, [MessageTypes.RepoPushed, MessageTypes.RepoPushFailed], 10, TestContext.Current.CancellationToken);

        var pushed = Assert.Single(rows, r => r.Type == MessageTypes.RepoPushed);
        Assert.Contains("b-fresh", Field(pushed, "branches"));

        var failed = Assert.Single(rows, r => r.Type == MessageTypes.RepoPushFailed);
        Assert.Equal("a-rewritten", Field(failed, "branches"));
        Assert.False(JsonDocument.Parse(failed.Payload).RootElement.GetProperty("retryable").GetBoolean());

        Assert.True(report.AnyPushFailed);
    }

    /// <summary>
    /// The stored default branch is never published, whatever it is called: a push is not a
    /// merge, and `trunk` is main on a repository whose origin names it.
    /// </summary>
    [Fact]
    public async Task The_stored_default_branch_is_never_published()
    {
        var paths = new TeamPaths(_root);
        paths.Register("t1", null);

        var origin = Path.Combine(_root, "origin.git");
        var clone = Path.Combine(paths.ReposFor("t1"), "demo", "main");

        Git(_root, "init", "--bare", "-b", "trunk", origin);
        Git(_root, "clone", origin, clone);
        Git(clone, "checkout", "-b", "trunk");
        Commit(clone, "base");
        Git(clone, "push", "origin", "trunk");
        var originTrunk = Git(origin, "rev-parse", "refs/heads/trunk").Output.Trim();

        Commit(clone, "unmerged-on-trunk");
        Git(clone, "checkout", "-b", "feature");
        Commit(clone, "feature");

        await new TeamPublisher(paths, new GitRunner(), _bed.Store, (_, _) => "trunk").PublishAsync(
            "t1", ["https://example.invalid/org/demo.git"], new ContainerId("t1", "Dev"), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, Git(clone, "ls-remote", "--exit-code", origin, "refs/heads/feature").ExitCode);
        Assert.Equal(originTrunk, Git(origin, "rev-parse", "refs/heads/trunk").Output.Trim());
    }

    private static string Field(Message message, string name) =>
        JsonDocument.Parse(message.Payload).RootElement.GetProperty(name).GetString() ?? "";

    internal static void Commit(string repo, string name)
    {
        File.WriteAllText(Path.Combine(repo, $"{name}.txt"), name);
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", name);
    }

    internal static (int ExitCode, string Output) Git(string workingDirectory, params string[] args)
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

    public async ValueTask DisposeAsync()
    {
        await _bed.DisposeAsync();
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

/// <summary>
/// PUBLISH THE BRANCHES, THEN APPEND THE ROW. The ordering is the whole guarantee: a `repo.pushed`
/// row that lands before its push is a record of something that may never happen. The log here
/// checks origin at the moment the row is appended.
/// </summary>
public sealed class PublishOrderTests : IAsyncDisposable
{
    private readonly ContainerTestBed _bed = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-pub-{Guid.NewGuid():N}");

    [Fact]
    public async Task The_pushed_row_is_appended_only_after_its_branch_is_on_origin()
    {
        var paths = new TeamPaths(_root);
        paths.Register("t1", null);

        var origin = Path.Combine(_root, "origin.git");
        var clone = Path.Combine(paths.ReposFor("t1"), "demo", "main");

        PublisherRejectionTests.Git(_root, "init", "--bare", "-b", "main", origin);
        PublisherRejectionTests.Git(_root, "clone", origin, clone);
        PublisherRejectionTests.Commit(clone, "base");
        PublisherRejectionTests.Git(clone, "push", "origin", "main");
        PublisherRejectionTests.Git(clone, "checkout", "-b", "feature");
        PublisherRejectionTests.Commit(clone, "work");

        var onOriginWhenAppended = new List<bool>();
        var log = OrderProbe.Wrap(_bed.Store, message =>
        {
            if (message.Type != MessageTypes.RepoPushed) return;
            onOriginWhenAppended.Add(
                PublisherRejectionTests.Git(clone, "ls-remote", "--exit-code", origin, "refs/heads/feature").ExitCode == 0);
        });

        await new TeamPublisher(paths, new GitRunner(), log).PublishAsync(
            "t1", ["https://example.invalid/org/demo.git"], new ContainerId("t1", "Dev"), null,
            TestContext.Current.CancellationToken);

        Assert.Equal([true], onOriginWhenAppended);
    }

    public async ValueTask DisposeAsync()
    {
        await _bed.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>An <see cref="IMessageLog"/> that runs a probe on each append before passing it on.</summary>
public class OrderProbe : DispatchProxy
{
    private IMessageLog _inner = null!;
    private Action<NewMessage> _beforeAppend = null!;

    public static IMessageLog Wrap(IMessageLog inner, Action<NewMessage> beforeAppend)
    {
        var proxy = Create<IMessageLog, OrderProbe>();
        var probe = (OrderProbe)(object)proxy;
        probe._inner = inner;
        probe._beforeAppend = beforeAppend;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IMessageLog.AppendAsync) && args?[0] is NewMessage message)
        {
            _beforeAppend(message);
        }

        try
        {
            return method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
