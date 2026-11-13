using System.Text.Json.Nodes;
using Yawble.Core.Agents;
using Yawble.Core.Agents.Profiles;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Launch;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Agents.Profiles;

/// <summary>
/// Every kind of agent or model change writes its agent_changes row, including the less obvious ones: the Concierge's
/// agent, agents.setDefaultModel, a team's first Manager and members (team_create, members.add)
/// and a Manager's hire. Fakes only: temp SQLite, scripted runners, no CLI.
/// </summary>
public class AgentChangeCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentChangeLog Log(TempDb db) => new(db.ConnectionString, null, (_, m) => m);

    [Fact]
    public async Task AgentsSetDefaultModel_WritesOneRowPerRealChange_WithTheActorAndHow()
    {
        await using var kit = await LaunchKit.StartAsync();
        var log = Log(kit.Db);
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(), kit.Clis, kit.Records,
            new SqliteMemberDirectory(kit.Db.ConnectionString), new NoTerminal(), null, log);

        using (Actor.Use(Actor.Window))
        {
            await rpc.DispatchAsync("agents.setDefaultModel", new JsonObject { ["cli"] = "claude", ["model"] = "sonnet" }, Ct);
            await rpc.DispatchAsync("agents.setDefaultModel", new JsonObject { ["cli"] = "claude", ["model"] = "sonnet" }, Ct); // no change: no row
        }
        using (Actor.Use(new Actor(Actor.Concierge, null)))
            await rpc.DispatchAsync("agents.setDefaultModel", new JsonObject { ["cli"] = "claude", ["model"] = "" }, Ct);

        var rows = log.HistoryOfAgent("claude").Where(r => r.Kind == AgentChangeLog.KindAgentDefaultModel).Reverse().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("agent:claude", "", "sonnet", "sonnet", "app", "settings"), (rows[0].Subject, rows[0].FromModel, rows[0].ToModel, rows[0].EffectiveModel, rows[0].ActorVia, rows[0].How));
        Assert.Equal(("sonnet", "", AgentChangeLog.CliDefault, "concierge", "concierge-tool"), (rows[1].FromModel, rows[1].ToModel, rows[1].EffectiveModel, rows[1].ActorVia, rows[1].How));
    }

    [Fact]
    public async Task TeamCreate_MembersAdd_AndAHire_WriteTheFirstAgentRow()
    {
        using var db = new TempDb();
        var log = Log(db);
        await using var bus = new BusEngine(db.OpenStore(), new HiringRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        bus.AgentChanges = log;
        await bus.StartAsync(Ct);

        long teamId;
        using (Actor.Use(Actor.Window))
        {
            teamId = await bus.CreateTeamAsync("Payments", "Manager", "grok", null, Ct);
            await bus.AddMemberAsync(teamId, "Developer Ada", "codex", Ct, AgentChangeLog.HowTeamCreate);
            await bus.AddMemberAsync(teamId, "Tester Ben", "claude", Ct);
        }

        var manager = Assert.Single(log.HistoryOfSubject(AgentChangeLog.ManagerSubject("Payments")));
        Assert.Equal((AgentChangeLog.KindManagerAgent, "", "grok", "team_create", "app"), (manager.Kind, manager.FromAgent, manager.ToAgent, manager.How, manager.ActorVia));
        var ada = Assert.Single(log.HistoryOfMember("Payments", "DeveloperAda"));
        Assert.Equal((AgentChangeLog.KindMemberAgent, "codex", "team_create", AgentChangeLog.CliDefault), (ada.Kind, ada.ToAgent, ada.How, ada.EffectiveModel));
        Assert.Equal("settings", Assert.Single(log.HistoryOfMember("Payments", "TesterBen")).How);

        // The Manager hires through its member tool: one row, the Manager is the actor.
        await bus.PersonTellAsync("Payments", "Manager", "Hire a developer.", ct: Ct);
        await Wait.UntilAsync(() => log.HistoryOfMember("Payments", "DeveloperPriya").Count == 1, "the hire's row");
        var hired = Assert.Single(log.HistoryOfMember("Payments", "DeveloperPriya"));
        Assert.Equal(("grok", AgentChangeLog.HowManagerHire, Actor.Agent, "Payments/Manager"), (hired.ToAgent, hired.How, hired.ActorVia, hired.ActorUser));
    }

    [Fact]
    public async Task TheConciergesAgentChange_WritesARow_AndItsFirstAgentToo()
    {
        using var db = new TempDb();
        var log = Log(db);
        await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner());
        bus.AgentChanges = log;
        await bus.StartAsync(Ct);
        var service = new ConciergeService(new ConciergeStore(db.ConnectionString), bus,
            (_, _) => Task.FromResult(new LaunchOutcome("r-x", RunEnd.StartFailed, "not signed in (fake)", null, [])),
            new RunRegistry(), new AppEvents(), (_, _, _) => Task.FromResult<JsonNode?>(null), _ => Task.FromResult("claude"));

        string id;
        using (Actor.Use(Actor.Window))
        {
            id = (await service.DispatchAsync("concierge.send", new JsonObject { ["text"] = "hello" }, Ct))!["id"]!.GetValue<string>();
            await Wait.UntilAsync(async () => (await service.DispatchAsync("concierge.get", new JsonObject { ["id"] = id }, Ct))!["messages"]!.AsArray().Count == 2, "the first answer");
            await service.DispatchAsync("concierge.send", new JsonObject { ["id"] = id, ["text"] = "again", ["cli"] = "codex" }, Ct);
        }

        var rows = log.HistoryOfSubject(AgentChangeLog.ConciergeSubject(id)).Reverse().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(AgentChangeLog.KindConciergeAgent, r.Kind));
        Assert.Equal(("", "claude"), (rows[0].FromAgent, rows[0].ToAgent));
        Assert.Equal(("claude", "codex", "app"), (rows[1].FromAgent, rows[1].ToAgent, rows[1].ActorVia));
    }

    private sealed class NoTerminal : IUpdateTerminal
    {
        public void Open(string title, IReadOnlyList<string> command) => throw new InvalidOperationException("no terminal in this test");
    }

    /// <summary>A Manager's run that hires one member through the member tool, then ends.</summary>
    private sealed class HiringRunner : IAgentRunner
    {
        public async Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
        {
            if (run.Member.Role == MemberRole.Manager) await run.HireAsync("Developer Priya", null, null, ct);
            return RunResult.Ok;
        }
    }
}
