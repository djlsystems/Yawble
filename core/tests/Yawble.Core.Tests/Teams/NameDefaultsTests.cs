using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Sandboxing;
using Yawble.Core.Storage;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The saved defaults (Settings > Concierge and Teams), the member-name list, members' first names
/// (0046 members.given_name) and how hire and every way of making a team use them. Generate runs a FAKE launcher only.
/// </summary>
public class NameDefaultsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- cleaning and the picker

    [Fact]
    public void Cleaning_TrimsDropsBlanksAndCaseInsensitiveRepeats_AndReportsLinesThatAreNotOneAsciiName()
    {
        var (names, rejected) = MemberNameList.Clean("  Priya \n\nAmara\r\npriya\nJean-Luc\nJosé\nA\nMary Ann\n-Ann\nAnn-\nO'Neil\n" + new string('x', 21) + "\nZoe");
        Assert.Equal(["Priya", "Amara", "Jean-Luc", "Zoe"], names);
        Assert.Equal(["José", "A", "Mary Ann", "-Ann", "Ann-", "O'Neil", new string('x', 21)], rejected.Select(r => r.Line));
        Assert.All(rejected, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        Assert.Null(MemberNameList.Problem("Al"));
        Assert.Null(MemberNameList.Problem(new string('a', 20)));
    }

    [Fact]
    public void AReplyGivesTheNamesInItsJsonArray_FoldedAndCleaned_OrNoneWithoutOne()
    {
        Assert.Equal(["Jose", "Zoe", "Anna"], MemberNameList.FromReply("Here you go:\n```json\n[\"José\", \"Zoë\", \"Anna\", \"anna\", \"Mary Ann\", 7]\n```\nEnjoy [them]."));
        Assert.Empty(MemberNameList.FromReply("I cannot help with that."));
        Assert.Empty(MemberNameList.FromReply("[1, 2, 3]"));
        Assert.Empty(MemberNameList.FromReply(null));
    }

    [Fact]
    public void ThePickerTakesARandomFreeName_InAtMostTenTries_OrNone()
    {
        var list = new[] { "Priya", "Amara", "Wren" };
        var picked = MemberNameList.Pick(list, n => n == "Wren", new Random(1));
        Assert.Contains(picked, new[] { "Priya", "Amara" });
        Assert.Null(MemberNameList.Pick([], _ => false));
        var tries = 0;
        Assert.Null(MemberNameList.Pick(list, _ => { tries++; return true; }, new Random(1)));
        Assert.Equal(MemberNameList.PickTries, tries);
    }

    [Fact]
    public void UsedNamesMatchFirstNamesCaseInsensitively_AndUnknownOnesByTheIdsEnd()
    {
        GivenNameRow[] rows = [new(1, "DeveloperTomas", "Tomas"), new(2, "dev1", null), new(3, "Alice", null)];
        Assert.True(BusEngine.IsGivenNameUsed("tomas", rows));
        Assert.True(BusEngine.IsGivenNameUsed("Alice", rows));
        Assert.False(BusEngine.IsGivenNameUsed("Priya", rows));
        // A job-word-only member ("Tester") does not use names its id merely ends with.
        GivenNameRow[] jobOnly = [new(4, "Tester", null), new(5, "DeveloperEster", null)];
        Assert.False(BusEngine.IsGivenNameUsed("Per", jobOnly));
        Assert.True(BusEngine.IsGivenNameUsed("ester", jobOnly));
        Assert.Equal("Priya", MemberNameList.GivenNameOf("Developer  Priya"));
        Assert.Null(MemberNameList.GivenNameOf("Developer"));
        Assert.Equal("Germany", MemberNameList.Country("de-de")!.DisplayName());
        Assert.InRange(MemberNameList.Countries.Count, 35, 60);
        Assert.Equal("United States (US English)", MemberNameList.Country(MemberNameList.DefaultCountry)!.Label);
    }

    // ---------------------------------------------------------------- 0046

    [Fact]
    public async Task Migration0046_BackfillsFirstNamesFromIds_LeavingManagersAndUnsplittableIdsNull()
    {
        using var db = new TempDb();
        await using (var bus = new BusEngine(db.OpenStore(), new UnavailableRunner()))
        {
            await bus.CreateTeamAsync("alpha", ct: Ct);
            foreach (var n in new[] { "DeveloperTomas", "dev1", "TesterWren2", "Alice", "QAZed" }) await bus.AddMemberAsync("alpha", n, ct: Ct);
        }
        // Wind the database back to before 0046, as an existing install is.
        Exec(db, "ALTER TABLE members DROP COLUMN given_name; DROP TABLE app_defaults; DELETE FROM schema_migrations WHERE name = '0046_defaults_given_names';");
        Assert.Contains("0046_defaults_given_names", SchemaMigrator.Migrate(db.ConnectionString));
        using var store = db.OpenStore();
        var given = store.GivenNames().ToDictionary(g => g.Name, g => g.GivenName);
        Assert.Equal("Tomas", given["DeveloperTomas"]);
        Assert.Equal("Wren2", given["TesterWren2"]);
        Assert.Null(given["dev1"]);
        Assert.Null(given["Alice"]);
        Assert.Null(given["QAZed"]); // "Q" then a capital: no run of lowercase letters to split after
        Assert.DoesNotContain("Manager", given.Keys); // Managers do not count
    }

    private static void Exec(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- the RPCs

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
                NameRandom = new Random(7),
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

        public async ValueTask DisposeAsync()
        {
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task TeamsDefaults_AreTodaysWhenNothingIsSaved_AndSetDefaultsSavesCleanedNames_ReportingRejectedLines()
    {
        await using var rig = await Rig.StartAsync();
        var empty = (await rig.Call("teams.defaults"))!;
        Assert.Equal("""{"name":"Manager","cli":"claude"}""", empty["manager"]!.ToJsonString());
        Assert.Empty(empty["members"]!.AsArray());
        Assert.Empty(empty["memberNames"]!.AsArray());
        Assert.Equal("en-US", empty["country"]!.GetValue<string>());
        Assert.Equal(["code", "label"], empty["countries"]![0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(0, empty["usedCount"]!.GetValue<int>());

        var r = (await rig.Call("teams.setDefaults", new
        {
            manager = new { name = "Lead", cli = "codex" },
            members = new object[] { new { name = "Developer", cli = "claude", onNewTeams = true }, new { name = "Tester Wren", cli = "grok", onNewTeams = false } },
            memberNamesText = "Priya\n priya \n\nAmara\nJosé\nTomas",
            country = "de-DE",
        }))!;
        Assert.Equal(["saved", "rejected"], r.AsObject().Select(kv => kv.Key));
        var rejected = Assert.Single(r["rejected"]!.AsArray())!;
        Assert.Equal("José", rejected["line"]!.GetValue<string>());
        Assert.NotNull(rejected["reason"]);
        var saved = r["saved"]!;
        Assert.Equal(["Priya", "Amara", "Tomas"], saved["memberNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("""[{"name":"Developer","cli":"claude","onNewTeams":true},{"name":"Tester Wren","cli":"grok","onNewTeams":false}]""", saved["members"]!.ToJsonString());
        Assert.Equal("de-DE", saved["country"]!.GetValue<string>());
        Assert.Equal("Lead", (await rig.Call("teams.defaults"))!["manager"]!["name"]!.GetValue<string>());

        // usedCount: list names a member of any team has (archived teams too).
        var old = await rig.Bus.CreateTeamAsync("old", "Manager", "claude", null, Ct);
        await rig.Bus.AddMemberAsync(old, "Developer Tomas", "claude", Ct);
        await rig.Bus.ArchiveTeamAsync("old", Ct);
        Assert.Equal(1, (await rig.Call("teams.defaults"))!["usedCount"]!.GetValue<int>());

        // A part left out keeps what is saved; a bad agent or country is refused.
        await rig.Call("teams.setDefaults", new { country = "ja-JP" });
        Assert.Equal(3, (await rig.Call("teams.defaults"))!["memberNames"]!.AsArray().Count);
        Assert.Equal("bad_cli", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("teams.setDefaults", new { manager = new { name = "Manager", cli = "nonesuch" } }))).Code);
        Assert.Equal("bad_country", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("teams.setDefaults", new { country = "xx-XX" }))).Code);
    }

    [Fact]
    public async Task TeamCreation_WithNothingGiven_UsesTheSavedManagerAndTickedRows_PickingFirstNamesForJobWords()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Call("teams.setDefaults", new
        {
            manager = new { name = "Lead", cli = "codex" },
            members = new object[] { new { name = "Developer", cli = "claude", onNewTeams = true }, new { name = "Tester Wren", cli = "grok", onNewTeams = true },
                new { name = "Researcher", cli = "claude", onNewTeams = false } },
            memberNamesText = "Priya\nAmara",
        });
        await rig.Call("teams.create", new { name = "alpha" });
        var alpha = await rig.Members("alpha");
        Assert.Equal(("Lead", "manager", "codex"), (alpha[0].Name, alpha[0].Role, alpha[0].Cli));
        var dev = alpha.Single(m => m.Name.StartsWith("Developer", StringComparison.Ordinal));
        Assert.Contains(dev.Name, new[] { "DeveloperPriya", "DeveloperAmara" });
        Assert.Contains(alpha, m => m is { Name: "TesterWren", Cli: "grok" });
        Assert.DoesNotContain(alpha, m => m.Name.StartsWith("Researcher", StringComparison.Ordinal)); // not ticked

        // A caller that gives its own uses its own; two job words get two different names.
        await rig.Call("teams.create", new { name = "beta", managerName = "Boss", managerCli = "claude", members = new object[] { new { name = "Developer", cli = "claude" } } });
        var beta = await rig.Members("beta");
        Assert.Equal(["Boss", "Developer" + (dev.Name == "DeveloperPriya" ? "Amara" : "Priya")], beta.Select(m => m.Name));

        // No free name left: refused before anything is made, with the message the window shows.
        var refused = await Assert.ThrowsAsync<RpcException>(() => rig.Call("teams.create", new { name = "gamma", members = new object[] { new { name = "Tester", cli = "claude" } } }));
        Assert.Equal("No free name on your member-name list for 'Tester'; type a full name or add names in Settings > Teams.", refused.Message);
        Assert.DoesNotContain(await rig.Bus.TeamsAsync(Ct), t => t.Name == "gamma");
        // Names typed in full are used as typed.
        await rig.Call("teams.create", new { name = "delta", members = new object[] { new { name = "Tester Priya", cli = "claude" } } });
        Assert.Contains(await rig.Members("delta"), m => m.Name == "TesterPriya");
    }

    [Fact]
    public async Task TwoDefaultRowsWithTheSameFullNameAreRefused_TwoJobWordRowsAreTwoMembers()
    {
        await using var rig = await Rig.StartAsync();
        var e = await Assert.ThrowsAsync<RpcException>(() => rig.Call("teams.setDefaults", new
        {
            members = new object[] { new { name = "Tester Wren", cli = "claude", onNewTeams = true }, new { name = "tester  wren", cli = "grok", onNewTeams = false } },
        }));
        Assert.Equal("exists", e.Code);
        await rig.Call("teams.setDefaults", new
        {
            members = new object[] { new { name = "Developer", cli = "claude", onNewTeams = true }, new { name = "Developer", cli = "grok", onNewTeams = true } },
            memberNamesText = "Priya\nAmara",
        });
        await rig.Call("teams.create", new { name = "alpha" });
        Assert.Equal(["DeveloperAmara", "DeveloperPriya", "Manager"], (await rig.Members("alpha")).Select(m => m.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DispatchNew_MakesTheTeamWithTheSavedManagerAndTickedMembers()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Call("teams.setDefaults", new
        {
            manager = new { name = "Lead", cli = "codex" },
            members = new object[] { new { name = "Developer", cli = "claude", onNewTeams = true } },
            memberNamesText = "Priya",
        });
        var add = await rig.Board.DispatchAsync("backlog.add", new JsonObject { ["title"] = "Spin out billing" }, Ct);
        var id = add!["id"]!.GetValue<long>();
        await rig.Board.DispatchAsync("backlog.edit", new JsonObject { ["id"] = id, ["state"] = "ready" }, Ct);
        await rig.Board.DispatchAsync("backlog.dispatchNew", new JsonObject { ["id"] = id, ["name"] = "billing" }, Ct);
        var billing = await rig.Members("billing");
        Assert.Equal([("Lead", "codex"), ("DeveloperPriya", "claude")], billing.Select(m => (m.Name, m.Cli)));

        // The list is used up: the next dispatch to a new team is refused before the team is made.
        var id2 = (await rig.Board.DispatchAsync("backlog.add", new JsonObject { ["title"] = "Another" }, Ct))!["id"]!.GetValue<long>();
        await rig.Board.DispatchAsync("backlog.edit", new JsonObject { ["id"] = id2, ["state"] = "ready" }, Ct);
        var refused = await Assert.ThrowsAsync<RpcException>(() => rig.Board.DispatchAsync("backlog.dispatchNew", new JsonObject { ["id"] = id2, ["name"] = "ledger", ["managerCli"] = "claude" }, Ct));
        Assert.Contains("No free name on your member-name list for 'Developer'", refused.Message);
        Assert.DoesNotContain(await rig.Bus.TeamsAsync(Ct), t => t.Name == "ledger");
    }

    [Fact]
    public async Task TheConciergeToolsTeamCreateLeavesTheManagerAndMembersToTheDefaults()
    {
        using var db = new TempDb();
        await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner());
        JsonObject? sent = null;
        var tools = new ConciergeTools(bus, new ConciergeStore(db.ConnectionString), (method, p, _) =>
        {
            sent = p;
            return Task.FromResult<JsonNode?>(new JsonObject { ["teamId"] = 1L });
        }, () => null, "c1", actions: null, remote: false);
        await tools.HandleAsync(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "team_create", ["arguments"] = new JsonObject { ["name"] = "web" } },
        }.ToJsonString()), Ct);
        Assert.NotNull(sent);
        Assert.Null(sent!["managerCli"]);
        Assert.False(sent.ContainsKey("members"));
        Assert.False(sent.ContainsKey("managerName"));
    }

    // ---------------------------------------------------------------- hire

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

    [Fact]
    public async Task Hire_JobWordGetsAPickedFirstName_AFullNameMustBeUnusedOnEveryTeam_AndAnEmptyListAsksForAFullName()
    {
        var done = new TaskCompletionSource();
        var results = new List<(string Text, bool Error)>();
        var runner = new ScriptedRunner();
        Rig? rigRef = null;
        runner.On("Alpha/Manager", async (run, _) =>
        {
            try
            {
                results.Add(await Hire(run, new { name = "Developer", job = "Developer: writes the code" }));
                results.Add(await Hire(run, new { name = "Tester Tomas" })); // Tomas is a member of another team
                results.Add(await Hire(run, new { name = "Tester Wren" }));
                results.Add(await Hire(run, new { name = "Tester" })); // the list is used up
                await rigRef!.Call("teams.setDefaults", new { country = "de-DE", memberNamesText = "" });
                results.Add(await Hire(run, new { name = "Researcher" }));
                results.Add(await Hire(run, new { name = "Con" })); // still not a folder name
                done.TrySetResult();
            }
            catch (Exception e) { done.TrySetException(e); }
            return RunResult.Ok;
        });
        await using var rig = await Rig.StartAsync(runner);
        rigRef = rig;
        await rig.Call("teams.setDefaults", new { memberNamesText = "Priya\nTomas" });
        var other = await rig.Bus.CreateTeamAsync("Other", "Manager", "claude", null, Ct);
        await rig.Bus.AddMemberAsync(other, "Developer Tomas", "claude", Ct);
        await rig.Bus.CreateTeamAsync("Alpha", ct: Ct);
        await rig.Bus.PersonTellAsync("Alpha", "Manager", "build it", ct: Ct);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.False(results[0].Error, results[0].Text);
        Assert.Contains("hired DeveloperPriya", results[0].Text); // Tomas is used on another team, so Priya
        Assert.True(results[1].Error);
        Assert.Contains("the first name 'Tomas' is already used", results[1].Text);
        Assert.Contains("common in United States", results[1].Text);
        Assert.False(results[2].Error, results[2].Text);
        Assert.True(results[3].Error);
        Assert.Contains("No free name on the person's name list. Call hire again with name: 'Tester <a first name common in United States>', one not used by any member of any team.", results[3].Text);
        Assert.Contains("bad_name", results[3].Text);
        Assert.Contains("'Researcher <a first name common in Germany>'", results[4].Text);
        Assert.True(results[5].Error);
        Assert.Contains("bad_name", results[5].Text);

        var alpha = await rig.Members("Alpha");
        Assert.Equal(["Manager", "DeveloperPriya", "TesterWren"], alpha.Select(m => m.Name));
        Assert.Equal("Developer: writes the code", alpha[1].Instructions);
        using var store = rig.Db.OpenStore();
        var given = store.GivenNames().ToDictionary(g => g.Name, g => g.GivenName);
        Assert.Equal(("Priya", "Wren", "Tomas"), (given["DeveloperPriya"], given["TesterWren"], given["DeveloperTomas"]));
    }

    // ---------------------------------------------------------------- the Concierge's default agent and Generate

    private static ConciergeService Concierge(TempDb db, BusEngine bus, Func<LaunchRequest, CancellationToken, Task<LaunchOutcome>> launch, RunRegistry registry) =>
        new(new ConciergeStore(db.ConnectionString), bus, launch, registry, new AppEvents(),
            (_, _, _) => Task.FromResult<JsonNode?>(null), _ => Task.FromResult("codex"));

    [Fact]
    public async Task ConciergeDefaults_SavedAgentStartsNewConversations_WhileItResolves()
    {
        using var db = new TempDb();
        await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner());
        await bus.StartAsync(Ct);
        var service = Concierge(db, bus, (_, _) => Task.FromResult(new LaunchOutcome("", RunEnd.StartFailed, "no", null, [])), new RunRegistry());
        Assert.Null((await service.DispatchAsync("concierge.defaults", null, Ct))!["defaultAgent"]);
        Assert.Equal("codex", await service.DefaultAgentAsync(Ct)); // first available
        Assert.Equal("grok", (await service.DispatchAsync("concierge.setDefaults", new JsonObject { ["defaultAgent"] = "grok" }, Ct))!["defaultAgent"]!.GetValue<string>());
        Assert.Equal("grok", await service.DefaultAgentAsync(Ct));
        Assert.Equal("bad_cli", (await Assert.ThrowsAsync<RpcException>(() => service.DispatchAsync("concierge.setDefaults", new JsonObject { ["defaultAgent"] = "nonesuch" }, Ct))).Code);
        // A saved agent that no longer resolves (a custom agent removed): the first available again.
        using (var store = db.OpenStore()) store.SetAppDefault(BusEngine.DefaultAgentKey, "\"gone-agent\"");
        Assert.Equal("codex", await service.DefaultAgentAsync(Ct));
        Assert.Null((await service.DispatchAsync("concierge.setDefaults", new JsonObject { ["defaultAgent"] = null }, Ct))!["defaultAgent"]);
        Assert.Null(await bus.ConciergeDefaultAgentAsync(Ct));
    }

    [Fact]
    public async Task Generate_RunsTheDefaultAgentOnceWithNoBusTools_InAThrowawayFolder_AndReturnsTheCleanedNames()
    {
        using var db = new TempDb();
        var appRoot = Directory.CreateTempSubdirectory("yawble-names-").FullName;
        try
        {
            var repos = new TeamRepos(appRoot, db.ConnectionString);
            await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner(), new BusOptions { Repos = repos, IdleOffer = false });
            await bus.StartAsync(Ct);
            await bus.SetConciergeDefaultAgentAsync("claude", Ct);
            var registry = new RunRegistry();
            var launches = new List<LaunchRequest>();
            string? folder = null;
            var reply = "[\"José\", \"Emma\", \"emma\", \"Mary Ann\", \"Liam\"]";

            async Task<LaunchOutcome> Launch(LaunchRequest request, CancellationToken ct)
            {
                launches.Add(request);
                folder = repos.MemberRoot(request.Team, request.Member);
                Directory.CreateDirectory(Path.Combine(folder, "home"));
                // The bus endpoint has no Yawble tools.
                var tools = await request.Tools!.HandleAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), ct);
                Assert.Equal(404, tools.Status);
                var process = new FakeProcess(RunKind.Headless);
                var output = new RunOutputBuffer();
                var live = new LiveRun("r-names", null, request.Team, request.Member, RunKind.Headless, process,
                    new RunSupervisor(process, 60, 0, new RunActivity(TimeProvider.System), output), output);
                registry.Add(live);
                output.Write(Encoding.UTF8.GetBytes(
                    "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":" + JsonSerializer.Serialize(reply) + "}\n"));
                registry.Remove(live);
                return new LaunchOutcome("r-names", RunEnd.Completed, null, null, []);
            }

            var service = Concierge(db, bus, Launch, registry);
            bus.NameGenerator = service.GenerateNamesAsync;
            await using var rig = new UiOver(bus, db.ConnectionString);
            var names = (await rig.Call("teams.generateNames", new { country = "de-DE" }))!["names"]!.AsArray().Select(n => n!.GetValue<string>());
            Assert.Equal(["Jose", "Emma", "Liam"], names);
            var request = Assert.Single(launches);
            Assert.Equal((ConciergeService.Team, RunKind.Headless), (request.Team, request.Kind));
            Assert.StartsWith("names-", request.Member);
            Assert.Equal(AgentCli.Claude, request.Subject!.Cli);
            Assert.Null(request.Bus);
            Assert.Contains("Germany", request.Prompt);
            Assert.Contains("100", request.Prompt);
            Assert.Contains("JSON array", request.Prompt);
            Assert.False(Directory.Exists(folder)); // the throwaway folder is deleted afterwards
            Assert.Empty((await bus.TeamDefaultsAsync(Ct)).MemberNames); // nothing saved

            reply = "Sorry, no names today.";
            Assert.Equal("no_names", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("teams.generateNames", new { country = "en-US" }))).Code);
            Assert.Equal("bad_country", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("teams.generateNames", new { country = "xx" }))).Code);
            Assert.Equal(2, launches.Count); // a bad country runs nothing
        }
        finally
        {
            try { Directory.Delete(appRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AFailedGenerateRunIsAnError()
    {
        using var db = new TempDb();
        await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner());
        await bus.StartAsync(Ct);
        var service = Concierge(db, bus, (_, _) => Task.FromResult(new LaunchOutcome("", RunEnd.StartFailed, "agents cannot run on this computer", null, [])), new RunRegistry());
        var e = await Assert.ThrowsAsync<RpcException>(() => service.GenerateNamesAsync("en-US", Ct));
        Assert.Equal("generate_failed", e.Code);
        Assert.Contains("agents cannot run on this computer", e.Message);
    }

    /// <summary>The screens' dispatcher over an existing engine.</summary>
    private sealed class UiOver(BusEngine bus, string connectionString) : IAsyncDisposable
    {
        private readonly RpcDispatcher _rpc = Make(bus, connectionString);

        private static RpcDispatcher Make(BusEngine bus, string connectionString)
        {
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(bus, new PolicyStore(connectionString), new MemberLayouts(dataDir, install, home),
                new FakeModelCredentials(), new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, bus), terminals, 16_384);
            return new RpcDispatcher(new SandboxPlatformInfo(), () => [], ui: ui);
        }

        public async Task<JsonNode?> Call(string method, object args) =>
            await _rpc.DispatchAsync(method, JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- the Cloud Link and the audit log

    [Fact]
    public void ReadsAreReads_ChangesAreAuditedAndRefusedForReadOnlyViewers()
    {
        foreach (var read in new[] { "teams.defaults", "concierge.defaults" })
        {
            Assert.Null(RemoteAccess.Refusal(read, null, false, RemoteAccess.Read));
            Assert.True(AuditLog.IsReadOnly(read, null));
        }
        foreach (var change in new[] { "teams.setDefaults", "teams.generateNames", "concierge.setDefaults" })
        {
            Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal(change, null, false, RemoteAccess.Read));
            Assert.Null(RemoteAccess.Refusal(change, null, false, RemoteAccess.Full));
            Assert.False(AuditLog.IsReadOnly(change, null));
        }
        Assert.Equal("country=de-DE", AuditLog.Target(new JsonObject { ["country"] = "de-DE", ["memberNamesText"] = "Priya" }));
    }
}
