using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>The agent tools added from Yawble's list: lease, board edit, backlog list and add.</summary>
public class AgentToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(string Text, bool Error)> Call(RunContext run, string tool, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TheLeaseIsOneAtATime_NeverBlocks_AndARunThatEndsHandsItToTheQueue()
    {
        var adaHolds = new TaskCompletionSource();
        var releaseAda = new TaskCompletionSource();
        string? benFirst = null, benAfter = null;
        var runner = new ScriptedRunner()
            .On("Alpha/Ada", async (run, ct) =>
            {
                Assert.StartsWith("ok: you hold the heavy-work lease", (await Call(run, "lease", new { action = "acquire", what = "full test suite" })).Text);
                adaHolds.SetResult();
                await releaseAda.Task; // ends without releasing
                return RunResult.Ok;
            })
            .On("Alpha/Ben", async (run, ct) =>
            {
                benFirst = (await Call(run, "lease", new { action = "acquire", what = "build" })).Text;
                await Wait.UntilAsync(() => benAfter is not null || releaseAda.Task.IsCompleted, "Ada's run to end");
                await Task.Delay(300, ct);
                benAfter = (await Call(run, "lease", new { action = "acquire", what = "build" })).Text;
                return RunResult.Ok;
            });
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ben", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "run the tests", ct: Ct);
        await adaHolds.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await engine.PersonTellAsync("Alpha", "Ben", "build it", ct: Ct);
        await Wait.UntilAsync(() => benFirst is not null, "Ben asks");
        Assert.StartsWith("queued, position 1; Alpha/Ada holds it for \"full test suite\"", benFirst);
        releaseAda.SetResult();
        await Wait.UntilAsync(() => benAfter is not null, "Ben asks again");
        Assert.StartsWith("ok: you hold the heavy-work lease", benAfter);
    }

    [Fact]
    public async Task TheManagerEditsACard_ListsAndAddsBacklogItems_AndAMemberMayNot()
    {
        using var db = new TempDb();
        var boardStore = new BoardStore(db.ConnectionString);
        string? planned = null, edited = null, added = null, listed = null, memberAdd = null;
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                planned = (await Call(run, "board", new { action = "plan", title = "Login form", body = "the form" })).Text;
                var id = long.Parse(System.Text.RegularExpressions.Regex.Match(planned, @"card (\d+)").Groups[1].Value);
                edited = (await Call(run, "board", new { action = "edit", card = id, title = "Login form with remember-me" })).Text;
                added = (await Call(run, "backlog", new { action = "add", title = "Rate-limit the login", body = "seen while building the form" })).Text;
                listed = (await Call(run, "backlog", new { })).Text;
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) =>
            {
                memberAdd = (await Call(run, "backlog", new { action = "add", title = "mine" })).Text;
                return RunResult.Ok;
            });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        engine.Board = new BoardService(boardStore, engine);
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Manager", "plan the login", ct: Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "anything", ct: Ct);
        await Wait.UntilAsync(() => listed is not null && memberAdd is not null, "both runs");

        Assert.StartsWith("ok: card", edited);
        Assert.Equal("Login form with remember-me", Assert.Single(boardStore.Cards("Alpha"), c => c.Member is null).Title);
        Assert.StartsWith("ok: added B001", added);
        Assert.Contains("B001 [pending] Rate-limit the login", listed);
        Assert.Equal("Alpha", Assert.Single(boardStore.Items()).Team);
        Assert.Contains("only the Manager", memberAdd);
    }
}
