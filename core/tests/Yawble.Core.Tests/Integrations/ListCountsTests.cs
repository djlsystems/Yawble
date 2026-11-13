using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>The list items carry `counts` (from the default checklist) and `configured`, and never fail for an integration that was not set up.</summary>
public class ListCountsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> CliItem(IntegrationService s, string name)
    {
        var list = (await s.DispatchAsync("integrations.list", null, Ct))!["integrations"]!.AsArray();
        return list.Single(i => i!["name"]!.GetValue<string>() == name)!.AsObject();
    }

    [Fact]
    public async Task CliList_GivesCountsForADiscoveredCli_NullForOneNotSetUp_AndStillSucceeds()
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await using var s = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = s;

        var before = await CliItem(s, "gh"); // a catalog CLI nobody added
        Assert.Null(before["counts"]);
        Assert.False(before["configured"]!.GetValue<bool>());

        var o = await s.Discover.DiscoverAsync("gh", new DiscoverOptions
        {
            Runner = new FixtureCli("gh/2.45").Runner, Version = "gh version 2.45.0", Walk = new WalkOptions { Parallelism = 4, CallTimeout = TimeSpan.FromSeconds(2) },
        }, null, Ct);
        Assert.Null(o.Error);

        var gh = await CliItem(s, "gh");
        Assert.True(gh["configured"]!.GetValue<bool>());
        var want = s.Discover.GetChecklist("gh", null).Counts;
        Assert.True(want.Allowed + want.Ask + want.Off + want.Undecided > 0);
        var counts = gh["counts"]!.AsObject();
        Assert.Equal(
            new[] { want.Allowed, want.Ask, want.Off, want.Undecided },
            new[] { counts["allowed"]!.GetValue<int>(), counts["ask"]!.GetValue<int>(), counts["off"]!.GetValue<int>(), counts["undecided"]!.GetValue<int>() });

        await s.DispatchAsync("integrations.remove", new JsonObject { ["name"] = "gh" }, Ct);
        Assert.False((await CliItem(s, "gh"))["configured"]!.GetValue<bool>());
    }

    [Fact]
    public async Task McpList_GivesNullCounts_ForAServerNeverDiscovered()
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await using var s = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = s;
        await s.DispatchAsync("integrations.mcpSave", new JsonObject { ["name"] = "never", ["command"] = "node", ["args"] = new JsonArray() }, Ct);

        var list = (await s.DispatchAsync("integrations.mcpList", null, Ct))!.AsArray();
        var item = list.Single()!.AsObject();
        Assert.Equal("never", item["name"]!.GetValue<string>());
        Assert.Null(item["counts"]); // never discovered: null, and the list itself succeeds
    }
}
