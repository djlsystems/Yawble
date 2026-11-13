using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Watches and event triggers: a new item a watch finds becomes an event; a matching trigger tells its member.</summary>
public class EventTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANewItemBecomesAnEvent_AndAMatchingEventTriggerTellsItsMember()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        using var db = new TempDb();
        var items = Path.Combine(db.Dir, "items.json");
        var script = Path.Combine(db.Dir, "list.js");
        File.WriteAllText(script, $"process.stdout.write(require('fs').readFileSync({System.Text.Json.JsonSerializer.Serialize(items)}, 'utf8'));");
        File.WriteAllText(items, """[{"number": 1, "title": "Old one"}]""");

        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await using var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await integrations.DispatchAsync("integrations.enable", new JsonObject { ["name"] = "node", ["teamId"] = alpha, ["on"] = true }, Ct);

        // A watch's command must be allowed without asking: refused until a rule allows it.
        var refused = await Assert.ThrowsAsync<Ipc.RpcException>(() => integrations.DispatchAsync("integrations.saveWatch", new JsonObject
        {
            ["teamId"] = alpha, ["name"] = "prs", ["integration"] = "node", ["args"] = new JsonArray(script), ["keyField"] = "number", ["eventName"] = "web.pr", ["intervalSec"] = 60,
        }, Ct));
        Assert.Contains("nobody there", refused.Message);
        await integrations.DispatchAsync("integrations.saveRule", new JsonObject { ["name"] = "node", ["teamId"] = alpha, ["pattern"] = "*list.js", ["decision"] = "allow" }, Ct);
        await integrations.DispatchAsync("integrations.saveWatch", new JsonObject
        {
            ["teamId"] = alpha, ["name"] = "prs", ["integration"] = "node", ["args"] = new JsonArray(script), ["keyField"] = "number", ["eventName"] = "web.pr", ["intervalSec"] = 60,
        }, Ct);
        var t = await engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "review new PRs", new TriggerSchedule(TriggerKinds.Event, null, null, null, null),
            "Review the pull request.", false, true, EventName: "web.*"), Ct);
        Assert.Null(t.NextDueAt); // never on a schedule

        var watch = Assert.Single(integrations.Watches(alpha));
        Assert.Equal(0, await integrations.LookAsync(watch, Ct)); // the first look only remembers
        File.WriteAllText(items, """[{"number": 1, "title": "Old one"}, {"number": 2, "title": "Add the farewell", "url": "https://example.test/pr/2"}]""");
        Assert.Equal(1, await integrations.LookAsync(Assert.Single(integrations.Watches(alpha)), Ct));
        Assert.Equal(0, await integrations.LookAsync(Assert.Single(integrations.Watches(alpha)), Ct)); // seen now

        using var store = db.OpenStore();
        var row = store.Trigger(t.Id)!;
        Assert.Equal("fired", row.LastOutcome);
        var text = Payload.Field(store.Find(row.LastSeq!.Value)!, "text")!;
        Assert.Contains("Review the pull request.", text);
        Assert.Contains("Event web.pr", text);
        Assert.Contains("title: Add the farewell", text);
        Assert.Single(store.AllMessages(), m => m.Type == BusEngine.EventPrefix + "web.pr");
    }
}
