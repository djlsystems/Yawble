using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Agents.Profiles;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Sandboxing;
using Yawble.Core.Skills;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The role catalog (roles.*), a team's hire list (teams.hireList / teams.setHireList, the defaults and New team) and
/// hiring that follows it (the Manager's hire and hiring tools). Fake runners only: no agent CLI runs.
/// </summary>
public class RolesAndHireListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Bus { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public AgentChangeLog Log { get; private set; } = null!;

        public static async Task<Rig> StartAsync(IAgentRunner? runner = null)
        {
            var rig = new Rig();
            rig.Log = new AgentChangeLog(rig.Db.ConnectionString, null, (_, m) => m);
            rig.Bus = new BusEngine(rig.Db.OpenStore(), runner ?? new UnavailableRunner(), new BusOptions
            {
                SafetyPoll = TimeSpan.FromMilliseconds(100),
                IdleOffer = false,
                Skills = new SkillCatalog(rig.Db.ConnectionString),
                AvailableAgents = _ => Task.FromResult<IReadOnlyList<AgentChoice>>(
                    [new AgentChoice("claude", true, "installed and signed in"), new AgentChoice("codex", true, "installed and signed in"),
                     new AgentChoice("grok", false, "not installed on this computer")]),
            })
            {
                NameRandom = new Random(7),
                AgentChanges = rig.Log,
            };
            await rig.Bus.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Bus, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Bus), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new SandboxPlatformInfo(), () => [], ui: ui);
            await rig.Call("teams.setDefaults", new { memberNamesText = "Priya\nTomas\nWren\nAmara\nZed" });
            return rig;
        }

        public async Task<JsonNode?> Call(string method, object? args = null) =>
            await Rpc.DispatchAsync(method, args is null ? null : JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

        public async Task<RpcException> Refused(string method, object? args = null) =>
            await Assert.ThrowsAsync<RpcException>(() => Call(method, args));

        public async Task<IReadOnlyList<MemberRow>> Members(string team) =>
            (await Bus.AllMemberRowsAsync(Ct)).Where(m => m.Team == team).ToList();

        public async ValueTask DisposeAsync()
        {
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static async Task<(string Text, bool Error)> Tool(RunContext run, string tool, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    /// <summary>A rig whose team "Alpha" Manager runs <paramref name="script"/> once (after <paramref name="setUp"/>), waited for.</summary>
    private static async Task<Rig> ManagerRunAsync(Func<Rig, long, Task> setUp, Func<RunContext, Task> script)
    {
        var done = new TaskCompletionSource();
        var runner = new ScriptedRunner();
        runner.On("Alpha/Manager", async (run, _) =>
        {
            try { await script(run); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
            return RunResult.Ok;
        });
        var rig = await Rig.StartAsync(runner);
        var teamId = (await rig.Call("teams.create", new { name = "Alpha", managerCli = "claude", members = Array.Empty<object>(), hireList = Array.Empty<object>() }))!["teamId"]!.GetValue<long>();
        await setUp(rig, teamId);
        await rig.Bus.PersonTellAsync("Alpha", "Manager", "build it", ct: Ct);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        return rig;
    }

    // ---------------------------------------------------------------- the catalog

    [Fact]
    public async Task TheSixStartingRolesAreSeeded_AndTheCatalogRoundTrips()
    {
        await using var rig = await Rig.StartAsync();
        var roles = (await rig.Call("roles.list"))!.AsArray();
        Assert.Equal(["Developer", "Tester", "Reviewer", "Researcher", "Architect", "Writer"], roles.Select(r => r!["name"]!.GetValue<string>()));
        Assert.Equal("Writes the code and its unit tests on its own branch.", roles[0]!["job"]!.GetValue<string>());
        Assert.Contains("reports exactly what passed and failed", roles[1]!["job"]!.GetValue<string>());
        Assert.Contains("does not rewrite", roles[2]!["job"]!.GetValue<string>());
        Assert.All(roles, r => Assert.Equal("claude", r!["agent"]!.GetValue<string>()));
        Assert.All(roles, r => Assert.Null(r!["model"]));

        await rig.Call("roles.save", new { name = "Designer", job = "Draws the screens.", agent = "codex", model = "gpt-5" });
        await rig.Call("roles.save", new { name = "Writer", job = "Writes the docs.", agent = "codex", model = (string?)null, originalName = "Writer" });
        roles = (await rig.Call("roles.list"))!.AsArray();
        var designer = roles.Single(r => r!["name"]!.GetValue<string>() == "Designer")!;
        Assert.Equal(("Draws the screens.", "codex", "gpt-5"), (designer["job"]!.GetValue<string>(), designer["agent"]!.GetValue<string>(), designer["model"]!.GetValue<string>()));
        Assert.Equal("Writes the docs.", roles.Single(r => r!["name"]!.GetValue<string>() == "Writer")!["job"]!.GetValue<string>());

        Assert.Equal("bad_name", (await rig.Refused("roles.save", new { name = "Code Reviewer", job = "x", agent = "claude" })).Code);
        Assert.Equal("bad_role", (await rig.Refused("roles.save", new { name = "Lead", job = " ", agent = "claude" })).Code);
        Assert.Equal("bad_cli", (await rig.Refused("roles.save", new { name = "Lead", job = "Leads.", agent = "nonesuch" })).Code);
        Assert.Equal("exists", (await rig.Refused("roles.save", new { name = "tester", job = "Tests.", agent = "claude" })).Code);

        await rig.Call("roles.remove", new { name = "Designer" });
        Assert.DoesNotContain((await rig.Call("roles.list"))!.AsArray(), r => r!["name"]!.GetValue<string>() == "Designer");
    }

    [Fact]
    public async Task RemovingARoleAHireListUses_IsRefusedNamingTheTeams_AndARenameFollowsIntoTheList()
    {
        await using var rig = await Rig.StartAsync();
        foreach (var team in new[] { "Team B", "Team A" })
        {
            var id = (await rig.Call("teams.create", new { name = team, managerCli = "claude", members = Array.Empty<object>() }))!["teamId"]!.GetValue<long>();
            await rig.Call("teams.setHireList", new { team = id, rows = new object[] { new { role = "Tester", agent = "claude", model = (string?)null } } });
        }
        var refused = await rig.Refused("roles.remove", new { name = "Tester" });
        Assert.Equal("in_use", refused.Code);
        Assert.Equal("Role Tester is used by the hire list of: Team A, Team B", refused.Message);

        await rig.Call("roles.save", new { name = "Checker", job = "Checks it.", agent = "claude", originalName = "Tester" });
        var list = (await rig.Call("teams.hireList", new { team = "Team A" }))!.AsArray();
        Assert.Equal("Checker", Assert.Single(list)!["role"]!.GetValue<string>());
        Assert.Equal("Checks it.", list[0]!["job"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- the hire list: Team settings, defaults, New team

    [Fact]
    public async Task TheHireList_IsSetInTeamSettings_StartsFromTheDefaults_AndIsSetOnNewTeam()
    {
        await using var rig = await Rig.StartAsync();
        Assert.Empty((await rig.Call("teams.defaults"))!["hireList"]!.AsArray());
        await rig.Call("teams.setDefaults", new { hireList = new object[] { new { role = "developer", agent = "codex", model = "gpt-5" }, new { role = "Tester", agent = "claude", model = "" } } });
        var defaults = (await rig.Call("teams.defaults"))!["hireList"]!.AsArray();
        Assert.Equal(["Developer", "Tester"], defaults.Select(r => r!["role"]!.GetValue<string>()));
        Assert.Null(defaults[1]!["model"]); // blank: the agent's default
        Assert.Equal("no_such_role", (await rig.Refused("teams.setDefaults", new { hireList = new object[] { new { role = "Juggler", agent = "claude" } } })).Code);

        // A new team without a hireList starts from the defaults.
        var fromDefaults = (await rig.Call("teams.create", new { name = "alpha", managerCli = "claude", members = Array.Empty<object>() }))!["teamId"]!.GetValue<long>();
        var list = (await rig.Call("teams.hireList", new { teamId = fromDefaults }))!.AsArray();
        Assert.Equal(("Developer", "codex", "gpt-5"), (list[0]!["role"]!.GetValue<string>(), list[0]!["agent"]!.GetValue<string>(), list[0]!["model"]!.GetValue<string>()));
        Assert.Equal("Writes the code and its unit tests on its own branch.", list[0]!["job"]!.GetValue<string>());

        // New team with its own list and a member row with a role (no name: named for the role; its agent and model as given).
        var beta = (await rig.Call("teams.create", new
        {
            name = "beta", managerCli = "claude",
            members = new object[] { new { name = "", cli = "codex", model = "gpt-5-mini", role = "Reviewer" }, new { name = "Developer Zed", cli = "claude" } },
            hireList = new object[] { new { role = "Reviewer", agent = "codex", model = (string?)null } },
        }))!["teamId"]!.GetValue<long>();
        var betaList = (await rig.Call("teams.hireList", new { team = "beta" }))!.AsArray();
        var reviewerRow = Assert.Single(betaList)!;
        var members = await rig.Members("beta");
        var reviewer = members.Single(m => m.Name.StartsWith("Reviewer", StringComparison.Ordinal));
        Assert.Equal(("Reviewer", "codex", "gpt-5-mini"), (reviewer.HireRole, reviewer.Cli, reviewer.Model));
        Assert.Contains("reports findings, does not rewrite", reviewer.Instructions);
        Assert.Null(members.Single(m => m.Name == "DeveloperZed").HireRole);
        Assert.Equal(("Reviewer", "codex"), (reviewerRow["role"]!.GetValue<string>(), reviewerRow["agent"]!.GetValue<string>()));
        Assert.Null(reviewerRow["model"]);
        Assert.Contains("reports findings, does not rewrite", reviewerRow["job"]!.GetValue<string>());
        Assert.Equal(["agent", "job", "model", "role"], reviewerRow.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));

        // teams.agents carries the hire role as member.hireRole; member.role stays manager / member.
        var cards = (await rig.Call("teams.agents", new { teamId = beta }))!.AsArray().Select(c => c!["member"]!).ToList();
        var card = cards.Single(c => c["id"]!.GetValue<long>() == reviewer.Id);
        Assert.Equal(("member", "Reviewer"), (card["role"]!.GetValue<string>(), card["hireRole"]!.GetValue<string>()));
        Assert.Null(cards.Single(c => c["role"]!.GetValue<string>() == "manager")["hireRole"]);

        // Team settings > Hiring replaces the list; an empty list empties it; a role twice or no agent is refused.
        await rig.Call("teams.setHireList", new { team = beta, rows = new object[] { new { role = "Tester", agent = "claude" }, new { role = "Developer", agent = "codex", model = "gpt-5" } } });
        Assert.Equal(["Tester", "Developer"], (await rig.Call("teams.hireList", new { team = beta }))!.AsArray().Select(r => r!["role"]!.GetValue<string>()));
        Assert.Equal("exists", (await rig.Refused("teams.setHireList", new { team = beta, rows = new object[] { new { role = "Tester", agent = "claude" }, new { role = "tester", agent = "codex" } } })).Code);
        Assert.Equal("bad_cli", (await rig.Refused("teams.setHireList", new { team = beta, rows = new object[] { new { role = "Tester", agent = "nonesuch" } } })).Code);
        await rig.Call("teams.setHireList", new { team = beta, rows = Array.Empty<object>() });
        Assert.Empty((await rig.Call("teams.hireList", new { team = beta }))!.AsArray());
    }

    [Fact]
    public void TheNewCallsAreClassified_ReadsForEveryViewer_ChangesAudited()
    {
        Assert.True(UiRpc.Handles("roles.list"));
        Assert.True(AuditLog.IsReadOnly("roles.list"));
        Assert.True(AuditLog.IsReadOnly("teams.hireList"));
        Assert.False(AuditLog.IsReadOnly("teams.setHireList"));
        Assert.False(AuditLog.IsReadOnly("roles.save"));
        Assert.False(AuditLog.IsReadOnly("roles.remove"));
        Assert.Equal(RemoteAccess.Kind.Reads, RemoteAccess.Kinds["teams.hireList"]);
        Assert.Equal(RemoteAccess.Kind.Changes, RemoteAccess.Kinds["roles.remove"]);
    }

    // ---------------------------------------------------------------- hiring follows the list

    [Fact]
    public async Task WithAHireList_HireTakesTheRowsAgentModelAndJob_AndRefusesWhatIsNotOnIt()
    {
        var results = new Dictionary<string, (string Text, bool Error)>();
        await using var rig = await ManagerRunAsync(
            async (r, teamId) => await r.Call("teams.setHireList", new
            {
                team = teamId,
                rows = new object[] { new { role = "Developer", agent = "codex", model = "gpt-5" }, new { role = "Tester", agent = "claude", model = (string?)null } },
            }),
            async run =>
            {
                results["hiring"] = await Tool(run, "hiring", new { });
                results["dev"] = await Tool(run, "hire", new { role = "Developer" });
                results["dev2"] = await Tool(run, "hire", new { role = "developer", cli = "codex" }); // the row's own agent: fine
                results["tester"] = await Tool(run, "hire", new { role = "Tester", name = "Tester Wren" });
                results["noRole"] = await Tool(run, "hire", new { name = "Developer" });
                results["offList"] = await Tool(run, "hire", new { role = "Writer" });
                results["cli"] = await Tool(run, "hire", new { role = "Developer", cli = "claude" });
                results["job"] = await Tool(run, "hire", new { role = "Developer", job = "Developer: anything I like" });
                results["name"] = await Tool(run, "hire", new { role = "Tester", name = "Developer" });
            });

        var hiring = results["hiring"].Text;
        Assert.Contains("\"role\":\"Developer\"", hiring);
        Assert.Contains("\"description\":\"Writes the code and its unit tests on its own branch.\"", hiring);
        Assert.Contains("\"agent\":\"codex\",\"model\":\"gpt-5\",\"ready\":true", hiring);
        Assert.DoesNotContain("Writer", hiring);

        Assert.False(results["dev"].Error, results["dev"].Text);
        Assert.Contains("(codex, gpt-5) as Developer", results["dev"].Text);
        Assert.False(results["dev2"].Error, results["dev2"].Text);
        Assert.False(results["tester"].Error, results["tester"].Text);
        Assert.Contains("hired TesterWren (claude) as Tester", results["tester"].Text);

        Assert.True(results["noRole"].Error);
        Assert.Contains("role_required", results["noRole"].Text);
        Assert.Contains("Developer, Tester", results["noRole"].Text);
        Assert.True(results["offList"].Error);
        Assert.Contains("not_on_list", results["offList"].Text);
        Assert.Contains("the roles you may hire: Developer, Tester", results["offList"].Text);
        Assert.True(results["cli"].Error);
        Assert.Contains("bad_cli", results["cli"].Text);
        Assert.Contains("runs on codex", results["cli"].Text);
        Assert.True(results["job"].Error);
        Assert.Contains("bad_job", results["job"].Text);
        Assert.True(results["name"].Error);
        Assert.Contains("bad_name", results["name"].Text);

        var members = (await rig.Members("Alpha")).Where(m => m.Role == "member").ToList();
        Assert.Equal(3, members.Count); // no count per role: two developers
        var devs = members.Where(m => m.HireRole == "Developer").ToList();
        Assert.Equal(2, devs.Count);
        Assert.All(devs, d => Assert.Equal(("codex", "gpt-5", "Writes the code and its unit tests on its own branch."), (d.Cli, d.Model, d.Instructions)));
        var tester = members.Single(m => m.HireRole == "Tester");
        Assert.Equal(("TesterWren", "claude", ""), (tester.Name, tester.Cli, tester.Model));

        // The change log records the role with the hire.
        var row = Assert.Single(rig.Log.HistoryOfMember("Alpha", devs[0].Name));
        Assert.Equal((AgentChangeLog.HowManagerHire, "codex", "gpt-5"), (row.How, row.ToAgent, row.ToModel));
        Assert.StartsWith("role Developer", row.Detail);
    }

    [Fact]
    public async Task AnEmptyHireList_KeepsTodaysHiring()
    {
        var results = new List<(string Text, bool Error)>();
        await using var rig = await ManagerRunAsync((_, _) => Task.CompletedTask, async run =>
        {
            results.Add(await Tool(run, "hiring", new { }));
            results.Add(await Tool(run, "hire", new { name = "Developer Dana", cli = "codex", job = "Developer: writes the console app" }));
            results.Add(await Tool(run, "hire", new { name = "Tester" }));
            results.Add(await Tool(run, "hire", new { }));
        });
        Assert.StartsWith("[", results[0].Text); // every agent, as before
        Assert.Contains("\"cli\":\"grok\",\"ready\":false", results[0].Text);
        Assert.Contains("hired DeveloperDana (codex) into team Alpha", results[1].Text);
        Assert.False(results[2].Error, results[2].Text);
        Assert.True(results[3].Error);
        Assert.Contains("'name' is required", results[3].Text);
        var members = (await rig.Members("Alpha")).Where(m => m.Role == "member").ToList();
        Assert.Equal("Developer: writes the console app", members.Single(m => m.Name == "DeveloperDana").Instructions);
        Assert.All(members, m => Assert.Null(m.HireRole));
        Assert.Null(Assert.Single(rig.Log.HistoryOfMember("Alpha", "DeveloperDana")).Detail);
    }

    [Fact]
    public async Task TheStatusToolEndsWithTheRunSlots()
    {
        string? status = null;
        await using var rig = await ManagerRunAsync((_, _) => Task.CompletedTask, async run => status = (await Tool(run, "status", new { })).Text);
        Assert.Matches(@"Run slots: \d+ in use of \d+ on this computer; \d+ run\(s\) queued for a slot\.$", status);
    }

    // ---------------------------------------------------------------- the Manager's guidance

    [Fact]
    public void TheManagerSkill_OffersTheParallelWorkGuidance_AndTheHiringSkillTheHireList()
    {
        var manager = SkillCatalog.BuiltIns.Single(s => s.Name == "manager").Body;
        Assert.DoesNotContain("fewest", manager);
        Assert.Contains("Split the work into independent cards that do not share files", manager);
        Assert.Contains("hire as many members of a\n     role as there are independent cards for that role ready now", manager.Replace("\r\n", "\n"));
        Assert.Contains("run-slot limit (`status` shows it)", manager);
        Assert.Contains("heavy-work lease", manager);
        Assert.Contains("Do not hire a member there is no card for yet", manager);
        Assert.Contains("keep them in order rather\n     than adding people", manager.Replace("\r\n", "\n"));
        Assert.Contains("Verify cards", manager); // the rest of the skill stays

        var hiring = SkillCatalog.BuiltIns.Single(s => s.Name == "hiring").Body;
        Assert.Contains("hire list", hiring);
        Assert.Contains("`role`", hiring);
        Assert.DoesNotContain("Most tasks need one developer and one tester", hiring);
    }
}
