using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests;

/// <summary>
/// THE MESSAGE LOG KEEPS <c>log.retentionDays</c> OF FINISHED WORK. A pass removes, through the log's
/// one delete, the rows of workflows finished longer ago than the setting, and keeps every row of an
/// open workflow, a recently finished one and a row still queued for a run; what a kept row cites is
/// retained and said to be; the recorded file changes before the oldest kept row go with them; and
/// no ledger, tenant row, hold, landed or backlog stat moves.
/// </summary>
public sealed class LogRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-log-retention-{Guid.NewGuid():N}");
    private readonly string _database;
    private readonly SqliteMessageStore _log;
    private readonly SqliteTenantLog _tenant;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ContainerId Dev = new("Alpha", "Dev");
    private static readonly ContainerId Manager = new("Alpha", "Manager");

    public LogRetentionTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(_database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _log = new SqliteMessageStore(_database);
        _tenant = new SqliteTenantLog(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_workflow_completed_100_days_ago_goes_and_one_completed_10_days_ago_an_open_one_rooted_200_days_ago_and_a_row_a_pending_delivery_names_stay()
    {
        var old = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        var recent = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 10);
        var open = await WorkflowAsync(rootDaysAgo: 200, declaredDaysAgo: null);
        var queued = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await QueueAsync(queued.Progress);

        var pass = await Retention().RunAsync(Now, Ct);

        foreach (var seq in old.All) Assert.Null(await _log.FindAsync(seq, Ct));
        foreach (var seq in recent.All.Concat(open.All)) Assert.NotNull(await _log.FindAsync(seq, Ct));
        Assert.NotNull(await _log.FindAsync(queued.Progress, Ct));

        Assert.Equal(90, pass.Days);
        Assert.Equal(Now.AddDays(-90), pass.Cutoff);
        Assert.True(pass.Purged >= old.All.Count);
    }

    [Fact]
    public async Task A_workflow_a_live_backlog_dispatch_names_keeps_every_row_its_stats_are_read_from()
    {
        var dispatched = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await ExecuteAsync(
            $"""
             INSERT INTO backlog_dispatches (item, team_id, team_name, correlation, dispatched_at, dispatched_by)
             VALUES ('item-one', 'Alpha', 'Alpha', {dispatched.Root}, '{Stamp(Now.AddDays(-110))}', 'person@example.test')
             """);

        var pass = await Retention().RunAsync(Now, Ct);

        Assert.False(pass.Removed);
        foreach (var seq in dispatched.All) Assert.NotNull(await _log.FindAsync(seq, Ct));
    }

    [Fact]
    public async Task A_row_a_surviving_row_cites_is_retained_and_the_pass_reports_it_retained_not_purged()
    {
        // An outcome link keeps the workflow's root and declaration, which cites the Manager's run;
        // that run's rows are chosen, and retained because the declaration cites them.
        var linked = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await ExecuteAsync(
            $"""
             INSERT INTO workflow_outcome_links (correlation, outcome_id, set_by, set_by_kind, set_at, how)
             VALUES ({linked.Root}, NULL, 'person@example.test', 'person', '{Stamp(Now.AddDays(-100))}', 'person')
             """);

        var pass = await Retention().RunAsync(Now, Ct);

        // The progress row nothing cites goes; the chain the declaration cites stays.
        Assert.Null(await _log.FindAsync(linked.Progress, Ct));
        Assert.NotNull(await _log.FindAsync(linked.ManagerRun, Ct));
        Assert.NotNull(await _log.FindAsync(linked.DevRun, Ct));
        Assert.NotNull(await _log.FindAsync(linked.Root, Ct));
        Assert.NotNull(await _log.FindAsync(linked.Declared!.Value, Ct));

        Assert.Equal(1, pass.Purged);
        Assert.Equal(2, pass.Retained);

        var row = Assert.Single(await PrunedRowsAsync());
        Assert.Equal(1, row.GetProperty("purged").GetInt32());
        Assert.Equal(2, row.GetProperty("retained").GetInt32());

        // Still closed: the outcome figures' open read does not see it as open again.
        Assert.Empty(await _log.OpenWorkflowsAmongAsync([linked.Root], Ct));
    }

    [Fact]
    public async Task The_file_changes_older_than_the_oldest_kept_row_go_in_the_same_pass_and_newer_ones_stay()
    {
        await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await WorkflowAsync(rootDaysAgo: 20, declaredDaysAgo: 20);

        var older = await FileChangeAsync(Now.AddDays(-105));
        var newer = await FileChangeAsync(Now.AddDays(-5));

        var pass = await Retention().RunAsync(Now, Ct);

        Assert.Equal([newer], await FileChangeIdsAsync());
        Assert.DoesNotContain(older, await FileChangeIdsAsync());
        Assert.Equal(1, pass.FileChanges);
        Assert.Equal(1, Assert.Single(await PrunedRowsAsync()).GetProperty("fileChanges").GetInt32());
    }

    [Fact]
    public async Task The_agent_steps_older_than_the_oldest_kept_row_go_in_the_same_pass_and_newer_ones_stay()
    {
        await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        await WorkflowAsync(rootDaysAgo: 20, declaredDaysAgo: 20);

        var steps = new AgentStepStore(_database);
        var older = new NewAgentStep(Now.AddDays(-105), LiveLineKinds.Tool, "Bash", null);
        var newer = new NewAgentStep(Now.AddDays(-5), LiveLineKinds.Text, "", null);
        await steps.WriteAsync("Alpha", "Dev", 1, null, [older, newer], [], Ct);

        var pass = await Retention().RunAsync(Now, Ct);

        var left = await steps.ReadAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10,
            new Dictionary<string, DateTimeOffset> { ["Alpha"] = DateTimeOffset.MinValue }, Ct);
        Assert.Equal([newer.Id], left.Select(s => s.Id));
        Assert.Equal(1, pass.RunSteps);
        Assert.Equal(1, Assert.Single(await PrunedRowsAsync()).GetProperty("runSteps").GetInt32());
    }

    [Fact]
    public async Task No_ledger_row_tenant_row_admission_hold_stored_landed_or_backlog_stat_changes()
    {
        var landed = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        var plain = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        var frozen = JsonSerializer.Serialize(new { correlation = landed.Root, outcome = "completed" });

        await ExecuteAsync(
            $"""
             INSERT INTO backlog_items (id, team, title, body, state, position, archived_at, created_at, updated_at, created_by)
             VALUES ('item-one', 'Alpha', 'Item', 'Body', 'implemented', 1, '{Stamp(Now.AddDays(-95))}', '{Stamp(Now.AddDays(-110))}', '{Stamp(Now.AddDays(-95))}', 'person@example.test');
             INSERT INTO backlog_dispatches (item, team_id, team_name, correlation, dispatched_at, dispatched_by, frozen_at, frozen_stats, landed_at, landed_sha, landed_branch)
             VALUES ('item-one', 'Alpha', 'Alpha', {landed.Root}, '{Stamp(Now.AddDays(-110))}', 'person@example.test', '{Stamp(Now.AddDays(-95))}', '{frozen}', '{Stamp(Now.AddDays(-99))}', 'abc123', 'main');
             INSERT INTO admission_holds (team_id, team_name, member, delivery_seq, held_at, released_at, reason_kind, reason)
             VALUES ('Alpha', 'Alpha', 'Dev', {plain.Root}, '{Stamp(Now.AddDays(-110))}', '{Stamp(Now.AddDays(-109))}', 'memory', 'held');
             INSERT INTO nudge_ledger (nudge_seq, correlation, nudged_at) VALUES ({plain.Progress}, {plain.Root}, '{Stamp(Now.AddDays(-105))}');
             INSERT INTO delivery_ledger (delivery_seq, correlation, queued_at) VALUES ({plain.Root}, {plain.Root}, '{Stamp(Now.AddDays(-110))}');
             """);
        await _tenant.WriteAsync(null, "person@example.test", TenantActions.TenantSettingChanged, "x", null, "{}", Ct);

        var before = await KeptTablesAsync();
        var tenantBefore = await TenantRowsAsync();
        Assert.NotEmpty(before["usage_ledger"]);
        Assert.NotEmpty(before["workflow_ledger"]);

        var pass = await Retention().RunAsync(Now, Ct);
        Assert.True(pass.Purged > 0);

        Assert.Equal(before, await KeptTablesAsync());

        // The tenant log only grows by the pass's own row.
        var tenantAfter = await TenantRowsAsync();
        Assert.Equal(tenantBefore, tenantAfter.Take(tenantBefore.Count).ToList());
        Assert.Single(tenantAfter.Skip(tenantBefore.Count));

        // The dispatch's workflow keeps its root and declaration; the plain one goes whole.
        Assert.NotNull(await _log.FindAsync(landed.Root, Ct));
        Assert.NotNull(await _log.FindAsync(landed.Declared!.Value, Ct));
        foreach (var seq in plain.All) Assert.Null(await _log.FindAsync(seq, Ct));
    }

    [Fact]
    public async Task Zero_removes_nothing_under_seven_is_refused_naming_the_setting_and_the_setting_is_read_at_every_pass()
    {
        var store = new SqliteTenantSettingsStore(_database);
        var settings = new TenantSettings(store, new ConfigurationBuilder().Build(), cpuCount: 2, memoryLimitMb: 0);
        await settings.LoadAsync(Ct);

        // Listed, with its sentence and its default.
        var definition = settings.Definition(TenantSettings.LogRetentionDaysName)!;
        Assert.NotNull(definition);
        Assert.Equal("90", definition.BuiltInDefault);
        Assert.StartsWith(TenantSettings.LogRetentionSentence, settings.DescriptionOf(TenantSettings.LogRetentionDaysName));
        Assert.Equal(90, settings.LogRetentionDays);

        foreach (var refused in new[] { 1, 6 })
        {
            var rejected = await Assert.ThrowsAsync<TenantSettingRejected>(() => WriteAsync(settings, refused));
            Assert.Equal("log.retentionDays", rejected.Field);
            Assert.Contains("log.retentionDays", rejected.Message);
        }

        var old = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        var retention = Retention(() => settings.LogRetentionDays);

        await WriteAsync(settings, 0);
        var off = await retention.RunAsync(Now, Ct);
        Assert.Null(off.Cutoff);
        Assert.Equal(0, off.Chosen);
        foreach (var seq in old.All) Assert.NotNull(await _log.FindAsync(seq, Ct));
        Assert.Empty(await PrunedRowsAsync());

        // 120 days keeps a workflow finished 100 days ago; 7, written without a restart, does not.
        await WriteAsync(settings, 120);
        Assert.False((await retention.RunAsync(Now, Ct)).Removed);
        foreach (var seq in old.All) Assert.NotNull(await _log.FindAsync(seq, Ct));

        await WriteAsync(settings, 7);
        var on = await retention.RunAsync(Now, Ct);
        Assert.Equal(7, on.Days);
        foreach (var seq in old.All) Assert.Null(await _log.FindAsync(seq, Ct));
    }

    [Fact]
    public async Task A_pass_writes_one_log_pruned_row_with_its_counts_a_pass_removing_nothing_writes_none_and_a_pass_larger_than_the_batch_finishes_later()
    {
        var workflows = new List<Workflow>();
        for (var i = 0; i < 4; i++) workflows.Add(await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100));
        var total = workflows.Sum(w => w.All.Count);

        var retention = Retention(batch: 4);
        var passes = new List<LogRetentionPass>();
        for (var i = 0; i < 30 && (long)(await ScalarAsync("SELECT COUNT(*) FROM messages"))! > 0; i++)
        {
            passes.Add(await retention.RunAsync(Now, Ct));
        }

        // Every row went, no pass chose more than the batch, and it took more than one pass.
        foreach (var seq in workflows.SelectMany(w => w.All)) Assert.Null(await _log.FindAsync(seq, Ct));
        Assert.All(passes, p => Assert.True(p.Chosen <= 4, $"a pass chose {p.Chosen}"));
        Assert.True(passes.Count > 1);
        Assert.Equal(total, passes.Sum(p => p.Purged));

        // One row per pass that removed something, carrying that pass's counts.
        var rows = await PrunedRowsAsync();
        var removing = passes.Where(p => p.Removed).ToList();
        Assert.Equal(removing.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal("log.retentionDays", rows[i].GetProperty("setting").GetString());
            Assert.Equal(90, rows[i].GetProperty("days").GetInt32());
            Assert.Equal(Now.AddDays(-90), DateTimeOffset.Parse(rows[i].GetProperty("cutoff").GetString()!, CultureInfo.InvariantCulture));
            Assert.Equal(removing[i].Purged, rows[i].GetProperty("purged").GetInt32());
            Assert.Equal(removing[i].Retained, rows[i].GetProperty("retained").GetInt32());
            Assert.Equal(removing[i].FileChanges, rows[i].GetProperty("fileChanges").GetInt32());
        }

        // Nothing left to remove: the next pass writes no row.
        var idle = await retention.RunAsync(Now, Ct);
        Assert.False(idle.Removed);
        Assert.Equal(rows.Count, (await PrunedRowsAsync()).Count);
    }

    [Fact]
    public async Task A_failed_pass_is_logged_and_the_next_pass_runs()
    {
        var old = await WorkflowAsync(rootDaysAgo: 110, declaredDaysAgo: 100);
        var calls = 0;
        var retention = Retention(() => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("setting unreadable") : 90);
        var lines = new LineLogger<LogRetentionLoop>();

        using var loop = new LogRetentionLoop(retention, lines, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50));
        await loop.StartAsync(Ct);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await _log.FindAsync(old.Root, Ct) is not null && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);

        Assert.Null(await _log.FindAsync(old.Root, Ct));
        Assert.Contains(lines.Lines, line => line.Contains("log retention pass failed", StringComparison.Ordinal));
        Assert.False(loop.ExecuteTask!.IsFaulted);

        await loop.StopAsync(Ct);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private sealed record Workflow(long Root, long DevRun, long Progress, long ManagerRun, long? Declared)
    {
        public IReadOnlyList<long> All => Declared is { } d ? [Root, DevRun, Progress, ManagerRun, d] : [Root, DevRun, Progress, ManagerRun];
    }

    private LogRetention Retention(Func<int>? days = null, int batch = LogRetention.DefaultBatch) =>
        new(new SqliteLogRetention(_database), _log, _tenant, days ?? (() => LogRetention.DefaultDays),
            NullLogger<LogRetention>.Instance, batch);

    /// <summary>
    /// A workflow: a person's instruction to Dev, Dev's run and a progress row, the Manager's run,
    /// and, unless null, the Manager's declaration. Rooted <paramref name="rootDaysAgo"/> days ago;
    /// the declaration is dated <paramref name="declaredDaysAgo"/> days ago.
    /// </summary>
    private async Task<Workflow> WorkflowAsync(int rootDaysAgo, int? declaredDaysAgo)
    {
        var root = await _log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "look" }), "console"), Ct);
        var dev = await _log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", Dev.ToString(), root.Seq), Ct);
        var progress = await _log.AppendAsync(new NewMessage(MessageTypes.Progress, "{}", Dev.ToString(), root.Seq), Ct);
        var manager = await _log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", Manager.ToString(), dev.Seq), Ct);

        await AgeAsync([root.Seq, dev.Seq, progress.Seq, manager.Seq], Now.AddDays(-rootDaysAgo));

        long? declared = null;
        if (declaredDaysAgo is { } days)
        {
            declared = (await _log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", Manager.ToString(), manager.Seq), Ct)).Seq;
            await AgeAsync([declared.Value], Now.AddDays(-days));
        }

        return new Workflow(root.Seq, dev.Seq, progress.Seq, manager.Seq, declared);
    }

    private Task AgeAsync(IEnumerable<long> seqs, DateTimeOffset at) =>
        ExecuteAsync($"UPDATE messages SET occurred_at = '{Stamp(at)}' WHERE seq IN ({string.Join(',', seqs)})");

    private Task QueueAsync(long seq) =>
        ExecuteAsync($"INSERT INTO pending_deliveries (subscriber, seq) VALUES ('{Dev}', {seq})");

    private async Task<long> FileChangeAsync(DateTimeOffset at)
    {
        await ExecuteAsync(
            $"""
             INSERT INTO file_changes (team, member, repo, at, correlation, files_json, commits_json, more)
             VALUES ('Alpha', 'Dev', 'Repo', '{MessageSchema.FileChangeInstant(at)}', NULL, '[]', '[]', 0)
             """);
        return (long)(await ScalarAsync("SELECT MAX(id) FROM file_changes"))!;
    }

    private async Task<List<long>> FileChangeIdsAsync()
    {
        var ids = new List<long>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM file_changes ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static Task WriteAsync(TenantSettings settings, int days) =>
        settings.WriteAsync(
            new Dictionary<string, JsonElement> { ["log.retentionDays"] = JsonSerializer.SerializeToElement(days) },
            null, "person@example.test", Ct);

    private async Task<List<JsonElement>> PrunedRowsAsync()
    {
        var rows = new List<JsonElement>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT subject, actor_id, detail FROM tenant_events WHERE action = 'log.pruned' ORDER BY seq";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            Assert.Equal("log.retentionDays", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            rows.Add(JsonDocument.Parse(reader.GetString(2)).RootElement.Clone());
        }

        return rows;
    }

    private async Task<List<string>> TenantRowsAsync() => (await RowsAsync("tenant_events", "seq"));

    /// <summary>Every row of every table a pass must never change, as JSON text, by table.</summary>
    private async Task<Dictionary<string, List<string>>> KeptTablesAsync()
    {
        var tables = new Dictionary<string, List<string>>();
        foreach (var (table, order) in new[]
                 {
                     ("usage_ledger", "run_seq"), ("workflow_ledger", "close_seq"), ("delivery_ledger", "delivery_seq"),
                     ("nudge_ledger", "nudge_seq"), ("admission_holds", "id"), ("backlog_items", "id"),
                     ("backlog_dispatches", "id"), ("workflow_outcome_links", "id"),
                 })
        {
            tables[table] = await RowsAsync(table, order);
        }

        return tables.ToDictionary(e => e.Key, e => e.Value);
    }

    private async Task<List<string>> RowsAsync(string table, string order)
    {
        var rows = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY {order}";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(JsonSerializer.Serialize(values));
        }

        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={_database};Pooling=False");
        await connection.OpenAsync(Ct);
        return connection;
    }

    private sealed class LineLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue(formatter(state, exception));
    }
}
