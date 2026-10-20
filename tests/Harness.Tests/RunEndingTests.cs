using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;
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
            idleOffer: _ => { called.Add("idleOffer"); return Task.CompletedTask; },
            declare: _ => { called.Add("declare"); return Task.FromResult(true); });

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
            idleOffer: _ => Task.CompletedTask,
            declare: _ => throw new InvalidOperationException("log locked"));

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
            idleOffer: _ => Task.CompletedTask,
            declare: _ => Task.FromResult(false)));
    }

    [Fact]
    public async Task The_idle_offer_and_the_declaration_share_one_read_of_the_workflow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"harness-run-ending-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "messages.db");
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, Ct);
            var store = new SqliteMessageStore(database);
            var root = await store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Member), """{"text":"go"}""", "person-1"), Ct);
            await store.AppendAsync(new NewMessage(MessageTypes.Progress, """{"status":"working"}""", Member.ToString(), root.Seq), Ct);
            var log = CountingProxy.Over<IMessageLog>(store);

            IReadOnlyList<Message>? offered = null;
            IReadOnlyList<Message>? declared = null;

            await RunEnding.RunAsync(
                new CapturingLogger(), Member, root.Seq, succeeded: true, Ct,
                releaseLeases: () => Task.CompletedTask,
                publish: () => Task.CompletedTask,
                defaultBranch: () => Task.CompletedTask,
                idleOffer: async reads => offered = await reads.ThreadAsync(log, root.Seq, Ct),
                declare: async reads => (declared = await reads.ThreadAsync(log, root.Seq, Ct)).Count > 0);

            Assert.Equal(1, CountingProxy.Of(log).CallsTo("ReadCorrelationAsync"));
            Assert.NotNull(offered);
            Assert.Same(offered, declared);
            Assert.Equal(2, offered.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
