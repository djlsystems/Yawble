using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>MCP servers' transport, URL, sign-in and secret env (0030). No network, no real programs, no OS store.</summary>
public class McpRemoteStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Secret = "sk_live_SUPERSECRET_1234567890";

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public MemoryCredentialStore Store = new();
        public ConnectionService Conns = null!;
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;

        public static Rig Make()
        {
            var r = new Rig();
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
            r.Conns = new ConnectionService(r.Store, r.Db.ConnectionString);
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, connections: r.Conns);
            r.Engine.Integrations = r.Svc;
            return r;
        }

        public async Task<JsonNode?> Rpc(string method, JsonObject? p = null) => await Svc.DispatchAsync(method, p, Ct);

        public Task<JsonNode?> SaveLocal(string name, string secret = Secret, JsonObject? extra = null)
        {
            var p = new JsonObject
            {
                ["name"] = name, ["command"] = "node", ["args"] = new JsonArray("x.js"),
                ["env"] = new JsonArray(new JsonObject { ["name"] = "API_KEY", ["secret"] = true }, new JsonObject { ["name"] = "REGION", ["value"] = "eu", ["secret"] = false }),
                ["secrets"] = new JsonObject { ["API_KEY"] = secret },
            };
            if (extra is not null) foreach (var (k, v) in extra.ToArray()) { extra.Remove(k); p[k] = v; }
            return Rpc("integrations.mcpSave", p);
        }

        public async Task<JsonObject> Row(string name) =>
            (JsonObject)((JsonArray)(await Rpc("integrations.mcpList"))!).OfType<JsonObject>().Single(o => o["name"]!.GetValue<string>() == name);

        public async ValueTask DisposeAsync() { await Svc.DisposeAsync(); await Engine.DisposeAsync(); Db.Dispose(); }
    }

    private static string StoredEnv(TempDb db, string name)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT env FROM mcp_servers WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task SaveWithSecretEnv_StoresItInAConnection_NotTheRow()
    {
        await using var r = Rig.Make();
        await r.SaveLocal("fake");
        var env = StoredEnv(r.Db, "fake");
        Assert.DoesNotContain(Secret, env);
        var entry = JsonNode.Parse(env)!.AsArray().OfType<JsonObject>().Single(o => o["name"]!.GetValue<string>() == "API_KEY");
        Assert.Null(entry["value"]);
        Assert.True(entry["secret"]!.GetValue<bool>());
        var conn = Assert.Single(r.Conns.List());
        Assert.Equal("fake secrets", conn.Name);
        Assert.Contains(r.Store.Keys, k => k.EndsWith("/API_KEY", StringComparison.Ordinal) && k.StartsWith("connection/", StringComparison.Ordinal));
        Assert.Equal(Secret, r.Conns.Secrets(conn.Id)["API_KEY"]);
    }

    [Fact]
    public async Task ListShowsSecretEnvMasked()
    {
        await using var r = Rig.Make();
        await r.SaveLocal("fake");
        var row = await r.Row("fake");
        var api = row["env"]!.AsArray().OfType<JsonObject>().Single(o => o["name"]!.GetValue<string>() == "API_KEY");
        Assert.Equal(ConnectionService.Mask(Secret), api["shown"]!.GetValue<string>());
        Assert.True(api["set"]!.GetValue<bool>());
        Assert.True(api["secret"]!.GetValue<bool>());
        var region = row["env"]!.AsArray().OfType<JsonObject>().Single(o => o["name"]!.GetValue<string>() == "REGION");
        Assert.Equal("eu", region["value"]!.GetValue<string>());
        Assert.DoesNotContain(Secret, row.ToJsonString());
        Assert.Equal("stdio", row["transport"]!.GetValue<string>());
        Assert.Equal("fake secrets", row["connection"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task EditWithoutResendingASecretKeepsIt()
    {
        await using var r = Rig.Make();
        await r.SaveLocal("fake");
        await r.Rpc("integrations.mcpSave", new JsonObject
        {
            ["name"] = "fake", ["command"] = "node", ["args"] = new JsonArray("y.js"),
            ["env"] = new JsonArray(new JsonObject { ["name"] = "API_KEY", ["secret"] = true }),
        });
        var conn = Assert.Single(r.Conns.List());
        Assert.Equal(Secret, r.Conns.Secrets(conn.Id)["API_KEY"]);
        var row = await r.Row("fake");
        Assert.True(row["env"]![0]!["set"]!.GetValue<bool>());
        Assert.Equal("y.js", row["args"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task SameSecretsReuseTheConnection()
    {
        await using var r = Rig.Make();
        await r.SaveLocal("one");
        await r.SaveLocal("two");
        var conn = Assert.Single(r.Conns.List());
        Assert.Equal((await r.Row("one"))["connection"]!["id"]!.GetValue<long>(), conn.Id);
        Assert.Equal(conn.Id, (await r.Row("two"))["connection"]!["id"]!.GetValue<long>());
    }

    [Fact]
    public async Task HttpServerNeedsHttpsUrl()
    {
        await using var r = Rig.Make();
        JsonObject Http(string url) => new() { ["name"] = "remote", ["transport"] = "http", ["url"] = url, ["auth"] = new JsonObject { ["type"] = "none" } };
        await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Rpc("integrations.mcpSave", Http("http://example.com/mcp")));
        await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Rpc("integrations.mcpSave", Http("ftp://example.com/mcp")));
        var ex = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Rpc("integrations.mcpSave", Http("https://user:pass@example.com/mcp")));
        Assert.Contains("token field", ex.Message);
        Assert.Empty(await r.Rpc("integrations.mcpList") is JsonArray none ? none : []);
        await r.Rpc("integrations.mcpSave", Http("http://127.0.0.1:3000/mcp"));
        await r.Rpc("integrations.mcpSave", Http("https://example.com/mcp"));
        var row = await r.Row("remote");
        Assert.Equal("http", row["transport"]!.GetValue<string>());
        Assert.Equal("https://example.com/mcp", row["url"]!.GetValue<string>());
        await r.Rpc("integrations.mcpSave", Http("https://example.com"));
        Assert.Equal("https://example.com/", (await r.Row("remote"))["url"]!.GetValue<string>()); // AbsoluteUri
        Assert.Equal("none", row["auth"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task HttpBearerTokenIsSavedAsAConnectionNamedToken()
    {
        await using var r = Rig.Make();
        await r.Rpc("integrations.mcpSave", new JsonObject
        {
            ["name"] = "remote", ["transport"] = "http", ["url"] = "https://example.com/mcp",
            ["auth"] = new JsonObject { ["type"] = "bearer" }, ["secrets"] = new JsonObject { ["token"] = Secret },
        });
        var conn = Assert.Single(r.Conns.List());
        Assert.Equal("remote token", conn.Name);
        Assert.Equal(Secret, r.Conns.Secrets(conn.Id)["token"]);
        Assert.DoesNotContain(Secret, (await r.Row("remote")).ToJsonString());
    }

    [Fact]
    public async Task ConnectionUsedByNamesTheMcpServer()
    {
        await using var r = Rig.Make();
        await r.SaveLocal("fake");
        Assert.Equal(["MCP: fake"], Assert.Single(r.Conns.List()).UsedBy);
    }

    [Fact]
    public async Task ExistingRowsMigrateAsStdio()
    {
        await using var r = Rig.Make();
        using (var c = new SqliteConnection(r.Db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO mcp_servers (name, command, args, created_at, updated_at) VALUES ('old', 'node', '[]', 'now', 'now');";
            cmd.ExecuteNonQuery();
        }
        var row = await r.Row("old");
        Assert.Equal("stdio", row["transport"]!.GetValue<string>());
        Assert.Empty(row["env"]!.AsArray());
        Assert.Null(row["connection"]);
        Assert.Empty(row["sources"]!.AsArray());
    }

    private const string StderrServer = """
        console.error('bad key: ' + process.env.API_KEY);
        process.exit(1);
        """;

    [Fact]
    public async Task ASecretPrintedOnTheServersErrorStream_NeverReachesStatusListOrError()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        await using var r = Rig.Make();
        var script = Path.Combine(r.Db.Dir, "leaky-mcp.js");
        File.WriteAllText(script, StderrServer);
        await r.SaveLocal("leaky", extra: new JsonObject { ["args"] = new JsonArray(script) });
        var ex = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "leaky" }));
        Assert.DoesNotContain(Secret, ex.Message);
        var row = await r.Row("leaky");
        Assert.StartsWith("not working", row["status"]!.GetValue<string>());
        Assert.DoesNotContain(Secret, row.ToJsonString());
    }

    [Fact]
    public async Task LastErrorScrubsRegisteredSecrets()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        using var db = new TempDb();
        var script = Path.Combine(db.Dir, "leaky-mcp.js");
        File.WriteAllText(script, StderrServer);
        var owner = new object();
        Yawble.Core.Connections.SecretScrubber.Set(owner, 1, [Secret]);
        try
        {
            await using var client = new McpClient("node", [script], new Dictionary<string, string> { ["API_KEY"] = Secret });
            var ex = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync(Ct));
            Assert.DoesNotContain(Secret, ex.Message);
            Assert.DoesNotContain(Secret, client.LastError());
        }
        finally { Yawble.Core.Connections.SecretScrubber.Forget(owner, 1); }
    }

    [Fact]
    public async Task EditingToAnotherConnectionOrNoSecrets_DropsTheOrphanedConnection()
    {
        await using var r = Rig.Make();
        await r.SaveLocal("fake");
        Assert.Single(r.Conns.List());
        // the same server now with a different secret value, shared by nobody: edited in place, still one Connection
        await r.SaveLocal("fake", "another_SECRET_value_999");
        Assert.Single(r.Conns.List());
        // a second server holds a Connection of its own; "fake" moves to it (identical values): fake's old one is dropped
        await r.SaveLocal("other", "third_SECRET_value_777");
        Assert.Equal(2, r.Conns.List().Count);
        await r.SaveLocal("fake", "third_SECRET_value_777");
        var only = Assert.Single(r.Conns.List());
        Assert.Equal(["MCP: fake", "MCP: other"], only.UsedBy);
        // no secrets any more: "other" keeps the shared one, "fake" lets go; then the last user going drops it
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "fake", ["command"] = "node", ["env"] = new JsonArray() });
        Assert.Equal(["MCP: other"], Assert.Single(r.Conns.List()).UsedBy);
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "other", ["command"] = "node", ["env"] = new JsonArray() });
        Assert.Empty(r.Conns.List());
    }

    [Fact]
    public async Task ConnectionIdWithSecretsIsRefused()
    {
        await using var r = Rig.Make();
        await r.SaveLocal("fake");
        var id = Assert.Single(r.Conns.List()).Id;
        var ex = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.SaveLocal("again", extra: new JsonObject { ["connectionId"] = id }));
        Assert.Contains("not both", ex.Message);
    }

    [Fact]
    public async Task APersonMadeConnectionPickedByIdSurvivesTheServerLeavingIt()
    {
        await using var r = Rig.Make();
        var made = (await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "My own key", ["fields"] = new JsonArray(new JsonObject { ["key"] = "API_KEY", ["label"] = "Key", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["API_KEY"] = Secret },
        }))!["id"]!.GetValue<long>();
        await r.Rpc("integrations.mcpSave", new JsonObject
        {
            ["name"] = "fake", ["command"] = "node", ["connectionId"] = made,
            ["env"] = new JsonArray(new JsonObject { ["name"] = "API_KEY", ["secret"] = true }),
        });
        Assert.Equal(["MCP: fake"], Assert.Single(r.Conns.List()).UsedBy);
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "fake", ["command"] = "node", ["env"] = new JsonArray() });
        Assert.Equal("My own key", Assert.Single(r.Conns.List()).Name);
    }

    [Fact]
    public async Task OrphanRules_SuffixedAutoNamesGoButAPersonMadeKindMismatchStays()
    {
        await using var r = Rig.Make();
        // a person's own Connection already has the plain name, so the server's own is "fake secrets 2"
        await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "fake secrets", ["fields"] = new JsonArray(new JsonObject { ["key"] = "API_KEY", ["label"] = "Key", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["API_KEY"] = "someone_elses_key_000" },
        });
        await r.SaveLocal("fake");
        Assert.Contains(r.Conns.List(), c => c.Name == "fake secrets 2");
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "fake", ["command"] = "node", ["env"] = new JsonArray() });
        Assert.DoesNotContain(r.Conns.List(), c => c.Name == "fake secrets 2");
        Assert.Contains(r.Conns.List(), c => c.Name == "fake secrets");

        // a person-made secret Connection that happens to be called "<server> sign-in" is never dropped
        var mine = (await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "other sign-in", ["fields"] = new JsonArray(new JsonObject { ["key"] = "API_KEY", ["label"] = "Key", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["API_KEY"] = Secret },
        }))!["id"]!.GetValue<long>();
        await r.Rpc("integrations.mcpSave", new JsonObject
        {
            ["name"] = "other", ["command"] = "node", ["connectionId"] = mine,
            ["env"] = new JsonArray(new JsonObject { ["name"] = "API_KEY", ["secret"] = true }),
        });
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "other", ["command"] = "node", ["env"] = new JsonArray() });
        Assert.Contains(r.Conns.List(), c => c.Name == "other sign-in");
    }

    [Fact]
    public async Task ListMasksArgsAndAddress_AndAnEditWithoutArgsKeepsThemAsStored()
    {
        await using var r = Rig.Make();
        const string ArgKey = "ARGKEY_secret_value_424242";
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "fake", ["command"] = "node", ["args"] = new JsonArray("x.js", "--api-key", ArgKey) });
        var row = await r.Row("fake");
        Assert.DoesNotContain(ArgKey, row.ToJsonString());
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "fake", ["command"] = "node" });
        using var c = new SqliteConnection(r.Db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT args FROM mcp_servers WHERE name = 'fake';";
        Assert.Contains(ArgKey, (string)cmd.ExecuteScalar()!);
    }
}
