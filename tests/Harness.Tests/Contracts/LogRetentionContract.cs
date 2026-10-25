using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ILogRetention"/>, which rows the daily pass may hand to the log's one delete, as each
/// provider must keep it - run through the real pass (<see cref="LogRetention"/>) over the provider's
/// own log, with the tenant row it writes caught by a stand-in. The tests are <c>LogRetentionTests</c>'
/// with the same names and values, but for the setting's own rules (a tenant settings store) and the
/// agent steps (a store of another task); the pass's own tenant row is read from the stand-in.
/// </summary>
public abstract class LogRetentionContract : ProviderContract
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly ContainerId Dev = new("Alpha", "Dev");
    private static readonly ContainerId Manager = new("Alpha", "Manager");

    private readonly Tenant _tenant = new();

    protected async Task<ILogRetention> CreateAsync() => (await StoresAsync()).Retention;

    [Fact]
    public async Task A_workflow_completed_100_days_ago_goes_and_one_completed_10_days_ago_an_open_one_rooted_200_days_ago_and_a_row_a_pending_delivery_names_stay()
    {
        var log = (await StoresAsync()).Log;
        var old = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        var recent = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 10);
        var open = await WorkflowAsync(rootDaysAgo: 200, declaredDaysAgo: null);
        var queued = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await QueueAsync(queued.Progress);

        var pass = await (await RetentionAsync()).RunAsync(Now, Ct);

        foreach (var seq in old.All) Assert.Null(await log.FindAsync(seq, Ct));
        foreach (var seq in recent.All.Concat(open.All)) Assert.NotNull(await log.FindAsync(seq, Ct));
        Assert.NotNull(await log.FindAsync(queued.Progress, Ct));

        Assert.Equal(90, pass.Days);
        Assert.Equal(Now.AddDays(-90), pass.Cutoff);
        Assert.True(pass.Purged >= old.All.Count);
    }

    [Fact]
    public async Task A_workflow_a_live_backlog_dispatch_names_keeps_every_row_its_stats_are_read_from()
    {
        var stores = await StoresAsync();
        var dispatched = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await stores.ExecuteAsync(
            $"""
             INSERT INTO backlog_dispatches (item, team_id, team_name, correlation, dispatched_at, dispatched_by)
             VALUES ('item-one', 'Alpha', 'Alpha', {dispatched.Root}, '{Stamp(Now.AddDays(-110))}', 'person@example.test')
             """);

        var pass = await (await RetentionAsync()).RunAsync(Now, Ct);

        Assert.False(pass.Removed);
        foreach (var seq in dispatched.All) Assert.NotNull(await stores.Log.FindAsync(seq, Ct));
    }

    [Fact]
    public async Task A_row_a_surviving_row_cites_is_retained_and_the_pass_reports_it_retained_not_purged()
    {
        var stores = await StoresAsync();
        var log = stores.Log;

        // An outcome link keeps the workflow's root and declaration, which cites the Manager's run;
        // that run's rows are chosen, and retained because the declaration cites them.
        var linked = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await stores.ExecuteAsync(
            $"""
             INSERT INTO workflow_outcome_links (correlation, outcome_id, set_by, set_by_kind, set_at, how)
             VALUES ({linked.Root}, NULL, 'person@example.test', 'person', '{Stamp(Now.AddDays(-100))}', 'person')
             """);

        var pass = await (await RetentionAsync()).RunAsync(Now, Ct);

        // The progress row nothing cites goes; the chain the declaration cites stays.
        Assert.Null(await log.FindAsync(linked.Progress, Ct));
        Assert.NotNull(await log.FindAsync(linked.ManagerRun, Ct));
        Assert.NotNull(await log.FindAsync(linked.DevRun, Ct));
        Assert.NotNull(await log.FindAsync(linked.Root, Ct));
        Assert.NotNull(await log.FindAsync(linked.Declared!.Value, Ct));

        Assert.Equal(1, pass.Purged);
        Assert.Equal(2, pass.Retained);

        var row = Assert.Single(_tenant.Pruned);
        Assert.Equal(1, row.GetProperty("purged").GetInt32());
        Assert.Equal(2, row.GetProperty("retained").GetInt32());

        // Still closed: the outcome figures' open read does not see it as open again.
        Assert.Empty(await log.OpenWorkflowsAmongAsync([linked.Root], Ct));
    }

    [Fact]
    public async Task The_file_changes_older_than_the_oldest_kept_row_go_in_the_same_pass_and_newer_ones_stay()
    {
        var stores = await StoresAsync();
        await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await WorkflowAsync(rootDaysAgo: 20, declaredDaysAgo: 20);

        async Task<long> FileChangeAsync(DateTimeOffset at)
        {
            await stores.ExecuteAsync(
                $"""
                 INSERT INTO file_changes (team, member, repo, at, correlation, files_json, commits_json, more)
                 VALUES ('Alpha', 'Dev', 'Repo', '{MessageSchema.FileChangeInstant(at)}', NULL, '[]', '[]', 0)
                 """);
            return await stores.LongAsync("SELECT MAX(id) FROM file_changes");
        }

        var older = await FileChangeAsync(Now.AddDays(-105));
        var newer = await FileChangeAsync(Now.AddDays(-5));

        var pass = await (await RetentionAsync()).RunAsync(Now, Ct);

        var left = (await stores.RowsAsync("SELECT id FROM file_changes ORDER BY id")).Select(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal([newer], left);
        Assert.DoesNotContain(older, left);
        Assert.Equal(1, pass.FileChanges);
        Assert.Equal(1, Assert.Single(_tenant.Pruned).GetProperty("fileChanges").GetInt32());
    }

    [Fact]
    public async Task No_ledger_row_tenant_row_admission_hold_stored_landed_or_backlog_stat_changes()
    {
        var stores = await StoresAsync();
        var log = stores.Log;
        var landed = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        var plain = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        var frozen = JsonSerializer.Serialize(new { correlation = landed.Root, outcome = "completed" });
        const string empty = "{}";

        await stores.ExecuteAsync(
            $"""
             INSERT INTO backlog_items (id, team, title, body, state, position, archived_at, created_at, updated_at, created_by)
             VALUES ('item-one', 'Alpha', 'Item', 'Body', 'implemented', 1, '{Stamp(Now.AddDays(-95))}', '{Stamp(Now.AddDays(-110))}', '{Stamp(Now.AddDays(-95))}', 'person@example.test');
             INSERT INTO backlog_dispatches (item, team_id, team_name, correlation, dispatched_at, dispatched_by, frozen_at, frozen_stats, landed_at, landed_sha, landed_branch)
             VALUES ('item-one', 'Alpha', 'Alpha', {landed.Root}, '{Stamp(Now.AddDays(-110))}', 'person@example.test', '{Stamp(Now.AddDays(-95))}', '{frozen}', '{Stamp(Now.AddDays(-99))}', 'abc123', 'main');
             INSERT INTO admission_holds (team_id, team_name, member, delivery_seq, held_at, released_at, reason_kind, reason)
             VALUES ('Alpha', 'Alpha', 'Dev', {plain.Root}, '{Stamp(Now.AddDays(-110))}', '{Stamp(Now.AddDays(-109))}', 'memory', 'held');
             INSERT INTO nudge_ledger (nudge_seq, correlation, nudged_at) VALUES ({plain.Progress}, {plain.Root}, '{Stamp(Now.AddDays(-105))}');
             INSERT INTO delivery_ledger (delivery_seq, correlation, queued_at) VALUES ({plain.Root}, {plain.Root}, '{Stamp(Now.AddDays(-110))}');
             INSERT INTO tenant_events (occurred_at, actor_email, action, subject, detail)
             VALUES ('{Stamp(Now.AddDays(-100))}', 'person@example.test', '{TenantActions.TenantSettingChanged}', 'x', '{empty}');
             """);

        var before = await KeptTablesAsync(stores);
        Assert.NotEmpty(before["usage_ledger"]);
        Assert.NotEmpty(before["workflow_ledger"]);

        var pass = await (await RetentionAsync()).RunAsync(Now, Ct);
        Assert.True(pass.Purged > 0);

        Assert.Equal(before, await KeptTablesAsync(stores));

        // The tenant log grows only by the pass's own row.
        Assert.Single(_tenant.Pruned);

        // The dispatch's workflow keeps its root and declaration; the plain one goes whole.
        Assert.NotNull(await log.FindAsync(landed.Root, Ct));
        Assert.NotNull(await log.FindAsync(landed.Declared!.Value, Ct));
        foreach (var seq in plain.All) Assert.Null(await log.FindAsync(seq, Ct));
    }

    [Fact]
    public async Task A_pass_writes_one_log_pruned_row_with_its_counts_a_pass_removing_nothing_writes_none_and_a_pass_larger_than_the_batch_finishes_later()
    {
        var stores = await StoresAsync();
        var workflows = new List<Workflow>();
        for (var i = 0; i < 4; i++) workflows.Add(await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100));
        var total = workflows.Sum(w => w.All.Count);

        var retention = await RetentionAsync(batch: 4);
        var passes = new List<LogRetentionPass>();
        for (var i = 0; i < 30 && await stores.LongAsync("SELECT COUNT(*) FROM messages") > 0; i++)
        {
            passes.Add(await retention.RunAsync(Now, Ct));
        }

        // Every row went, no pass chose more than the batch, and it took more than one pass.
        foreach (var seq in workflows.SelectMany(w => w.All)) Assert.Null(await stores.Log.FindAsync(seq, Ct));
        Assert.All(passes, p => Assert.True(p.Chosen <= 4, $"a pass chose {p.Chosen}"));
        Assert.True(passes.Count > 1);
        Assert.Equal(total, passes.Sum(p => p.Purged));

        // One row per pass that removed something, carrying that pass's counts.
        var rows = _tenant.Pruned;
        var removing = passes.Where(p => p.Removed).ToList();
        Assert.Equal(removing.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal("log.retentionDays", rows[i].GetProperty("setting").GetString());
            Assert.Equal(90, rows[i].GetProperty("days").GetInt32());
            Assert.Equal(removing[i].Purged, rows[i].GetProperty("purged").GetInt32());
            Assert.Equal(removing[i].Retained, rows[i].GetProperty("retained").GetInt32());
            Assert.Equal(removing[i].FileChanges, rows[i].GetProperty("fileChanges").GetInt32());
        }

        // Nothing left to remove: the next pass writes no row.
        var idle = await retention.RunAsync(Now, Ct);
        Assert.False(idle.Removed);
        Assert.Equal(rows.Count, _tenant.Pruned.Count);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private sealed record Workflow(long Root, long DevRun, long Progress, long ManagerRun, long? Declared)
    {
        public IReadOnlyList<long> All => Declared is { } d ? [Root, DevRun, Progress, ManagerRun, d] : [Root, DevRun, Progress, ManagerRun];
    }

    private async Task<LogRetention> RetentionAsync(int batch = LogRetention.DefaultBatch) =>
        new(await CreateAsync(), (await StoresAsync()).Log, _tenant, () => LogRetention.DefaultDays,
            NullLogger<LogRetention>.Instance, batch);

    /// <summary>
    /// A workflow: a person's instruction to Dev, Dev's run and a progress row, the Manager's run,
    /// and, unless null, the Manager's declaration. Rooted <paramref name="rootDaysAgo"/> days ago;
    /// the declaration is dated <paramref name="declaredDaysAgo"/> days ago.
    /// </summary>
    private async Task<Workflow> WorkflowAsync(int rootDaysAgo, int? declaredDaysAgo)
    {
        var log = (await StoresAsync()).Log;
        var root = await log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "look" }), "console"), Ct);
        var dev = await log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", Dev.ToString(), root.Seq), Ct);
        var progress = await log.AppendAsync(new NewMessage(MessageTypes.Progress, "{}", Dev.ToString(), root.Seq), Ct);
        var manager = await log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", Manager.ToString(), dev.Seq), Ct);

        await AgeAsync([root.Seq, dev.Seq, progress.Seq, manager.Seq], Now.AddDays(-rootDaysAgo));

        long? declared = null;
        if (declaredDaysAgo is { } days)
        {
            declared = (await log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", Manager.ToString(), manager.Seq), Ct)).Seq;
            await AgeAsync([declared.Value], Now.AddDays(-days));
        }

        return new Workflow(root.Seq, dev.Seq, progress.Seq, manager.Seq, declared);
    }

    private async Task AgeAsync(IEnumerable<long> seqs, DateTimeOffset at) =>
        await (await StoresAsync()).ExecuteAsync($"UPDATE messages SET occurred_at = '{Stamp(at)}' WHERE seq IN ({string.Join(',', seqs)})");

    private async Task QueueAsync(long seq) =>
        await (await StoresAsync()).ExecuteAsync($"INSERT INTO pending_deliveries (subscriber, seq) VALUES ('{Dev}', {seq})");

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Every row of every table a pass must never change, as JSON text, by table.</summary>
    private static async Task<Dictionary<string, List<string>>> KeptTablesAsync(ContractStores stores)
    {
        var tables = new Dictionary<string, List<string>>();
        foreach (var (table, order) in new[]
                 {
                     ("usage_ledger", "run_seq"), ("workflow_ledger", "close_seq"), ("delivery_ledger", "delivery_seq"),
                     ("nudge_ledger", "nudge_seq"), ("admission_holds", "id"), ("backlog_items", "id"),
                     ("backlog_dispatches", "id"), ("workflow_outcome_links", "id"), ("tenant_events", "seq"),
                 })
        {
            tables[table] = [.. (await stores.RowsAsync($"SELECT * FROM {table} ORDER BY {order}")).Select(r => JsonSerializer.Serialize(r))];
        }

        return tables;
    }

    /// <summary>The tenant log the pass writes its <c>log.pruned</c> row to; it reads nothing back.</summary>
    private sealed class Tenant : ITenantLog
    {
        private readonly ConcurrentQueue<(string Action, string? Subject, string? ActorId, string? Detail)> _rows = new();

        public List<JsonElement> Pruned =>
            [.. _rows.Where(r => r.Action == TenantActions.LogPruned).Select(r =>
            {
                Assert.Equal("log.retentionDays", r.Subject);
                Assert.Null(r.ActorId);
                return JsonDocument.Parse(r.Detail!).RootElement.Clone();
            })];

        public Task WriteAsync(
            string? actorId, string? actorEmail, string action, string? subject = null, string? subjectName = null,
            string? detail = null, CancellationToken ct = default)
        {
            _rows.Enqueue((action, subject, actorId, detail));
            return Task.CompletedTask;
        }

        public Task<TenantEvent?> FindLatestAsync(string action, string subject, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(IReadOnlyCollection<string> actions, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TenantLogPage> ReadAsync(long? before = null, int take = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

public sealed class SqliteLogRetentionTests : LogRetentionContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerLogRetentionTests : LogRetentionContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
