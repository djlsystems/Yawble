using System.Text.Json;
using Yawble.Core.Bus;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>A member's last report (its card's status line) survives a restart: the engine seeds it from the log.</summary>
public class StatusLineRestartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task ANewEngineSeedsEachMembersStatusLineFromItsLastReport()
    {
        using var db = new TempDb();
        long teamId;
        await using (var first = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false }))
        {
            teamId = await first.CreateTeamAsync("Alpha", ct: Ct);
            foreach (var m in new[] { "Ada", "Ben", "Cy", "Di", "Ed" }) await first.AddMemberAsync("Alpha", m, ct: Ct);
        }
        using (var store = db.OpenStore())
        {
            store.Append(new NewMessage(MessageTypes.Progress, Json(new { status = "reading" }), "Alpha/Ada"));
            store.Append(new NewMessage(MessageTypes.Handback, Json(new { delivered = "the report" }), "Alpha/Ada"));
            store.Append(new NewMessage(MessageTypes.Progress, Json(new { status = "polishing" }), "Alpha/Ada"));
            store.Append(new NewMessage(MessageTypes.Progress, Json(new { status = "early" }), "Alpha/Ben"));
            store.Append(new NewMessage(MessageTypes.Blocked, Json(new { reason = "no key", item = (int?)null, instruction = (string?)null }), "Alpha/Ben"));
            store.Append(new NewMessage(MessageTypes.Completed, Json(new { runId = 1 }), "Alpha/Ben")); // not a status kind
            store.Append(new NewMessage(MessageTypes.WorkflowCompleted, Json(new { delivered = "all done", declaredBy = "Alpha/Manager", onBehalfOf = (string?)null }), "Alpha/Manager"));
            store.Append(new NewMessage(MessageTypes.Handback, Json(new { delivered = "the fix" }), "Alpha/Cy"));
            store.Append(new NewMessage(MessageTypes.NeedsDecision, Json(new { question = "which db?" }), "Alpha/Di"));
            store.Append(new NewMessage(MessageTypes.Progress, Json(new { status = "other team" }), "Beta/Ed"));
        }

        await using var second = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await second.StartAsync(Ct);
        var cards = (await second.AgentCardsAsync(teamId, Ct)).ToDictionary(c => c.Member.Name);

        Assert.Equal("polishing", cards["Ada"].StatusLine);
        Assert.Equal("blocked: no key", cards["Ben"].StatusLine);
        Assert.Equal("handed back: the fix", cards["Cy"].StatusLine);
        Assert.Equal("needs decision: which db?", cards["Di"].StatusLine);
        Assert.Equal("declared complete: all done", cards["Manager"].StatusLine);
        Assert.Null(cards["Ed"].StatusLine);
    }

    [Fact]
    public async Task AMemberMadeAgainUnderTheSameNameDoesNotInheritTheOldReport()
    {
        using var db = new TempDb();
        long teamId;
        await using (var first = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false }))
        {
            teamId = await first.CreateTeamAsync("Alpha", ct: Ct);
            var ada = await first.AddMemberAsync("Alpha", "Ada", ct: Ct);
            using (var store = db.OpenStore())
                store.Append(new NewMessage(MessageTypes.Progress, Json(new { status = "old" }), "Alpha/Ada"));
            await Task.Delay(20, Ct);
            await first.RemoveMemberAsync(ada.Id, Ct);
            await first.AddMemberAsync("Alpha", "Ada", ct: Ct);
        }

        await using var second = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await second.StartAsync(Ct);
        var card = Assert.Single(await second.AgentCardsAsync(teamId, Ct), c => c.Member.Name == "Ada");
        Assert.Null(card.StatusLine);
    }
}
