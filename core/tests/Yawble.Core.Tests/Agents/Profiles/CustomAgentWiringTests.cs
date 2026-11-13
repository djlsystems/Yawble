using System.Text.Json.Nodes;
using Yawble.Core.Agents;
using Yawble.Core.Agents.Profiles;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Xunit;

namespace Yawble.Core.Tests.Agents.Profiles;

/// <summary>Card 52: a member whose cli is a custom agent's name resolves to its profile everywhere (members.add/update/team_create, launch, run records, usage). Fakes only.</summary>
public class CustomAgentWiringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AgentProfileStore Store, AgentProfile Mine) Profile(string cs, AgentCli baseCli = AgentCli.Claude)
    {
        AgentProfileStore? store = null;
        var log = new AgentChangeLog(cs, null, (a, m) => store!.ResolveModel(a, m));
        store = new AgentProfileStore(cs, log, new SqliteAgentUsage(cs));
        var p = store.Duplicate(baseCli, "Mine");
        p = store.Update(p.Id, p.Name, "haiku", ["--custom-before", "b"], ["--custom-after", "x"], []);
        return (store, p);
    }

    private static string[] Argv(RunRecordStore.Record r) => JsonNode.Parse(r.Argv!)!.AsArray().Select(a => a!.GetValue<string>()).ToArray();

    [Fact]
    public void TheResolverKnowsBuiltInsAndCustomAgents_AndRefusesTheRest()
    {
        using var db = new TempDb();
        var (store, mine) = Profile(db.ConnectionString, AgentCli.Grok);
        var r = store.Resolve("mine");
        Assert.Equal((AgentCli.Grok, true, "haiku", "Mine"), (r.Cli, r.Custom, r.DefaultModel, r.Name));
        Assert.Equal(["--custom-before", "b"], r.Argv!.Before);
        Assert.Equal(("grok", false), (store.Resolve("GROK").Name, store.Resolve("grok").Custom));
        Assert.Null(store.Resolve("grok").Argv);
        Assert.Throws<ArgumentException>(() => store.Resolve("nobody"));
        Assert.Equal("Mine", store.Canonical("mine"));
        Assert.Null(store.Canonical("nobody"));
        Assert.Equal(AgentCli.Claude, ((IAgentResolver?)null).Resolve("claude").Cli); // without a resolver: built-ins only
        Assert.Contains("Mine", store.Names());
        _ = mine;
    }

    [Fact]
    public async Task MembersAddUpdateAndTeamCreateAcceptACustomAgent_AndRefuseAnUnknownOne()
    {
        using var db = new TempDb();
        var (store, _) = Profile(db.ConnectionString);
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false }) { AgentResolver = store };

        var team = await engine.CreateTeamAsync("Alpha", "Boss", "Mine", null, Ct); // team_create: a custom Manager
        var ada = await engine.AddMemberAsync(team, "Developer Ada", "mine", Ct);   // members.add (the name's case is normalised)
        Assert.Equal("Mine", ada.Cli);
        Assert.Equal("Mine", db.OpenStore().MemberRows(team).Single(m => m.Role == "manager").Cli);

        var bad = await Assert.ThrowsAsync<BusRefusal>(() => engine.AddMemberAsync(team, "Developer Bob", "nobody", Ct));
        Assert.Equal("bad_cli", bad.Code);
        Assert.Contains("Mine", bad.Message);
        await Assert.ThrowsAsync<BusRefusal>(() => engine.CreateTeamAsync("Beta", "Boss", "nobody", null, Ct));

        // members.update to and from a custom agent; the credential modes are the base CLI's (claude: subscription is one).
        var grok = await engine.UpdateMemberAsync(ada.Id, "grok", "", "", "default", Ct);
        Assert.Equal("grok", grok.Cli);
        var back = await engine.UpdateMemberAsync(ada.Id, "Mine", "", "", "subscription", Ct);
        Assert.Equal(("Mine", "subscription"), (back.Cli, back.Credential));
        await Assert.ThrowsAsync<BusRefusal>(() => engine.UpdateMemberAsync(ada.Id, "Mine", "", "", "login", Ct)); // grok's mode, not claude's
        await Assert.ThrowsAsync<BusRefusal>(() => engine.UpdateMemberAsync(ada.Id, "nobody", "", "", "default", Ct));

        // Without a resolver a custom name is not an agent.
        await using var plain = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await Assert.ThrowsAsync<BusRefusal>(() => plain.AddMemberAsync(team, "Developer Cy", "Mine", Ct));
    }

    [Fact]
    public async Task ACustomAgentMemberLaunchesWithTheProfilesArgv_AndTheRunRecordsAgentModelVersionAndArgv()
    {
        await using var kit = await LaunchKit.StartAsync();
        var (store, mine) = Profile(kit.Db.ConnectionString);
        kit.Bus.AgentResolver = store;
        var team = (await kit.Bus.TeamsAsync(Ct)).Single(t => t.Name == "alpha").Id;
        await kit.Bus.AddMemberAsync(team, "custom1", "Mine", Ct);

        var launchMember = new SqliteMemberDirectory(kit.Db.ConnectionString).Find("alpha", "custom1")!;
        Assert.Equal((AgentCli.Claude, "Mine", "haiku"), (launchMember.Cli, launchMember.Agent, launchMember.AgentModel));
        Assert.Equal(["--custom-after", "x"], launchMember.Custom!.After);
        Assert.Equal(("haiku", ModelSources.Agent), (kit.Launcher.ResolveModel(launchMember).Model, kit.Launcher.ResolveModel(launchMember).Source));

        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };
        await kit.Bus.PersonTellAsync("alpha", "custom1", "do it", ct: Ct);
        await Wait.UntilAsync(() => kit.Records.All().Any(r => r.Member == "custom1" && r.EndClass is not null), "custom1's run");
        var rec = kit.Records.All().Single(r => r.Member == "custom1");
        var argv = Argv(rec);
        Assert.Equal(("Mine", "claude", "haiku", ModelSources.Agent), (rec.Agent, rec.Cli, rec.Model, rec.ModelSource));
        Assert.NotNull(rec.CliVersion);
        Assert.Contains("--custom-before", argv);
        Assert.Contains("--custom-after", argv);
        Assert.Equal("haiku", argv[Array.IndexOf(argv, "--model") + 1]);
        Assert.True(Array.IndexOf(argv, "--custom-before") < Array.IndexOf(argv, "--custom-after"));

        // The member's own model still wins over the custom agent's default; a built-in member of the same base has no custom argv.
        using (var s = new BusStore(kit.Db.ConnectionString)) s.UpdateMember(launchMember.Id, "Mine", "", "opus", "default");
        Assert.Equal(("opus", ModelSources.Member), (kit.Launcher.ResolveModel(new SqliteMemberDirectory(kit.Db.ConnectionString).Find("alpha", "custom1")!).Model, ModelSources.Member));
        var dev1 = new SqliteMemberDirectory(kit.Db.ConnectionString).Find("alpha", "dev1")!;
        Assert.Null(dev1.Custom);
        Assert.Null(dev1.Agent);
        _ = mine;
    }

    [Fact]
    public async Task DeletingACustomAgentThatMembersRunOnIsRefusedNamingThem_OrMovesThemToTheBaseWithOneLogRowEach()
    {
        using var db = new TempDb();
        var (store, mine) = Profile(db.ConnectionString);
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false }) { AgentResolver = store };
        var team = await engine.CreateTeamAsync("Alpha", "Boss", "Mine", null, Ct);
        var ada = await engine.AddMemberAsync(team, "Developer Ada", "Mine", Ct);
        // A Concierge chat on it counts too.
        new Yawble.Core.Concierge.ConciergeStore(db.ConnectionString).Create("c1", "Plan", "Mine", null);

        var e = Assert.Throws<AgentProfileException>(() => store.Delete(mine.Id));
        Assert.Contains($"Alpha/{ada.Name}", e.Message);
        Assert.Contains("Alpha/Boss", e.Message);
        Assert.Contains("Plan", e.Message);
        Assert.NotNull(store.Get(mine.Id));
        Assert.Throws<AgentProfileException>(() => store.Update(mine.Id, "Renamed", "", [], [], [])); // no rename while in use

        store.Delete(mine.Id, moveToBase: true);
        Assert.Null(store.Get("Mine"));
        Assert.All(db.OpenStore().MemberRows(team), m => Assert.Equal("claude", m.Cli));
        Assert.Equal("claude", new Yawble.Core.Concierge.ConciergeStore(db.ConnectionString).Get("c1")!.Cli);
        var log = new AgentChangeLog(db.ConnectionString);
        Assert.Equal(3, log.All().Count(c => c.FromAgent == "Mine" && c.ToAgent == "claude"));
        Assert.Empty(new SqliteAgentUsage(db.ConnectionString).UsersOf("Mine"));
    }
}


