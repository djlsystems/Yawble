using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The tester's name-default gap checks over the real UiRpc, BoardService, McpBusServer hire and ConciergeService. No real
/// agent CLI runs: the hires run in a ScriptedRunner, the Concierge's launcher is a fake that fails to start.
/// </summary>
public class NameDefaultsVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Bus { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public BoardService Board { get; private set; } = null!;

        public static async Task<Rig> StartAsync(IAgentRunner? runner = null)
        {
            var rig = new Rig();
            rig.Bus = new BusEngine(rig.Db.OpenStore(), runner ?? new UnavailableRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false })
            {
                NameRandom = new Random(11),
            };
            await rig.Bus.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Bus, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Bus), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new SandboxPlatformInfo(), () => [], ui: ui);
            rig.Board = new BoardService(new BoardStore(rig.Db.ConnectionString), rig.Bus);
            return rig;
        }

        public async Task<JsonNode?> Call(string method, object? args = null) =>
            await Rpc.DispatchAsync(method, args is null ? null : JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

        public async Task<IReadOnlyList<MemberRow>> Members(string team) =>
            (await Bus.AllMemberRowsAsync(Ct)).Where(m => m.Team == team).ToList();

        public void SetRaw(string key, string json)
        {
            using var store = Db.OpenStore();
            store.SetAppDefault(key, json);
        }

        public async ValueTask DisposeAsync()
        {
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static async Task<(string Text, bool Error)> Hire(RunContext run, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "hire", ["arguments"] = JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    /// <summary>Runs the scripted Manager's hires on a fresh team "Alpha" and returns each answer.</summary>
    private static async Task<(Rig Rig, List<(string Text, bool Error)> Results)> HiresAsync(Func<Rig, Task> setup, params object[] hires)
    {
        var done = new TaskCompletionSource();
        var results = new List<(string Text, bool Error)>();
        var runner = new ScriptedRunner();
        runner.On("Alpha/Manager", async (run, _) =>
        {
            try
            {
                foreach (var h in hires) results.Add(await Hire(run, h));
                done.TrySetResult();
            }
            catch (Exception e) { done.TrySetException(e); }
            return RunResult.Ok;
        });
        var rig = await Rig.StartAsync(runner);
        await setup(rig);
        await rig.Bus.CreateTeamAsync("Alpha", ct: Ct);
        await rig.Bus.PersonTellAsync("Alpha", "Manager", "build it", ct: Ct);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        return (rig, results);
    }

    // ---------------------------------------------------------------- anti-goal: a malformed list never fails or stalls a hire

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"names\":[\"Priya\"]}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"Priya\"")]
    [InlineData("[null, \"bad name\", \"x\"]")]
    public async Task AMalformedSavedList_ReadsAsEmpty_HireAsksForAFullName_AndAFullNameStillHires(string raw)
    {
        var (rig, results) = await HiresAsync(r => { r.SetRaw(BusEngine.MemberNamesKey, raw); return Task.CompletedTask; },
            new { name = "Developer" }, new { name = "Developer Priya" });
        await using (rig)
        {
            var defaults = (await rig.Call("teams.defaults"))!;
            Assert.Empty(defaults["memberNames"]!.AsArray());
            Assert.True(results[0].Error);
            Assert.Contains("No free name on the person's name list", results[0].Text);
            Assert.False(results[1].Error, results[1].Text);
            Assert.Contains("hired DeveloperPriya", results[1].Text);
            // Team creation with a job word is refused cleanly too, nothing made.
            var e = await Assert.ThrowsAsync<RpcException>(() => rig.Call("teams.create", new { name = "beta", members = new object[] { new { name = "Tester", cli = "claude" } } }));
            Assert.Equal("no_free_name", e.Code);
            Assert.DoesNotContain(await rig.Bus.TeamsAsync(Ct), t => t.Name == "beta");
        }
    }

    [Fact]
    public async Task MalformedManagerAndMemberDefaults_FallBackToTodays()
    {
        await using var rig = await Rig.StartAsync();
        rig.SetRaw(BusEngine.ManagerKey, "[\"oops\"]");
        rig.SetRaw(BusEngine.MembersKey, "{\"x\":1}");
        rig.SetRaw(BusEngine.CountryKey, "\"zz-ZZ\"");
        var d = (await rig.Call("teams.defaults"))!;
        Assert.Equal("""{"name":"Manager","cli":"claude"}""", d["manager"]!.ToJsonString());
        Assert.Empty(d["members"]!.AsArray());
        Assert.Equal("en-US", d["country"]!.GetValue<string>());
        await rig.Call("teams.create", new { name = "alpha" });
        Assert.Equal([("Manager", "claude")], (await rig.Members("alpha")).Select(m => (m.Name, m.Cli)));
    }

    // ---------------------------------------------------------------- hire: names used on any team, archived too, case-insensitive

    [Fact]
    public async Task Hire_NeverPicksAFirstNameUsedOnAnArchivedTeam_AndAFullNameIsCheckedCaseInsensitively()
    {
        var (rig, results) = await HiresAsync(async r =>
            {
                await r.Call("teams.setDefaults", new { memberNamesText = "Tomas\nPriya", country = "ja-JP" });
                var old = await r.Bus.CreateTeamAsync("Old", "Manager", "claude", null, Ct);
                await r.Bus.AddMemberAsync(old, "Developer tomas", "claude", Ct);
                await r.Bus.ArchiveTeamAsync("Old", Ct);
            },
            new { name = "Developer" }, new { name = "Developer" }, new { name = "Tester TOMAS" }, new { name = "Tester Hana" }, new { name = "Researcher priya" });
        await using (rig)
        {
            Assert.False(results[0].Error, results[0].Text);
            Assert.Contains("hired DeveloperPriya", results[0].Text); // Tomas is used on the archived team
            Assert.True(results[1].Error); // the list is used up now
            Assert.Contains("'Developer <a first name common in Japan>'", results[1].Text);
            Assert.True(results[2].Error);
            Assert.Contains("the first name 'TOMAS' is already used", results[2].Text);
            Assert.Contains("common in Japan", results[2].Text);
            Assert.False(results[3].Error, results[3].Text);
            Assert.True(results[4].Error); // Priya was just hired on this team
            Assert.Equal(["Manager", "DeveloperPriya", "TesterHana"], (await rig.Members("Alpha")).Select(m => m.Name));
            Assert.Equal(2, (await rig.Call("teams.defaults"))!["usedCount"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task Hire_WithManyNames_AlwaysPicksFromTheListAndNeverRepeats()
    {
        var list = Enumerable.Range(0, 30).Select(i => "Nm" + (char)('a' + i % 26) + (char)('a' + i / 26)).ToArray();
        var (rig, results) = await HiresAsync(r => r.Call("teams.setDefaults", new { memberNamesText = string.Join('\n', list) }),
            Enumerable.Range(0, 8).Select(_ => (object)new { name = "Developer" }).ToArray());
        await using (rig)
        {
            Assert.All(results, r => Assert.False(r.Error, r.Text));
            var hired = (await rig.Members("Alpha")).Where(m => m.Role == "member").Select(m => m.Name["Developer".Length..]).ToList();
            Assert.Equal(8, hired.Count);
            Assert.Equal(8, hired.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(hired, h => Assert.Contains(h, list));
        }
    }

    // ---------------------------------------------------------------- every way a team is made: the caller's own win

    [Fact]
    public async Task CallerGivenValuesWin_InTeamsCreateAndDispatchNew()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Call("teams.setDefaults", new
        {
            manager = new { name = "Lead", cli = "codex" },
            members = new object[] { new { name = "Developer", cli = "grok", onNewTeams = true } },
            memberNamesText = "Priya\nAmara\nWren",
        });
        // Only the Manager's agent given: the saved Manager name and ticked rows still apply.
        await rig.Call("teams.create", new { name = "a1", managerCli = "claude" });
        var a1 = await rig.Members("a1");
        Assert.Equal(("Lead", "claude"), (a1[0].Name, a1[0].Cli));
        Assert.Single(a1, m => m.Role == "member" && m.Cli == "grok");
        // An empty member list given: no members (the caller's own), the saved Manager.
        await rig.Call("teams.create", new { name = "a2", members = Array.Empty<object>() });
        Assert.Equal([("Lead", "codex")], (await rig.Members("a2")).Select(m => (m.Name, m.Cli)));
        // dispatchNew with a Manager agent: the saved Manager on that agent.
        var id = (await rig.Board.DispatchAsync("backlog.add", new JsonObject { ["title"] = "x" }, Ct))!["id"]!.GetValue<long>();
        await rig.Board.DispatchAsync("backlog.edit", new JsonObject { ["id"] = id, ["state"] = "ready" }, Ct);
        await rig.Board.DispatchAsync("backlog.dispatchNew", new JsonObject { ["id"] = id, ["name"] = "a3", ["managerCli"] = "claude" }, Ct);
        var a3 = await rig.Members("a3");
        Assert.Equal(("Lead", "claude"), (a3[0].Name, a3[0].Cli));
        Assert.Single(a3, m => m.Role == "member" && m.Cli == "grok");
        // Names typed in full on a new team are used as typed, even when another team has the first name.
        await rig.Call("teams.create", new { name = "a4", members = new object[] { new { name = "Tester Wren", cli = "claude" } } });
        await rig.Call("teams.create", new { name = "a5", members = new object[] { new { name = "Tester Wren", cli = "claude" } } });
        Assert.Contains(await rig.Members("a5"), m => m.Name == "TesterWren");
    }

    // ---------------------------------------------------------------- the Concierge: new conversations only

    [Fact]
    public async Task ConciergeDefault_AppliesToNewConversationsOnly_AnExistingOneKeepsItsAgent()
    {
        using var db = new TempDb();
        await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner());
        await bus.StartAsync(Ct);
        var launches = new List<LaunchRequest>();
        var service = new ConciergeService(new ConciergeStore(db.ConnectionString), bus,
            (r, _) => { lock (launches) launches.Add(r); return Task.FromResult(new LaunchOutcome("", RunEnd.StartFailed, "fake: no agent runs here", null, [])); },
            new RunRegistry(), new AppEvents(), (_, _, _) => Task.FromResult<JsonNode?>(null), _ => Task.FromResult("codex"));

        var first = (await service.DispatchAsync("concierge.send", new JsonObject { ["text"] = "hello" }, Ct))!["id"]!.GetValue<string>();
        async Task<JsonNode> Get(string id) => (await service.DispatchAsync("concierge.get", new JsonObject { ["id"] = id }, Ct))!;
        await Wait.UntilAsync(async () => (await Get(first))["running"]?.GetValue<bool>() == false, "the first turn ends");
        Assert.Equal("codex", (await Get(first))["cli"]!.GetValue<string>()); // nothing saved: the first available

        await service.DispatchAsync("concierge.setDefaults", new JsonObject { ["defaultAgent"] = "grok" }, Ct);
        await service.DispatchAsync("concierge.send", new JsonObject { ["id"] = first, ["text"] = "again" }, Ct);
        await Wait.UntilAsync(async () => (await Get(first))["running"]?.GetValue<bool>() == false, "the second turn ends");
        Assert.Equal("codex", (await Get(first))["cli"]!.GetValue<string>()); // the existing one keeps its agent

        var second = (await service.DispatchAsync("concierge.send", new JsonObject { ["text"] = "new one" }, Ct))!["id"]!.GetValue<string>();
        await Wait.UntilAsync(async () => (await Get(second))["running"]?.GetValue<bool>() == false, "the new turn ends");
        Assert.Equal("grok", (await Get(second))["cli"]!.GetValue<string>());
        // A caller that names its agent keeps it.
        var third = (await service.DispatchAsync("concierge.send", new JsonObject { ["text"] = "mine", ["cli"] = "claude" }, Ct))!["id"]!.GetValue<string>();
        await Wait.UntilAsync(async () => (await Get(third))["running"]?.GetValue<bool>() == false, "the third turn ends");
        Assert.Equal("claude", (await Get(third))["cli"]!.GetValue<string>());
    }
}
