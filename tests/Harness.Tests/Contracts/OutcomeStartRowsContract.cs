using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IOutcomeStartRows"/>, the one-time back-fill of backlog items' outcomes, as each provider
/// must keep it: <c>OutcomeDashboardTests</c>' back-fill, with the same name, run through the
/// interface over rows written as a volume from before the link writer held them. Then the
/// newest dispatch and the merge the remarks name, and an item that has an outcome left as it is.
/// </summary>
public abstract class OutcomeStartRowsContract : ProviderContract
{
    private const string At = "2026-10-01T00:00:00.0000000+00:00";

    protected async Task<IOutcomeStartRows> CreateAsync() => (await StoresAsync()).OutcomeStart;

    [Fact]
    public async Task The_start_back_fill_gives_an_item_its_newest_dispatch_workflows_outcome_once()
    {
        var start = await CreateAsync();
        var stores = await StoresAsync();

        var outcome = await OutcomeAsync(stores, "Back-filled");
        await ItemAsync(stores, "item-1", null);
        await DispatchAsync(stores, "item-1", 501);

        // A LINK FROM BEFORE THE CHANGE: written as a row, so the live writer's inheritance never ran.
        await LinkAsync(stores, 501, outcome);

        Assert.True(await start.RunAsync(Ct) >= 1);
        Assert.Equal(outcome, await ItemOutcomeAsync(stores, "item-1"));
        Assert.Equal(1, await InheritedRowsAsync(stores, "item-1"));

        Assert.Null(await start.RunAsync(Ct));
        Assert.Equal(1, await InheritedRowsAsync(stores, "item-1"));
    }

    [Fact]
    public async Task An_item_takes_its_newest_dispatchs_outcome_followed_through_a_merge_and_one_with_an_outcome_keeps_it()
    {
        var start = await CreateAsync();
        var stores = await StoresAsync();

        var older = await OutcomeAsync(stores, "Older");
        var merged = await OutcomeAsync(stores, "Merged away");
        var into = await OutcomeAsync(stores, "Merged into");
        await stores.ExecuteAsync($"UPDATE outcomes SET status = 'merged', merged_into = '{into}' WHERE id = '{merged}'");

        await ItemAsync(stores, "item-1", null);
        await DispatchAsync(stores, "item-1", 601);
        await DispatchAsync(stores, "item-1", 602);
        await LinkAsync(stores, 601, older);
        await LinkAsync(stores, 602, merged);

        await ItemAsync(stores, "item-2", older);
        await DispatchAsync(stores, "item-2", 603);
        await LinkAsync(stores, 603, into);

        await ItemAsync(stores, "item-3", null);
        await DispatchAsync(stores, "item-3", 604);

        Assert.Equal(1, await start.RunAsync(Ct));
        Assert.Equal(into, await ItemOutcomeAsync(stores, "item-1"));
        Assert.Equal(older, await ItemOutcomeAsync(stores, "item-2"));
        Assert.Null(await ItemOutcomeAsync(stores, "item-3"));
        Assert.Equal(1, await stores.LongAsync($"SELECT COUNT(*) FROM tenant_settings WHERE name = '{BacklogOutcomeStart.DoneName}'"));
    }

    private static async Task<string> OutcomeAsync(ContractStores stores, string name)
    {
        var id = Guid.NewGuid().ToString("D");
        await stores.ExecuteAsync(
            $"""
             INSERT INTO outcomes (id, name, name_key, status, created_by, created_by_kind, created_at, updated_at)
             VALUES ('{id}', '{name}', '{OutcomeNames.Key(name)}', 'active', 'person@example.test', 'person', '{At}', '{At}')
             """);
        return id;
    }

    private static Task ItemAsync(ContractStores stores, string id, string? outcome) =>
        stores.ExecuteAsync(
            $"""
             INSERT INTO backlog_items (id, team, title, body, state, position, created_at, updated_at, created_by, outcome_id)
             VALUES ('{id}', 'Alpha', 'Title {id}', 'Body', 'ready', 1, '{At}', '{At}', 'person@example.test', {(outcome is null ? "NULL" : $"'{outcome}'")})
             """);

    private static Task DispatchAsync(ContractStores stores, string item, long correlation) =>
        stores.ExecuteAsync(
            $"""
             INSERT INTO backlog_dispatches (item, team_id, team_name, correlation, dispatched_at, dispatched_by)
             VALUES ('{item}', 'Alpha', 'Alpha', {correlation}, '{At}', 'person@example.test')
             """);

    private static Task LinkAsync(ContractStores stores, long correlation, string outcome) =>
        stores.ExecuteAsync(
            $"""
             INSERT INTO workflow_outcome_links (correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link,
                 set_by, set_by_kind, set_at, how)
             VALUES ({correlation}, '{outcome}', 'Alpha', 'Alpha', 'old', 'alpha/Manager', 'member', '{At}', 'manager')
             """);

    private static Task<string?> ItemOutcomeAsync(ContractStores stores, string item) =>
        stores.TextAsync($"SELECT outcome_id FROM backlog_items WHERE id = '{item}'");

    private static Task<long> InheritedRowsAsync(ContractStores stores, string item) =>
        stores.LongAsync(
            $"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.BacklogItemOutcomeInherited}' AND subject = '{item}'");
}

public sealed class SqliteOutcomeStartRowsTests : OutcomeStartRowsContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerOutcomeStartRowsTests : OutcomeStartRowsContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
