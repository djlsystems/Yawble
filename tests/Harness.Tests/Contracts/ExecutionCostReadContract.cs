using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IOutcomeStore.ReadExecutionRunsAsync"/>, as each provider must answer it: the ledger LEFT
/// JOIN its execution snapshots on <c>start_seq</c>, with the outcome each snapshot's <c>link_id</c>
/// names, WINDOWED IN THE QUERY by overlap - a run that started before the window and ended in it is
/// there, a run wholly before or after it is not - and, asked for priced runs only, just the runs
/// whose snapshot carries a rate. The rows are written as the writer and the link store leave them.
/// </summary>
public abstract class ExecutionCostReadContract : ProviderContract
{
    private static readonly DateTimeOffset From = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_read_is_windowed_by_overlap_in_the_query()
    {
        var stores = await StoresAsync();

        await RunAsync(stores, 1, From.AddHours(-1), From.AddHours(1));        // crosses the start: in
        await RunAsync(stores, 2, To.AddHours(-1), To.AddHours(1));            // crosses the end: in
        await RunAsync(stores, 3, From.AddHours(2), From.AddHours(3));         // inside: in
        await RunAsync(stores, 4, From.AddDays(-3), From.AddDays(-2));         // wholly before: out
        await RunAsync(stores, 5, To.AddHours(1), To.AddHours(2));             // wholly after: out
        await RunAsync(stores, 6, null, From.AddHours(5));                     // never started, ended in it: in
        await RunAsync(stores, 7, null, To.AddHours(5));                       // never started, ended after: out
        await RunAsync(stores, 8, From.AddDays(-1), To.AddDays(1));            // spans it: in

        var read = await stores.Outcomes.ReadExecutionRunsAsync(From, To, pricedOnly: false, Ct);
        Assert.Equal([1L, 2, 3, 6, 8], read.Runs.Select(r => r.Run.RunSeq));

        var all = await stores.Outcomes.ReadExecutionRunsAsync(null, null, pricedOnly: false, Ct);
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7, 8], all.Runs.Select(r => r.Run.RunSeq));
    }

    [Fact]
    public async Task Each_run_carries_its_snapshot_and_the_outcome_its_link_named_and_a_run_from_before_snapshots_none()
    {
        var stores = await StoresAsync();
        var linked = await LinkAsync(stores, 70, "'outcome-a'");
        var unlink = await LinkAsync(stores, 71, "NULL");
        // The workflow moves on after the run started: the snapshot still names the first link.
        await LinkAsync(stores, 70, "'outcome-b'");

        await RunAsync(stores, 10, From.AddHours(1), From.AddHours(2), correlation: 70,
            snapshot: ("agent", "62.5", "EUR", "member", linked));
        await RunAsync(stores, 11, From.AddHours(1), From.AddHours(2), correlation: 71,
            snapshot: ("plugin", null, null, "none", unlink));
        await RunAsync(stores, 12, From.AddHours(1), From.AddHours(2), correlation: 72,
            snapshot: ("agent", "60", "USD", "tenant", null));
        await RunAsync(stores, 13, From.AddHours(1), From.AddHours(2), correlation: 70, kind: "plugin");

        var read = await stores.Outcomes.ReadExecutionRunsAsync(From, To, pricedOnly: false, Ct);
        var byRun = read.Runs.ToDictionary(r => r.Run.RunSeq);

        Assert.Equal(new ExecutionRunSnapshot("agent", "62.5", "EUR", "member", linked, true, "outcome-a"), byRun[10].Snapshot);
        Assert.Equal(new ExecutionRunSnapshot("plugin", null, null, "none", unlink, true, null), byRun[11].Snapshot);
        Assert.Equal(new ExecutionRunSnapshot("agent", "60", "USD", "tenant", null, false, null), byRun[12].Snapshot);
        Assert.Equal(1010L, byRun[10].StartSeq);

        // Before snapshots: no start, no snapshot, its own ledger kind.
        Assert.Null(byRun[13].Snapshot);
        Assert.Null(byRun[13].StartSeq);
        Assert.Equal("plugin", byRun[13].Run.MemberKind);

        // And every workflow's newest link, for the runs that have no link of their own.
        Assert.Equal("outcome-b", read.CurrentLinks.Single(l => l.Correlation == 70).OutcomeId);
    }

    [Fact]
    public async Task Priced_only_answers_just_the_runs_whose_snapshot_carries_a_rate()
    {
        var stores = await StoresAsync();
        await RunAsync(stores, 20, From.AddHours(1), From.AddHours(2), snapshot: ("agent", "60", "USD", "tenant", null));
        await RunAsync(stores, 21, From.AddHours(1), From.AddHours(2), snapshot: ("agent", null, null, "none", null));
        await RunAsync(stores, 22, From.AddHours(1), From.AddHours(2));
        await RunAsync(stores, 23, From.AddDays(-30), From.AddDays(-30).AddHours(1), snapshot: ("plugin", "15", "USD", "member", null));

        Assert.Equal([20L, 23], (await stores.Outcomes.ReadExecutionRunsAsync(null, null, pricedOnly: true, Ct)).Runs.Select(r => r.Run.RunSeq));
        Assert.Equal([20L], (await stores.Outcomes.ReadExecutionRunsAsync(From, To, pricedOnly: true, Ct)).Runs.Select(r => r.Run.RunSeq));
    }

    // ---- fixtures ----

    private static string Sql(string? value) => value is null ? "NULL" : $"'{value}'";

    private static string Sql(DateTimeOffset? at) => at is { } value ? $"'{Instant.Stamp(value)}'" : "NULL";

    /// <summary>A ledger row, and - with <paramref name="snapshot"/> - the snapshot its start wrote, at
    /// start seq <c>run + 1000</c>.</summary>
    private static async Task RunAsync(
        ContractStores stores, long run, DateTimeOffset? started, DateTimeOffset ended, long correlation = 1,
        string kind = "agent", (string Kind, string? Rate, string? Currency, string Source, long? Link)? snapshot = null)
    {
        var start = snapshot is null ? "NULL" : (run + 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (snapshot is { } s)
        {
            await stores.ExecuteAsync(
                "INSERT INTO execution_snapshots (start_seq, correlation, team_id, team_name, member, member_kind, "
                + "rate_amount, rate_currency, rate_source, link_id, started_at) "
                + $"VALUES ({start}, {correlation}, 'alpha', 'Alpha', 'Dev', {Sql(s.Kind)}, {Sql(s.Rate)}, {Sql(s.Currency)}, "
                + $"{Sql(s.Source)}, {(s.Link is { } link ? link.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")}, "
                + $"{Sql(started ?? ended)})");
        }

        await stores.ExecuteAsync(
            "INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome, "
            + "started_at, ended_at, measured, billable, backfilled, start_seq) "
            + $"VALUES ({run}, {correlation}, 'alpha', 'Alpha', 'Dev', {Sql(kind)}, 'completed', {Sql(started)}, {Sql(ended)}, 1, 10, 0, {start})");
    }

    /// <summary>A link row, as the link store appends one; <paramref name="outcome"/> is SQL (NULL is an unlink). Answers its id.</summary>
    private static async Task<long> LinkAsync(ContractStores stores, long correlation, string outcome)
    {
        await stores.ExecuteAsync(
            "INSERT INTO workflow_outcome_links (correlation, outcome_id, set_by, set_by_kind, set_at, how) "
            + $"VALUES ({correlation}, {outcome}, 'person@example.test', 'person', '2026-10-01T00:00:00.0000000+00:00', 'person')");
        return await stores.LongAsync($"SELECT MAX(id) FROM workflow_outcome_links WHERE correlation = {correlation}");
    }
}

public sealed class SqliteExecutionCostReadTests : ExecutionCostReadContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerExecutionCostReadTests : ExecutionCostReadContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
