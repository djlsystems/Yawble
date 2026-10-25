using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IUsageLedger"/>, and the writer behind it, as each provider must keep them. The first nine
/// are <c>UsageLedgerTests</c>' with the same names and values (a run's terminal row writes its row in
/// the append's own transaction, one per run; a failing ledger write stores neither row; nothing
/// updates or deletes one); then <c>TeamRunsReadTests</c>' and <c>LedgerParityTests</c>' tile;
/// then the export page and the runs ended in a period, from the interface's remarks; and two
/// subscribers accepting one delivery at once.
/// </summary>
public abstract class UsageLedgerContract : ProviderContract
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");
    private static readonly ContainerId Echo = new("Alpha", "Echo");
    private static readonly ContainerId Manager = new("Alpha", "Manager");

    protected async Task<IUsageLedger> CreateAsync() => (await StoresAsync()).UsageLedger;

    [Fact]
    public async Task A_finished_run_writes_one_ledger_row_with_its_times_and_weighted_tokens()
    {
        var ledger = await CreateAsync();
        var log = (await StoresAsync()).Log;

        var delivery = await log.AppendAsync(Instruction("look"), Ct);
        var started = await log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", Dev.ToString(), delivery.Seq), Ct);
        var terminal = await log.AppendAsync(Terminal(delivery.Seq, new
        {
            tokensIn = 1000, tokensOut = 200, tokensCachedIn = 5000, tokensCacheCreation = 400, tokensSource = "claude",
        }), Ct);

        var row = Assert.Single(await ledger.ReadRecentRunsAsync(Dev, 10, Ct));

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
        Assert.Equal(new InvocationUsage(1000, 200, "claude", cachedIn: 5000, cacheCreation: 400).BillableTokens(), row.Billable);
        Assert.False(row.Backfilled);
    }

    [Fact]
    public async Task Unmeasured_is_null_and_counted_never_zero()
    {
        var ledger = await CreateAsync();
        var log = (await StoresAsync()).Log;

        var delivery = await log.AppendAsync(Instruction("look"), Ct);
        await log.AppendAsync(Terminal(delivery.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null }), Ct);
        var estimated = await log.AppendAsync(Instruction("again", delivery.Seq), Ct);
        await log.AppendAsync(Terminal(estimated.Seq, new { tokensIn = 10, tokensOut = 10, tokensSource = UsageSource.ExcludedEstimate }), Ct);

        var rows = await ledger.ReadRecentRunsAsync(Dev, 10, Ct);

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

        Assert.Equal(new WorkflowSpend(0, 0, 2), await log.GetWorkflowSpendAsync(delivery.CorrelationId, Ct));
    }

    [Fact]
    public async Task A_combined_total_is_billed_as_reported_with_no_split()
    {
        var ledger = await CreateAsync();
        var log = (await StoresAsync()).Log;

        var delivery = await log.AppendAsync(Instruction("look"), Ct);
        await log.AppendAsync(Terminal(delivery.Seq, new { tokensTotal = 4321, tokensSource = "codex" }), Ct);

        var row = Assert.Single(await ledger.ReadRecentRunsAsync(Dev, 10, Ct));

        Assert.True(row.Measured);
        Assert.Equal(4321, row.TokensCombined);
        Assert.Equal(4321, row.Billable);
        Assert.Null(row.TokensIn);
        Assert.Null(row.TokensOut);
    }

    [Fact]
    public async Task A_batched_runs_extra_rows_write_no_ledger_row()
    {
        var ledger = await CreateAsync();
        var log = (await StoresAsync()).Log;

        var first = await log.AppendAsync(Instruction("one"), Ct);
        var second = await log.AppendAsync(Instruction("two", first.Seq), Ct);
        var carrying = await log.AppendAsync(Terminal(first.Seq, new { tokensIn = 100, tokensOut = 40, tokensSource = "claude" }), Ct);
        await log.AppendAsync(Terminal(second.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null, usageCountedOn = first.Seq }), Ct);

        var row = Assert.Single(await ledger.ReadRecentRunsAsync(Dev, 10, Ct));
        Assert.Equal(carrying.Seq, row.RunSeq);
        Assert.Equal(140, row.Billable);
    }

    [Fact]
    public async Task When_the_ledger_row_cannot_be_written_the_terminal_row_is_not_written_either()
    {
        var stores = await StoresAsync();
        var log = stores.Log;

        var delivery = await log.AppendAsync(Instruction("look"), Ct);

        // The next seq the log will hand out is already taken in the ledger, so the ledger insert
        // inside the append fails - and with it the append.
        var next = delivery.Seq + 1;
        await stores.ExecuteAsync(
            $"""
             INSERT INTO usage_ledger (run_seq, correlation, member, run_outcome, ended_at, measured)
             VALUES ({next}, 0, 'Squatter', 'completed', '2026-01-01T00:00:00.0000000+00:00', 0)
             """);

        await Assert.ThrowsAnyAsync<DbException>(() =>
            log.AppendAsync(Terminal(delivery.Seq, new { tokensIn = 1, tokensOut = 1, tokensSource = "claude" }), Ct));

        Assert.Null(await log.FindAsync(next, Ct));
        Assert.DoesNotContain(await log.ReadCorrelationAsync(delivery.CorrelationId, Ct), m => m.Type == MessageTypes.Completed);
    }

    [Fact]
    public async Task A_workflow_gets_a_row_per_completion_and_close_and_the_newest_is_read()
    {
        var ledger = await CreateAsync();
        var stores = await StoresAsync();
        var log = stores.Log;

        var root = await log.AppendAsync(Instruction("look"), Ct);
        await log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", Manager.ToString(), root.Seq), Ct);
        await log.AppendAsync(new NewMessage(MessageTypes.WorkflowClosed, JsonSerializer.Serialize(new { team = "Alpha" }), "person-1", root.Seq), Ct);
        await Task.Delay(20, Ct);
        var again = await log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", Manager.ToString(), root.Seq), Ct);

        Assert.Equal(3, await stores.LongAsync("SELECT COUNT(*) FROM workflow_ledger WHERE correlation = " + root.CorrelationId));

        var newest = await ledger.ReadWorkflowAsync(root.CorrelationId, Ct);
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
        var stores = await StoresAsync();
        var log = stores.Log;

        var delivery = await log.AppendAsync(Instruction("look"), Ct);
        var terminal = await log.AppendAsync(Terminal(delivery.Seq, new { tokensIn = 5, tokensOut = 5, tokensSource = "claude" }), Ct);
        await log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", Manager.ToString(), terminal.Seq), Ct);

        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE usage_ledger SET billable = 0"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("DELETE FROM usage_ledger"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE workflow_ledger SET how_closed = 'closed'"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("DELETE FROM workflow_ledger"));

        // The log's one deletion - Reset's purge, and any retention built on it - takes every row.
        var all = (await log.ReadRangeAsync(0, 100, Ct)).Select(m => m.Seq).ToList();
        Assert.Equal(all.Count, (await log.DeleteAsync(all, Ct)).Purged);

        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM usage_ledger"));
        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM workflow_ledger"));
        Assert.Equal(new WorkflowSpend(10, 1, 0), await log.GetWorkflowSpendAsync(delivery.CorrelationId, Ct));
    }

    [Fact]
    public async Task A_delivery_is_attributed_when_it_is_accepted_and_the_run_reads_that_record()
    {
        var ledger = await CreateAsync();
        var stores = await StoresAsync();
        var log = stores.Log;
        var pending = stores.Pending;

        // A schedule's fire to Dev, and the hand-back Dev's run writes, which wakes the Manager: the
        // ledger's two hops, each resolved when its delivery is accepted.
        var fire = await log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "poll" }), "schedule:s1"), Ct);
        await pending.AddAsync(Dev, fire.Seq, Ct);
        var handback = await log.AppendAsync(new NewMessage(MessageTypes.Handback, "{}", Dev.ToString(), fire.Seq), Ct);
        await pending.AddAsync(Manager, handback.Seq, Ct);
        await pending.AddAsync(Manager, handback.Seq, Ct);

        var fired = fire.OccurredAt.ToString("O", CultureInfo.InvariantCulture);
        Assert.Equal(
            [$"{fire.Seq}|{fired}|schedule:s1|{fired}",
             $"{handback.Seq}|{handback.OccurredAt.ToString("O", CultureInfo.InvariantCulture)}|schedule:s1|{fired}"],
            await DeliveryRecordsAsync(stores));

        // THE RUN READS THE RECORD, not the log: a record that says otherwise than the log wins.
        var tell = await log.AppendAsync(Instruction("look"), Ct);
        await stores.ExecuteAsync(
            $"INSERT INTO delivery_ledger VALUES ({tell.Seq}, {tell.CorrelationId}, '2026-01-01T00:00:00.0000000+00:00', 'trigger:t9', '2026-01-01T00:00:00.0000000+00:00')");
        await pending.AddAsync(Dev, tell.Seq, Ct);
        await log.AppendAsync(Terminal(tell.Seq, new { tokensIn = 5, tokensOut = 5, tokensSource = "claude" }), Ct);

        var run = Assert.Single(await ledger.ReadRecentRunsAsync(Dev, 10, Ct));
        Assert.Equal("trigger:t9", run.TriggerSource);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture), run.QueuedAt);

        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE delivery_ledger SET trigger_source = NULL"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("DELETE FROM delivery_ledger"));
    }

    [Fact]
    public async Task A_nudge_writes_its_row_with_its_log_row_and_nothing_updates_or_deletes_it()
    {
        var stores = await StoresAsync();
        var log = stores.Log;

        var root = await log.AppendAsync(Instruction("look"), Ct);
        var nudge = await log.AppendAsync(Instruction("nudged", root.Seq), Ct);
        await log.AppendAsync(Instruction("not a nudge", nudge.Seq), Ct);

        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM nudge_ledger"));
        Assert.Equal(nudge.Seq, await stores.LongAsync($"SELECT nudge_seq FROM nudge_ledger WHERE correlation = {root.Seq}"));

        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE nudge_ledger SET correlation = 0"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("DELETE FROM nudge_ledger"));
    }

    [Fact]
    public async Task The_runs_overlapping_the_period_and_each_named_or_seen_members_run_either_side_and_nothing_else()
    {
        var ledger = await CreateAsync();
        var stores = await StoresAsync();

        async Task RunAsync(long seq, string team, string member, DateTimeOffset queued, DateTimeOffset ended) =>
            await stores.ExecuteAsync(
                $"""
                 INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, run_outcome,
                     queued_at, started_at, ended_at, measured, tokens_in, tokens_out, billable)
                 VALUES ({seq}, {seq}, '{team}', '{team}', '{member}', 'completed',
                     '{Instant.Stamp(queued)}', '{Instant.Stamp(queued)}', '{Instant.Stamp(ended)}', 1, 1, 1, 1)
                 """);

        await RunAsync(1, "alpha", "Dev", T(45), T(50));    // at the floor: not read
        await RunAsync(2, "alpha", "Dev", T(10), T(12));    // the run before
        await RunAsync(3, "alpha", "Dev", T(45), T(50));    // inside
        await RunAsync(4, "alpha", "Dev", T(70), T(75));    // later than the run after: not read
        await RunAsync(5, "alpha", "Dev", T(90), T(95));    // later still: not read
        await RunAsync(6, "alpha", "Ops", T(10), T(20));    // Ops is named and has no run inside: its run before is 7
        await RunAsync(7, "alpha", "Ops", T(20), T(30));
        await RunAsync(8, "alpha", "Gone", T(10), T(20));   // neither named nor seen: not read
        await RunAsync(9, "alpha", "Dev", T(65), T(66));    // began after the period: not inside, but Dev's run after
        await RunAsync(10, "beta", "Dev", T(45), T(50));    // another team
        await RunAsync(11, "alpha", "Dev", T(5), T(6));     // older than the run before: not read

        var rows = await ledger.ReadTeamRunsAsync("alpha", floor: 1, T(40), T(60), ["Dev", "ops"], Ct);

        Assert.Equal([2, 3, 7, 9], rows.Select(r => r.RunSeq));
    }

    [Fact]
    public async Task The_teams_tokens_tile_reads_the_same_figures_from_the_ledger_as_from_the_log()
    {
        var ledger = await CreateAsync();
        var log = (await StoresAsync()).Log;

        var root = await log.AppendAsync(Instruction(Dev, "build it", "console"), Ct);
        var first = await RunAsync(log, Dev, root.Seq, new { tokensIn = 1000, tokensOut = 200, tokensCachedIn = 5000, tokensCacheCreation = 400, tokensSource = "claude" });
        var failedDelivery = await log.AppendAsync(Instruction(Dev, "again", "console", root.Seq), Ct);
        await RunAsync(log, Dev, failedDelivery.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null }, MessageTypes.Failed);
        var codexDelivery = await log.AppendAsync(Instruction(Dev, "codex", "console", root.Seq), Ct);
        var batchedDelivery = await log.AppendAsync(Instruction(Dev, "batched", "console", root.Seq), Ct);
        await RunAsync(log, Dev, codexDelivery.Seq, new { tokensTotal = 777, tokensSource = "codex" });
        await log.AppendAsync(new NewMessage(MessageTypes.Completed,
            JsonSerializer.Serialize(new { tokensIn = (int?)null, tokensOut = (int?)null, usageCountedOn = codexDelivery.Seq }),
            Dev.ToString(), batchedDelivery.Seq), Ct);
        var estimateDelivery = await log.AppendAsync(Instruction(Dev, "estimate", "console", root.Seq), Ct);
        await RunAsync(log, Dev, estimateDelivery.Seq, new { tokensIn = 90, tokensOut = 10, tokensSource = UsageSource.ExcludedEstimate });
        var echoDelivery = await log.AppendAsync(Instruction(Echo, "echo", "console", root.Seq), Ct);
        await RunAsync(log, Echo, echoDelivery.Seq, new { tokensIn = (int?)null, tokensOut = (int?)null, tokensSource = UsageSource.NoModel });
        var managerDelivery = await log.AppendAsync(Instruction(Manager, "carry on", "person-1", root.Seq), Ct);
        await RunAsync(log, Manager, managerDelivery.Seq, new { tokensIn = 300, tokensOut = 100, tokensCachedIn = 999, tokensSource = "claude" });

        foreach (var floor in new[] { 0L, first.Seq })
        {
            var fromLog = (await log.SumUsageForTeamAsync("Alpha", floor, Ct)).OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase).ToList();
            var fromLedger = (await ledger.SumForTeamAsync("Alpha", floor, Ct)).OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase).ToList();

            Assert.Equal(fromLog, fromLedger);
        }

        var dev = Assert.Single(await ledger.SumForTeamAsync("Alpha", 0, Ct), r => r.Source == "Alpha/Dev");
        Assert.Equal((1000L, 200L, 1, 3, 777L), (dev.TokensIn, dev.TokensOut, dev.RunsWithUsage, dev.RunsWithoutUsage, dev.TokensTotal));
    }

    /// <summary>The export: runs and closes share one seq cursor, newest first, and a full page names
    /// the next.</summary>
    [Fact]
    public async Task A_page_is_the_newest_runs_and_closes_together_and_names_where_the_next_begins()
    {
        var ledger = await CreateAsync();
        var log = (await StoresAsync()).Log;

        var root = await log.AppendAsync(Instruction("look"), Ct);
        var run1 = await log.AppendAsync(Terminal(root.Seq, new { tokensIn = 1, tokensOut = 1, tokensSource = "claude" }), Ct);
        var close = await log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", Manager.ToString(), run1.Seq), Ct);
        var again = await log.AppendAsync(Instruction("again", root.Seq), Ct);
        var run2 = await log.AppendAsync(Terminal(again.Seq, new { tokensIn = 2, tokensOut = 2, tokensSource = "claude" }), Ct);

        var first = await ledger.ReadPageAsync(DateTimeOffset.MinValue, null, 2, Ct);
        Assert.Equal([run2.Seq], first.Runs.Select(r => r.RunSeq));
        Assert.Equal([close.Seq], first.Workflows.Select(w => w.CloseSeq));
        Assert.Equal(close.Seq, first.NextBefore);

        var second = await ledger.ReadPageAsync(DateTimeOffset.MinValue, first.NextBefore, 2, Ct);
        Assert.Equal([run1.Seq], second.Runs.Select(r => r.RunSeq));
        Assert.Empty(second.Workflows);
        Assert.Null(second.NextBefore);

        var none = await ledger.ReadPageAsync(DateTimeOffset.UtcNow.AddDays(1), null, 2, Ct);
        Assert.Empty(none.Runs);
        Assert.Empty(none.Workflows);
        Assert.Null(none.NextBefore);
    }

    [Fact]
    public async Task A_teams_runs_ended_in_a_period_are_read_in_order_of_their_end_above_its_floor()
    {
        var ledger = await CreateAsync();
        var stores = await StoresAsync();

        foreach (var (seq, team, ended) in new[] { (1L, "alpha", 30), (2L, "alpha", 10), (3L, "ALPHA", 20), (4L, "beta", 15), (5L, "alpha", 50) })
        {
            await stores.ExecuteAsync(
                $"""
                 INSERT INTO usage_ledger (run_seq, correlation, team_id, member, run_outcome, ended_at, measured)
                 VALUES ({seq}, {seq}, '{team}', 'Dev', 'completed', '{Instant.Stamp(T(ended))}', 0)
                 """);
        }

        Assert.Equal([2, 3, 5], (await ledger.ReadTeamRunsEndedAsync("Alpha", 1, null, null, Ct)).Select(r => r.RunSeq));
        Assert.Equal([2, 3], (await ledger.ReadTeamRunsEndedAsync("alpha", 1, T(0), T(30), Ct)).Select(r => r.RunSeq));
    }

    /// <summary>
    /// REVIEW FOCUS 2, TWO WRITERS AT ONCE: eight subscribers accept one delivery at the same moment.
    /// Each writes its own pending row; the delivery's record is decided once - SQLite has one writer,
    /// SQL Server holds the key under UPDLOCK, HOLDLOCK - so no accept fails on its key and one record
    /// stands. Accepting a delivery takes no append lock, so nothing else serialises them.
    /// </summary>
    [Fact]
    public async Task Concurrent_offers_of_one_delivery_record_it_once()
    {
        var stores = await StoresAsync();
        var fire = await stores.Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "poll" }), "schedule:s1"), Ct);

        for (var round = 0; round < 5; round++)
        {
            var seq = round == 0
                ? fire.Seq
                : (await stores.Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Dev), "{}", "schedule:s1", fire.Seq), Ct)).Seq;

            using var start = new ManualResetEventSlim();
            var offers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                start.Wait(Ct);
                await stores.Pending.AddAsync(new ContainerId("Alpha", $"Member{i}"), seq, Ct);
            }, Ct)).ToList();

            start.Set();
            await Task.WhenAll(offers);

            Assert.Equal(1, await stores.LongAsync($"SELECT COUNT(*) FROM delivery_ledger WHERE delivery_seq = {seq}"));
            Assert.Equal(8, await stores.LongAsync($"SELECT COUNT(*) FROM pending_deliveries WHERE seq = {seq}"));
        }
    }

    private static DateTimeOffset T(int minutes) => new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static async Task<List<string>> DeliveryRecordsAsync(ContractStores stores) =>
        [.. (await stores.RowsAsync(
                "SELECT delivery_seq, queued_at, trigger_source, trigger_fired_at FROM delivery_ledger ORDER BY delivery_seq"))
            .Select(r => $"{r[0]}|{r[1]}|{r[2]}|{r[3]}")];

    private static NewMessage Instruction(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }), "console", causation);

    private static NewMessage Instruction(ContainerId to, string text, string source, long? causation = null) =>
        new(MessageTypes.InstructionFor(to), JsonSerializer.Serialize(new { instruction = text }), source, causation);

    private static NewMessage Terminal(long causation, object payload) =>
        new(MessageTypes.Completed, JsonSerializer.Serialize(payload), Dev.ToString(), causation);

    private static async Task<Message> RunAsync(
        IMessageLog log, ContainerId member, long delivery, object usage, string type = MessageTypes.Completed)
    {
        await log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", member.ToString(), delivery), Ct);
        return await log.AppendAsync(new NewMessage(type, JsonSerializer.Serialize(usage), member.ToString(), delivery), Ct);
    }
}

public sealed class SqliteUsageLedgerTests : UsageLedgerContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerUsageLedgerTests : UsageLedgerContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
