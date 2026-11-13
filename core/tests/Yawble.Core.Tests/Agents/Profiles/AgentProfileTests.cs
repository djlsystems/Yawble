using Yawble.Core.Agents.Profiles;
using Yawble.Core.Bus;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Agents.Profiles;

/// <summary>Custom agents, the locked arguments and the append-only agent_changes log. Fakes only; no port, no CLI.</summary>
public class AgentProfileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Usage(params AgentUser[] users) : IAgentUsage
    {
        public List<(AgentUser, string)> Moved { get; } = [];
        public IReadOnlyList<AgentUser> UsersOf(string agent) => users.Where(u => u.Label.EndsWith("@" + agent)).ToList();
        public void Move(AgentUser user, string toAgent) => Moved.Add((user, toAgent));
    }

    private static (TempDb Db, AgentProfileStore Store, AgentChangeLog Log) Make(IAgentUsage? usage = null)
    {
        var db = new TempDb();
        AgentProfileStore? store = null;
        var log = new AgentChangeLog(db.ConnectionString, null, (a, m) => store!.ResolveModel(a, m));
        store = new AgentProfileStore(db.ConnectionString, log, usage);
        return (db, store, log);
    }

    [Theory]
    [InlineData(AgentCli.Claude, "--mcp-config")]
    [InlineData(AgentCli.Claude, "--strict-mcp-config")]
    [InlineData(AgentCli.Claude, "--session-id")]
    [InlineData(AgentCli.Claude, "--output-format=json")]
    [InlineData(AgentCli.Claude, "-p")]
    [InlineData(AgentCli.Claude, "--dangerously-skip-permissions")]
    [InlineData(AgentCli.Grok, "--always-approve")]
    [InlineData(AgentCli.Codex, "--dangerously-bypass-approvals-and-sandbox")]
    [InlineData(AgentCli.Antigravity, "--settings")]
    [InlineData(AgentCli.Claude, "--model")]
    public void LockedArgumentsAreRefusedWithTheirReason_InEveryList(AgentCli cli, string arg)
    {
        foreach (var lists in new[] { (new[] { arg }, Array.Empty<string>(), Array.Empty<string>()), ([], new[] { arg }, []), ([], [], new[] { arg }) })
        {
            var problem = Assert.Single(AgentArgsValidator.Validate(cli, lists.Item1, lists.Item2, lists.Item3));
            Assert.Contains(arg.Split('=')[0], problem.Reason);
            Assert.True(problem.Reason.Length > arg.Length + 10, "a reason is given");
        }
    }

    [Fact]
    public void FreeArgumentsPass_AndShellMetacharactersAreJustCharacters_ButLineBreaksAreRefused()
    {
        Assert.Empty(AgentArgsValidator.Validate(AgentCli.Codex, ["-c", "model_reasoning_effort=high; rm -rf /"], ["$(whoami)", "a && b"], ["-c check_for_update_on_startup=false"]));
        Assert.NotEmpty(AgentArgsValidator.Validate(AgentCli.Claude, ["a\nb"], [], []));
        Assert.NotEmpty(AgentArgsValidator.Validate(AgentCli.Claude, [""], [], []));
    }

    [Fact]
    public void SavingALockedArgumentFails_AndKeepsWhatWasSaved()
    {
        var (db, store, log) = Make();
        using var _ = db;
        var p = store.Duplicate(AgentCli.Claude, "Claude - Opus long runs", "opus");
        var e = Assert.Throws<AgentProfileException>(() => store.Update(p.Id, p.Name, "opus", ["--session-id", "x"], [], []));
        Assert.Contains("--session-id", e.Message);
        Assert.Empty(store.Get(p.Id)!.ArgsBefore);
        Assert.Empty(log.HistoryOfAgent(p.Name));
    }

    [Fact]
    public void ACustomAgentRunsWithItsArguments_KeepsTheBaseArgumentsAndTheBaseCli_AndTheLastItemsStayLast()
    {
        var (db, store, _) = Make();
        using var _ = db;
        var p = store.Duplicate(AgentCli.Codex, "Codex - high effort");
        p = store.Update(p.Id, p.Name, "gpt-x", ["--add-dir", "fast"], ["--color", "never"], ["-c check_for_update_on_startup=false"]);
        var layout = PolicyFixtures.Run(TargetOs.Windows, CredentialMode.CodexGatewaySignIn).Layout;
        var mode = CredentialMode.CodexGatewaySignIn;
        var plain = AgentPresets.Arguments(layout, mode, RunKind.Headless, "the prompt", "s-1", null);
        var argv = AgentPresets.Arguments(layout, mode, RunKind.Headless, "the prompt", "s-1", null, AgentProfileStore.Customization(p));
        var launch = AgentPresets.Launch(layout).Count;
        Assert.Equal(plain.Take(launch).Concat(["--add-dir", "fast"]).Concat(plain.Skip(launch).Take(plain.Count - launch - 1)
            .Where(a => a != "-c" && a != "check_for_update_on_startup=false")).Concat(["--color", "never", "the prompt"]), argv);
        Assert.Equal("the prompt", argv[^1]);
        Assert.Equal(AgentCli.Codex, store.CliOf(p.Name)); // detection, sign-in, credential mode, grants and hosts all follow the base CLI
        Assert.Null(AgentProfileStore.Customization(store.Get("codex"))); // a built-in is left as it is
    }

    [Fact]
    public void ModelResolution_MemberBeatsAgentDefaultBeatsCliDefault()
    {
        var (db, store, _) = Make();
        using var _ = db;
        Assert.Equal("", store.ResolveModel("claude", ""));
        store.SetDefaultModel(AgentCli.Claude, "sonnet");
        Assert.Equal("sonnet", store.ResolveModel("claude", ""));
        Assert.Equal("opus", store.ResolveModel("claude", "opus"));
        var custom = store.Duplicate(AgentCli.Claude, "Mine"); // inherits the base's default model
        Assert.Equal("sonnet", store.ResolveModel(custom.Name, " "));
    }

    [Fact]
    public void EachKindOfChangeWritesExactlyOneRow_WithTheResolvedModelAndTheActor()
    {
        var (db, store, log) = Make();
        using var _ = db;
        var cloud = Actor.CloudViewer("ines");
        store.SetDefaultModel(AgentCli.Claude, "sonnet", AgentChangeLog.HowSettings, Actor.Window);
        var p = store.Duplicate(AgentCli.Claude, "Mine");

        // default model of an agent
        store.SetDefaultModel(AgentCli.Claude, "opus", AgentChangeLog.HowSettings, cloud);
        var d = Assert.Single(log.HistoryOfAgent("claude"), c => c.ToModel == "opus");
        Assert.Equal((AgentChangeLog.KindAgentDefaultModel, "agent:claude", "sonnet", "opus", "opus", "cloud", "ines"), (d.Kind, d.Subject, d.FromModel, d.ToModel, d.EffectiveModel, d.ActorVia, d.ActorUser));
        store.SetDefaultModel(AgentCli.Claude, "opus"); // no change, no row
        Assert.Equal(2, log.HistoryOfAgent("claude").Count);

        // custom agent arguments (one row even when several lists change)
        store.Update(p.Id, p.Name, "opus", ["--effort", "high"], ["--x=1"], [], AgentChangeLog.HowSettings, Actor.Window);
        var a = Assert.Single(log.HistoryOfAgent("Mine"), c => c.Kind == AgentChangeLog.KindCustomAgentArgs);
        Assert.Equal(("agent:Mine", "opus", "app", "settings"), (a.Subject, a.EffectiveModel, a.ActorVia, a.How));
        Assert.Contains("before", a.Detail);

        // member, Manager, Concierge: agent and model; the effective model follows the agent default when the member sets none
        var m = log.RecordSubjectChange(AgentChangeLog.KindMemberAgent, AgentChangeLog.MemberSubject("payments", "Ada"), new("claude", "haiku"), new("Mine", ""), AgentChangeLog.HowConciergeTool, new Actor(Actor.Concierge, null))!;
        Assert.Equal(("claude", "Mine", "haiku", "", "opus", "concierge", "concierge-tool"), (m.FromAgent, m.ToAgent, m.FromModel, m.ToModel, m.EffectiveModel, m.ActorVia, m.How));
        Assert.NotNull(log.RecordSubjectChange(AgentChangeLog.KindManagerAgent, AgentChangeLog.ManagerSubject("payments"), new("claude", ""), new("grok", ""), AgentChangeLog.HowTeamCreate, Actor.Window));
        Assert.NotNull(log.RecordSubjectChange(AgentChangeLog.KindConciergeAgent, AgentChangeLog.ConciergeSubject("c1"), new("claude", ""), new("grok", "grok-4"), AgentChangeLog.HowSettings, Actor.Window));
        Assert.Null(log.RecordSubjectChange(AgentChangeLog.KindMemberAgent, "member:x/y", new("claude", "a"), new("CLAUDE", "a"), "settings")); // nothing changed

        Assert.Single(log.HistoryOfMember("payments", "Ada"));
        Assert.Equal(7, log.All().Count); // + Mine's default model, set by the same Update as its arguments
        Assert.Contains(log.HistoryOfAgent("Mine"), c => c.Subject == "member:payments/Ada"); // history per agent includes who moved to it
    }

    [Fact]
    public void TheLogIsAppendOnly()
    {
        var (db, store, log) = Make();
        using var _ = db;
        store.SetDefaultModel(AgentCli.Grok, "grok-4");
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE agent_changes SET to_model = 'x';";
        Assert.Contains("append-only", Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery()).Message);
        cmd.CommandText = "DELETE FROM agent_changes;";
        Assert.Contains("append-only", Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery()).Message);
        Assert.Single(log.All());
    }

    [Fact]
    public void DeletingACustomAgentInUseIsRefusedNamingTheMembers_OrMovesThemToTheBase()
    {
        var ada = new AgentUser("member", "member:p/Ada", "p/Ada@Mine", "haiku");
        var usage = new Usage(ada, new AgentUser("manager", "manager:p", "p/Manager@Mine", ""));
        var (db, store, log) = Make(usage);
        using var _ = db;
        var p = store.Duplicate(AgentCli.Grok, "Mine");
        var e = Assert.Throws<AgentProfileException>(() => store.Delete(p.Id));
        Assert.Contains("p/Ada", e.Message);
        Assert.Contains("p/Manager", e.Message);
        Assert.NotNull(store.Get(p.Id));
        Assert.Throws<AgentProfileException>(() => store.Update(p.Id, "Renamed", "", [], [], []));

        store.Delete(p.Id, moveToBase: true);
        Assert.Null(store.Get(p.Id));
        Assert.Equal(2, usage.Moved.Count);
        Assert.All(usage.Moved, x => Assert.Equal("grok", x.Item2));
        Assert.Equal(2, log.All().Count(c => c.FromAgent == "Mine" && c.ToAgent == "grok"));
    }

    [Fact]
    public void NamesAreChecked()
    {
        var (db, store, _) = Make();
        using var _ = db;
        store.Duplicate(AgentCli.Claude, "Mine");
        Assert.Throws<AgentProfileException>(() => store.Duplicate(AgentCli.Claude, "mine"));
        Assert.Throws<AgentProfileException>(() => store.Duplicate(AgentCli.Claude, "Claude"));
        Assert.Throws<AgentProfileException>(() => store.Duplicate(AgentCli.Claude, "  "));
        Assert.Equal(6, store.Choices().Count); // five built-ins and one custom agent
    }

    [Fact]
    public async Task ChangingAMembersAgentOrModelThroughTheBusWritesOneRowAndNothingWhenUnchanged()
    {
        using var db = new TempDb();
        var log = new AgentChangeLog(db.ConnectionString);
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false }) { AgentChanges = log };
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        var ada = db.OpenStore().MemberRows().Single(m => m.Name == "Ada");
        using (Actor.Use(Actor.CloudViewer("ines")))
            await engine.UpdateMemberAsync(ada.Id, "grok", "", "grok-4", "default", Ct);
        await engine.UpdateMemberAsync(ada.Id, "grok", "other instructions", "grok-4", "default", Ct); // instructions only: no row
        var rows = log.HistoryOfMember("Alpha", "Ada"); // newest first: the change, then the add (its first agent)
        Assert.Equal(2, rows.Count);
        Assert.Equal(("", "claude"), (rows[1].FromAgent, rows[1].ToAgent));
        var row = rows[0];
        Assert.Equal(("claude", "grok", "grok-4", "cloud", "ines", "settings"), (row.FromAgent, row.ToAgent, row.EffectiveModel, row.ActorVia, row.ActorUser, row.How));
        var manager = db.OpenStore().MemberRows().Single(m => m.Role == "manager");
        await engine.UpdateMemberAsync(manager.Id, "claude", "", "opus", "default", Ct);
        Assert.All(log.HistoryOfSubject("manager:Alpha"), r => Assert.Equal(AgentChangeLog.KindManagerAgent, r.Kind));
        Assert.Equal(2, log.HistoryOfSubject("manager:Alpha").Count); // team_create, then the model change
    }

    [Fact]
    public async Task AnAgentChangeFromTheCompanionIsLoggedAsCompanion_NotSettings()
    {
        using var db = new TempDb();
        var log = new AgentChangeLog(db.ConnectionString);
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false }) { AgentChanges = log };
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        using (Actor.Use(Actor.ExternalClient("cc")))
            await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        var ada = db.OpenStore().MemberRows().Single(m => m.Name == "Ada");
        using (Actor.Use(Actor.ExternalClient("cc")))
            await engine.UpdateMemberAsync(ada.Id, "grok", "", "grok-4", "default", Ct);
        var rows = log.HistoryOfMember("Alpha", "Ada"); // newest first
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((AgentChangeLog.HowCompanion, "external"), (r.How, r.ActorVia)));
        Assert.Equal("companion", AgentChangeLog.HowCompanion);
    }

    [Fact]
    public async Task TheRpcListsDuplicatesChecksAndShowsHistory_AndIsNotAnAllowedRemoteMethod()
    {
        var (db, store, log) = Make();
        using var _ = db;
        var rpc = new AgentProfileRpc(store, log);
        Assert.True(AgentProfileRpc.Handles("agentProfiles.list"));
        Assert.NotNull(Yawble.Core.Link.RemoteAccess.Refusal("agentProfiles.history", null, false));
        var made = (await rpc.DispatchAsync("agentProfiles.duplicate", new() { ["baseCli"] = "claude", ["name"] = "Mine" }, Ct))!.AsObject();
        Assert.Equal("claude", (string?)made["baseCli"]);
        var bad = (await rpc.DispatchAsync("agentProfiles.check", new() { ["baseCli"] = "claude", ["argsBefore"] = new System.Text.Json.Nodes.JsonArray("--mcp-config") }, Ct))!.AsArray();
        Assert.Single(bad);
        await rpc.DispatchAsync("agentProfiles.setDefaultModel", new() { ["baseCli"] = "claude", ["model"] = "opus" }, Ct);
        var history = (await rpc.DispatchAsync("agentProfiles.history", new() { ["agent"] = "claude" }, Ct))!.AsArray();
        Assert.Equal("opus", (string?)history[0]!["effectiveModel"]);
        Assert.Equal(6, (await rpc.DispatchAsync("agentProfiles.list", null, Ct))!.AsArray().Count);
    }
}
