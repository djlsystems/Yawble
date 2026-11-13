using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Tasks;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Tasks;

/// <summary>Host tasks: the agent's two tools, the person-only tasks.* RPCs, and the ask / approve / wake path through the bus.</summary>
public class HostTaskToolsTests
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

    private static async Task<JsonArray> ToolList(RunContext run)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        return JsonNode.Parse(reply.Json!)!["result"]!["tools"]!.AsArray();
    }

    // ------------------------------------------------------------------ test 10: schema and refusals

    [Fact]
    public async Task TaskRunHasExactlyNameAndReason_AndOnlyTeamsWithTasksSeeTheTools()
    {
        using var f = new TaskFixture();
        await using var engine = new BusEngine(f.Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        var integrations = new IntegrationService(f.Db.ConnectionString, engine) { HostTasks = new HostTaskRpc(f.Svc, _ => ["main"]) };

        Assert.DoesNotContain(integrations.ToolsFor(f.TeamId), t => t["name"]!.GetValue<string>() is "task_run" or "task_list"); // no task yet
        await f.SaveAsync(f.Def());
        var tools = integrations.ToolsFor(f.TeamId);
        var run = tools.Single(t => t["name"]!.GetValue<string>() == "task_run");
        var schema = run["inputSchema"]!.AsObject();
        Assert.Equal(["name", "reason"], schema["properties"]!.AsObject().Select(p => p.Key).Order().ToArray());
        Assert.Equal(["name", "reason"], schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order().ToArray());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Empty(tools.Single(t => t["name"]!.GetValue<string>() == "task_list")["inputSchema"]!["properties"]!.AsObject());
    }

    [Fact]
    public async Task TheTasksRpcsRefuseEveryActorButTheWindowAndACloudViewer()
    {
        using var f = new TaskFixture();
        var rpc = new HostTaskRpc(f.Svc, _ => ["main"]);
        var def = new JsonObject
        {
            ["teamId"] = f.TeamId,
            ["def"] = new JsonObject { ["name"] = "x", ["repo"] = "main", ["program"] = "git", ["args"] = new JsonArray("status"), ["workdir"] = ".", ["timeoutSeconds"] = 60, ["pinMode"] = "commit" },
        };
        var writes = new[] { "tasks.save", "tasks.delete", "tasks.revoke", "tasks.runNow", "tasks.cancel", "tasks.approve", "tasks.decline", "tasks.preview", "tasks.list", "tasks.asks", "tasks.runs" };
        var refused = new List<Actor?> { null, new Actor(Actor.Agent, null), new Actor(Actor.Concierge, "me"), new Actor(Actor.Trigger, null), new Actor(Actor.Host, null) };
        foreach (var actor in refused)
        {
            foreach (var method in writes)
            {
                using var _ = actor is null ? null : Actor.Use(actor);
                var e = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync(method, def, Ct));
                Assert.Equal("forbidden", e.Code);
            }
        }
        Assert.Empty(f.Svc.Store.Tasks(f.TeamId)); // nothing was written

        using (Actor.Use(Actor.Window))
        {
            await rpc.DispatchAsync("tasks.save", def, Ct);
            Assert.Single(f.Svc.Store.Tasks(f.TeamId));
        }
        using (Actor.Use(Actor.CloudViewer("me")))
            Assert.NotNull(await rpc.DispatchAsync("tasks.list", new JsonObject { ["teamId"] = f.TeamId }, Ct));
    }

    // ------------------------------------------------------------------ the ask / approve / wake path

    [Fact]
    public async Task ARunRequestAsksThePerson_OnceApprovedItRuns_AndTheAgentIsWokenWithTheResult()
    {
        using var f = new TaskFixture();
        string? listed = null, extra = null, first = null, again = null, noReason = null;
        string[]? names = null;
        var runner = new ScriptedRunner().On("Beta/Ada", async (run, ct) =>
        {
            if (run.Items[0].Payload.Contains("exit ")) return RunResult.Ok; // woken with the result
            names = (await ToolList(run)).Select(t => t!["name"]!.GetValue<string>()).ToArray();
            listed = (await Call(run, "task_list", new { })).Text;
            extra = (await Call(run, "task_run", new { name = "build", reason = "r", args = new[] { "--evil" } })).Text;
            noReason = (await Call(run, "task_run", new { name = "build" })).Text;
            first = (await Call(run, "task_run", new { name = "build", reason = "check my change" })).Text;
            again = (await Call(run, "task_run", new { name = "build", reason = "check my change" })).Text;
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(f.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        var tasks = new HostTaskService(f.Db.ConnectionString, f.Git, (_, name) => name == "main" ? new TaskRepoInfo(f.Repo, "main") : null, _ => f.Docs, async w =>
        {
            var slash = w.Member.IndexOf('/');
            await engine.PersonTellAsync(w.Member[..slash], w.Member[(slash + 1)..], w.Text, w.CausationSeq);
        });
        var integrations = new IntegrationService(f.Db.ConnectionString, engine) { HostTasks = new HostTaskRpc(tasks, _ => ["main"]) };
        engine.Integrations = integrations;
        var team = await engine.CreateTeamAsync("Beta", ct: Ct);
        await engine.AddMemberAsync("Beta", "Ada", ct: Ct);
        await tasks.SaveAsync(team, f.Def(), Ct);
        await engine.StartAsync(Ct);
        var root = await engine.PersonTellAsync("Beta", "Ada", "run the build", ct: Ct);
        await Wait.UntilAsync(() => again is not null, "Ada's calls");

        Assert.Contains("task_run", names!);
        Assert.Contains("task_list", names!);
        Assert.Contains("\"name\": \"build\"", listed);
        Assert.Contains("\"approval\": \"asks\"", listed);
        Assert.DoesNotContain("--evil", listed);
        Assert.Contains("takes only 'name' and 'reason'", extra);
        Assert.Contains("reason", noReason);
        Assert.StartsWith("asked: task build waits for the person", first);
        Assert.Contains("already waits", again);

        // The team page lists it with what the person is shown.
        var asks = (JsonArray)(await integrations.DispatchAsync("integrations.asks", new JsonObject { ["teamId"] = team }, Ct))!;
        var ask = Assert.Single(asks)!;
        Assert.True(ask["task"]!.GetValue<bool>());
        Assert.Equal("check my change", ask["reason"]!.GetValue<string>());
        Assert.NotNull(ask["detail"]!["pinned"]);

        long askId = ask["id"]!.GetValue<long>();
        using (Actor.Use(Actor.Window))
            await integrations.DispatchAsync("integrations.approve", new JsonObject { ["id"] = askId, ["always"] = true }, Ct);
        using var store = f.Db.OpenStore();
        await Wait.UntilAsync(() => store.Workflow(root.Seq).Any(m => m.Type == MessageTypes.InstructionPrefix + "Beta/Ada" && Payload.Field(m, "text")?.Contains("task build: exit") == true),
            "Ada woken with the result");
        var told = store.Workflow(root.Seq).Last(m => m.Type == MessageTypes.InstructionPrefix + "Beta/Ada");
        Assert.Contains("task build: exit 0", Payload.Field(told, "text"));
        Assert.Contains("hello", Payload.Field(told, "text"));
        var record = Assert.Single(tasks.Store.Runs(tasks.Store.Task(team, "build")!.Id));
        Assert.Equal("Beta/Ada", record.RequestedBy);
        Assert.Equal("always", await tasks.ApprovalStateAsync(tasks.Store.Task(team, "build")!, Ct));

        var calls = (JsonArray)(await integrations.DispatchAsync("integrations.calls", null, Ct))!;
        Assert.Contains(calls, c => c!["integration"]!.GetValue<string>() == "task:build" && c["decision"]!.GetValue<string>() == "approved");
    }
}
