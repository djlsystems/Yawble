using Harness.Contracts;
using Harness.Host;
using Harness.Host.SqlServer;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IMcpServerStore"/> (MCP 1), as each provider must keep it: an entry round trips with its URL's
/// query only as ciphertext; a name is unique whatever its case; an edit keeps the stored URL when given
/// none; a check that marks nothing keeps the health; a team is given an entry once; and a removal takes it
/// from every team, with the rows it is handed, in one transaction.
/// </summary>
public abstract class McpServerStoreContract : HostStoreContract
{
    private static readonly DateTimeOffset At = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private const string Url = "https://mcp.example.test/mcp?key=query-secret-1";

    private async Task<IMcpServerStore> StoreAsync() => (await DatabaseAsync()).McpServers(Protection);

    private static McpServerRecord Web(string id, string name) => new(
        id, name, McpServerRecord.Web, "https://mcp.example.test/mcp", null, null, null, null, null, null, null, [], [],
        McpServerRecord.Unchecked, null, null, null, At, At);

    [Fact]
    public async Task An_entry_round_trips_with_its_query_sealed_and_its_name_unique_whatever_its_case()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();

        Assert.Equal(McpServerWrite.Done, await store.AddAsync(Web("mcp-1", "github"), Url, Audit(TenantActions.McpServerAdded, "mcp-1"), Ct));
        Assert.Equal(McpServerWrite.NameTaken, await store.AddAsync(Web("mcp-2", "GitHub"), Url, Audit(TenantActions.McpServerAdded, "mcp-2"), Ct));

        var runs = Web("mcp-3", "local") with
        {
            Kind = McpServerRecord.Runs, UrlShown = null, Command = "npx", Package = "@scope/pkg", Version = "1.2.3",
            Args = ["--read-only"], Env = [new McpServerEnv("TOKEN", "conn-1", "token")],
        };
        Assert.Equal(McpServerWrite.Done, await store.AddAsync(runs, null, Audit(TenantActions.McpServerAdded, "mcp-3"), Ct));

        Assert.Equal(["github", "local"], (await store.ListAsync(Ct)).Select(s => s.Name));
        Assert.Equal(Url, await store.UrlAsync("mcp-1", Ct));
        Assert.Null(await store.UrlAsync("mcp-3", Ct));

        var read = (await store.GetAsync("mcp-3", Ct))!;
        Assert.Equal(["--read-only"], read.Args);
        Assert.Equal([new McpServerEnv("TOKEN", "conn-1", "token")], read.Env);
        Assert.Equal(At, read.CreatedAt);

