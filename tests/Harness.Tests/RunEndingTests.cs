using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// The run-ending chain runs every step whatever an earlier one threw: a lease release that fails
/// must not skip the publish that pushes the team branch, and a publish that fails must not skip the
/// declaration that closes the workflow.
/// </summary>
public sealed class RunEndingTests
{
    private static readonly ContainerId Member = new("alpha", "Developer");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_lease_release_that_throws_still_publishes_and_offers()
    {
        var log = new CapturingLogger();
        var called = new List<string>();

        var declared = await RunEnding.RunAsync(
            log, Member, 7, succeeded: true, Ct,
            releaseLeases: () => throw new InvalidOperationException("the worker is gone"),
            publish: () => { called.Add("publish"); return Task.CompletedTask; },
            defaultBranch: () => { called.Add("defaultBranch"); return Task.CompletedTask; },
            idleOffer: () => { called.Add("idleOffer"); return Task.CompletedTask; },
            declare: () => { called.Add("declare"); return Task.FromResult(true); });

        Assert.True(declared);
        Assert.Equal(["publish", "defaultBranch", "idleOffer", "declare"], called);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Developer", entry.Message);
        Assert.Contains("the worker is gone", entry.Message);
    }

    [Fact]
    public async Task A_publish_that_throws_still_declares_and_a_declaration_that_throws_answers_false()
    {
        var log = new CapturingLogger();

        var declared = await RunEnding.RunAsync(
            log, Member, 7, succeeded: false, Ct,
            releaseLeases: () => Task.CompletedTask,
            publish: () => throw new IOException("origin refused"),
            defaultBranch: () => Task.CompletedTask,
            idleOffer: () => Task.CompletedTask,
            declare: () => throw new InvalidOperationException("log locked"));

        Assert.False(declared);
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
    }

    [Fact]
    public async Task A_cancelled_chain_stops_with_the_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunEnding.RunAsync(
            new CapturingLogger(), Member, 7, succeeded: true, cancelled.Token,
            releaseLeases: () => throw new OperationCanceledException(cancelled.Token),
            publish: () => Task.CompletedTask,
            defaultBranch: () => Task.CompletedTask,
            idleOffer: () => Task.CompletedTask,
            declare: () => Task.FromResult(false)));
    }
}
