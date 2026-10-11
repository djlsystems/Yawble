using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// THE USAGE LEDGER, AT THE STORE. A run's terminal row writes its <c>usage_ledger</c> row in the
/// same transaction, one per run; a workflow's completion or close writes its <c>workflow_ledger</c>
/// row the same way; and nothing updates or deletes either. See AGENTS.md, Usage.
/// </summary>
public sealed class UsageLedgerTests : IDisposable
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-ledger-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_directory, "messages.db");

    private readonly SqliteMessageStore _store;
    private readonly SqliteUsageLedger _ledger;

    public UsageLedgerTests()
    {
        Directory.CreateDirectory(_directory);
        new SchemaMigrator(Database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _store = new SqliteMessageStore(Database);
        _ledger = new SqliteUsageLedger(Database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_finished_run_writes_one_ledger_row_with_its_times_and_weighted_tokens()
    {
        var delivery = await _store.AppendAsync(Instruction("look"), Ct);
        var started = await _store.AppendAsync(new NewMessage(MessageTypes.Started, "{}", Dev.ToString(), delivery.Seq), Ct);
        var terminal = await _store.AppendAsync(Terminal(delivery.Seq, new
        {
            tokensIn = 1000, tokensOut = 200, tokensCachedIn = 5000, tokensCacheCreation = 400, tokensSource = "claude",
        }), Ct);

        var row = Assert.Single(await _ledger.ReadRecentRunsAsync(Dev, 10, Ct));

        Assert.Equal(terminal.Seq, row.RunSeq);
        Assert.Equal(delivery.CorrelationId, row.Correlation);
        Assert.Equal("Alpha", row.TeamId);
        Assert.Equal("Alpha", row.TeamName);
        Assert.Equal("Dev", row.Member);
        Assert.Equal("completed", row.RunOutcome);
        Assert.Equal(delivery.OccurredAt, row.QueuedAt);
        Assert.Equal(started.OccurredAt, row.StartedAt);
        Assert.Equal(terminal.OccurredAt, row.EndedAt);
        Assert.True(row.Measured);
        Assert.Equal(1000, row.TokensIn);
        Assert.Equal(200, row.TokensOut);
        Assert.Equal(5000, row.TokensCachedIn);
        Assert.Equal(400, row.TokensCacheCreation);
        Assert.Null(row.TokensCombined);

        // InvocationUsage.BillableTokens' weights: cache reads 1/10, cache writes 5/4.
        Assert.Equal(new InvocationUsage(1000, 200, "claude", cachedIn: 5000, cacheCreation: 400).BillableTokens(), row.Billable);
        Assert.False(row.Backfilled);
    }

    [Fact]
    public async Task Unmeasured_is_null_and_counted_never_zero()
    {
        var delivery = await _store.AppendAsync(Instruction("look"), Ct);
        await _store.AppendAsync(Terminal(delivery.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null }), Ct);
        var estimated = await _store.AppendAsync(Instruction("again", delivery.Seq), Ct);
        await _store.AppendAsync(Terminal(estimated.Seq, new { tokensIn = 10, tokensOut = 10, tokensSource = UsageSource.ExcludedEstimate }), Ct);

        var rows = await _ledger.ReadRecentRunsAsync(Dev, 10, Ct);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.False(row.Measured);
            Assert.Null(row.TokensIn);
            Assert.Null(row.TokensOut);
            Assert.Null(row.TokensCachedIn);
            Assert.Null(row.TokensCacheCreation);
            Assert.Null(row.TokensCombined);
            Assert.Null(row.Billable);
        });

        var spend = await _store.GetWorkflowSpendAsync(delivery.CorrelationId, Ct);
        Assert.Equal(new WorkflowSpend(0, 0, 2), spend);
    }

    [Fact]
    public async Task A_combined_total_is_billed_as_reported_with_no_split()
    {
        var delivery = await _store.AppendAsync(Instruction("look"), Ct);
        await _store.AppendAsync(Terminal(delivery.Seq, new { tokensTotal = 4321, tokensSource = "codex" }), Ct);

        var row = Assert.Single(await _ledger.ReadRecentRunsAsync(Dev, 10, Ct));

        Assert.True(row.Measured);
        Assert.Equal(4321, row.TokensCombined);
        Assert.Equal(4321, row.Billable);
        Assert.Null(row.TokensIn);
        Assert.Null(row.TokensOut);
    }

    [Fact]
    public async Task A_batched_runs_extra_rows_write_no_ledger_row()
    {
        var first = await _store.AppendAsync(Instruction("one"), Ct);
        var second = await _store.AppendAsync(Instruction("two", first.Seq), Ct);
        var carrying = await _store.AppendAsync(Terminal(first.Seq, new { tokensIn = 100, tokensOut = 40, tokensSource = "claude" }), Ct);
        await _store.AppendAsync(Terminal(second.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null, usageCountedOn = first.Seq }), Ct);

        var row = Assert.Single(await _ledger.ReadRecentRunsAsync(Dev, 10, Ct));
        Assert.Equal(carrying.Seq, row.RunSeq);
        Assert.Equal(140, row.Billable);
    }

    [Fact]
    public async Task When_the_ledger_row_cannot_be_written_the_terminal_row_is_not_written_either()
    {
        var delivery = await _store.AppendAsync(Instruction("look"), Ct);

        // The next seq the log will hand out is already taken in the ledger, so the ledger insert
        // inside the append fails - and with it the append.
        var next = delivery.Seq + 1;
        await ExecuteAsync(
            $"""
             INSERT INTO usage_ledger (run_seq, correlation, member, run_outcome, ended_at, measured)
             VALUES ({next}, 0, 'Squatter', 'completed', '2026-01-01T00:00:00.0000000+00:00', 0)
             """);

        await Assert.ThrowsAsync<SqliteException>(() =>
            _store.AppendAsync(Terminal(delivery.Seq, new { tokensIn = 1, tokensOut = 1, tokensSource = "claude" }), Ct));

        Assert.Null(await _store.FindAsync(next, Ct));
        Assert.DoesNotContain(await _store.ReadCorrelationAsync(delivery.CorrelationId, Ct), m => m.Type == MessageTypes.Completed);
    }

    [Fact]
    public async Task A_workflow_gets_a_row_per_completion_and_close_and_the_newest_is_read()
    {
        var root = await _store.AppendAsync(Instruction("look"), Ct);
        await _store.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", new ContainerId("Alpha", "Manager").ToString(), root.Seq), Ct);
        await _store.AppendAsync(new NewMessage(MessageTypes.WorkflowClosed, JsonSerializer.Serialize(new { team = "Alpha" }), "person-1", root.Seq), Ct);
        await Task.Delay(20, Ct);
        var again = await _store.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", new ContainerId("Alpha", "Manager").ToString(), root.Seq), Ct);

        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM workflow_ledger WHERE correlation = " + root.CorrelationId));

        var newest = await _ledger.ReadWorkflowAsync(root.CorrelationId, Ct);
        Assert.NotNull(newest);
        Assert.Equal(again.Seq, newest.CloseSeq);
        Assert.Equal(WorkflowLedgerRow.Completed, newest.HowClosed);
        Assert.Equal("Alpha", newest.TeamId);
        Assert.Equal(root.OccurredAt, newest.RootAt);
        Assert.Equal((again.OccurredAt - root.OccurredAt).TotalSeconds, newest.ElapsedSeconds);
        Assert.Null(newest.OutcomeIdAtClose);
    }

    [Fact]
    public async Task Nothing_updates_or_deletes_a_ledger_row_and_a_log_purge_leaves_it()
    {
        var delivery = await _store.AppendAsync(Instruction("look"), Ct);
        var terminal = await _store.AppendAsync(Terminal(delivery.Seq, new { tokensIn = 5, tokensOut = 5, tokensSource = "claude" }), Ct);
        await _store.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", new ContainerId("Alpha", "Manager").ToString(), terminal.Seq), Ct);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE usage_ledger SET billable = 0"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM usage_ledger"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE workflow_ledger SET how_closed = 'closed'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM workflow_ledger"));

        // The log's one deletion - Reset's purge, and any retention built on it - takes every row.
        var all = (await _store.ReadRangeAsync(0, 100, Ct)).Select(m => m.Seq).ToList();
        Assert.Equal(all.Count, (await _store.DeleteAsync(all, Ct)).Purged);

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM usage_ledger"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM workflow_ledger"));
        Assert.Equal(new WorkflowSpend(10, 1, 0), await _store.GetWorkflowSpendAsync(delivery.CorrelationId, Ct));
    }

    [Fact]
    public async Task A_delivery_is_attributed_when_it_is_accepted_and_the_run_reads_that_record()
    {
        var pending = new SqlitePendingDeliveries(Database);
        var manager = new ContainerId("Alpha", "Manager");

        // A schedule's fire to Dev, and the hand-back Dev's run writes, which wakes the Manager: the
        // ledger's two hops, each resolved when its delivery is accepted.
        var fire = await _store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "poll" }), "schedule:s1"), Ct);
        await pending.AddAsync(Dev, fire.Seq, Ct);
        var handback = await _store.AppendAsync(new NewMessage(MessageTypes.Handback, "{}", Dev.ToString(), fire.Seq), Ct);
        await pending.AddAsync(manager, handback.Seq, Ct);
        await pending.AddAsync(manager, handback.Seq, Ct);

        var fired = fire.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            [$"{fire.Seq}|{fired}|schedule:s1|{fired}",
             $"{handback.Seq}|{handback.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}|schedule:s1|{fired}"],
            await DeliveryRecordsAsync());

        // THE RUN READS THE RECORD, not the log: a record that says otherwise than the log wins.
        var tell = await _store.AppendAsync(Instruction("look"), Ct);
        await ExecuteAsync(
            $"INSERT INTO delivery_ledger VALUES ({tell.Seq}, {tell.CorrelationId}, '2026-01-01T00:00:00.0000000+00:00', 'trigger:t9', '2026-01-01T00:00:00.0000000+00:00')");
        await pending.AddAsync(Dev, tell.Seq, Ct);
        await _store.AppendAsync(Terminal(tell.Seq, new { tokensIn = 5, tokensOut = 5, tokensSource = "claude" }), Ct);

        var run = Assert.Single(await _ledger.ReadRecentRunsAsync(Dev, 10, Ct));
        Assert.Equal("trigger:t9", run.TriggerSource);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), run.QueuedAt);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE delivery_ledger SET trigger_source = NULL"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM delivery_ledger"));
    }

    [Fact]
    public async Task A_nudge_writes_its_row_with_its_log_row_and_nothing_updates_or_deletes_it()
    {
        var root = await _store.AppendAsync(Instruction("look"), Ct);
        var nudge = await _store.AppendAsync(Instruction("nudged", root.Seq), Ct);
        await _store.AppendAsync(Instruction("not a nudge", nudge.Seq), Ct);

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM nudge_ledger"));
        Assert.Equal(nudge.Seq, await CountAsync($"SELECT nudge_seq FROM nudge_ledger WHERE correlation = {root.Seq}"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE nudge_ledger SET correlation = 0"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM nudge_ledger"));
    }

    private async Task<List<string>> DeliveryRecordsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT delivery_seq || '|' || queued_at || '|' || trigger_source || '|' || trigger_fired_at FROM delivery_ledger ORDER BY delivery_seq";

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    private static NewMessage Instruction(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }), "console", causation);

    private static NewMessage Terminal(long causation, object payload) =>
        new(MessageTypes.Completed, JsonSerializer.Serialize(payload), Dev.ToString(), causation);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
