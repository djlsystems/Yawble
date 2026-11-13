using System.Text.Json.Nodes;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Each checklist row is allowed / ask first / off / never; a team may override it; reset to default.</summary>
public class ChecklistStatesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static WalkOptions Fast => new() { Parallelism = 4, CallTimeout = TimeSpan.FromSeconds(2) };

    private static async Task<DiscoverRig> GhRig()
    {
        var rig = new DiscoverRig();
        var o = await rig.Svc.DiscoverAsync("gh", new DiscoverOptions { Runner = new FixtureCli("gh/2.45").Runner, Version = "gh version 2.45.0", Walk = Fast }, null, Ct);
        Assert.Null(o.Error);
        // the fixture only has the help of "pr": add the rows the checklist would show from sightings in use
        var store = new DiscoverStore(rig.Db.ConnectionString, TimeProvider.System);
        var id = rig.Scalar("SELECT id FROM integrations WHERE name = 'gh'");
        foreach (var (path, parent) in new[] { ("repo delete", "repo"), ("auth token", "auth"), ("issue list", "issue") })
            store.UpsertRow(id, path, parent, "", CommandClassifier.Classify("gh", CliCatalog.Of("gh"), path, false), RowOrigin.Found);
        return rig;
    }

    private static string Decide(DiscoverRig rig, long? team, params string[] args)
    {
        var (t, g) = rig.RulesFor("gh", team);
        return CliRules.Decide(args, CliCatalog.Of("gh"), t, g).Decision;
    }

    private static ChecklistNode Node(DiscoverRig rig, long? team, string path) => rig.Svc.GetChecklist("gh", team).Nodes.Single(n => n.Path == path);

    private static RowChoice C(string path, string state) => new(path, state);

    [Fact]
    public async Task SuggestedStates_FollowTheClass()
    {
        using var rig = await GhRig();
        var nodes = rig.Svc.GetChecklist("gh", null).Nodes.ToDictionary(n => n.Path);
        Assert.Equal(RowStates.Allow, nodes["pr list"].Suggested);
        Assert.Equal(RowStates.Ask, nodes["pr create"].Suggested);
        Assert.Equal(RowStates.Off, nodes["repo delete"].Suggested);
        Assert.Equal(RowStates.Never, nodes["auth token"].Suggested);
        Assert.Null(nodes["pr"].Suggested); // a group

        new DiscoverStore(rig.Db.ConnectionString, TimeProvider.System).UpsertRow(rig.Scalar("SELECT id FROM integrations WHERE name = 'gh'"), "model thing", "",
            "", new Classification(CommandClass.ReadOnly, ClassSource.Model, "the model said so"), RowOrigin.Found);
        Assert.Equal(RowStates.Ask, Node(rig, null, "model thing").Suggested);

        Assert.Equal(RowStates.Allow, DiscoverService.Suggested(CommandClass.ReadOnly, ClassSource.Floor));
        Assert.Equal(RowStates.Allow, DiscoverService.Suggested(CommandClass.ReadOnly, "annotation"));
        Assert.Equal(RowStates.Ask, DiscoverService.Suggested(CommandClass.Unknown, ClassSource.None));
        Assert.Equal(RowStates.Ask, DiscoverService.Suggested(CommandClass.Change, ClassSource.Verb));
    }

    [Fact]
    public async Task SaveChoices_WritesAllowAskAndOff_AndTheCallFollowsThem()
    {
        using var rig = await GhRig();
        var r = rig.Svc.SaveChoices("gh", null, [C("pr list", "allow"), C("pr create", "ask"), C("pr merge", "off")]);
        Assert.Equal(new SaveResult(1, 1, 1, 0), r);
        Assert.Equal(Decisions.Allow, Decide(rig, null, "pr", "list"));
        Assert.Equal(Decisions.Ask, Decide(rig, null, "pr", "create", "--fill"));
        Assert.Equal(Decisions.Deny, Decide(rig, null, "pr", "merge", "5"));
        Assert.Equal(RowStates.Off, Node(rig, null, "pr merge").Effective == Decisions.Deny ? RowStates.Off : "?");
        Assert.Equal(Decisions.Allow, Node(rig, null, "pr list").DefaultDecision);
        Assert.Equal(Decisions.Deny, Node(rig, null, "pr merge").DefaultDecision);
        // rows not in the save are untouched (a save that replaced every row would have made "pr view" ask)
        Assert.DoesNotContain(rig.Rules("gh"), x => x.Pattern == "pr view*");
        // and a second save of another row leaves these in place
        rig.Svc.SaveChoices("gh", null, [C("issue list", "off")]);
        Assert.Equal(Decisions.Allow, Decide(rig, null, "pr", "list"));
        Assert.Equal(Decisions.Deny, Decide(rig, null, "issue", "list"));
    }

    [Fact]
    public async Task ATeamOverride_WinsForThatTeamOnly_AndResetToDefaultRemovesIt()
    {
        using var rig = await GhRig();
        var alpha = rig.AddTeam("Alpha");
        var beta = rig.AddTeam("Beta");
        rig.Svc.SaveChoices("gh", null, [C("pr create", "ask")]);
        rig.Svc.SaveChoices("gh", alpha, [C("pr create", "allow")]);

        var a = Node(rig, alpha, "pr create");
        Assert.Equal(Decisions.Allow, a.TeamDecision);
        Assert.Equal(Decisions.Ask, a.DefaultDecision);
        Assert.Equal(Decisions.Allow, a.Effective);
        var b = Node(rig, beta, "pr create");
        Assert.Null(b.TeamDecision);
        Assert.Equal(Decisions.Ask, b.Effective);
        Assert.Equal(Decisions.Allow, Decide(rig, alpha, "pr", "create"));
        Assert.Equal(Decisions.Ask, Decide(rig, beta, "pr", "create"));

        var r = rig.Svc.SaveChoices("gh", alpha, [C("pr create", "default")]);
        Assert.Equal(1, r.Removed);
        var a2 = Node(rig, alpha, "pr create");
        Assert.Null(a2.TeamDecision);
        Assert.Equal(Decisions.Ask, a2.Effective);
        Assert.DoesNotContain(rig.Rules("gh"), x => x.Team == alpha);
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM integration_choices WHERE team_id IS NOT NULL"));
    }

    [Fact]
    public async Task ATeamChoiceEqualToTheDefaultIsNotStored()
    {
        using var rig = await GhRig();
        var alpha = rig.AddTeam("Alpha");
        rig.Svc.SaveChoices("gh", null, [C("pr create", "ask"), C("pr list", "allow")]);
        var r = rig.Svc.SaveChoices("gh", alpha, [C("pr create", "ask"), C("pr list", "allow")]);
        Assert.Equal(new SaveResult(0, 0, 0, 0), r);
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM integration_choices WHERE team_id IS NOT NULL"));
        Assert.DoesNotContain(rig.Rules("gh"), x => x.Team == alpha);

        // an existing override that is now set to what the default says is removed, not rewritten
        rig.Svc.SaveChoices("gh", alpha, [C("pr create", "allow")]);
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM integration_choices WHERE team_id IS NOT NULL"));
        var back = rig.Svc.SaveChoices("gh", alpha, [C("pr create", "ask")]);
        Assert.Equal(1, back.Removed);
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM integration_choices WHERE team_id IS NOT NULL"));
        Assert.Null(Node(rig, alpha, "pr create").TeamDecision);

        // a preset-equal state counts too: "pr view" is allowed by the preset, so allow is not stored for a team
        rig.Svc.SaveChoices("gh", alpha, [C("pr view", "allow")]);
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM integration_choices WHERE team_id IS NOT NULL"));
    }

    [Fact]
    public async Task ATeamOverrideCanBeStricter()
    {
        using var rig = await GhRig();
        var alpha = rig.AddTeam("Alpha");
        var beta = rig.AddTeam("Beta");
        rig.Svc.SaveChoices("gh", null, [C("pr create", "allow")]);
        rig.Svc.SaveChoices("gh", alpha, [C("pr create", "off")]);
        Assert.Equal(Decisions.Deny, Decide(rig, alpha, "pr", "create"));
        Assert.Equal(Decisions.Deny, Node(rig, alpha, "pr create").Effective);
        Assert.Equal(Decisions.Allow, Decide(rig, beta, "pr", "create"));
    }

    [Fact]
    public async Task UndecidedRemovesTheDefaultChoice_AndTheCallAsksAgain()
    {
        using var rig = await GhRig();
        var alpha = rig.AddTeam("Alpha");
        rig.Svc.SaveChoices("gh", null, [C("pr create", "allow")]);
        Assert.Equal(Decisions.Allow, Decide(rig, null, "pr", "create"));
        var r = rig.Svc.SaveChoices("gh", null, [C("pr create", "undecided")]);
        Assert.Equal(1, r.Removed);
        Assert.Equal(Decisions.Ask, Decide(rig, null, "pr", "create"));
        Assert.Null(Node(rig, null, "pr create").DefaultDecision);
        Assert.Empty(rig.Rules("gh"));

        // default needs a team, undecided must not have one
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("pr create", "default")]));
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", alpha, [C("pr create", "undecided")]));
    }

    [Fact]
    public async Task NeverAndCredentialRowsCannotBeSaved_AndDestructiveCanBeAnyState()
    {
        using var rig = await GhRig();
        var locked = Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("auth token", "allow")]));
        Assert.Contains("never allowed", locked.Message);
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("auth token", "off")]));
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("pr list", "never")]));
        Assert.Contains("pick its commands", Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("pr", "allow")])).Message);
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("pr nuke", "allow")]));
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("pr list", "maybe")]));
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", 999, [C("pr list", "allow")]));
        // a floor that the walk did not class as a credential cannot be loosened either (pr checkout is gh's own floor)
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("pr checkout", "allow")]));
        // a stale class cannot sneak a credential through
        using (var c = rig.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE integration_commands SET class = 'change', class_source = 'verb' WHERE path = 'secret';";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("secret", "allow")]));
        // a rejected save writes nothing, even when an earlier choice in it was fine
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveChoices("gh", null, [C("pr list", "allow"), C("auth token", "allow")]));
        Assert.Empty(rig.Rules("gh"));

        Assert.Equal(1, rig.Svc.SaveChoices("gh", null, [C("repo delete", "allow")]).Allowed);
        Assert.Equal(Decisions.Allow, Decide(rig, null, "repo", "delete", "x"));
        Assert.Equal(1, rig.Svc.SaveChoices("gh", null, [C("repo delete", "ask")]).Asked);
        Assert.Equal(Decisions.Ask, Decide(rig, null, "repo", "delete", "x"));
        Assert.Equal(Decisions.Deny, Decide(rig, null, "auth", "token"));
        Assert.Equal(RowStates.Never, Node(rig, null, "auth token").Effective);
    }

    [Fact]
    public async Task LegacyTickedSaveIsUnchanged()
    {
        using var rig = await GhRig();
        var team = rig.AddTeam("Alpha");
        var r = rig.Svc.SaveChecklist("gh", team, ["pr list", "pr create"]);
        Assert.Equal(2, r.Allowed);
        var rules = rig.Rules("gh");
        Assert.Contains(rules, x => x is (_, "pr list *", Decisions.Allow));
        Assert.Contains(rules, x => x is (_, "pr create *", Decisions.Allow));
        Assert.Contains(rules, x => x is (_, "pr checkout *", Decisions.Deny));
        Assert.Contains(rules, x => x is (_, "pr view *", Decisions.Ask));
        // destructive ticks are refused on the ticked-list save too
        Assert.Contains("refused", Assert.Throws<ArgumentException>(() => rig.Svc.SaveChecklist("gh", null, ["repo delete"])).Message);
        // the checklist's ticked fields read the same
        var n = Node(rig, team, "pr create");
        Assert.True(n.Ticked);
        Assert.Equal("choice", n.TickedFrom);
        Assert.Equal(Decisions.Allow, n.TeamDecision);
    }

    [Fact]
    public async Task CountsFollowTheScope()
    {
        using var rig = await GhRig();
        var alpha = rig.AddTeam("Alpha");
        var before = rig.Svc.GetChecklist("gh", null);
        Assert.Equal("cli", before.Kind);
        var leaves = before.Nodes.Count(n => !n.HasChildren && !n.Gone);
        Assert.Equal(leaves, before.Counts.Allowed + before.Counts.Ask + before.Counts.Off + before.Counts.Undecided);
        Assert.True(before.Counts.Undecided > 0);

        rig.Svc.SaveChoices("gh", null, [C("pr create", "allow"), C("pr merge", "ask"), C("pr close", "off")]);
        var after = rig.Svc.GetChecklist("gh", null);
        Assert.Equal(before.Counts.Allowed + 1, after.Counts.Allowed);
        Assert.Equal(before.Counts.Ask + 1, after.Counts.Ask);
        Assert.Equal(before.Counts.Off + 1, after.Counts.Off);
        Assert.Equal(before.Counts.Undecided - 3, after.Counts.Undecided);

        // a team's view counts its own override
        rig.Svc.SaveChoices("gh", alpha, [C("pr create", "off")]);
        var team = rig.Svc.GetChecklist("gh", alpha);
        Assert.Equal(after.Counts.Allowed - 1, team.Counts.Allowed);
        Assert.Equal(after.Counts.Off + 1, team.Counts.Off);
        Assert.Equal(after.Counts.Allowed, rig.Svc.GetChecklist("gh", null).Counts.Allowed);
    }

    [Fact]
    public async Task TheRpcTakesChoices_AndOnlyAnOfferedTeamsOrOneWithChoicesIsServed()
    {
        using var db = new TempDb();
        await using var engine = new Yawble.Core.Bus.BusEngine(db.OpenStore(), new ScriptedRunner(), new Yawble.Core.Bus.BusOptions { IdleOffer = false });
        var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var o = await integrations.Discover.DiscoverAsync("gh", new DiscoverOptions { Runner = new FixtureCli("gh/2.45").Runner, Version = "gh version 2.45.0", Walk = Fast }, null, Ct);
        Assert.Null(o.Error);

        var ask = await Assert.ThrowsAsync<RpcException>(() => integrations.DispatchAsync("integrations.checklist", new JsonObject { ["name"] = "gh", ["teamId"] = alpha }, Ct));
        Assert.Contains("not offered", ask.Message);
        Assert.Contains("offer it first", ask.Message);
        await Assert.ThrowsAsync<RpcException>(() => integrations.DispatchAsync("integrations.saveChecklist",
            new JsonObject { ["name"] = "gh", ["teamId"] = alpha, ["choices"] = new JsonArray(new JsonObject { ["path"] = "pr list", ["state"] = "off" }) }, Ct));

        // the default list needs no offer
        var save = (await integrations.DispatchAsync("integrations.saveChecklist",
            new JsonObject { ["name"] = "gh", ["choices"] = new JsonArray(new JsonObject { ["path"] = "pr create", ["state"] = "ask" }), ["ticked"] = new JsonArray("pr list") }, Ct))!;
        Assert.Equal(1, save["asked"]!.GetValue<int>()); // choices win: the ticked list was ignored
        Assert.Equal(0, save["allowed"]!.GetValue<int>());

        // offer Alpha, give it an override, then un-offer it: the override stays visible and removable
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO integration_teams (integration_id, team_id) SELECT id, $t FROM integrations WHERE name = 'gh';";
            cmd.Parameters.AddWithValue("$t", alpha);
            cmd.ExecuteNonQuery();
        }
        await integrations.DispatchAsync("integrations.saveChecklist",
            new JsonObject { ["name"] = "gh", ["teamId"] = alpha, ["choices"] = new JsonArray(new JsonObject { ["path"] = "pr create", ["state"] = "allow" }) }, Ct);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM integration_teams;";
            cmd.ExecuteNonQuery();
        }
        var cl = (await integrations.DispatchAsync("integrations.checklist", new JsonObject { ["name"] = "gh", ["teamId"] = alpha }, Ct))!;
        var row = cl["nodes"]!.AsArray().Single(n => n!["path"]!.GetValue<string>() == "pr create")!;
        Assert.Equal("allow", row["teamDecision"]!.GetValue<string>());
        Assert.Equal("ask", row["defaultDecision"]!.GetValue<string>());
        Assert.Equal("allow", row["effective"]!.GetValue<string>());
        Assert.Equal("cli", cl["kind"]!.GetValue<string>());
        Assert.NotNull(cl["counts"]!["undecided"]);
        var reset = (await integrations.DispatchAsync("integrations.saveChecklist",
            new JsonObject { ["name"] = "gh", ["teamId"] = alpha, ["choices"] = new JsonArray(new JsonObject { ["path"] = "pr create", ["state"] = "default" }) }, Ct))!;
        Assert.Equal(1, reset["removed"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ edge cases

    private static void AddRule(DiscoverRig rig, long? team, string pattern, string decision)
    {
        using var c = rig.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO integration_rules (integration_id, team_id, pattern, decision, created_at) VALUES ((SELECT id FROM integrations WHERE name = 'gh'), $t, $p, $d, 'x');";
        cmd.Parameters.AddWithValue("$t", (object?)team ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", pattern);
        cmd.Parameters.AddWithValue("$d", decision);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task I2_ATeamChoiceIsComparedWithTheTeamsOtherRules_NotOnlyTheDefault()
    {
        using var rig = await GhRig();
        var alpha = rig.AddTeam("Alpha");
        AddRule(rig, alpha, "pr*", Decisions.Allow); // typed by hand for the team: the whole pr group runs
        Assert.Equal(Decisions.Allow, Decide(rig, alpha, "pr", "create", "--fill"));
        var r = rig.Svc.SaveChoices("gh", alpha, [C("pr create", "ask")]);
        Assert.Equal(1, r.Asked);
        Assert.Equal(Decisions.Ask, Node(rig, alpha, "pr create").TeamDecision);
        Assert.Equal(Decisions.Ask, Node(rig, alpha, "pr create").Effective);
        Assert.Equal(Decisions.Ask, Decide(rig, alpha, "pr", "create", "--fill"));
    }

    [Fact]
    public async Task I3_GroupPrefixRulesAndSecondRulesForARow_AreOtherRules()
    {
        using var rig = await GhRig();
        var alpha = rig.AddTeam("Alpha");
        rig.Svc.SaveChoices("gh", alpha, [C("pr create", "allow")]); // the choice owns "pr create *"
        AddRule(rig, alpha, "pr*", Decisions.Allow);
        AddRule(rig, alpha, "pr *", Decisions.Deny);
        AddRule(rig, alpha, "pr create*", Decisions.Deny); // a second rule for a row that has a choice
        AddRule(rig, null, "zap go*", Decisions.Allow); // adopted as a found row...
        rig.Svc.GetChecklist("gh", null);
        AddRule(rig, null, "zap go *", Decisions.Deny); // ...and a second rule for it
        var other = rig.Svc.GetChecklist("gh", alpha).OtherRules.Select(o => o.Pattern).ToList();
        Assert.Contains("pr*", other);
        Assert.Contains("pr *", other);
        Assert.Contains("pr create*", other);
        Assert.Contains("zap go *", other);
        Assert.DoesNotContain("pr create *", other); // owned by the row's choice
        Assert.DoesNotContain("zap go*", other); // owned by the adopted row
        Assert.Equal(other.Count, other.Distinct().Count());
    }
}