        Assert.DoesNotContain(await database.ColumnAsync("SELECT url_protected FROM mcp_servers"), v => v?.Contains("query-secret-1") == true);
        Assert.Equal(2, (await database.ColumnAsync($"SELECT subject FROM tenant_events WHERE action = '{TenantActions.McpServerAdded}'")).Count);
    }

    [Fact]
    public async Task An_edit_keeps_the_url_when_given_none_and_a_check_that_marks_nothing_keeps_the_health()
    {
        var store = await StoreAsync();
        await store.AddAsync(Web("mcp-1", "github"), Url, Audit(TenantActions.McpServerAdded, "mcp-1"), Ct);
        await store.AddAsync(Web("mcp-2", "other"), Url, Audit(TenantActions.McpServerAdded, "mcp-2"), Ct);

        Assert.Equal(McpServerWrite.Done, await store.UpdateAsync(Web("mcp-1", "github2"), null, Audit(TenantActions.McpServerEdited, "mcp-1"), Ct));
        Assert.Equal(Url, await store.UrlAsync("mcp-1", Ct));
        Assert.Equal(McpServerWrite.NameTaken, await store.UpdateAsync(Web("mcp-1", "OTHER"), null, Audit(TenantActions.McpServerEdited, "mcp-1"), Ct));
        Assert.Equal(McpServerWrite.Missing, await store.UpdateAsync(Web("mcp-9", "nine"), null, Audit(TenantActions.McpServerEdited, "mcp-9"), Ct));

        await store.RecordCheckAsync("mcp-1", McpServerRecord.Ok, null, ["a", "b"], At, Audit(TenantActions.McpServerChecked, "mcp-1"), Ct);
        await store.RecordCheckAsync("mcp-1", null, null, null, At.AddMinutes(1), Audit(TenantActions.McpServerChecked, "mcp-1"), Ct);

        var read = (await store.GetAsync("mcp-1", Ct))!;
        Assert.Equal((McpServerRecord.Ok, (string?)null), (read.Status, read.StatusReason));
        Assert.Equal(["a", "b"], read.Tools);
        Assert.Equal(At.AddMinutes(1), read.CheckedAt);

        await store.RecordCheckAsync("mcp-1", McpServerRecord.NeedsYou, "refused", null, At, Audit(TenantActions.McpServerChecked, "mcp-1"), Ct);
        Assert.Equal((McpServerRecord.NeedsYou, "refused"), ((await store.GetAsync("mcp-1", Ct))!.Status, (await store.GetAsync("mcp-1", Ct))!.StatusReason));
    }

    [Fact]
    public async Task A_team_is_given_an_entry_once_and_a_removal_takes_it_from_every_team_with_its_rows()
    {
        var database = await DatabaseAsync();
        await HireAsync(database, "Alpha");
        await HireAsync(database, "Beta");
        var store = await StoreAsync();
        await store.AddAsync(Web("mcp-1", "github"), Url, Audit(TenantActions.McpServerAdded, "mcp-1"), Ct);

        Assert.Equal(AssignOutcome.Assigned, await store.AssignAsync("Alpha", "mcp-1", Audit(TenantActions.TeamMcpServerAssigned, "mcp-1"), Ct));
        Assert.Equal(AssignOutcome.Already, await store.AssignAsync("alpha", "mcp-1", Audit(TenantActions.TeamMcpServerAssigned, "mcp-1"), Ct));
        Assert.Equal(AssignOutcome.Assigned, await store.AssignAsync("Beta", "mcp-1", Audit(TenantActions.TeamMcpServerAssigned, "mcp-1"), Ct));
        Assert.Equal(AssignOutcome.NoConnection, await store.AssignAsync("Beta", "mcp-9", Audit(TenantActions.TeamMcpServerAssigned, "mcp-9"), Ct));

        Assert.Equal(["mcp-1"], (await store.AssignedAsync("Alpha", Ct)).Select(a => a.ServerId));
        Assert.Equal(["Alpha", "Beta"], (await store.AllAssignmentsAsync(Ct))["mcp-1"]);

        Assert.True(await store.UnassignAsync("Beta", "mcp-1", Audit(TenantActions.TeamMcpServerRemoved, "mcp-1"), Ct));
        Assert.False(await store.UnassignAsync("Beta", "mcp-1", Audit(TenantActions.TeamMcpServerRemoved, "mcp-1"), Ct));
        await store.AssignAsync("Beta", "mcp-1", Audit(TenantActions.TeamMcpServerAssigned, "mcp-1"), Ct);

        var teams = await store.RemoveAsync("mcp-1", from => [.. from.Select(t => Audit(TenantActions.TeamMcpServerRemoved, t))], Ct);

        Assert.Equal(["Alpha", "Beta"], teams);
        Assert.Empty(await store.AssignedAsync("Alpha", Ct));
        Assert.Null(await store.GetAsync("mcp-1", Ct));
        Assert.Null(await store.RemoveAsync("mcp-1", _ => [], Ct));
        Assert.Equal(["mcp-1", "Alpha", "Beta"],
            await database.ColumnAsync($"SELECT subject FROM tenant_events WHERE action = '{TenantActions.TeamMcpServerRemoved}' ORDER BY seq"));
    }
}

public sealed class SqliteMcpServerStoreTests : McpServerStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerMcpServerStoreTests : McpServerStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
