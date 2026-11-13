using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>An API's operations get the same checklist as an MCP server's tools, classified from the HTTP method.</summary>
public class ApiChecklistTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Spec(params string[] operations)
    {
        var paths = new JsonObject();
        foreach (var o in operations)
        {
            var parts = o.Split(' ', 2);
            var item = paths[parts[1]] as JsonObject ?? new JsonObject();
            item[parts[0].ToLowerInvariant()] = new JsonObject { ["summary"] = "does " + o };
            paths[parts[1]] = item;
        }
        return new JsonObject { ["openapi"] = "3.0.0", ["info"] = new JsonObject { ["title"] = "Pets", ["version"] = "1" }, ["paths"] = paths }.ToJsonString();
    }

    private sealed class Rig : IDisposable
    {
        public TempDb Db = new();
        public DiscoverService Svc;
        public long Alpha, Beta, ApiId;
        public string Text = "";

        public Rig(params string[] ops)
        {
            Svc = new DiscoverService(Db.ConnectionString);
            Exec("INSERT INTO teams (name, created_at) VALUES ('Alpha', 'now'), ('Beta', 'now');");
            Alpha = Scalar("SELECT id FROM teams WHERE name = 'Alpha'");
            Beta = Scalar("SELECT id FROM teams WHERE name = 'Beta'");
            Exec("INSERT INTO apis (name, base_url, created_at, updated_at) VALUES ('pets', 'https://api.example.com', 'now', 'now');");
            ApiId = Scalar("SELECT id FROM apis WHERE name = 'pets'");
            SetOps(ops);
        }

        public void SetOps(params string[] ops) => Text = Spec(ops);

        public async Task Discover()
        {
            Assert.True(Svc.StartApi("pets", _ => Task.FromResult(OpenApiReader.Read(Text, new Uri("https://api.example.com/openapi.json")))));
            await Wait.UntilAsync(() => Svc.ApiStatus("pets")!.State != "running", "the spec was read");
            Assert.Equal("done", Svc.ApiStatus("pets")!.State);
        }

        public ChecklistNode Node(long? team, string id) => Svc.GetApiChecklist("pets", team).Nodes.Single(n => n.Path == id);

        public void Exec(string sql)
        {
            using var c = new SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public long Scalar(string sql)
        {
            using var c = new SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public string Text2(string sql)
        {
            using var c = new SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar()) ?? "";
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task ApiChecklist_DefaultsFollowMethods()
    {
        using var rig = new Rig("GET /pets", "POST /pets", "DELETE /pets/{petId}", "GET /tokens");
        await rig.Discover();
        var cl = rig.Svc.GetApiChecklist("pets", null);
        Assert.Equal("api", cl.Kind);
        Assert.Equal(1, cl.Runs);
        var get = rig.Node(null, "GET /pets");
        Assert.Equal(ClassSource.Method, get.Source);
        Assert.Equal(RowStates.Allow, get.Suggested);
        Assert.True(get.DefaultTick);
        Assert.True(get.Ticked);
        var post = rig.Node(null, "POST /pets");
        Assert.Equal(RowStates.Ask, post.Suggested);
        Assert.False(post.Ticked);
        var del = rig.Node(null, "DELETE /pets/{petId}");
        Assert.Equal(RowStates.Off, del.Suggested);
        Assert.False(del.Ticked);
        var tokens = rig.Node(null, "GET /tokens");
        Assert.Equal(CommandClass.Credential, tokens.Class);
        Assert.Equal(RowStates.Never, tokens.Suggested);
        Assert.Equal("locked", tokens.State);
        Assert.False(tokens.Ticked);
        Assert.Equal(RowStates.Never, tokens.Effective);
        Assert.Equal(Decisions.Ask, get.Effective); // nothing saved: the call still asks
        Assert.Equal(3, cl.Counts.Undecided);
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM apis WHERE title = 'Pets'")); // the spec's title is kept
    }

    [Fact]
    public void Suggested_MethodSourceBehavesLikeVerbAndAnnotation_OthersUnchanged()
    {
        Assert.Equal(RowStates.Allow, DiscoverService.Suggested(CommandClass.ReadOnly, ClassSource.Method));
        Assert.Equal(RowStates.Ask, DiscoverService.Suggested(CommandClass.Change, ClassSource.Method));
        Assert.Equal(RowStates.Off, DiscoverService.Suggested(CommandClass.Destructive, ClassSource.Method));
        Assert.Equal(RowStates.Ask, DiscoverService.Suggested(CommandClass.ReadOnly, ClassSource.Model));
    }

    [Fact]
    public async Task ApiChecklist_RediscoverMarksNewUnticked()
    {
        using var rig = new Rig("GET /pets", "POST /pets");
        await rig.Discover();
        rig.Svc.SaveApiChecklist("pets", null, new Dictionary<string, string?> { ["GET /pets"] = "allow" });
        Assert.Null(rig.Node(null, "GET /pets").Badge); // the first run marks nothing new
        rig.SetOps("GET /pets", "GET /owners");
        await rig.Discover();
        var added = rig.Node(null, "GET /owners");
        Assert.Equal("new", added.Badge);
        Assert.False(added.Ticked); // read-only, but new rows wait for the person
        Assert.Equal(RowStates.Allow, added.Suggested);
        var cl = rig.Svc.GetApiChecklist("pets", null);
        Assert.Equal(1, cl.NewCount);
        Assert.Equal(1, cl.GoneCount);
        Assert.Equal(2, cl.Runs);
        Assert.True(rig.Node(null, "POST /pets").Gone);
        Assert.Equal(Decisions.Allow, rig.Node(null, "GET /pets").DefaultDecision);
    }

    [Fact]
    public async Task ApiChecklist_TeamOverrideAndReset()
    {
        using var rig = new Rig("GET /pets", "POST /pets");
        await rig.Discover();
        rig.Svc.SaveApiChecklist("pets", null, new Dictionary<string, string?> { ["POST /pets"] = "ask" });
        rig.Svc.SaveApiChecklist("pets", rig.Alpha, new Dictionary<string, string?> { ["POST /pets"] = "off" });
        Assert.Equal(Decisions.Deny, rig.Node(rig.Alpha, "POST /pets").Effective);
        Assert.Equal(Decisions.Deny, rig.Node(rig.Alpha, "POST /pets").TeamDecision);
        Assert.Equal(Decisions.Ask, rig.Node(rig.Beta, "POST /pets").Effective);
        Assert.Equal(Decisions.Ask, rig.Node(null, "POST /pets").Effective);
        rig.Svc.SaveApiChecklist("pets", rig.Alpha, new Dictionary<string, string?> { ["POST /pets"] = null });
        Assert.Null(rig.Node(rig.Alpha, "POST /pets").TeamDecision);
        Assert.Equal(Decisions.Ask, rig.Node(rig.Alpha, "POST /pets").Effective);
        // a team state equal to the default is not stored
        rig.Svc.SaveApiChecklist("pets", rig.Alpha, new Dictionary<string, string?> { ["POST /pets"] = "ask" });
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM api_choices"));
        // null on the every-team list removes the default choice and its rule
        rig.Svc.SaveApiChecklist("pets", null, new Dictionary<string, string?> { ["POST /pets"] = null });
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM api_rules"));
        // refusals write nothing
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveApiChecklist("pets", null, new Dictionary<string, string?> { ["nope"] = "allow" }));
        Assert.Throws<ArgumentException>(() => rig.Svc.SaveApiChecklist("pets", null, new Dictionary<string, string?> { ["GET /pets"] = "never" }));
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM api_rules"));
        // the team must be offered the API (or already have choices on it)
        Assert.Throws<ArgumentException>(() => rig.Svc.CheckApiTeamOffered("pets", rig.Beta));
        rig.Exec($"INSERT INTO api_teams (api_id, team_id) VALUES ({rig.ApiId}, {rig.Beta});");
        rig.Svc.CheckApiTeamOffered("pets", rig.Beta);
    }

    [Fact]
    public async Task ApiChecklist_RulesMatchTheExactActionId_NotAPrefix()
    {
        using var rig = new Rig("GET /pets", "GET /pets/{petId}");
        await rig.Discover();
        rig.Svc.SaveApiChecklist("pets", null, new Dictionary<string, string?> { ["GET /pets"] = "allow" });
        Assert.Equal(Decisions.Allow, rig.Node(null, "GET /pets").Effective);
        Assert.Equal(Decisions.Ask, rig.Node(null, "GET /pets/{petId}").Effective);
        Assert.Equal(Decisions.Allow, CliRules.DecideExact("GET /pets", [], [new CliRule("GET /pets", Decisions.Allow, "global")]).Decision);
        Assert.Equal(Decisions.Ask, CliRules.DecideExact("GET /pets/{petId}", [], [new CliRule("GET /pets", Decisions.Allow, "global")]).Decision);
        Assert.Equal(Decisions.Ask, CliRules.DecideExact("GET /pets", [], [new CliRule("GET /pets*", Decisions.Allow, "global")]).Decision);
    }

    [Fact]
    public async Task ApiChecklist_SavedChoiceSurvivesRediscoverWithoutOperationIds()
    {
        using var rig = new Rig("GET /pets", "POST /pets/{petId}/adopt");
        await rig.Discover();
        rig.Svc.SaveApiChecklist("pets", null, new Dictionary<string, string?> { ["POST /pets/{petId}/adopt"] = "ask", ["GET /pets"] = "allow" });
        rig.Svc.SaveApiChecklist("pets", rig.Alpha, new Dictionary<string, string?> { ["GET /pets"] = "off" });
        await rig.Discover();
        Assert.Equal(Decisions.Allow, rig.Node(null, "GET /pets").DefaultDecision);
        Assert.Equal(Decisions.Deny, rig.Node(rig.Alpha, "GET /pets").TeamDecision);
        Assert.Equal(Decisions.Ask, rig.Node(null, "POST /pets/{petId}/adopt").DefaultDecision);
        Assert.Equal(3, rig.Scalar("SELECT count(*) FROM api_choices"));
        Assert.Equal(3, rig.Scalar("SELECT count(*) FROM api_rules"));
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM api_commands WHERE badge = 'new'"));
    }

    [Fact]
    public async Task ApiChecklist_RediscoverTakesAnAllowBackWhenARowGetsLessSafe()
    {
        using var rig = new Rig("GET /pets");
        await rig.Discover();
        rig.Svc.SaveApiChecklist("pets", null, new Dictionary<string, string?> { ["GET /pets"] = "allow" });
        // the same id is now a credential endpoint (as a tokens route would be)
        var spec = OpenApiReader.Read(rig.Text, null);
        var locked = new ApiSpec(spec.Title, spec.ServerUrl,
            [.. spec.Actions.Select(a => a with { Class = new Classification(CommandClass.Credential, ClassSource.Floor, "reveals a sign-in") })], spec.Auth, 0);
        var merge = new DiscoverStore(rig.Db.ConnectionString, TimeProvider.System).MergeApi(rig.ApiId, locked);
        Assert.Contains(merge.Dropped, d => d.StartsWith("GET /pets") && d.Contains("locked"));
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM api_choices"));
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM api_rules"));
    }

    [Fact]
    public async Task RememberApi_RefusesCredentialRow()
    {
        using var rig = new Rig("GET /pets", "GET /tokens");
        await rig.Discover();
        Assert.Throws<ArgumentException>(() => rig.Svc.RememberApi("pets", null, "GET /tokens", Decisions.Allow));
        Assert.Throws<ArgumentException>(() => rig.Svc.RememberApi("pets", null, "GET /tokens", Decisions.Ask));
        Assert.Null(rig.Svc.RememberApi("pets", null, "GET /tokens", Decisions.Deny)); // nothing to store: it is locked already
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM api_rules"));
        Assert.Throws<ArgumentException>(() => rig.Svc.RememberApi("pets", null, "GET /unknown", Decisions.Allow));
        Assert.Equal("GET /pets", rig.Svc.RememberApi("pets", rig.Alpha, "GET /pets", Decisions.Allow));
        Assert.Equal(Decisions.Allow, rig.Node(rig.Alpha, "GET /pets").TeamDecision);
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM api_rules WHERE pattern = 'GET /pets'"));
    }

    [Fact]
    public async Task StartApi_AFailedReadSaysWhy_AndAnUnknownApiIsRefused()
    {
        using var rig = new Rig("GET /pets");
        Assert.Throws<ArgumentException>(() => rig.Svc.StartApi("nope", _ => Task.FromResult(OpenApiReader.Read(rig.Text, null))));
        Assert.True(rig.Svc.StartApi("pets", _ => throw new ArgumentException("no OpenAPI spec found")));
        await Wait.UntilAsync(() => rig.Svc.ApiStatus("pets")!.State != "running", "the read failed");
        Assert.Equal("failed", rig.Svc.ApiStatus("pets")!.State);
        Assert.Equal("no OpenAPI spec found", rig.Svc.ApiStatus("pets")!.Error);
        Assert.Null(rig.Svc.ApiStatus("other"));
    }

    [Fact]
    public async Task ApiChecklist_RediscoverLeavesASetAuthAndTitleAlone()
    {
        using var rig = new Rig("GET /pets");
        const string authSpec = """{"openapi":"3.0.0","info":{"title":"Pets","version":"1"},"paths":{"/pets":{"get":{}}},"components":{"securitySchemes":{"k":{"type":"apiKey","in":"header","name":"X-Key"}}}}""";
        rig.Text = authSpec;
        await rig.Discover();
        Assert.Contains("X-Key", rig.Text2("SELECT auth FROM apis"));
        rig.Exec("UPDATE apis SET auth = '{\"type\":\"bearer\"}', title = 'Mine'");
        await rig.Discover();
        Assert.Equal("{\"type\":\"bearer\"}", rig.Text2("SELECT auth FROM apis"));
        Assert.Equal("Mine", rig.Text2("SELECT title FROM apis"));
    }
}
