using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>An MCP server's tools get the same checklist as a CLI, classified from the tool annotations.</summary>
public class McpChecklistTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Like McpServerTests' fake server, but its tools/list is read from a JSON file (argv[2]) on every call, so a test can add a tool.</summary>
    private const string FakeServer = """
        const fs = require('fs');
        const rl = require('readline').createInterface({ input: process.stdin });
        const send = (m) => process.stdout.write(JSON.stringify(m) + '\n');
        rl.on('line', (line) => {
          const m = JSON.parse(line);
          if (m.method === 'initialize') send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: m.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'fake', version: '1' } } });
          else if (m.method === 'tools/list') send({ jsonrpc: '2.0', id: m.id, result: { tools: JSON.parse(fs.readFileSync(process.argv[2], 'utf8')) } });
          else if (m.method === 'tools/call') send({ jsonrpc: '2.0', id: m.id, result: { content: [{ type: 'text', text: 'ran ' + m.params.name }] } });
        });
        """;

    private static JsonArray Tools(params string[] names) => new(names.Select(n => (JsonNode?)new JsonObject
    {
        ["name"] = n, ["description"] = "does " + n, ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        ["annotations"] = n == "get_thing" ? new JsonObject { ["readOnlyHint"] = true } : null,
    }).ToArray());

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Integrations = null!;
        public string ToolsFile = "";
        public long Alpha;

        public static async Task<Rig> Make(ScriptedRunner? runner = null, params string[] tools)
        {
            var r = new Rig();
            r.ToolsFile = Path.Combine(r.Db.Dir, "tools.json");
            File.WriteAllText(r.ToolsFile, Tools(tools).ToJsonString());
            var script = Path.Combine(r.Db.Dir, "fake-mcp.js");
            File.WriteAllText(script, FakeServer);
            r.Engine = new BusEngine(r.Db.OpenStore(), runner ?? new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            r.Integrations = new IntegrationService(r.Db.ConnectionString, r.Engine);
            r.Engine.Integrations = r.Integrations;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "fake", ["command"] = "node", ["args"] = new JsonArray(script, r.ToolsFile) });
            return r;
        }

        public async Task<JsonNode?> Rpc(string method, JsonObject p) => await Integrations.DispatchAsync(method, p, Ct);

        public void SetTools(params string[] names) => File.WriteAllText(ToolsFile, Tools(names).ToJsonString());

        public async Task Discover()
        {
            var s = await Rpc("integrations.discover", new JsonObject { ["name"] = "fake", ["kind"] = "mcp" });
            Assert.True(s!["started"]!.GetValue<bool>());
            await Wait.UntilAsync(async () => (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "fake", ["kind"] = "mcp" }))!["state"]!.GetValue<string>() != "running", "the tools were listed");
            var done = await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "fake", ["kind"] = "mcp" });
            Assert.Equal("done", done!["state"]!.GetValue<string>());
        }

        public ChecklistNode Node(long? team, string tool) => Integrations.Discover.GetMcpChecklist("fake", team).Nodes.Single(n => n.Path == tool);

        public long ServerId() => Scalar("SELECT id FROM mcp_servers WHERE name = 'fake'");

        public long Scalar(string sql)
        {
            using var c = new SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public async ValueTask DisposeAsync()
        {
            await Integrations.DisposeAsync();
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static JsonObject Tool(string name, JsonObject? annotations = null)
    {
        var t = new JsonObject { ["name"] = name };
        if (annotations is not null) t["annotations"] = annotations;
        return t;
    }

    [Fact]
    public void Classify_UsesAnnotationsThenTheVerb_AndTheFloorFirst()
    {
        var ro = McpClassifier.Classify(Tool("anything", new JsonObject { ["readOnlyHint"] = true }));
        Assert.Equal(new[] { CommandClass.ReadOnly, ClassSource.Annotation }, new[] { ro.Class, ro.Source });
        var d = McpClassifier.Classify(Tool("anything", new JsonObject { ["destructiveHint"] = true }));
        Assert.Equal(new[] { CommandClass.Destructive, ClassSource.Annotation }, new[] { d.Class, d.Source });
        var ch = McpClassifier.Classify(Tool("create_issue"));
        Assert.Equal(new[] { CommandClass.Change, ClassSource.Verb }, new[] { ch.Class, ch.Source });
        var ls = McpClassifier.Classify(Tool("list-issues"));
        Assert.Equal(new[] { CommandClass.ReadOnly, ClassSource.Verb }, new[] { ls.Class, ls.Source });
        var cred = McpClassifier.Classify(Tool("get_api_key", new JsonObject { ["readOnlyHint"] = true }));
        Assert.Equal(new[] { CommandClass.Credential, ClassSource.Floor }, new[] { cred.Class, cred.Source });
        Assert.Equal(CommandClass.Unknown, McpClassifier.Classify(Tool("frobnicate")).Class);
        // destructive beats read-only when both hints are set; a hint that is not a boolean is ignored
        Assert.Equal(CommandClass.Destructive, McpClassifier.Classify(Tool("x", new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = true })).Class);
        Assert.Equal(CommandClass.Unknown, McpClassifier.Classify(Tool("x", new JsonObject { ["readOnlyHint"] = "yes" })).Class);
        Assert.Equal(CommandClass.Unknown, McpClassifier.Classify(new JsonObject { ["name"] = 5 }).Class);
    }

    [Fact]
    public async Task Mcp_Discover_FillsRows_WithSuggestedStates()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        await using var rig = await Rig.Make(null, "get_thing", "create_thing", "delete_thing");
        await rig.Discover();
        var cl = rig.Integrations.Discover.GetMcpChecklist("fake", null);
        Assert.Equal("mcp", cl.Kind);
        Assert.Equal(1, cl.Runs);
        Assert.All(cl.Nodes, n => Assert.Equal("", n.ParentPath));
        var get = rig.Node(null, "get_thing");
        Assert.Equal(RowStates.Allow, get.Suggested);
        Assert.True(get.DefaultTick);
        Assert.True(get.Ticked);
        Assert.Equal(ClassSource.Annotation, get.Source);
        Assert.Equal(RowStates.Ask, rig.Node(null, "create_thing").Suggested);
        Assert.Equal(RowStates.Off, rig.Node(null, "delete_thing").Suggested);
        Assert.Equal(Decisions.Ask, get.Effective); // nothing saved: the call still asks
        Assert.Equal(3, cl.Counts.Undecided);
        // the JSON carries the kind, for the shell
        var json = await rig.Rpc("integrations.checklist", new JsonObject { ["name"] = "fake", ["kind"] = "mcp" });
        Assert.Equal("mcp", json!["kind"]!.GetValue<string>());
        Assert.Equal(3, json["nodes"]!.AsArray().Count);
    }

    [Fact]
    public async Task McpList_CountsEqualTheDefaultChecklistTotals_AfterDiscoverAndSave()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        await using var rig = await Rig.Make(null, "get_thing", "create_thing", "delete_thing");
        Assert.Null((await rig.Rpc("integrations.mcpList", new JsonObject()))!.AsArray().Single()!["counts"]); // before Discover
        await rig.Discover();
        await rig.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "fake", ["kind"] = "mcp",
            ["choices"] = new JsonArray(
                new JsonObject { ["path"] = "get_thing", ["state"] = "allow" },
                new JsonObject { ["path"] = "create_thing", ["state"] = "ask" }),
        });
        var want = rig.Integrations.Discover.GetMcpChecklist("fake", null).Counts;
        Assert.True(want.Allowed >= 1 && want.Ask >= 1);
        var counts = (await rig.Rpc("integrations.mcpList", new JsonObject()))!.AsArray().Single()!["counts"]!.AsObject();
        Assert.Equal(
            new[] { want.Allowed, want.Ask, want.Off, want.Undecided },
            new[] { counts["allowed"]!.GetValue<int>(), counts["ask"]!.GetValue<int>(), counts["off"]!.GetValue<int>(), counts["undecided"]!.GetValue<int>() });
    }

    [Fact]
    public async Task Mcp_SaveChoices_ThenTheAgentsCallFollowsThem()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        string? off = null, asked = null, allowed = null;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            async Task<string> Call(string tool)
            {
                var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject { ["_reason"] = "testing" } } };
                var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), ct);
                return JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
            }
            off = await Call("fake__delete_thing");
            asked = await Call("fake__create_thing");
            allowed = await Call("fake__get_thing");
            return RunResult.Ok;
        });
        await using var rig = await Rig.Make(runner, "get_thing", "create_thing", "delete_thing");
        await rig.Discover();
        await rig.Rpc("integrations.mcpEnable", new JsonObject { ["name"] = "fake", ["teamId"] = rig.Alpha, ["on"] = true });
        var save = await rig.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "fake", ["kind"] = "mcp",
            ["choices"] = new JsonArray(
                new JsonObject { ["path"] = "delete_thing", ["state"] = "off" },
                new JsonObject { ["path"] = "create_thing", ["state"] = "ask" },
                new JsonObject { ["path"] = "get_thing", ["state"] = "allow" }),
        });
        Assert.Equal(1, save!["allowed"]!.GetValue<int>());
        Assert.Equal(1, save["denied"]!.GetValue<int>());
        Assert.Equal(3, rig.Scalar("SELECT count(*) FROM mcp_rules WHERE server_id = " + rig.ServerId()));
        Assert.Equal(Decisions.Deny, rig.Node(null, "delete_thing").Effective);
        Assert.Equal(Decisions.Allow, rig.Node(null, "get_thing").Effective);

        await rig.Engine.StartAsync(Ct);
        await rig.Engine.PersonTellAsync("Alpha", "Ada", "use the tools", ct: Ct);
        await Wait.UntilAsync(() => allowed is not null, "Ada's calls");
        Assert.StartsWith("refused:", off);
        Assert.StartsWith("asked:", asked);
        Assert.Equal("ran get_thing", allowed);
    }

    [Fact]
    public async Task Mcp_SaveChoices_KeepsTheSameRules_AsTheCliOnes()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        await using var rig = await Rig.Make(null, "get_thing", "create_thing", "get_api_key");
        await rig.Discover();
        var svc = rig.Integrations.Discover;
        Assert.Equal(CommandClass.Credential, rig.Node(null, "get_api_key").Class);
        Assert.Equal("locked", rig.Node(null, "get_api_key").State);
        Assert.Equal(RowStates.Never, rig.Node(null, "get_api_key").Suggested);
        Assert.Throws<ArgumentException>(() => svc.SaveMcpChoices("fake", null, [new RowChoice("get_api_key", "allow")]));
        Assert.Throws<ArgumentException>(() => svc.SaveMcpChoices("fake", null, [new RowChoice("get_thing", "never")]));
        Assert.Throws<ArgumentException>(() => svc.SaveMcpChoices("fake", null, [new RowChoice("nope", "allow")]));
        Assert.Throws<ArgumentException>(() => svc.SaveMcpChoices("fake", null, [new RowChoice("get_thing", "default")]));
        Assert.Throws<ArgumentException>(() => svc.SaveMcpChoices("fake", rig.Alpha, [new RowChoice("get_thing", "undecided")]));
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM mcp_rules")); // a refused save writes nothing

        // a team override, then a reset to default
        svc.SaveMcpChoices("fake", null, [new RowChoice("create_thing", "ask")]);
        svc.SaveMcpChoices("fake", rig.Alpha, [new RowChoice("create_thing", "off")]);
        Assert.Equal(Decisions.Deny, rig.Node(rig.Alpha, "create_thing").Effective);
        Assert.Equal(Decisions.Ask, rig.Node(null, "create_thing").Effective);
        Assert.Equal(Decisions.Deny, rig.Node(rig.Alpha, "create_thing").TeamDecision);
        svc.SaveMcpChoices("fake", rig.Alpha, [new RowChoice("create_thing", "default")]);
        Assert.Null(rig.Node(rig.Alpha, "create_thing").TeamDecision);
        // a team state equal to the default is not stored
        svc.SaveMcpChoices("fake", rig.Alpha, [new RowChoice("create_thing", "ask")]);
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM mcp_choices"));
        // undecided removes the default choice and its rule
        svc.SaveMcpChoices("fake", null, [new RowChoice("create_thing", "undecided")]);
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM mcp_rules"));

        // the team must be offered the server (or already have choices on it)
        var ex = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => rig.Rpc("integrations.checklist", new JsonObject { ["name"] = "fake", ["kind"] = "mcp", ["teamId"] = rig.Alpha }));
        Assert.Contains("not offered", ex.Message);
        await rig.Rpc("integrations.mcpEnable", new JsonObject { ["name"] = "fake", ["teamId"] = rig.Alpha, ["on"] = true });
        Assert.NotNull(await rig.Rpc("integrations.checklist", new JsonObject { ["name"] = "fake", ["kind"] = "mcp", ["teamId"] = rig.Alpha }));
    }

    [Fact]
    public async Task Mcp_ReDiscover_AddsANewToolUndecided()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        await using var rig = await Rig.Make(null, "get_thing", "create_thing");
        await rig.Discover();
        rig.Integrations.Discover.SaveMcpChoices("fake", null, [new RowChoice("get_thing", "allow")]);
        Assert.Null(rig.Node(null, "get_thing").Badge); // the first run marks nothing new
        rig.SetTools("get_thing", "create_thing", "list_other");
        await rig.Discover();
        var added = rig.Node(null, "list_other");
        Assert.Equal("new", added.Badge);
        Assert.Null(added.DefaultDecision);
        Assert.False(added.Ticked); // even though its verb says read-only: new rows wait for the person
        Assert.Equal(RowStates.Allow, added.Suggested);
        Assert.Equal(1, rig.Integrations.Discover.GetMcpChecklist("fake", null).NewCount);
        Assert.Equal(2, rig.Integrations.Discover.GetMcpChecklist("fake", null).Runs);
        Assert.Equal(Decisions.Allow, rig.Node(null, "get_thing").DefaultDecision); // a saved choice is kept
        // the tools of the server are stored as before (mcpCheck keeps working)
        Assert.Equal(3, (await rig.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "fake" }))!["tools"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ the store: reconcile and a description that is not a string

    private static (TempDb Db, DiscoverStore Store, long Server) StoreRig()
    {
        var db = new TempDb();
        using (var c = new SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO mcp_servers (name, command, args, created_at, updated_at) VALUES ('s', 'x', '[]', 'now', 'now');";
            cmd.ExecuteNonQuery();
        }
        var store = new DiscoverStore(db.ConnectionString, TimeProvider.System);
        using var c2 = new SqliteConnection(db.ConnectionString);
        c2.Open();
        using var q = c2.CreateCommand();
        q.CommandText = "SELECT id FROM mcp_servers WHERE name = 's';";
        return (db, store, Convert.ToInt64(q.ExecuteScalar()));
    }

    private static Dictionary<string, Classification> Classes(JsonArray tools) =>
        tools.OfType<JsonObject>().ToDictionary(t => t["name"]!.GetValue<string>(), McpClassifier.Classify);

    private static void Allow(DiscoverStore store, long server, string tool)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        store.SetMcpChoice(c, tx, server, null, tool, Decisions.Allow, DateTimeOffset.UtcNow.ToString("O"));
        tx.Commit();
    }

    [Fact]
    public void Mcp_ReDiscover_TakesAnAllowBackWhenAToolGetsLessSafe()
    {
        var (db, store, s) = StoreRig();
        using var _ = db;
        var v1 = new JsonArray(Tool("alpha", new JsonObject { ["readOnlyHint"] = true }), Tool("beta", new JsonObject { ["readOnlyHint"] = true }),
            Tool("gamma", new JsonObject { ["readOnlyHint"] = true }), Tool("delta", new JsonObject { ["readOnlyHint"] = true }));
        store.MergeMcp(s, v1, Classes(v1));
        foreach (var t in new[] { "alpha", "beta", "gamma", "delta" }) Allow(store, s, t);

        // v2: alpha is now a change, beta destructive, gamma a credential by name; delta stays read-only
        var v2 = new JsonArray(Tool("alpha"), Tool("beta", new JsonObject { ["destructiveHint"] = true }), Tool("gamma", new JsonObject { ["readOnlyHint"] = true }), Tool("delta", new JsonObject { ["readOnlyHint"] = true }));
        // gamma becomes a credential: rename-free way is a floor word in the name, so use a new classification directly
        var classes = Classes(v2);
        classes["gamma"] = new Classification(CommandClass.Credential, ClassSource.Floor, "word reveals a sign-in");
        var summary = store.MergeMcp(s, v2, classes);

        Assert.Equal(3, summary.Dropped.Count);
        Assert.Contains(summary.Dropped, d => d.StartsWith("alpha") && d.Contains("please review"));
        Assert.Contains(summary.Dropped, d => d.StartsWith("beta") && d.Contains("destructive"));
        Assert.Contains(summary.Dropped, d => d.StartsWith("gamma") && d.Contains("locked"));
        var choices = store.McpChoices(s).ToDictionary(c => c.Path);
        Assert.Equal(Decisions.Ask, choices["alpha"].Decision);
        Assert.Equal(Decisions.Deny, choices["beta"].Decision);
        Assert.False(choices.ContainsKey("gamma"));
        Assert.Equal(Decisions.Allow, choices["delta"].Decision); // unchanged: kept
        // each choice still owns exactly one mcp_rules row, and the credential's is gone
        using var c = store.Open();
        Assert.Equal(3L, DiscoverStore.Scalar(c, null, "SELECT count(*) FROM mcp_rules WHERE server_id = $s;", ("$s", s)));
        Assert.Equal(0L, DiscoverStore.Scalar(c, null, "SELECT count(*) FROM mcp_rules WHERE pattern = 'gamma';"));
    }

    [Fact]
    public void Mcp_ReDiscover_LeavesAChoiceAloneWhenAToolStaysAsSafe()
    {
        var (db, store, s) = StoreRig();
        using var _ = db;
        var v1 = new JsonArray(Tool("create_x"));
        store.MergeMcp(s, v1, Classes(v1));
        Allow(store, s, "create_x");
        var v2 = new JsonArray(Tool("create_x", new JsonObject { ["idempotentHint"] = true }));
        Assert.Empty(store.MergeMcp(s, v2, Classes(v2)).Dropped);
        Assert.Equal(Decisions.Allow, store.McpChoices(s).Single().Decision);
    }

    [Fact]
    public void Merge_ReadsAToolWhoseDescriptionIsNotAString_WithoutThrowing()
    {
        var (db, store, s) = StoreRig();
        using var _ = db;
        var tools = new JsonArray(
            new JsonObject { ["name"] = "one", ["description"] = 5 },
            new JsonObject { ["name"] = "two", ["description"] = new JsonObject { ["text"] = "x" } },
            new JsonObject { ["name"] = "three", ["description"] = null },
            new JsonObject { ["name"] = "four", ["description"] = "fine" },
            new JsonObject { ["name"] = 7, ["description"] = "name is no string" });
        var summary = store.MergeMcp(s, tools, new Dictionary<string, Classification>());
        Assert.Equal(4, summary.New);
        var rows = store.McpCommands(s).ToDictionary(r => r.Path);
        Assert.Equal("", rows["one"].Summary);
        Assert.Equal("", rows["two"].Summary);
        Assert.Equal("fine", rows["four"].Summary);
    }

    [Fact]
    public async Task I2_Mcp_ATeamChoiceIsComparedWithTheTeamsOtherRules_NotOnlyTheDefault()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        await using var rig = await Rig.Make(null, "create_issue", "get_thing");
        await rig.Discover();
        await rig.Rpc("integrations.mcpSaveRule", new JsonObject { ["name"] = "fake", ["teamId"] = rig.Alpha, ["pattern"] = "create_*", ["decision"] = "allow" });
        Assert.Equal(Decisions.Allow, rig.Node(rig.Alpha, "create_issue").Effective);
        var r = rig.Integrations.Discover.SaveMcpChoices("fake", rig.Alpha, [new RowChoice("create_issue", RowStates.Ask)]);
        Assert.Equal(1, r.Asked);
        Assert.Equal(Decisions.Ask, rig.Node(rig.Alpha, "create_issue").TeamDecision);
        Assert.Equal(Decisions.Ask, rig.Node(rig.Alpha, "create_issue").Effective);
    }
}
