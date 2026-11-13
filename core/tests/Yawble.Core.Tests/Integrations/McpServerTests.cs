using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>MCP servers as integrations: a local stdio server's tools offered to a team, under rules, with approvals.</summary>
public class McpServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A small MCP server in Node: echo, and delete_all (which only says what it would do).</summary>
    private const string FakeServer = """
        const rl = require('readline').createInterface({ input: process.stdin });
        const send = (m) => process.stdout.write(JSON.stringify(m) + '\n');
        console.error('fake server starting');
        rl.on('line', (line) => {
          const m = JSON.parse(line);
          if (m.method === 'initialize') send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: m.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'fake', version: '1' } } });
          else if (m.method === 'tools/list') send({ jsonrpc: '2.0', id: m.id, result: { tools: [
            { name: 'echo', description: 'Says it back.', inputSchema: { type: 'object', properties: { msg: { type: 'string' } }, required: ['msg'] } },
            { name: 'delete_all', description: 'Deletes everything.', inputSchema: { type: 'object', properties: {} } } ] } });
          else if (m.method === 'tools/call') send({ jsonrpc: '2.0', id: m.id, result: { content: [{ type: 'text',
            text: m.params.name === 'echo' ? 'echo: ' + m.params.arguments.msg + (m.params.arguments._reason ? ' LEAKED' : '') : 'would delete everything' }] } });
        });
        """;

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
    public async Task AnMcpServersToolsAreOfferedUnderRules_AndAnApprovedCallRuns()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        using var db = new TempDb();
        var script = Path.Combine(db.Dir, "fake-mcp.js");
        File.WriteAllText(script, FakeServer);
        string? echoed = null, asked = null;
        string[]? tools = null;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (run.Items[0].Payload.Contains("approved")) return RunResult.Ok;
            tools = (await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), ct)) is var r
                ? JsonNode.Parse(r.Json!)!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray() : null;
            echoed = (await Call(run, "fake__echo", new { msg = "hi", _reason = "testing" })).Text;
            asked = (await Call(run, "fake__delete_all", new { _reason = "clean up" })).Text;
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await using var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await integrations.DispatchAsync("integrations.mcpSave", new JsonObject { ["name"] = "fake", ["command"] = "node", ["args"] = new JsonArray(script) }, Ct);
        var check = await integrations.DispatchAsync("integrations.mcpCheck", new JsonObject { ["name"] = "fake" }, Ct);
        Assert.Equal(2, check!["tools"]!.GetValue<int>());
        await integrations.DispatchAsync("integrations.mcpEnable", new JsonObject { ["name"] = "fake", ["teamId"] = alpha, ["on"] = true }, Ct);
        await integrations.DispatchAsync("integrations.mcpSaveRule", new JsonObject { ["name"] = "fake", ["pattern"] = "echo", ["decision"] = "allow" }, Ct);
        await engine.StartAsync(Ct);
        var root = await engine.PersonTellAsync("Alpha", "Ada", "use the tools", ct: Ct);
        await Wait.UntilAsync(() => asked is not null, "Ada's calls");

        Assert.Contains("fake__echo", tools!);
        Assert.Contains("fake__delete_all", tools!);
        Assert.Equal("echo: hi", echoed); // allowed, and the reason never reaches the server
        Assert.StartsWith("asked:", asked); // nothing said about delete_all: ask
        var ask = Assert.Single(integrations.Asks(alpha, pendingOnly: true));
        Assert.Equal("mcp:fake", ask.Integration);

        await integrations.DispatchAsync("integrations.approve", new JsonObject { ["id"] = ask.Id }, Ct);
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "Ada told the result");
        using var store = db.OpenStore();
        var told = store.Workflow(root.Seq).Last(m => m.Type == MessageTypes.InstructionPrefix + "Alpha/Ada");
        Assert.Contains("would delete everything", Payload.Field(told, "text"));
    }
}
