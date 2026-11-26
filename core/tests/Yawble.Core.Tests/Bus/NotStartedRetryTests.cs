using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// A run the host could not get ready (its agent never started) goes again instead of stranding its deliverable, and a
/// fetch from a local repository path is tried again when only git's transport failed. Fakes only: no git, no agent CLI.
/// </summary>
public class NotStartedRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RunResult NotStarted() =>
        new(false, "could not prepare the member's clone: git fetch failed (128): fatal: Could not read from remote repository.", FailureClasses.NotStarted);

    private static BusOptions Options => new()
    {
        SafetyPoll = TimeSpan.FromMilliseconds(50),
        IdleOffer = false,
        NotStartedRetryDelay = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task ARunThatDidNotStart_GoesAgainWithTheSameItems_AndNobodyIsToldAboutTheTry()
    {
        var tries = 0;
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => Task.FromResult(++tries == 1 ? NotStarted() : RunResult.Ok));
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), runner, Options);
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        var root = await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct);

        await Wait.UntilAsync(() => tries >= 2, "Ada's second try");
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus settles");

        var adaRuns = runner.Admitted.Where(r => r.Member.Qualified == "Alpha/Ada").OrderBy(r => r.RunId).ToList();
        Assert.Equal(2, adaRuns.Count);
        Assert.Equal(adaRuns[0].Items.Select(i => i.Seq), adaRuns[1].Items.Select(i => i.Seq));
        using var s = db.OpenStore();
        var thread = s.Deliverable(root.Seq);
        Assert.DoesNotContain(thread, m => m.Type == MessageTypes.Failed && m.Source == "Alpha/Ada");
        Assert.Single(thread, m => m.Type == MessageTypes.Completed && m.Source == "Alpha/Ada");
        Assert.Equal(FailureClasses.NotStarted, s.FailureClassOf(adaRuns[0].RunId));
        Assert.Empty(s.Pending("Alpha/Ada"));
    }

    [Fact]
    public async Task AManagersOwnRunThatDidNotStart_StillReachesTheManager()
    {
        // The case that stranded a deliverable: the Manager's run carrying a hand-back failed before its agent started, and
        // nobody else is told about a Manager's failure.
        var managerTries = 0;
        var runner = new ScriptedRunner().On("Alpha/Manager", (_, _) => Task.FromResult(++managerTries == 1 ? NotStarted() : RunResult.Ok));
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), runner, Options);
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Manager", "plan it", ct: Ct);

        await Wait.UntilAsync(() => managerTries >= 2, "the Manager's second try");
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus settles");
        using var s = db.OpenStore();
        Assert.Empty(s.Pending("Alpha/Manager"));
    }

    [Fact]
    public async Task ARunThatNeverStarts_IsTriedThreeTimes_ThenReportedFailedOnce()
    {
        var tries = 0;
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => { tries++; return Task.FromResult(NotStarted()); });
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), runner, Options);
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        var root = await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct);

        await Wait.UntilAsync(() =>
        {
            using var s = db.OpenStore();
            return s.Deliverable(root.Seq).Any(m => m.Type == MessageTypes.Failed && m.Source == "Alpha/Ada");
        }, "Ada's failure is reported");
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus settles");
        await Task.Delay(200, Ct); // a fourth try would have come by now

        Assert.Equal(1 + Options.NotStartedRetries, tries);
        using var store = db.OpenStore();
        var failed = Assert.Single(store.Deliverable(root.Seq), m => m.Type == MessageTypes.Failed && m.Source == "Alpha/Ada");
        Assert.Contains("\"failureClass\":\"not_started\"", failed.Payload);
        Assert.Contains("Could not read from remote repository", failed.Payload);
        Assert.Empty(store.Pending("Alpha/Ada"));
    }

    [Fact]
    public async Task AnyOtherFailure_IsReportedAtOnce()
    {
        var tries = 0;
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => { tries++; return Task.FromResult(RunResult.Fail("the agent crashed")); });
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), runner, Options);
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct);

        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus settles");
        await Task.Delay(200, Ct);
        Assert.Equal(1, tries);
    }

    private static GitResult Git(int exit, string stderr = "") => new(exit, "", stderr);

    [Fact]
    public async Task ALocalFetch_IsTriedAgainOnlyWhenTheTransportFailed()
    {
        const string transport = "fatal: Could not read from remote repository.\n\nPlease make sure you have the correct access rights\nand the repository exists.";

        // Fails twice in transport, then works.
        var calls = 0;
        var retries = new List<int>();
        var ok = await HostGit.RetryTransportAsync(_ => Task.FromResult(++calls < 3 ? Git(128, transport) : Git(0)), 3, TimeSpan.FromMilliseconds(1),
            (n, _) => retries.Add(n), Ct);
        Assert.Equal(0, ok.ExitCode);
        Assert.Equal(3, calls);
        Assert.Equal([1, 2], retries);

        // Never works: tried the number of times asked, and git's whole error comes back.
        calls = 0;
        var last = await HostGit.RetryTransportAsync(_ => { calls++; return Task.FromResult(Git(128, transport)); }, 3, TimeSpan.FromMilliseconds(1), null, Ct);
        Assert.Equal(3, calls);
        Assert.Equal(transport, last.Stderr);

        // A refusal is not the transport: answered at once.
        calls = 0;
        var refused = await HostGit.RetryTransportAsync(_ => { calls++; return Task.FromResult(Git(1, "fatal: couldn't find remote ref refs/heads/nope")); },
            3, TimeSpan.FromMilliseconds(1), null, Ct);
        Assert.Equal(1, calls);
        Assert.Equal(1, refused.ExitCode);
    }

    [Theory]
    [InlineData("fatal: Could not read from remote repository.", true)]
    [InlineData("fatal: the remote end hung up unexpectedly", true)]
    [InlineData("fatal: early EOF", true)]
    [InlineData("sh.exe: fork: Resource temporarily unavailable", true)]
    [InlineData("fatal: couldn't find remote ref refs/heads/nope", false)]
    [InlineData("fatal: '/no/such/repo' does not appear to be a git repository\nfatal: Could not read from remote repository.", false)]
    [InlineData("", false)]
    public void WhatCountsAsATransportFailure(string stderr, bool transport) =>
        Assert.Equal(transport, HostGit.IsTransportFailure(stderr));
}
