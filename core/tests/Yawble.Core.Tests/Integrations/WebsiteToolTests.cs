using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Agents use a website through web_&lt;name&gt;. A fake IWebsiteHost; no window, no network, no OS store.</summary>
public class WebsiteToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private sealed class FakeHost : IWebsiteHost
    {
        private readonly List<WebsiteRequest> _requests = [];
        public Func<WebsiteRequest, WebsiteReply> Script { get; set; } = q => new WebsiteReply(true, null, "https://example.com/", "Example", "page text", null, null);
        public bool Connected => true;
        public Task<WebsiteReply> RunAsync(WebsiteRequest request, CancellationToken ct)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(Script(request));
        }
        public WebsiteRequest[] Seen() { lock (_requests) return [.. _requests]; }
    }

    /// <summary>The raw tool result an agent got: its text, and how many images came with it.</summary>
    private sealed record Got(string Text, int Images, string? ImageData);

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;
        public ConnectionService Conns = null!;
        public readonly FakeHost Host = new();
        public readonly StepClock Clock = new();
        public long Alpha, Beta, Login;
        private readonly ConcurrentDictionary<int, (string Tool, object Args, TaskCompletionSource<Got> Done)> _calls = new();
        private int _next;

        /// <summary>Alpha/Ada and Beta/Bo; the website "Example" (https://example.com/) with a login, offered to both teams; for Alpha:
        /// open, read, screenshot allowed, click asks, fill off.</summary>
        public static async Task<Rig> Make(bool withLogin = true)
        {
            var r = new Rig();
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner { Default = r.Handle }, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            r.Conns = new ConnectionService(new MemoryCredentialStore(), r.Db.ConnectionString);
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, r.Clock, connections: r.Conns, websites: r.Host);
            r.Engine.Integrations = r.Svc;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            r.Beta = await r.Engine.CreateTeamAsync("Beta", ct: Ct);
            await r.Engine.AddMemberAsync("Beta", "Bo", ct: Ct);
            await r.Engine.StartAsync(Ct);

            var save = new JsonObject { ["kind"] = "website", ["name"] = "Example", ["url"] = "https://example.com/app/" };
            if (withLogin)
            {
                r.Login = (await r.Rpc("integrations.connectionSave", new JsonObject { ["kind"] = "website", ["site"] = "https://example.com/app/" }))!["id"]!.GetValue<long>();
                save["connectionId"] = r.Login;
            }
            await r.Rpc("integrations.apiSave", save);
            await r.Rpc("integrations.discover", new JsonObject { ["name"] = "Example", ["kind"] = "api" });
            await Wait.UntilAsync(async () => (await r.Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "Example", ["kind"] = "api" }))!["state"]!.GetValue<string>() != "running", "discovered");
            await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = "Example", ["teamId"] = r.Alpha });
            await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = "Example", ["teamId"] = r.Beta });
            await r.Checklist(r.Alpha, ("open", "allow"), ("read", "allow"), ("screenshot", "allow"), ("click", "ask"), ("fill", "off"), ("submit", "off"));
            await r.Checklist(r.Beta, ("open", "allow"), ("read", "allow"), ("screenshot", "allow"), ("click", "off"), ("fill", "off"), ("submit", "off"));
            return r;
        }

        public Task Checklist(long team, params (string Op, string State)[] states) => Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "Example", ["kind"] = "api", ["teamId"] = team,
            ["choices"] = new JsonArray(states.Select(s => (JsonNode)new JsonObject { ["path"] = s.Op, ["state"] = s.State }).ToArray()),
        });

        public async Task<JsonNode?> Rpc(string method, JsonObject? p = null) => await Svc.DispatchAsync(method, p, Ct);

        private async Task<RunResult> Handle(RunContext run, CancellationToken ct)
        {
            // Every item of the batch, not just the first: a call told to Ada while her previous run is still ending (slow
            // under a loaded full suite) waits and is batched with whatever else is pending for her in that workflow, e.g.
            // the "approved: call screenshot again" notice, which then comes first.
            foreach (var (id, call) in _calls)
            {
                if (!run.Items.Any(m => m.Payload.Contains($"call#{id}#", StringComparison.Ordinal))) continue;
                _calls.TryRemove(id, out _);
                try { call.Done.TrySetResult(await CallTool(run, call.Tool, call.Args)); }
                catch (Exception e) { call.Done.TrySetException(e); }
            }
            return RunResult.Ok;
        }

        /// <summary>Through the MCP server the agent talks to, so the image content is checked where it is built.</summary>
        private static async Task<Got> CallTool(RunContext run, string tool, object args)
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
            };
            var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
            var node = JsonNode.Parse(reply.Json!)!;
            var content = node["result"]?["content"]?.AsArray();
            if (content is null) return new Got(node["error"]?["message"]?.GetValue<string>() ?? reply.Json!, 0, null);
            var image = content.OfType<JsonObject>().FirstOrDefault(c => c["type"]?.GetValue<string>() == "image");
            return new Got(content[0]!["text"]!.GetValue<string>(), content.Count(c => c!["type"]!.GetValue<string>() == "image"), image?["data"]?.GetValue<string>());
        }

        public async Task<Got> CallAs(string team, string member, object args, string tool = "web_example")
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<Got>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = (tool, args, done);
            await Engine.PersonTellAsync(team, member, $"call#{id}#", ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        /// <summary>Ada's screenshot call, made in a run of the given workflow (a person's message to her joins it).</summary>
        public async Task<Got> CallInWorkflow(long causation)
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<Got>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = ("web_example", new { action = "screenshot", _reason = "see it again" }, done);
            await Engine.PersonTellAsync("Alpha", "Ada", $"call#{id}#", causation, ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        public async Task<string> CallInWorkflowText(long causation) => (await CallInWorkflow(causation)).Text;

        public async Task<string> Ada(object args) => (await CallAs("Alpha", "Ada", args)).Text;

        public bool Told(long workflow, string text)
        {
            using var s = Db.OpenStore();
            return s.Workflow(workflow).Any(m => m.Payload.Contains(text, StringComparison.Ordinal));
        }

        public long Scalar(string sql)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public string AllLoggedText()
        {
            var sb = new StringBuilder();
            foreach (var sql in new[] { "SELECT args || ifnull(rule, '') FROM integration_calls", "SELECT args || reason FROM integration_asks" })
            {
                using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) sb.Append(rd.GetString(0)).Append('\n');
            }
            return sb.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await Wait.UntilAsync(() => Engine.IsQuiescentAsync(Ct), "the runs ended");
            await Svc.DisposeAsync();
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task WebsiteToolIsOfferedWithItsOpenActions()
    {
        await using var r = await Rig.Make();
        var tool = Assert.Single(r.Svc.ToolsFor(r.Alpha), t => t["name"]!.GetValue<string>() == "web_example");
        Assert.True(r.Svc.IsTool("web_example"));
        var d = tool["description"]!.GetValue<string>();
        Assert.StartsWith("Use the person's website Example (example.com) in a browser on their computer", d);
        Assert.Contains("You never get the sign-in or cookies. Pages outside example.com are stopped.", d);
        Assert.Contains("- open — ", d);
        Assert.Contains("- screenshot — ", d);
        Assert.Contains("- click — ", d); // asks first: still listed
        Assert.DoesNotContain("- fill — ", d); // off
        Assert.DoesNotContain("- submit — ", d);
        var props = (JsonObject)tool["inputSchema"]!["properties"]!;
        Assert.Equal(["action", "url", "selector", "text", "_reason"], props.Select(p => p.Key));
        Assert.Equal(["open", "read", "screenshot", "click", "fill", "submit"], ((JsonArray)props["action"]!["enum"]!).Select(n => n!.GetValue<string>()));
        // the web API tool list does not carry it
        Assert.DoesNotContain(r.Svc.ToolsFor(r.Alpha), t => t["name"]!.GetValue<string>() == "api_example");

        // nothing allowed or asked: no tool at all
        await r.Checklist(r.Beta, ("open", "off"), ("read", "off"), ("screenshot", "off"), ("click", "off"), ("fill", "off"), ("submit", "off"));
        Assert.DoesNotContain(r.Svc.ToolsFor(r.Beta), t => t["name"]!.GetValue<string>() == "web_example");
    }

    [Fact]
    public async Task OpenReadsThePage()
    {
        await using var r = await Rig.Make();
        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/app/inbox", "Inbox", "3 new messages", null, null);
        var text = await r.Ada(new { action = "open", url = "https://example.com/app/inbox" });
        Assert.Equal("Inbox — https://example.com/app/inbox\n\n3 new messages", text);
        var sent = Assert.Single(r.Host.Seen());
        Assert.Equal("open", sent.Op);
        Assert.Equal("https://example.com/app/", sent.SiteUrl);
        Assert.Equal("https://example.com/app/inbox", sent.Args["url"]!.GetValue<string>());
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:Example' AND decision = 'allow'"));
        Assert.DoesNotContain("3 new messages", r.AllLoggedText()); // never the page text
    }

    [Fact]
    public async Task RelativeUrlResolvesAgainstTheSite()
    {
        await using var r = await Rig.Make();
        await r.Ada(new { action = "open", url = "/app/settings?tab=1" });
        await r.Ada(new { action = "open", url = "billing" });
        Assert.Equal(["https://example.com/app/settings?tab=1", "https://example.com/app/billing"], r.Host.Seen().Select(s => s.Args["url"]!.GetValue<string>()));
    }

    [Fact]
    public async Task OutsideUrlIsRefusedBeforeTheShell()
    {
        await using var r = await Rig.Make();
        foreach (var url in new[] { "https://evil.test/", "https://example.com.evil.test/", "//evil.test/x", "https://user:pw@example.com/" })
            Assert.StartsWith("refused:", await r.Ada(new { action = "open", url }));
        Assert.Contains("is outside https://example.com/app/", await r.Ada(new { action = "open", url = "https://evil.test/" }));
        Assert.Empty(r.Host.Seen());
        Assert.StartsWith("refused:", await r.Ada(new { action = "open" })); // no url
    }

    [Fact]
    public async Task NonWebUrlIsRefused()
    {
        await using var r = await Rig.Make();
        foreach (var url in new[] { "javascript:alert(1)", "file:///c:/windows/win.ini", "data:text/html,<script>1</script>", "ftp://example.com/x" })
            Assert.StartsWith("refused:", await r.Ada(new { action = "open", url }));
        Assert.Empty(r.Host.Seen());
    }

    [Fact]
    public async Task OffActionIsRefused()
    {
        await using var r = await Rig.Make();
        var text = await r.Ada(new { action = "fill", selector = "#q", text = "hello", _reason = "type it" });
        Assert.StartsWith("refused:", text);
        Assert.Empty(r.Host.Seen());
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:Example' AND decision = 'deny'"));
        Assert.StartsWith("refused:", await r.Ada(new { action = "bogus" }));
        // Beta's team has click off: Bo is refused even though Ada may ask
        Assert.StartsWith("refused:", (await r.CallAs("Beta", "Bo", new { action = "click", selector = "#go", _reason = "x" })).Text);
    }

    [Fact]
    public async Task AskedClickRunsAfterApproval()
    {
        await using var r = await Rig.Make();
        var asked = await r.Ada(new { action = "click", selector = "#send", _reason = "send the draft" });
        Assert.StartsWith("asked:", asked);
        Assert.Empty(r.Host.Seen()); // nothing sent yet
        var ask = Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: true));
        Assert.Equal("api:Example", ask.Integration);
        Assert.Equal("click", ask.Args[0]);
        Assert.Contains("#send", ask.Args[1]);

        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/app/sent", "Sent", "Draft sent", null, null);
        var approved = await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id });
        Assert.True(approved!["started"]!.GetValue<bool>());
        await Wait.UntilAsync(() => r.Told(ask.Workflow, "Draft sent"), "Ada told the result");
        var sent = Assert.Single(r.Host.Seen());
        Assert.Equal("click", sent.Op);
        Assert.Equal("#send", sent.Args["selector"]!.GetValue<string>());
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:Example' AND decision = 'approved'"));

        // a click without a selector is refused before anyone is asked
        Assert.StartsWith("refused:", await r.Ada(new { action = "click", _reason = "x" }));
        Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: false));
    }

    private static async Task<AskRow> AskForScreenshot(Rig r)
    {
        await r.Checklist(r.Alpha, ("open", "allow"), ("read", "allow"), ("screenshot", "ask"), ("click", "off"), ("fill", "off"), ("submit", "off"));
        Assert.StartsWith("asked:", await r.Ada(new { action = "screenshot", _reason = "see it" }));
        return Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: true));
    }

    [Fact]
    public async Task ApprovedScreenshotIsGrantedOnceToADirectCall()
    {
        await using var r = await Rig.Make();
        var ask = await AskForScreenshot(r);
        await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id });
        await Wait.UntilAsync(() => r.Told(ask.Workflow, "approved: call screenshot again now (within 10 minutes) to get the picture"), "Ada told");
        Assert.Empty(r.Host.Seen());

        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/app/", "Home", null, Png, null);
        var got = await r.CallInWorkflow(ask.CausationSeq);
        Assert.Equal(1, got.Images);
        Assert.Single(r.Host.Seen());
        Assert.Equal(1, r.Scalar($"SELECT count(*) FROM integration_calls WHERE decision = 'allow' AND rule = 'approved request {ask.Id}'"));
        // the grant is used up: the next picture asks again
        Assert.StartsWith("asked:", await r.CallInWorkflowText(ask.CausationSeq));
    }

    [Fact]
    public async Task ExpiredScreenshotGrantAsksAgain()
    {
        await using var r = await Rig.Make();
        var ask = await AskForScreenshot(r);
        await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id });
        await Wait.UntilAsync(() => r.Told(ask.Workflow, "within 10 minutes"), "Ada told");
        r.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.StartsWith("asked:", await r.CallInWorkflowText(ask.CausationSeq));
        Assert.Empty(r.Host.Seen());
    }

    [Fact]
    public async Task ScreenshotReturnsAnImage()
    {
        await using var r = await Rig.Make();
        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/app/", "Home", null, Png, null);
        // through McpBusServer: the image content reaches the tool result
        var got = await r.CallAs("Alpha", "Ada", new { action = "screenshot" });
        Assert.Equal("Home — https://example.com/app/ (screenshot attached)", got.Text);
        Assert.Equal(1, got.Images);
        Assert.Equal(Convert.ToBase64String(Png), got.ImageData);
        // a text reply carries no image
        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/app/", "Home", "hi", null, null);
        Assert.Equal(0, (await r.CallAs("Alpha", "Ada", new { action = "read" })).Images);
    }

    [Fact]
    public async Task StoppedRedirectSaysSignInAgain()
    {
        await using var r = await Rig.Make();
        r.Host.Script = _ => new WebsiteReply(true, null, null, null, null, null, "https://login.idp.test/oauth/authorize?x=1");
        var text = await r.Ada(new { action = "open", url = "/app/" });
        Assert.StartsWith("stopped: the site sent the page to login.idp.test, outside example.com.", text);
        Assert.Contains("The website login may need signing in again (Settings → Integrations → Connections); stop and tell the person.", text);
        // and the login shows in Needs You
        var b = r.Conns.Brief(r.Login)!;
        Assert.Equal("needs_reconnect", b.Status);
        Assert.Contains("login.idp.test", b.StatusReason);
        var attention = (JsonArray)(await r.Rpc("integrations.connectionAttention"))!;
        Assert.Equal(r.Login, Assert.Single(attention)!["id"]!.GetValue<long>());

        r.Host.Script = _ => new WebsiteReply(true, null, null, null, null, null, "https://news.other.test/story");
        var plain = await r.Ada(new { action = "open", url = "/app/" });
        Assert.Equal("stopped: the site sent the page to news.other.test, outside example.com.", plain);
        Assert.Contains("login.idp.test", r.Conns.Brief(r.Login)!.StatusReason); // a plain outside link is not a sign-in: the reason stays

        r.Host.Script = _ => throw new WebsiteException("website actions need the app to be open on this computer");
        Assert.Equal("could not use Example: website actions need the app to be open on this computer", await r.Ada(new { action = "read" }));
        r.Host.Script = _ => new WebsiteReply(false, "the window broke", null, null, null, null, null);
        Assert.Equal("could not use Example: the window broke", await r.Ada(new { action = "read" }));
    }

    [Fact]
    public async Task PageTextIsScrubbedAndCapped()
    {
        await using var r = await Rig.Make();
        const string Secret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var owner = new object();
        SecretScrubber.Set(owner, 1, [Secret]);
        try
        {
            r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/app/", "Keys", $"token {Secret}\n" + new string('x', 200_000), null, null);
            var text = await r.Ada(new { action = "read" });
            Assert.DoesNotContain(Secret, text);
            Assert.Contains("[redacted]", text);
            Assert.True(text.Length <= 64 * 1024 + 64, $"{text.Length}");
            Assert.EndsWith("(output cut at 64 KB)", text);
        }
        finally { SecretScrubber.Forget(owner, 1); }
    }

    [Fact]
    public async Task HostileSelectorAndTextArePassedAsData()
    {
        await using var r = await Rig.Make();
        await r.Checklist(r.Alpha, ("open", "allow"), ("read", "allow"), ("screenshot", "allow"), ("click", "allow"), ("fill", "allow"), ("submit", "off"));
        const string Hostile = "a\"]'</script>`${x}";
        await r.Ada(new { action = "fill", selector = Hostile, text = Hostile });
        var sent = Assert.Single(r.Host.Seen());
        Assert.Equal(Hostile, sent.Args["selector"]!.GetValue<string>());
        Assert.StartsWith("refused:", await r.Ada(new { action = "fill", selector = "#a", text = new string('x', 10_001) }));
        Assert.Single(r.Host.Seen());
    }

    [Fact]
    public async Task MembersGetTheirOwnPage()
    {
        await using var r = await Rig.Make();
        await r.Engine.AddMemberAsync("Alpha", "Al", ct: Ct);
        await r.Ada(new { action = "read" });
        await r.CallAs("Alpha", "Al", new { action = "read" });
        await r.CallAs("Beta", "Bo", new { action = "read" });
        await r.Ada(new { action = "read" });
        var keys = r.Host.Seen().Select(s => s.PageKey).ToList();
        Assert.Equal(3, keys.Distinct().Count()); // two agents of one team never share a page
        Assert.Equal(keys[0], keys[3]); // the same agent keeps its page
        Assert.StartsWith($"{keys[0].Split(':')[0]}:{r.Alpha}:", keys[0]);
        Assert.StartsWith($"{keys[0].Split(':')[0]}:{r.Alpha}:", keys[1]);
        Assert.StartsWith($"{keys[0].Split(':')[0]}:{r.Beta}:", keys[2]); // one website, two teams
        Assert.Contains("Ada", keys[0]);
        Assert.Contains("Al", keys[1]);
        Assert.Single(r.Host.Seen().Select(x => x.Partition).Distinct()); // one login: one session
    }

    [Fact]
    public async Task EveryRequestCarriesADeadline()
    {
        await using var r = await Rig.Make();
        await r.Ada(new { action = "read" });
        var sent = Assert.Single(r.Host.Seen());
        Assert.Equal((r.Clock.GetUtcNow() + ShellWebsiteHost.WebsiteTimeout).ToUnixTimeMilliseconds(), sent.Deadline);
    }

    [Fact]
    public async Task ScreenshotWithNoAddressHasNoEmptyHeader()
    {
        await using var r = await Rig.Make();
        r.Host.Script = _ => new WebsiteReply(true, null, null, null, null, Png, null);
        Assert.Equal("screenshot attached", (await r.CallAs("Alpha", "Ada", new { action = "screenshot" })).Text);
        r.Host.Script = _ => new WebsiteReply(true, null, null, null, "filled", null, null);
        await r.Checklist(r.Alpha, ("open", "allow"), ("read", "allow"), ("screenshot", "allow"), ("click", "allow"), ("fill", "allow"), ("submit", "off"));
        Assert.Equal("filled", await r.Ada(new { action = "fill", selector = "#q", text = "hello" }));
        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/app/", "Search", "filled", null, null);
        Assert.Equal("Search — https://example.com/app/\n\nfilled", await r.Ada(new { action = "fill", selector = "#q", text = "hello" }));
    }

    [Fact]
    public async Task LoginPartitionIsTheConnectionsOwn()
    {
        await using var r = await Rig.Make();
        await r.Ada(new { action = "read" });
        Assert.Equal(IntegrationService.WebsitePartitionPrefix + r.Conns.StoreKeyOf(r.Login), r.Host.Seen()[0].Partition);
    }

    [Fact]
    public async Task NoConnectionUsesAPublicPartition()
    {
        await using var r = await Rig.Make(withLogin: false);
        await r.Ada(new { action = "read" });
        var apiId = r.Scalar("SELECT id FROM apis WHERE name = 'Example'");
        Assert.Equal($"persist:website-public-{apiId}", r.Host.Seen()[0].Partition);
    }

    [Fact]
    public async Task ReasonAliasIsAcceptedAndBlankIsMissing()
    {
        await using var r = await Rig.Make();
        Assert.StartsWith("asked:", await r.Ada(new { action = "click", selector = "#a", reason = "because" }));
        Assert.Contains("give a reason", await r.Ada(new { action = "click", selector = "#b", _reason = "   " }));
    }

    [Fact]
    public async Task BackslashUrlsAreRefused()
    {
        await using var r = await Rig.Make();
        foreach (var url in new[] { "/\\evil.test", "\\\\evil.test\\x" })
            Assert.StartsWith("refused:", await r.Ada(new { action = "open", url }));
        Assert.Empty(r.Host.Seen());
    }

    [Fact]
    public async Task StoredSecretIsNeverTyped()
    {
        await using var r = await Rig.Make();
        const string Secret = "pw_correct_horse_battery";
        var owner = new object();
        SecretScrubber.Set(owner, 1, [Secret]);
        try
        {
            Assert.StartsWith("refused:", await r.Ada(new { action = "fill", selector = "#p", text = Secret })); // off here: refused either way
            await r.Checklist(r.Alpha, ("open", "allow"), ("read", "allow"), ("screenshot", "allow"), ("click", "ask"), ("fill", "allow"), ("submit", "off"));
            Assert.Contains("stored secret", await r.Ada(new { action = "fill", selector = "#p", text = "x " + Secret }));
            await r.Checklist(r.Alpha, ("open", "allow"), ("read", "allow"), ("screenshot", "allow"), ("click", "ask"), ("fill", "ask"), ("submit", "off"));
            Assert.Contains("stored secret", await r.Ada(new { action = "fill", selector = "#p", text = Secret, _reason = "type it" }));
            Assert.Empty(r.Svc.Asks(r.Alpha, pendingOnly: false));
            Assert.Empty(r.Host.Seen());
        }
        finally { SecretScrubber.Forget(owner, 1); }
    }

    [Fact]
    public async Task AllowedCallIsLoggedEvenIfTheWindowThrows()
    {
        await using var r = await Rig.Make();
        r.Host.Script = _ => throw new InvalidOperationException("boom");
        await Assert.ThrowsAnyAsync<Exception>(() => r.Ada(new { action = "read" }));
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:Example' AND decision = 'allow'"));
    }

    [Fact]
    public async Task RemovedLoginFallsBackToAPublicPartition()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.connectionRemove", new JsonObject { ["id"] = r.Login, ["confirm"] = true });
        Assert.Null(r.Conns.Get(r.Login));
        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/app/", "Home", "hi", null, null);
        await r.Ada(new { action = "read" });
        var apiId = r.Scalar("SELECT id FROM apis WHERE name = 'Example'");
        Assert.Equal($"persist:website-public-{apiId}", r.Host.Seen().Last().Partition);
    }
}
