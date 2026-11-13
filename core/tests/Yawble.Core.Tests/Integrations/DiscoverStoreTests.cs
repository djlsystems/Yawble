using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Integrations storage (migration 0028): row origin, the ask's action key, the MCP checklist tables.</summary>
public class DiscoverStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static WalkOptions Fast => new() { Parallelism = 4, CallTimeout = TimeSpan.FromSeconds(2) };
    private const string Now = "2026-11-10T00:00:00.0000000+00:00";

    private static List<string> Columns(DiscoverRig rig, string table)
    {
        using var c = rig.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(1));
        return list;
    }

    private static DiscoverStore StoreOf(DiscoverRig rig) => new(rig.Db.ConnectionString, TimeProvider.System);

    private static DiscoverOptions GhOptions(FixtureCli? fx = null, string version = "gh version 2.45.0") =>
        new() { Runner = (fx ?? new FixtureCli("gh/2.45")).Runner, Version = version, Walk = Fast };

    [Fact]
    public void TheMigrationAddsOriginAndTheAsksActionKey_AndKeepsExistingRows()
    {
        using var rig = new DiscoverRig();
        Assert.Contains("origin", Columns(rig, "integration_commands"));
        Assert.Contains("action", Columns(rig, "integration_asks"));
        Assert.Contains("discover_runs", Columns(rig, "mcp_servers"));
        Assert.Contains("path", Columns(rig, "mcp_commands"));
        Assert.Contains("path", Columns(rig, "mcp_choices"));

        using var c = rig.Open();
        DiscoverStore.Run(c, null, "INSERT INTO integrations (kind, name, created_at, updated_at) VALUES ('cli', 'old', $n, $n);", ("$n", Now));
        DiscoverStore.Run(c, null,
            "INSERT INTO integration_commands (integration_id, path, parent_path, summary, has_children, class, class_source, class_reason, first_seen_at, last_seen_at) " +
            "VALUES ((SELECT id FROM integrations WHERE name = 'old'), 'x', '', 's', 0, 'readonly', 'verb', 'r', $n, $n);", ("$n", Now));
        Assert.Equal("discovered", DiscoverStore.Scalar(c, null, "SELECT origin FROM integration_commands WHERE path = 'x';"));
    }

    [Fact]
    public async Task AFoundRowIsNeverMarkedGone_AndBecomesDiscoveredWhenAWalkFindsIt()
    {
        using var rig = new DiscoverRig();
        var o = await rig.Svc.DiscoverAsync("gh", GhOptions(), null, Ct);
        Assert.Null(o.Error);
        var store = StoreOf(rig);
        var id = (long)rig.Scalar("SELECT id FROM integrations WHERE name = 'gh'");
        store.UpsertRow(id, "pr lock", "pr", "Lock a pull request", new Classification(CommandClass.Change, ClassSource.None, "found in a log"), RowOrigin.Found);
        using (var c = rig.Open())
        {
            using var tx = c.BeginTransaction();
            store.SetChoice(c, tx, id, null, "pr lock", Decisions.Allow, Now);
            tx.Commit();
        }
        var row = store.Commands(id).Single(r => r.Path == "pr lock");
        Assert.Equal(RowOrigin.Found, row.Origin);
        Assert.Null(row.Badge);

        // a full re-walk that does not list pr lock leaves it alone
        o = await rig.Svc.DiscoverAsync("gh", GhOptions(version: "gh version 2.46.0"), null, Ct);
        Assert.Null(o.Error);
        Assert.False(o.Partial);
        row = store.Commands(id).Single(r => r.Path == "pr lock");
        Assert.False(row.Gone);
        Assert.Equal(RowOrigin.Found, row.Origin);
        Assert.Single(store.Choices(id), ch => ch.Path == "pr lock" && ch.Decision == Decisions.Allow);

        // a walk that lists it turns it into a discovered row, the choice kept
        var pr = FixtureCli.Read("gh/2.45/pr.txt").Replace("  merge:      Merge a pull request\n", "  merge:      Merge a pull request\n  lock:       Lock a pull request\n");
        var fx = new FixtureCli("gh/2.45", new Dictionary<string, string>
        {
            ["pr"] = pr,
            ["pr_lock"] = "Lock a pull request.\n\nUSAGE\n  gh pr lock [flags]\n",
        });
        o = await rig.Svc.DiscoverAsync("gh", GhOptions(fx, "gh version 2.47.0"), null, Ct);
        Assert.Null(o.Error);
        row = store.Commands(id).Single(r => r.Path == "pr lock");
        Assert.Equal(RowOrigin.Discovered, row.Origin);
        Assert.False(row.Gone);
        Assert.Single(store.Choices(id), ch => ch.Path == "pr lock" && ch.Decision == Decisions.Allow);
    }

    [Fact]
    public void UpsertRowNeverChangesAnExistingRowsOriginOrBadge()
    {
        using var rig = new DiscoverRig();
        var store = StoreOf(rig);
        using (var c = rig.Open()) DiscoverStore.Run(c, null, "INSERT INTO integrations (kind, name, created_at, updated_at) VALUES ('cli', 'zz', $n, $n);", ("$n", Now));
        var id = (long)rig.Scalar("SELECT id FROM integrations WHERE name = 'zz'");
        var cls = new Classification(CommandClass.Unknown, ClassSource.None, "r");
        store.UpsertRow(id, "a b", "a", "s", cls, RowOrigin.Builtin);
        store.UpsertRow(id, "a b", "a", "s", cls, RowOrigin.Found);
        Assert.Equal(RowOrigin.Builtin, store.Commands(id).Single().Origin);
    }

    private static JsonArray Tools(params string[] names) =>
        new(names.Select(n => (JsonNode?)new JsonObject { ["name"] = n, ["description"] = $"Does {n}." }).ToArray());

    private static long AddServer(DiscoverRig rig, string name = "fake")
    {
        using var c = rig.Open();
        return Convert.ToInt64(DiscoverStore.Scalar(c, null,
            "INSERT INTO mcp_servers (name, command, created_at, updated_at) VALUES ($n, 'node', $now, $now) RETURNING id;", ("$n", name), ("$now", Now)));
    }

    private static IReadOnlyDictionary<string, Classification> Classes(params (string, string)[] items) =>
        items.ToDictionary(i => i.Item1, i => new Classification(i.Item2, ClassSource.Verb, "test"));

    [Fact]
    public void MergeMcp_InsertsTools_MarksNewOnTheSecondRun_AndGoneWhenMissing()
    {
        using var rig = new DiscoverRig();
        var store = StoreOf(rig);
        var s = AddServer(rig);

        var first = store.MergeMcp(s, Tools("list_issues", "delete_repo"), Classes(("list_issues", CommandClass.ReadOnly), ("delete_repo", CommandClass.Destructive)));
        Assert.Equal(2, first.New);
        var rows = store.McpCommands(s).ToDictionary(r => r.Path);
        Assert.Null(rows["list_issues"].Badge); // the first run has nothing to compare with
        Assert.Equal("Does list_issues.", rows["list_issues"].Summary);
        Assert.Equal(CommandClass.Destructive, rows["delete_repo"].Class);
        Assert.Equal(RowOrigin.Discovered, rows["list_issues"].Origin);

        var second = store.MergeMcp(s, Tools("list_issues", "create_issue"), Classes(("list_issues", CommandClass.ReadOnly), ("create_issue", CommandClass.Change)));
        Assert.Equal((1, 1, 1), (second.New, second.Gone, second.Unchanged));
        rows = store.McpCommands(s).ToDictionary(r => r.Path);
        Assert.Equal("new", rows["create_issue"].Badge);
        Assert.Null(rows["list_issues"].Badge);
        Assert.True(rows["delete_repo"].Gone);

        // a found row is not marked gone, and a listing that has it makes it discovered
        store.UpsertMcpRow(s, "pin_issue", "Pins one.", new Classification(CommandClass.Change, ClassSource.None, "found"), RowOrigin.Found);
        store.MergeMcp(s, Tools("list_issues", "create_issue"), Classes(("list_issues", CommandClass.ReadOnly), ("create_issue", CommandClass.Change)));
        rows = store.McpCommands(s).ToDictionary(r => r.Path);
        Assert.False(rows["pin_issue"].Gone);
        Assert.Equal(RowOrigin.Found, rows["pin_issue"].Origin);
        store.MergeMcp(s, Tools("list_issues", "pin_issue"), Classes(("list_issues", CommandClass.ReadOnly), ("pin_issue", CommandClass.Change)));
        Assert.Equal(RowOrigin.Discovered, store.McpCommands(s).Single(r => r.Path == "pin_issue").Origin);
    }

    [Fact]
    public async Task RemovingAnMcpServerRemovesItsRowsAndChoices()
    {
        using var db = new TempDb();
        await using var engine = new Yawble.Core.Bus.BusEngine(db.OpenStore(), new ScriptedRunner(), new Yawble.Core.Bus.BusOptions { IdleOffer = false });
        var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var store = new DiscoverStore(db.ConnectionString, TimeProvider.System);
        long s;
        using (var c = store.Open())
            s = Convert.ToInt64(DiscoverStore.Scalar(c, null, "INSERT INTO mcp_servers (name, command, created_at, updated_at) VALUES ('fake', 'node', $n, $n) RETURNING id;", ("$n", Now)));
        store.MergeMcp(s, Tools("echo"), Classes(("echo", CommandClass.ReadOnly)));
        using (var c = store.Open())
        {
            using var tx = c.BeginTransaction();
            store.SetMcpChoice(c, tx, s, null, "echo", Decisions.Allow, Now);
            tx.Commit();
        }
        Assert.Single(store.McpCommands(s));
        Assert.Single(store.McpChoices(s));
        using (var c = store.Open()) Assert.Equal(1L, DiscoverStore.Scalar(c, null, "SELECT count(*) FROM mcp_rules WHERE pattern = 'echo' AND decision = 'allow';"));

        await integrations.DispatchAsync("integrations.mcpRemove", new JsonObject { ["name"] = "fake" }, Ct);
        Assert.Empty(store.McpCommands(s));
        Assert.Empty(store.McpChoices(s));
    }
}
