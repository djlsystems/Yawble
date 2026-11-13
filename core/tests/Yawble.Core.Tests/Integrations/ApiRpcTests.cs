using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>The RPC methods for APIs and Connections, and what the Cloud Link refuses. No network: FakeHttp; no OS store: MemoryCredentialStore.</summary>
public class ApiRpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Secret = "sk_live_SUPERSECRET_1234567890";

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;
        public ConnectionService Conns = null!;
        public FakeHttp Http = new();
        public long Alpha;

        public static async Task<Rig> Make()
        {
            var r = new Rig();
            r.Http.Add("https://petstore.example/openapi.json", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Integrations", "Fixtures", "openapi", "petstore-v3.json")));
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
            r.Conns = new ConnectionService(new MemoryCredentialStore(), r.Db.ConnectionString);
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, connections: r.Conns, httpHandler: () => r.Http);
            r.Engine.Integrations = r.Svc;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            return r;
        }

        public async Task<JsonNode?> Rpc(string method, JsonObject? p = null) => await Svc.DispatchAsync(method, p, Ct);

        public async Task<JsonNode> Discover(string name)
        {
            var s = await Rpc("integrations.discover", new JsonObject { ["name"] = name, ["kind"] = "api" });
            Assert.True(s!["started"]!.GetValue<bool>());
            await Wait.UntilAsync(async () => (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = name, ["kind"] = "api" }))!["state"]!.GetValue<string>() != "running", "the spec was read");
            return (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = name, ["kind"] = "api" }))!;
        }

        public async Task<JsonNode> SaveConnection(string name = "Petstore key") => (await Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = name,
            ["fields"] = new JsonArray(new JsonObject { ["key"] = "Authorization", ["label"] = "API key", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["Authorization"] = Secret },
        }))!;

        public async ValueTask DisposeAsync() { await Svc.DisposeAsync(); await Engine.DisposeAsync(); Db.Dispose(); }
    }

    [Fact]
    public async Task ApiRpc_SaveDiscoverChecklistRoundTrip()
    {
        await using var r = await Rig.Make();
        var saved = await r.Rpc("integrations.apiSave", new JsonObject { ["docsUrl"] = "https://petstore.example/openapi.json", ["baseUrl"] = "" });
        Assert.Equal("petstore.example", saved!["name"]!.GetValue<string>()); // no name: the docs URL host, as no service URL was given
        var name = "petstore.example";

        var done = await r.Discover(name);
        Assert.Equal("done", done["state"]!.GetValue<string>());
        var api = (JsonObject)((JsonArray)(await r.Rpc("integrations.apiList"))!).Single()!;
        Assert.Equal("https://eu.petstore.example/v1", api["baseUrl"]!.GetValue<string>()); // blank base filled from the spec
        Assert.Equal("https://petstore.example/openapi.json", api["specUrl"]!.GetValue<string>());
        Assert.Equal("Petstore", api["title"]!.GetValue<string>());
        Assert.Equal("read 3 actions from the OpenAPI spec", api["status"]!.GetValue<string>());
        Assert.Equal(name, api["name"]!.GetValue<string>()); // the title never replaces the name
        Assert.NotNull(api["counts"]);

        var team = r.Alpha;
        await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = name, ["teamId"] = team });
        Assert.Equal(team, ((JsonArray)((JsonObject)((JsonArray)(await r.Rpc("integrations.apiList"))!).Single()!)["teams"]!).Single()!.GetValue<long>());

        var checklist = (await r.Rpc("integrations.checklist", new JsonObject { ["name"] = name, ["kind"] = "api", ["teamId"] = team }))!;
        Assert.Contains(((JsonArray)checklist["nodes"]!).OfType<JsonObject>(), n => n["path"]!.GetValue<string>() == "listPets");
        var save = await r.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = name, ["kind"] = "api", ["teamId"] = team,
            ["choices"] = new JsonArray(new JsonObject { ["path"] = "listPets", ["state"] = "allow" }, new JsonObject { ["path"] = "createPets", ["state"] = "ask" }),
        });
        Assert.True(save!["ok"]!.GetValue<bool>());
        Assert.Equal(1, save["allowed"]!.GetValue<int>());
        var again = (await r.Rpc("integrations.checklist", new JsonObject { ["name"] = name, ["kind"] = "api", ["teamId"] = team }))!;
        Assert.Equal("free", ((JsonArray)again["nodes"]!).OfType<JsonObject>().Single(n => n["path"]!.GetValue<string>() == "listPets")["state"]!.GetValue<string>());
        // a team that is not offered the API is refused, as for MCP
        var other = await r.Engine.CreateTeamAsync("Beta", ct: Ct);
        await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.checklist", new JsonObject { ["name"] = name, ["kind"] = "api", ["teamId"] = other }));
    }

    [Fact]
    public async Task ApiRpc_ChecklistRowsCarryTagMethodAndRoute()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = "", ["docsUrl"] = "https://petstore.example/openapi.json" });
        await r.Discover("pets");
        var nodes = ((JsonArray)(await r.Rpc("integrations.checklist", new JsonObject { ["name"] = "pets", ["kind"] = "api" }))!["nodes"]!).OfType<JsonObject>().ToList();
        var list = nodes.Single(n => n["path"]!.GetValue<string>() == "listPets");
        Assert.Equal("GET", list["method"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(list["route"]!.GetValue<string>()));
        Assert.All(nodes, n => Assert.NotNull(n["method"]));
        Assert.Contains(nodes, n => n["tag"] is not null);
    }

    [Fact]
    public async Task ApiRpc_ConnectionSaveReuseIdentical_ReturnsTheExistingOne()
    {
        await using var r = await Rig.Make();
        var first = await r.SaveConnection("Petstore key");
        JsonObject Again(bool reuse) => new()
        {
            ["name"] = "Second", ["reuseIdentical"] = reuse,
            ["fields"] = new JsonArray(new JsonObject { ["key"] = "Authorization", ["label"] = "API key", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["Authorization"] = Secret },
        };
        var reused = (await r.Rpc("integrations.connectionSave", Again(true)))!;
        Assert.True(reused["reused"]!.GetValue<bool>());
        Assert.Equal(first["id"]!.GetValue<long>(), reused["id"]!.GetValue<long>());
        Assert.DoesNotContain(Secret, reused.ToJsonString());
        Assert.Single((JsonArray)(await r.Rpc("integrations.connections"))!);
        var made = (await r.Rpc("integrations.connectionSave", Again(false)))!; // not asked: a new one
        Assert.NotEqual(first["id"]!.GetValue<long>(), made["id"]!.GetValue<long>());
        Assert.Null(made["reused"]);
        Assert.Contains("connections can only", RemoteAccess.Refusal("integrations.connectionSave", Again(true), true));
    }

    [Fact]
    public async Task ApiRpc_ApiDeleteRule_RemovesAnOtherRule()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = "", ["docsUrl"] = "https://petstore.example/openapi.json" });
        await r.Discover("pets");
        await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = "pets", ["all"] = true });
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(r.Db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO api_rules (api_id, team_id, pattern, decision, created_at) SELECT id, NULL, 'odd*', 'allow', '2026-11-10' FROM apis WHERE name = 'pets';";
            cmd.ExecuteNonQuery();
        }
        JsonArray Other() => (JsonArray)(await_(r.Rpc("integrations.checklist", new JsonObject { ["name"] = "pets", ["kind"] = "api" })))["otherRules"]!;
        var id = Other().Single()!["id"]!.GetValue<long>();
        await r.Rpc("integrations.apiDeleteRule", new JsonObject { ["id"] = id });
        Assert.Empty(Other());
        Assert.Null(RemoteAccess.Refusal("integrations.apiDeleteRule", new JsonObject { ["id"] = 1 }, false)); // as mcpDeleteRule is
    }

    static JsonNode await_(Task<JsonNode?> t) => t.GetAwaiter().GetResult()!;

    [Fact]
    public async Task ApiRpc_DiscoverNeedsAServiceUrlFromSomewhere()
    {
        await using var r = await Rig.Make();
        r.Http.Add("https://nosrv.example/openapi.json", "{\"openapi\":\"3.0.0\",\"servers\":[{\"url\":\"ftp://x\"}],\"info\":{\"title\":\"X\",\"version\":\"1\"},\"paths\":{\"/a\":{\"get\":{}}}}");
        await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "x", ["baseUrl"] = "" }));
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "x", ["baseUrl"] = "", ["docsUrl"] = "https://nosrv.example/openapi.json" });
        var s = await r.Discover("x");
        Assert.Equal("failed", s["state"]!.GetValue<string>());
        Assert.Contains("add the service URL", s["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApiRpc_ConnectionsNeverReturnSecrets()
    {
        await using var r = await Rig.Make();
        var saved = await r.SaveConnection();
        await r.Rpc("integrations.apiSave", new JsonObject
        {
            ["name"] = "pets", ["baseUrl"] = "https://pets.example", ["connectionId"] = saved["id"]!.GetValue<long>(),
            ["auth"] = new JsonObject { ["type"] = "header", ["name"] = "Authorization" },
        });
        var all = new[] { saved.ToJsonString(), (await r.Rpc("integrations.connections"))!.ToJsonString(), (await r.Rpc("integrations.apiList"))!.ToJsonString() };
        foreach (var json in all) Assert.DoesNotContain("SUPERSECRET", json);
        var shown = ((JsonArray)(await r.Rpc("integrations.connections"))!).Single()!["fields"]![0]!["shown"]!.GetValue<string>();
        Assert.Equal("sk_l••••••7890", shown);
        var api = (JsonObject)((JsonArray)(await r.Rpc("integrations.apiList"))!).Single()!;
        Assert.Equal("Petstore key", api["connection"]!["name"]!.GetValue<string>());
        Assert.Equal("API key", api["signIn"]!["label"]!.GetValue<string>());
        Assert.Equal("Authorization", api["signIn"]!["fields"]![0]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApiRpc_RemoveConnectionInUseNeedsConfirm()
    {
        await using var r = await Rig.Make();
        var id = (await r.SaveConnection())["id"]!.GetValue<long>();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = "https://pets.example", ["connectionId"] = id });

        var refused = (await r.Rpc("integrations.connectionRemove", new JsonObject { ["id"] = id }))!;
        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Equal("pets", refused["usedBy"]![0]!.GetValue<string>());
        Assert.Single((JsonArray)(await r.Rpc("integrations.connections"))!);

        var removed = (await r.Rpc("integrations.connectionRemove", new JsonObject { ["id"] = id, ["confirm"] = true }))!;
        Assert.True(removed["ok"]!.GetValue<bool>());
        Assert.Empty((JsonArray)(await r.Rpc("integrations.connections"))!);
        Assert.Null(((JsonArray)(await r.Rpc("integrations.apiList"))!).Single()!["connection"]);
    }

    [Fact]
    public async Task ApiRpc_RemoveApiOffersSoleConnection()
    {
        await using var r = await Rig.Make();
        var id = (await r.SaveConnection())["id"]!.GetValue<long>();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = "https://pets.example", ["connectionId"] = id });
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "shop", ["baseUrl"] = "https://shop.example", ["connectionId"] = id });

        // shared with another API: not offered, never removed
        var first = (await r.Rpc("integrations.apiRemove", new JsonObject { ["name"] = "pets", ["removeConnection"] = true }))!;
        Assert.True(first["ok"]!.GetValue<bool>());
        Assert.Null(first["soleConnection"]);
        Assert.Single((JsonArray)(await r.Rpc("integrations.connections"))!);

        // now only shop uses it: offered, and kept unless asked
        var second = (await r.Rpc("integrations.apiRemove", new JsonObject { ["name"] = "shop" }))!;
        Assert.Equal("Petstore key", second["soleConnection"]!["name"]!.GetValue<string>());
        Assert.False(second["connectionRemoved"]!.GetValue<bool>());
        Assert.Single((JsonArray)(await r.Rpc("integrations.connections"))!);

        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "shop", ["baseUrl"] = "https://shop.example", ["connectionId"] = id });
        var third = (await r.Rpc("integrations.apiRemove", new JsonObject { ["name"] = "shop", ["removeConnection"] = true }))!;
        Assert.True(third["connectionRemoved"]!.GetValue<bool>());
        Assert.Empty((JsonArray)(await r.Rpc("integrations.connections"))!);
    }

    [Fact]
    public async Task ApiRpc_NameCollisionRefused()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "My API", ["baseUrl"] = "https://a.example" });
        var e = await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "my-api", ["baseUrl"] = "https://b.example" }));
        Assert.Contains("api_my_api", e.Message);
        // the same name again is an upsert, not a collision
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "my api", ["baseUrl"] = "https://c.example" });
        var row = (JsonObject)((JsonArray)(await r.Rpc("integrations.apiList"))!).Single()!;
        Assert.Equal("https://c.example", row["baseUrl"]!.GetValue<string>());
        // a base URL must be absolute http(s)
        await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "z", ["baseUrl"] = "ftp://x" }));
    }

    [Theory]
    [InlineData("integrations.connections", null)]
    [InlineData("integrations.connectionSave", null)]
    [InlineData("integrations.connectionRemove", null)]
    [InlineData("integrations.connectionTest", null)]
    [InlineData("integrations.connectionAttention", null)]
    [InlineData("integrations.apiSave", null)]
    [InlineData("integrations.apiRemove", null)]
    [InlineData("integrations.discover", "api")]
    public void RemoteAccess_RefusesConnectionsAndApiSetup(string method, string? kind)
    {
        var p = kind is null ? null : new JsonObject { ["kind"] = kind, ["name"] = "pets" };
        foreach (var terminals in new[] { true, false })
            Assert.NotNull(RemoteAccess.Refusal(method, p, terminals));
        Assert.Contains("connections can only", RemoteAccess.Refusal("integrations.connections", null, true));
        Assert.Contains("setting up an API", RemoteAccess.Refusal("integrations.apiSave", null, true));
    }

    [Theory]
    [InlineData("integrations.apiList", null)]
    [InlineData("integrations.apiEnable", null)]
    [InlineData("integrations.checklist", "api")]
    [InlineData("integrations.saveChecklist", "api")]
    [InlineData("integrations.discoverStatus", "api")]
    [InlineData("integrations.discoverCancel", "api")]
    public void RemoteAccess_AllowsApiListEnableAndChecklist(string method, string? kind)
    {
        var p = kind is null ? null : new JsonObject { ["kind"] = kind, ["name"] = "pets" };
        foreach (var terminals in new[] { true, false })
            Assert.Null(RemoteAccess.Refusal(method, p, terminals));
    }
}
