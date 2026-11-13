using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Import MCP servers from other apps' config files (injected temp files only; never the real ones).</summary>
public class McpImporterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Key1 = "sk_live_SUPERSECRET_1234567890";
    private const string Key2 = "sk_live_OTHERSECRET_0987654321";

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public MemoryCredentialStore Store = new();
        public ConnectionService Conns = null!;
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;
        public List<McpConfigSource> Sources = [];

        public static Rig Make()
        {
            var r = new Rig();
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
            r.Conns = new ConnectionService(r.Store, r.Db.ConnectionString);
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, connections: r.Conns, mcpSources: () => r.Sources);
            r.Engine.Integrations = r.Svc;
            return r;
        }

        public string File(string name, string text, string app, string shape)
        {
            var path = Path.Combine(Db.Dir, name);
            System.IO.File.WriteAllText(path, text);
            Sources.Add(new McpConfigSource(app, path, shape));
            return path;
        }

        public async Task<JsonArray> Scan() => (JsonArray)(await Svc.DispatchAsync("integrations.mcpImportScan", new JsonObject(), Ct))!;

        public async Task<JsonObject> Import(params string[] keys) =>
            (JsonObject)(await Svc.DispatchAsync("integrations.mcpImport", new JsonObject { ["keys"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()) }, Ct))!;

        public async Task<JsonObject> Row(string name) =>
            ((JsonArray)(await Svc.DispatchAsync("integrations.mcpList", null, Ct))!).OfType<JsonObject>().Single(o => o["name"]!.GetValue<string>() == name);

        public async ValueTask DisposeAsync() { await Svc.DisposeAsync(); await Engine.DisposeAsync(); Db.Dispose(); }
    }

    private static string Stdio(string key = Key1, string extra = "") =>
        $$"""{ "mcpServers": { "files": { "command": "node", "args": ["srv.js"], "env": { "API_KEY": "{{key}}", "REGION": "eu" }{{extra}} } } }""";

    [Fact]
    public async Task ReadsEveryShape()
    {
        await using var r = Rig.Make();
        r.File("desktop.json", """{ "mcpServers": { "a": { "command": "a-cmd" } } }""", "Claude Desktop", "mcpServers");
        r.File("claude.json", """{ "mcpServers": { "b": { "command": "b-cmd" } }, "projects": { "C:/p": { "mcpServers": { "c": { "command": "c-cmd" } } }, "C:/q": {} } }""", "Claude Code", "claudeCode");
        r.File("mcp.json", """{ "servers": { "d": { "type": "stdio", "command": "d-cmd" } } }""", "VS Code", "vscode");
        r.File("settings.json", """{ "editor.x": 1, "mcp": { "servers": { "e": { "command": "e-cmd" } } } }""", "VS Code", "vscode");
        r.File("remote.json", """{ "mcpServers": { "f": { "type": "sse", "url": "https://f.example.com/sse" }, "g": { "url": "https://g.example.com/mcp/" } } }""", "Cursor", "mcpServers");
        var names = (await r.Scan()).Select(n => n!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], names);
        var scan = await r.Scan();
        Assert.Equal("http", scan[5]!["transport"]!.GetValue<string>());
        Assert.Equal("http", scan[6]!["transport"]!.GetValue<string>());
        Assert.Equal("stdio", scan[0]!["transport"]!.GetValue<string>());
    }

    [Fact]
    public async Task SourceFilesAreNeverChanged()
    {
        await using var r = Rig.Make();
        var path = r.File("c.json", Stdio(), "Cursor", "mcpServers");
        var bytes = File.ReadAllBytes(path);
        var when = File.GetLastWriteTimeUtc(path);
        var key = (await r.Scan())[0]!["key"]!.GetValue<string>();
        await r.Import(key);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(when, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task SameServerInTwoAppsShowsOnce()
    {
        await using var r = Rig.Make();
        r.File("a.json", Stdio(), "Claude Desktop", "mcpServers");
        r.File("b.json", Stdio(), "Cursor", "mcpServers");
        var scan = await r.Scan();
        var one = Assert.Single(scan);
        Assert.Equal(["Claude Desktop", "Cursor"], one!["sources"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray());
        await r.Import(one["key"]!.GetValue<string>());
        Assert.Equal(["Claude Desktop", "Cursor"], (await r.Row("files"))["sources"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task DifferentSecretsAreTwoRows()
    {
        await using var r = Rig.Make();
        r.File("a.json", Stdio(Key1), "Claude Desktop", "mcpServers");
        r.File("b.json", Stdio(Key2), "Cursor", "mcpServers");
        Assert.Equal(2, (await r.Scan()).Count);
    }

    [Fact]
    public async Task AlreadyAddedIsMarked()
    {
        await using var r = Rig.Make();
        await SaveMine(r);
        r.File("a.json", Stdio(), "Cursor", "mcpServers");
        Assert.Equal("mine", (await r.Scan())[0]!["alreadyAdded"]!.GetValue<string>());
    }

    private static Task<JsonNode?> SaveMine(Rig r, string region = "eu", string command = "node") => r.Svc.DispatchAsync("integrations.mcpSave", new JsonObject
    {
        ["name"] = "mine", ["command"] = command, ["args"] = new JsonArray("srv.js"),
        ["env"] = new JsonArray(new JsonObject { ["name"] = "API_KEY", ["secret"] = true }, new JsonObject { ["name"] = "REGION", ["value"] = region, ["secret"] = false }),
        ["secrets"] = new JsonObject { ["API_KEY"] = Key1 },
    }, Ct);

    [Fact]
    public async Task ImportSkipsAlreadyAdded()
    {
        await using var r = Rig.Make();
        await SaveMine(r);
        r.File("a.json", Stdio(), "Cursor", "mcpServers");
        var done = await r.Import((await r.Scan())[0]!["key"]!.GetValue<string>());
        Assert.Empty(done["imported"]!.AsArray());
        Assert.Equal("mine", done["alreadyAdded"]![0]!.GetValue<string>());
        Assert.Single(((JsonArray)(await r.Svc.DispatchAsync("integrations.mcpList", null, Ct))!));
    }

    [Fact]
    public async Task DifferentPlainEnvOrCaseOfUrlIdentity()
    {
        await using var r = Rig.Make();
        await SaveMine(r, region: "us");
        r.File("a.json", Stdio(), "Cursor", "mcpServers"); // REGION=eu differs from the saved us
        Assert.Null((await r.Scan())[0]!["alreadyAdded"]);
        await r.Svc.DispatchAsync("integrations.mcpSave", new JsonObject { ["name"] = "web", ["transport"] = "http", ["url"] = "https://Web.Example.com/mcp" }, Ct);
        r.File("b.json", """{ "mcpServers": { "w": { "url": "https://web.example.COM/mcp/" } } }""", "Claude Desktop", "mcpServers");
        Assert.Equal("web", (await r.Scan()).Single(c => c!["name"]!.GetValue<string>() == "w")!["alreadyAdded"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScanMasksSecretsInArgsUrlsAndConnectionStrings()
    {
        await using var r = Rig.Make();
        const string A = "argsecretvalue12345678", B = "eqsecretvalue12345678", C = "hunter2hunter2xx", D = "querysecret1234567890", E = "dbpassword9999";
        r.File("a.json", $$"""
            { "mcpServers": {
              "s": { "command": "srv", "args": ["--api-key", "{{A}}", "--port", "80", "--token={{B}}", "ACCESS_TOKEN={{C}}"], "env": { "DATABASE_URL": "postgres://bob:{{E}}@db/x" } },
              "u": { "url": "https://user:{{C}}@h.example.com/mcp?token={{D}}&x=1" } } }
            """, "Cursor", "mcpServers");
        var scan = await r.Scan();
        var text = scan.ToJsonString();
        foreach (var v in new[] { A, B, C, D, E }) Assert.DoesNotContain(v, text);
        Assert.Contains("--port", text);
        var done = await r.Import(scan[0]!["key"]!.GetValue<string>());
        Assert.Single(done["imported"]!.AsArray());
        var row = await r.Row("s");
        Assert.DoesNotContain(A, row["args"]!.ToJsonString()); // the list shows them masked (the stored row keeps them as written)
        Assert.Equal("[redacted]", Yawble.Core.Connections.SecretScrubber.Scrub(A));
        Assert.Equal("[redacted]", Yawble.Core.Connections.SecretScrubber.Scrub(E));
    }

    [Fact]
    public async Task OnlyAuthLikeHeadersAreTheSecret()
    {
        await using var r = Rig.Make();
        r.File("a.json", """{ "mcpServers": { "r": { "url": "https://r.example.com/mcp", "headers": { "Content-Type": "application/json", "X-Api-Key": "kkkk_ABCDEFGHIJKLMNOPQRST" } } } }""", "Cursor", "mcpServers");
        var c = (await r.Scan())[0]!;
        Assert.Equal("X-Api-Key", c["auth"]!["header"]!.GetValue<string>());
        Assert.Contains("Content-Type", c["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task OversizedFileIsSkipped()
    {
        await using var r = Rig.Make();
        var path = r.File("big.json", Stdio(), "Cursor", "mcpServers");
        using (var fs = new FileStream(path, FileMode.Append)) fs.Write(new byte[33 * 1024 * 1024]);
        Assert.Empty(await r.Scan());
    }

    [Fact]
    public async Task KeyIsNotAPlainHash()
    {
        await using var r = Rig.Make();
        r.File("a.json", Stdio(), "Cursor", "mcpServers");
        var key = (await r.Scan())[0]!["key"]!.GetValue<string>();
        Assert.Equal(key, (await r.Scan())[0]!["key"]!.GetValue<string>());
        var plain = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("node srv.js " + Key1)));
        Assert.NotEqual(plain, key);
    }

    [Fact]
    public void LooksSecretWholeWordsAndDefaults()
    {
        Assert.False(McpImporter.LooksSecret("KEYBOARD", "us"));
        Assert.False(McpImporter.LooksSecret("MONKEY", "1"));
        Assert.True(McpImporter.LooksSecret("apiKey", "1"));
        Assert.True(McpImporter.LooksSecret("APIKEY", "1"));
        Assert.True(McpImporter.LooksSecret("X-AUTH", "1"));
        Assert.True(McpImporter.LooksSecret("PRIVATE_THING", "1"));
        Assert.False(McpImporter.LooksSecret("REGION", "${REGION:-eu}"));
        Assert.False(McpImporter.LooksSecret("DIR", "${HOME}/x"));
        Assert.True(McpImporter.LooksSecret("DIR", "${DIRX}"));
    }

    [Fact]
    public async Task SecretsMoveIntoAConnection_ScanNeverReturnsThem()
    {
        await using var r = Rig.Make();
        r.File("a.json", Stdio(), "Cursor", "mcpServers");
        var scan = await r.Scan();
        Assert.DoesNotContain(Key1, scan.ToJsonString());
        Assert.Contains("REGION", scan.ToJsonString());
        var done = await r.Import(scan[0]!["key"]!.GetValue<string>());
        Assert.Equal("files", done["imported"]![0]!.GetValue<string>());
        var conn = Assert.Single(r.Conns.List(), c => c.Name == "files secrets");
        Assert.Equal(Key1, r.Conns.Secrets(conn.Id)["API_KEY"]);
        var row = await r.Row("files");
        Assert.DoesNotContain(Key1, row.ToJsonString());
        Assert.Equal("eu", row["env"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "REGION")!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task PlaceholderBecomesANeededSecret()
    {
        await using var r = Rig.Make();
        r.File("a.json", Stdio("<your-api-key>"), "Cursor", "mcpServers");
        var scan = await r.Scan();
        var env = scan[0]!["env"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "API_KEY")!;
        Assert.True(env["secret"]!.GetValue<bool>());
        Assert.Null(env["shown"]);
        Assert.DoesNotContain("your-api-key", scan.ToJsonString());
        var done = await r.Import(scan[0]!["key"]!.GetValue<string>());
        Assert.Single(done["imported"]!.AsArray());
        var row = await r.Row("files");
        Assert.True(row["env"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "API_KEY")!["secret"]!.GetValue<bool>());
        Assert.Empty(r.Conns.List());
    }

    [Fact]
    public void LooksSecretRules()
    {
        Assert.True(McpImporter.LooksSecret("GITHUB_TOKEN", "x1"));
        Assert.True(McpImporter.LooksSecret("my_pat", "x1"));
        Assert.True(McpImporter.LooksSecret("DB_PASS", "x1"));
        Assert.True(McpImporter.LooksSecret("API_Credential", "x1"));
        Assert.False(McpImporter.LooksSecret("PATH", "/usr/bin"));
        Assert.False(McpImporter.LooksSecret("PASSIVE", "1"));
        Assert.True(McpImporter.LooksSecret("HOST", "${HOST}"));
        Assert.True(McpImporter.LooksSecret("HOST", "YOUR_HOST"));
        Assert.True(McpImporter.LooksSecret("HOST", "xxxxxxxx"));
        Assert.False(McpImporter.LooksSecret("REGION", "eu"));
    }

    [Fact]
    public async Task TakenNameGetsSuffix()
    {
        await using var r = Rig.Make();
        await r.Svc.DispatchAsync("integrations.mcpSave", new JsonObject { ["name"] = "files", ["command"] = "other" }, Ct);
        r.File("a.json", Stdio(Key1), "Cursor", "mcpServers");
        r.File("b.json", Stdio(Key2), "Claude Desktop", "mcpServers");
        var keys = (await r.Scan()).Select(s => s!["key"]!.GetValue<string>()).ToArray();
        var done = await r.Import(keys);
        Assert.Equal(["files-2", "files-3"], done["imported"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task CommentsAndTrailingCommasAreRead()
    {
        await using var r = Rig.Make();
        r.File("a.json", "// mine\n{ /* x */ \"mcpServers\": { \"a\": { \"command\": \"a\", \"args\": [\"1\",], }, }, }", "Cursor", "mcpServers");
        Assert.Single(await r.Scan());
    }

    [Fact]
    public async Task BrokenFileIsSkipped()
    {
        await using var r = Rig.Make();
        r.File("bad.json", "{ not json", "Cursor", "mcpServers");
        r.Sources.Add(new McpConfigSource("Nope", Path.Combine(r.Db.Dir, "missing.json"), "mcpServers"));
        r.File("arr.json", "[1,2]", "Cursor", "mcpServers");
        r.File("ok.json", Stdio(), "Claude Desktop", "mcpServers");
        Assert.Single(await r.Scan());
    }

    [Fact]
    public async Task RemoteHeadersBecomeTokenSignIn()
    {
        await using var r = Rig.Make();
        r.File("a.json", """{ "mcpServers": { "r": { "url": "https://r.example.com/mcp", "headers": { "Authorization": "Bearer tok_ABCDEFGHIJKLMNOPQRST" } }, "k": { "type": "http", "url": "https://k.example.com/mcp", "headers": { "X-Api-Key": "kkkk_ABCDEFGHIJKLMNOPQRST" } } } }""", "Cursor", "mcpServers");
        var scan = await r.Scan();
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRST", scan.ToJsonString());
        Assert.Equal("bearer", scan[0]!["auth"]!["type"]!.GetValue<string>());
        Assert.Equal("header", scan[1]!["auth"]!["type"]!.GetValue<string>());
        var done = await r.Import(scan[0]!["key"]!.GetValue<string>(), scan[1]!["key"]!.GetValue<string>());
        Assert.Equal(2, done["imported"]!.AsArray().Count);
        var conn = Assert.Single(r.Conns.List(), c => c.Name == "r token");
        Assert.Equal("tok_ABCDEFGHIJKLMNOPQRST", r.Conns.Secrets(conn.Id)["token"]);
        Assert.Equal("X-Api-Key", (await r.Row("k"))["auth"]!["header"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownKeyIsReportedMissing()
    {
        await using var r = Rig.Make();
        var done = await r.Import("nope");
        Assert.Equal("nope", done["missing"]![0]!.GetValue<string>());
    }
}
