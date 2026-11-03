using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;
using Harness.Messaging;

namespace Harness.Tests.Contracts;

/// <summary>
/// <c>execution_snapshots</c> (<c>outcome-008</c>) and <c>usage_ledger.start_seq</c>, as each provider
/// must write them: one immutable snapshot per <c>agentContainer.started</c> row, in its append's
/// transaction, saying what the run was configured as when it started - the member's kind and preset,
/// the rate it is priced at and where that came from, and the outcome link current then - and the run's
/// ledger row naming it. The roster, the tenant's settings and the links are written as rows, the way
/// the Host's stores leave them.
/// </summary>
public abstract class ExecutionSnapshotContract : ProviderContract
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");
    private static readonly ContainerId Fx = new("Alpha", "Fx");

    private const string Columns =
        "correlation, team_id, team_name, member, member_kind, agent_preset, model, rate_amount, rate_currency, "
        + "rate_source, billing_account_id, link_id, started_at, origin_kind, origin_actor_id, origin_actor_email, origin_trigger";

    [Fact]
    public async Task A_start_writes_one_snapshot_at_the_tenant_rate_and_the_runs_ledger_row_names_it()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude");
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");
        await SettingAsync(stores, ExecutionSnapshotFacts.CurrencySetting, "eur");

        var delivery = await stores.Log.AppendAsync(Instruction(Dev, "build it"), Ct);
        var started = await StartAsync(stores, Dev, delivery.Seq);
        var terminal = await stores.Log.AppendAsync(Completed(Dev, delivery.Seq), Ct);

        var row = Assert.Single(await stores.RowsAsync($"SELECT {Columns} FROM execution_snapshots WHERE start_seq = {started.Seq}"));
        Assert.Equal(
            [delivery.CorrelationId, "Alpha", "Alpha Squad", "Dev", MemberRef.AgentKind, "claude", null, "60", "EUR",
             ExecutionSnapshotFacts.RateSourceTenant, null, null, Instant.Stamp(started.OccurredAt), null, null, null, null],
            Normalised(row));

        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM execution_snapshots"));
        Assert.Equal(started.Seq, await stores.LongAsync($"SELECT start_seq FROM usage_ledger WHERE run_seq = {terminal.Seq}"));
    }

    [Fact]
    public async Task An_agent_with_no_tenant_rate_or_a_zero_rate_is_not_priced_and_never_priced_at_zero()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude");

        var unset = await RunAsync(stores, Dev);

        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "0");
        var zero = await RunAsync(stores, Dev);

        foreach (var start in new[] { unset, zero })
        {
            Assert.Equal(
                [null, null, ExecutionSnapshotFacts.RateSourceNone],
                Normalised(Assert.Single(await stores.RowsAsync(
                    $"SELECT rate_amount, rate_currency, rate_source FROM execution_snapshots WHERE start_seq = {start}"))));
        }
    }

    [Fact]
    public async Task A_plugin_is_not_priced_at_the_agent_rate()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude");
        await HireAsync(stores, Fx, "plugin:fixture");
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");

        var plugin = await RunAsync(stores, Fx);
        var agent = await RunAsync(stores, Dev);

        Assert.Equal(
            [MemberRef.PluginKind, "plugin:fixture", null, null, ExecutionSnapshotFacts.RateSourceNone],
            Normalised(Assert.Single(await stores.RowsAsync(
                $"SELECT member_kind, agent_preset, rate_amount, rate_currency, rate_source FROM execution_snapshots WHERE start_seq = {plugin}"))));

        Assert.Equal("60", await stores.TextAsync($"SELECT rate_amount FROM execution_snapshots WHERE start_seq = {agent}"));
    }

    [Fact]
    public async Task A_member_no_longer_on_the_roster_has_no_kind_and_is_not_priced()
    {
        var stores = await StoresAsync();
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");

        var start = await RunAsync(stores, Dev);

        Assert.Equal(
            [null, null, null, ExecutionSnapshotFacts.RateSourceNone],
            Normalised(Assert.Single(await stores.RowsAsync(
                $"SELECT member_kind, agent_preset, rate_amount, rate_source FROM execution_snapshots WHERE start_seq = {start}"))));
    }

    [Fact]
    public async Task A_rate_changed_during_a_run_leaves_that_runs_snapshot_and_prices_the_next_run()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude");
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");

        var delivery = await stores.Log.AppendAsync(Instruction(Dev, "first"), Ct);
        var first = await StartAsync(stores, Dev, delivery.Seq);

        // Under way: the rate changes, and the run ends.
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "90");
        var firstEnd = await stores.Log.AppendAsync(Completed(Dev, delivery.Seq), Ct);

        var next = await RunAsync(stores, Dev);

        Assert.Equal(
            ["60", ExecutionSnapshotFacts.RateSourceTenant],
            Normalised(Assert.Single(await stores.RowsAsync($"SELECT rate_amount, rate_source FROM execution_snapshots WHERE start_seq = {first.Seq}"))));
        Assert.Equal(
            ["90", ExecutionSnapshotFacts.RateSourceTenant],
            Normalised(Assert.Single(await stores.RowsAsync($"SELECT rate_amount, rate_source FROM execution_snapshots WHERE start_seq = {next}"))));

        // The run's ledger row still names the snapshot it started with.
        Assert.Equal(first.Seq, await stores.LongAsync($"SELECT start_seq FROM usage_ledger WHERE run_seq = {firstEnd.Seq}"));

        // The rate removed altogether, mid-run or not, never reaches a snapshot already written.
        await stores.ExecuteAsync($"DELETE FROM tenant_settings WHERE name = '{ExecutionSnapshotFacts.AgentHourlyRateSetting}'");
        Assert.Equal("60", await stores.TextAsync($"SELECT rate_amount FROM execution_snapshots WHERE start_seq = {first.Seq}"));
    }

    [Fact]
    public async Task The_link_current_at_start_is_kept_and_a_later_link_or_unlink_does_not_move_it()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude");

        var root = await stores.Log.AppendAsync(Instruction(Dev, "build it"), Ct);
        var unlinked = await StartAsync(stores, Dev, root.Seq);
        await stores.Log.AppendAsync(Completed(Dev, root.Seq), Ct);

        var first = await LinkAsync(stores, root.CorrelationId, "'outcome-a'");
        var second = await LinkAsync(stores, root.CorrelationId, "'outcome-b'");

        var again = await stores.Log.AppendAsync(Instruction(Dev, "again", root.Seq), Ct);
        var linked = await StartAsync(stores, Dev, again.Seq);

        // Moved and then unlinked while it runs: an unlink is a row, and a later one.
        await LinkAsync(stores, root.CorrelationId, "'outcome-c'");
        var unlink = await LinkAsync(stores, root.CorrelationId, "NULL");
        await stores.Log.AppendAsync(Completed(Dev, again.Seq), Ct);

        var last = await stores.Log.AppendAsync(Instruction(Dev, "last", root.Seq), Ct);
        var afterUnlink = await StartAsync(stores, Dev, last.Seq);

        Assert.NotEqual(first, second);
        Assert.Null((await stores.RowsAsync($"SELECT link_id FROM execution_snapshots WHERE start_seq = {unlinked.Seq}"))[0][0]);
        Assert.Equal(second, await stores.LongAsync($"SELECT link_id FROM execution_snapshots WHERE start_seq = {linked.Seq}"));
        Assert.Equal(unlink, await stores.LongAsync($"SELECT link_id FROM execution_snapshots WHERE start_seq = {afterUnlink.Seq}"));
    }

    [Fact]
    public async Task A_snapshot_is_never_updated_or_deleted()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude");
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");
        await RunAsync(stores, Dev);

        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE execution_snapshots SET rate_amount = '90'"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE execution_snapshots SET link_id = 1"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("DELETE FROM execution_snapshots"));

        Assert.Equal("60", await stores.TextAsync("SELECT rate_amount FROM execution_snapshots"));
    }

    [Fact]
    public async Task One_snapshot_per_start_and_one_ledger_row_per_run_even_for_a_batched_run()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude");

        var first = await stores.Log.AppendAsync(Instruction(Dev, "one"), Ct);
        var batched = await stores.Log.AppendAsync(Instruction(Dev, "two", first.Seq), Ct);
        var started = await StartAsync(stores, Dev, first.Seq);
        var terminal = await stores.Log.AppendAsync(Completed(Dev, first.Seq), Ct);
        await stores.Log.AppendAsync(new NewMessage(MessageTypes.Completed,
            JsonSerializer.Serialize(new { tokensIn = (int?)null, tokensOut = (int?)null, usageCountedOn = first.Seq }),
            Dev.ToString(), batched.Seq), Ct);

        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM execution_snapshots"));
        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM usage_ledger"));
        Assert.Equal(started.Seq, await stores.LongAsync($"SELECT start_seq FROM usage_ledger WHERE run_seq = {terminal.Seq}"));
    }

    [Fact]
    public async Task A_backfilled_run_names_its_start_but_history_gets_no_snapshot()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude");
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");

        // A volume from before the ledger: rows on the log, none of their ledger rows.
        var root = await stores.InsertRawMessageAsync(MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console", null);
        var started = await stores.InsertRawMessageAsync(MessageTypes.Started, "{}", Dev.ToString(), root);
        var terminal = await stores.InsertRawMessageAsync(MessageTypes.Completed, """{"tokensIn":10,"tokensOut":5,"tokensSource":"claude"}""", Dev.ToString(), root);

        Assert.Equal(1, (await stores.LedgerStart.RunAsync(Ct)).BackfilledRuns);

        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM execution_snapshots"));
        Assert.Equal(started, await stores.LongAsync($"SELECT start_seq FROM usage_ledger WHERE run_seq = {terminal}"));
    }

    private const string Account = "3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10";

    [Fact]
    public async Task A_members_own_positive_rate_is_snapshotted_as_member_agent_or_plugin_with_its_billing_account()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude", rate: "62.5", account: Account);
        await HireAsync(stores, Fx, "plugin:fixture", rate: "15");
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");
        await SettingAsync(stores, ExecutionSnapshotFacts.CurrencySetting, "eur");

        var agent = await RunAsync(stores, Dev);
        var plugin = await RunAsync(stores, Fx);

        Assert.Equal(
            [MemberRef.AgentKind, "62.5", "EUR", ExecutionSnapshotFacts.RateSourceMember, Account],
            Normalised(Assert.Single(await stores.RowsAsync(
                $"SELECT member_kind, rate_amount, rate_currency, rate_source, billing_account_id FROM execution_snapshots WHERE start_seq = {agent}"))));
        Assert.Equal(
            [MemberRef.PluginKind, "15", "EUR", ExecutionSnapshotFacts.RateSourceMember, null],
            Normalised(Assert.Single(await stores.RowsAsync(
                $"SELECT member_kind, rate_amount, rate_currency, rate_source, billing_account_id FROM execution_snapshots WHERE start_seq = {plugin}"))));
    }

    [Fact]
    public async Task An_own_zero_is_not_priced_and_does_not_fall_through_to_the_tenant_rate()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude", rate: "0");
        await HireAsync(stores, Fx, "plugin:fixture", rate: "0");
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");

        foreach (var start in new[] { await RunAsync(stores, Dev), await RunAsync(stores, Fx) })
        {
            Assert.Equal(
                [null, null, ExecutionSnapshotFacts.RateSourceNone],
                Normalised(Assert.Single(await stores.RowsAsync(
                    $"SELECT rate_amount, rate_currency, rate_source FROM execution_snapshots WHERE start_seq = {start}"))));
        }

        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM execution_snapshots WHERE rate_amount = '0'"));
    }

    [Fact]
    public async Task A_member_rate_or_account_changed_during_a_run_leaves_that_snapshot_and_the_next_run_carries_the_new_one()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude", rate: "50", account: Account);
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");

        var delivery = await stores.Log.AppendAsync(Instruction(Dev, "first"), Ct);
        var first = await StartAsync(stores, Dev, delivery.Seq);

        // Under way: the member's rate and account change, and the run ends.
        await stores.ExecuteAsync($"UPDATE team_members SET est_hourly_rate = '70', billing_account_id = NULL WHERE team = '{Dev.Team}' AND name = '{Dev.Name}'");
        await stores.Log.AppendAsync(Completed(Dev, delivery.Seq), Ct);

        var next = await RunAsync(stores, Dev);

        // Cleared altogether: the agent inherits the tenant default from its next run.
        await stores.ExecuteAsync($"UPDATE team_members SET est_hourly_rate = NULL WHERE team = '{Dev.Team}' AND name = '{Dev.Name}'");
        var inherited = await RunAsync(stores, Dev);

        Assert.Equal(
            ["50", ExecutionSnapshotFacts.RateSourceMember, Account],
            Normalised(Assert.Single(await stores.RowsAsync($"SELECT rate_amount, rate_source, billing_account_id FROM execution_snapshots WHERE start_seq = {first.Seq}"))));
        Assert.Equal(
            ["70", ExecutionSnapshotFacts.RateSourceMember, null],
            Normalised(Assert.Single(await stores.RowsAsync($"SELECT rate_amount, rate_source, billing_account_id FROM execution_snapshots WHERE start_seq = {next}"))));
        Assert.Equal(
            ["60", ExecutionSnapshotFacts.RateSourceTenant, null],
            Normalised(Assert.Single(await stores.RowsAsync($"SELECT rate_amount, rate_source, billing_account_id FROM execution_snapshots WHERE start_seq = {inherited}"))));
    }

    [Fact]
    public async Task An_agent_with_its_own_rate_does_not_pick_up_a_tenant_rate_change()
    {
        var stores = await StoresAsync();
        await HireAsync(stores, Dev, "claude", rate: "45");
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "60");

        var before = await RunAsync(stores, Dev);
        await SettingAsync(stores, ExecutionSnapshotFacts.AgentHourlyRateSetting, "90");
        var after = await RunAsync(stores, Dev);

        foreach (var start in new[] { before, after })
        {
            Assert.Equal(
                ["45", ExecutionSnapshotFacts.RateSourceMember],
                Normalised(Assert.Single(await stores.RowsAsync($"SELECT rate_amount, rate_source FROM execution_snapshots WHERE start_seq = {start}"))));
        }
    }

    // ---- fixtures ----

    private static async Task HireAsync(ContractStores stores, ContainerId member, string agent, string? rate = null, string? account = null)
    {
        if (await stores.LongAsync($"SELECT COUNT(*) FROM teams WHERE id = '{member.Team}'") == 0)
        {
            await stores.ExecuteAsync(
                $"INSERT INTO teams (id, name, created_utc) VALUES ('{member.Team}', '{member.Team} Squad', '2026-10-01T00:00:00.0000000+00:00')");
        }

        static string Sql(string? value) => value is null ? "NULL" : $"'{value}'";

        await stores.ExecuteAsync(
            "INSERT INTO team_members (team, name, agent, subscribes, permits, floor_seq, created_utc, est_hourly_rate, billing_account_id) "
            + $"VALUES ('{member.Team}', '{member.Name}', '{agent}', '[]', '[]', 0, '2026-10-01T00:00:00.0000000+00:00', {Sql(rate)}, {Sql(account)})");
    }

    /// <summary>A tenant setting as its store leaves it: the row replaced.</summary>
    private static async Task SettingAsync(ContractStores stores, string name, string value)
    {
        await stores.ExecuteAsync($"DELETE FROM tenant_settings WHERE name = '{name}'");
        await stores.ExecuteAsync(
            "INSERT INTO tenant_settings (name, value, updated_at, updated_by) "
            + $"VALUES ('{name}', '{value}', '2026-10-01T00:00:00.0000000+00:00', 'person@example.test')");
    }

    /// <summary>A link row, as <c>OutcomeLinks</c> appends one; <paramref name="outcome"/> is SQL (NULL is an unlink). Answers its id.</summary>
    private static async Task<long> LinkAsync(ContractStores stores, long correlation, string outcome)
    {
        await stores.ExecuteAsync(
            "INSERT INTO workflow_outcome_links (correlation, outcome_id, set_by, set_by_kind, set_at, how) "
            + $"VALUES ({correlation}, {outcome}, 'person@example.test', 'person', '2026-10-01T00:00:00.0000000+00:00', 'person')");
        return await stores.LongAsync($"SELECT MAX(id) FROM workflow_outcome_links WHERE correlation = {correlation}");
    }

    private static async Task<Message> StartAsync(ContractStores stores, ContainerId member, long delivery) =>
        await stores.Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", member.ToString(), delivery), Ct);

    /// <summary>A whole run on a workflow of its own; answers its start's seq.</summary>
    private static async Task<long> RunAsync(ContractStores stores, ContainerId member)
    {
        var delivery = await stores.Log.AppendAsync(Instruction(member, "go"), Ct);
        var started = await StartAsync(stores, member, delivery.Seq);
        await stores.Log.AppendAsync(Completed(member, delivery.Seq), Ct);
        return started.Seq;
    }

    private static NewMessage Instruction(ContainerId to, string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(to), JsonSerializer.Serialize(new { instruction = text }), "console", causation);

    private static NewMessage Completed(ContainerId member, long delivery) =>
        new(MessageTypes.Completed, """{"tokensIn":10,"tokensOut":5,"tokensSource":"claude"}""", member.ToString(), delivery);

    /// <summary>A row with every number as a long, so both providers' integers compare alike.</summary>
    private static object?[] Normalised(object?[] row) =>
        [.. row.Select(value => value is int or long or short ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : value)];
}

public sealed class SqliteExecutionSnapshotTests : ExecutionSnapshotContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerExecutionSnapshotTests : ExecutionSnapshotContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
