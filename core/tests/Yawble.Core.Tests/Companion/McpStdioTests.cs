extern alias yawble;

using System.Text.Json.Nodes;
using Yawble.Core.Companion;
using Yawble.Core.Gateway;
using Xunit;
using Y = yawble::Yawble.Companion;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// Companion task 6: `yawble mcp`, newline-delimited JSON-RPC 2.0 over stdin/stdout, in process: StringReader in,
/// StringWriter out, a <see cref="FakeConnector"/> for the app and a temp YAWBLE_HOME for the key. No process, no pipe.
/// </summary>
public class McpStdioTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _home = Directory.CreateTempSubdirectory("yawble-ymcp-").FullName;

    public void Dispose()
    {
        try { TestFiles.DeleteTree(_home); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Y.KeyFiles Keys => new(_home);

    private static string Line(object id, string method, JsonObject? p = null) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id is int i ? (JsonNode)i : (JsonNode)(string)id, ["method"] = method, ["params"] = p }.ToJsonString();

    private static string Note(string method, JsonObject? p = null) => new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p }.ToJsonString();

    private sealed record Session(int Exit, List<JsonObject> Replies, string RawOut, string Log);

    private async Task<Session> RunAsync(FakeConnector connector, TimeSpan? wait, params string[] lines)
    {
        var output = new StringWriter();
        var log = new StringWriter();
        var mcp = new Y.McpStdio(connector, Keys, null, new StringReader(string.Join("\n", lines) + "\n"), TextWriter.Synchronized(output), TextWriter.Synchronized(log), wait);
        var exit = await mcp.RunAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var raw = output.ToString();
        var replies = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        return new Session(exit, replies, raw, log.ToString());
    }

    private static JsonObject ById(Session s, int id) => s.Replies.Single(r => r["id"]?.GetValue<int>() == id);

    private static string ToolText(JsonObject reply) => reply["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static JsonObject Tools() => new()
    {
        ["tools"] = new JsonArray(new JsonObject
        {
            ["name"] = "team_list", ["description"] = "Every team.", ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        }),
    };

    [Fact]
    public async Task McpInitializeListCall_StdoutHasOnlyJsonRpc()
    {
        Keys.Save("claude-code", new string('k', 43));
        var fake = new FakeConnector
        {
            Handle = (m, p, _) => m switch
            {
                CompanionProtocol.Tools => Tools(),
                CompanionProtocol.Call => FakeConnector.Result($"teams: alpha ({p!["tool"]!.GetValue<string>()})"),
                _ => null,
            },
        };
        var s = await RunAsync(fake, null,
            Line(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "claude-code" } }),
            Note("notifications/initialized"),
            Line(2, "tools/list"),
            Line(3, "tools/call", new JsonObject { ["name"] = "team_list", ["arguments"] = new JsonObject() }),
            Line(4, "ping"),
            Line(5, "resources/list"));
        Assert.Equal(0, s.Exit);

        // every stdout line is a JSON-RPC 2.0 reply to one of the requests: nothing else
        Assert.Equal(5, s.Replies.Count);
        Assert.All(s.Replies, r => Assert.Equal("2.0", r["jsonrpc"]!.GetValue<string>()));
        Assert.Equal([1, 2, 3, 4, 5], s.Replies.Select(r => r["id"]!.GetValue<int>()).Order());
        Assert.True(s.RawOut.EndsWith('\n'));

        var init = ById(s, 1)["result"]!;
        Assert.Equal("2025-06-18", init["protocolVersion"]!.GetValue<string>());
        Assert.Equal("yawble", init["serverInfo"]!["name"]!.GetValue<string>());
        Assert.True(init["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.Equal("team_list", ById(s, 2)["result"]!["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("teams: alpha (team_list)", ToolText(ById(s, 3)));
        Assert.False(ById(s, 3)["result"]!["isError"]!.GetValue<bool>());
        Assert.NotNull(ById(s, 4)["result"]);
        Assert.Equal(-32601, ById(s, 5)["error"]!["code"]!.GetValue<int>());

        // one connection, made when first needed (initialize alone does not connect), authenticated with the only key here
        Assert.Equal(1, fake.Connects);
        Assert.Equal(CompanionProtocol.Auth, fake.Methods.First());
        Assert.Equal("claude-code", fake.Calls[0].P!["client"]!.GetValue<string>());
        Assert.DoesNotContain(new string('k', 43), s.RawOut + s.Log);

        // the versions are the core's own, and an unknown one gets the newest
        Assert.Equal(McpBusServer.SupportedProtocolVersions, Y.McpStdio.SupportedProtocolVersions);
        var other = await RunAsync(new FakeConnector(), null, Line(1, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }));
        Assert.Equal(McpBusServer.SupportedProtocolVersions[0], ById(other, 1)["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpNotificationsGetNoReply()
    {
        Keys.Save("cc", new string('k', 43));
        var fake = new FakeConnector();
        var s = await RunAsync(fake, null,
            Note("notifications/initialized"),
            Note("notifications/cancelled", new JsonObject { ["requestId"] = 9 }),
            Note("tools/list"), // no id: a notification, whatever its method
            "");
        Assert.Equal(0, s.Exit);
        Assert.Equal("", s.RawOut);
        Assert.Empty(fake.Methods);

        // garbage gets a parse error (id null), never a crash or stray text
        // and a message with an id but no method (yawble sends no requests, so no response is expected) is invalid
        var bad = await RunAsync(new FakeConnector(), null, "{not json", "[1,2]",
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject() }.ToJsonString());
        Assert.Equal(3, bad.Replies.Count);
        Assert.Equal(-32700, bad.Replies[0]["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32600, bad.Replies[1]["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32600, bad.Replies[2]["error"]!["code"]!.GetValue<int>());
        Assert.Equal(1, bad.Replies[2]["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task McpWhenTheAppIsNotRunningAnswersAClearError()
    {
        // not paired yet: says how
        var unpaired = await RunAsync(new FakeConnector(), null, Line(1, "tools/call", new JsonObject { ["name"] = "team_list" }));
        Assert.Contains("yawble pair --name", ToolText(ById(unpaired, 1)));
        Assert.True(ById(unpaired, 1)["result"]!["isError"]!.GetValue<bool>());

        Keys.Save("cc", new string('k', 43));
        var fake = new FakeConnector { NotRunning = true };
        var s = await RunAsync(fake, null,
            Line(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" }),
            Line(2, "tools/list"),
            Line(3, "tools/call", new JsonObject { ["name"] = "team_list", ["arguments"] = new JsonObject() }));
        Assert.NotNull(ById(s, 1)["result"]); // initialize needs no app
        // tools/list offers one tool, yawble_status, whose description says why there are no others
        var offered = Assert.Single(ById(s, 2)["result"]!["tools"]!.AsArray())!;
        Assert.Equal("yawble_status", offered["name"]!.GetValue<string>());
        Assert.Contains("Yawble is not running on this computer", offered["description"]!.GetValue<string>());
        var call = ById(s, 3)["result"]!.AsObject();
        Assert.True(call["isError"]!.GetValue<bool>());
        Assert.Contains("Yawble is not running on this computer", ToolText(ById(s, 3)));

        // the app restarts between calls: the old connection breaks on write, yawble connects again and the call goes through
        var restart = new FakeConnector { Handle = (m, _, _) => m == CompanionProtocol.Call ? FakeConnector.Result("ok") : null };
        var output = new StringWriter();
        var input = new BlockingLines();
        var mcp = new Y.McpStdio(restart, Keys, "cc", input, TextWriter.Synchronized(output), TextWriter.Null, null);
        var running = mcp.RunAsync(Ct);
        input.Add(Line(1, "tools/call", new JsonObject { ["name"] = "team_list" }));
        await WaitForAsync(() => output.ToString().Contains("\"id\":1"));
        restart.BreakNext = true;
        input.Add(Line(2, "tools/call", new JsonObject { ["name"] = "team_list" }));
        await WaitForAsync(() => output.ToString().Contains("\"id\":2"));
        input.End();
        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.Equal(2, restart.Connects);
        var second = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).Single(r => r["id"]!.GetValue<int>() == 2);
        Assert.Equal("ok", ToolText(second));

        // and the app coming up later is found on the next call (lazy, no restart of yawble needed); the client that was offered
        // only yawble_status is told the list changed, and then gets the real tools
        var late = new FakeConnector
        {
            NotRunning = true,
            Handle = (m, _, _) => m switch
            {
                CompanionProtocol.Call => FakeConnector.Result("up now"),
                CompanionProtocol.Tools => Tools(),
                CompanionProtocol.WhoAmI => new JsonObject { ["client"] = "cc", ["level"] = "read", ["levelDescription"] = "Can look." },
                _ => null,
            },
        };
        await using var live = Live.Start(late, Keys);
        live.Add(Line(1, "tools/list"));
        live.Add(Line(2, "tools/call", new JsonObject { ["name"] = "yawble_status" }));
        await WaitForAsync(() => live.Reply(2) is not null);
        Assert.True(live.Reply(2)!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("not running", ToolText(live.Reply(2)!));
        Assert.DoesNotContain(live.Lines(), l => l["method"] is not null);
        late.NotRunning = false;
        live.Add(Line(3, "tools/call", new JsonObject { ["name"] = "yawble_status" }));
        await WaitForAsync(() => live.Reply(3) is not null);
        Assert.False(live.Reply(3)!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("cc", ToolText(live.Reply(3)!));
        Assert.Single(live.Lines(), l => l["method"]?.GetValue<string>() == "notifications/tools/list_changed" && l["id"] is null);
        live.Add(Line(4, "tools/list"));
        live.Add(Line(5, "tools/call", new JsonObject { ["name"] = "team_list" }));
        await WaitForAsync(() => live.Reply(5) is not null);
        Assert.Equal("team_list", live.Reply(4)!["result"]!["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("up now", ToolText(live.Reply(5)!));
        await live.EndAsync();
    }

    [Fact]
    public async Task McpPendingApprovalIsPolledThenReported()
    {
        Keys.Save("cc", new string('k', 43));
        const string plan = "Delete branches of alpha: feature-a (abc1234).";
        var states = new Queue<string>();
        var waits = new List<int>();
        var fake = new FakeConnector
        {
            Handle = (m, p, _) =>
            {
                switch (m)
                {
                    case CompanionProtocol.Call:
                        return FakeConnector.Result($"NOT DONE: waiting for the person's approval in the Yawble window (request r7): {plan}", requestId: "r7");
                    case CompanionProtocol.Request:
                        lock (waits) waits.Add(p!["waitSeconds"]!.GetValue<int>());
                        var state = states.Count > 0 ? states.Dequeue() : "pending";
                        return new JsonObject { ["id"] = "r7", ["state"] = state, ["plan"] = plan, ["result"] = state == "done" ? "ok: deleted feature-a." : null, ["nextId"] = null };
                    default:
                        return null;
                }
            },
        };
        var call = Line(1, "tools/call", new JsonObject { ["name"] = "git_branches_delete", ["arguments"] = new JsonObject { ["team"] = "alpha" } });

        // still pending once the wait is over: reported as waiting for the person, how to check, never a token
        var waiting = await RunAsync(fake, TimeSpan.FromSeconds(90), call);
        var text = ToolText(ById(waiting, 1));
        Assert.Contains("waiting for the person's approval in the Yawble window", text);
        Assert.Contains("request_status", text);
        Assert.Contains("r7", text);
        Assert.Contains(plan, text);
        Assert.DoesNotContain("confirm", text);
        Assert.False(ById(waiting, 1)["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal([60, 30], waits);

        // approved during the wait: the result itself
        waits.Clear();
        states = new Queue<string>(["done"]);
        var done = await RunAsync(fake, null, call);
        Assert.Equal("ok: deleted feature-a.", ToolText(ById(done, 1)));
        Assert.Equal([60], waits); // YAWBLE_MCP_WAIT's default: 60 s

        // denied: an error result
        states = new Queue<string>(["denied"]);
        var denied = await RunAsync(fake, null, call);
        Assert.True(ById(denied, 1)["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("denied", ToolText(ById(denied, 1)));

        // the app restarted while it waited: the request is gone
        var lost = new FakeConnector
        {
            Handle = (m, p, e) => m == CompanionProtocol.Request ? throw new Y.ControlErrorException("not_found", "there is no request 'r7' of this client") : fake.Handle!(m, p, e),
        };
        var gone = await RunAsync(lost, null, call);
        Assert.True(ById(gone, 1)["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("restarted", ToolText(ById(gone, 1)));
        Assert.Contains("ask again", ToolText(ById(gone, 1)));
        Assert.DoesNotContain("waiting for the person", ToolText(ById(gone, 1)));
    }

    [Fact]
    public async Task McpAPendingApprovalDoesNotBlockOtherCalls()
    {
        Keys.Save("cc", new string('k', 43));
        var decided = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeConnector
        {
            HandleAsync = (m, p) => m switch
            {
                CompanionProtocol.Call when p!["tool"]!.GetValue<string>() == "team_delete" =>
                    Task.FromResult<JsonObject?>(FakeConnector.Result("NOT DONE: waiting for the person's approval in the Yawble window (request r1): Delete alpha.", requestId: "r1")),
                CompanionProtocol.Call => Task.FromResult<JsonObject?>(FakeConnector.Result("teams: alpha")),
                CompanionProtocol.Tools => Task.FromResult<JsonObject?>(Tools()),
                CompanionProtocol.Request => decided.Task, // the app's long-poll: answers when the person decides
                _ => Task.FromResult<JsonObject?>(null),
            },
        };
        await using var live = Live.Start(fake, Keys);
        live.Add(Line(1, "tools/call", new JsonObject { ["name"] = "team_delete", ["arguments"] = new JsonObject { ["team"] = "alpha" } }));
        await WaitForAsync(() => fake.Methods.Contains(CompanionProtocol.Request));

        // while the approval is polled, other calls are answered
        live.Add(Line(2, "tools/list"));
        live.Add(Line(3, "tools/call", new JsonObject { ["name"] = "team_list" }));
        await WaitForAsync(() => live.Reply(2) is not null && live.Reply(3) is not null);
        Assert.Null(live.Reply(1));
        Assert.Equal("teams: alpha", ToolText(live.Reply(3)!));

        decided.SetResult(new JsonObject { ["id"] = "r1", ["state"] = "done", ["plan"] = "Delete alpha.", ["result"] = "ok: deleted alpha.", ["nextId"] = null });
        await WaitForAsync(() => live.Reply(1) is not null);
        Assert.Equal("ok: deleted alpha.", ToolText(live.Reply(1)!));
        Assert.Equal(2, fake.Connects); // the poll had a connection of its own
        await live.EndAsync();
    }

    [Fact]
    public async Task McpCancellingOneCallLeavesTheOthersAndTheConnection()
    {
        Keys.Save("cc", new string('k', 43));
        var slow = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeConnector
        {
            HandleAsync = (m, p) => m == CompanionProtocol.Call
                ? p!["tool"]!.GetValue<string>() == "team_git" ? slow.Task : Task.FromResult<JsonObject?>(FakeConnector.Result("teams: alpha"))
                : Task.FromResult<JsonObject?>(null),
        };
        var live = Live.Start(fake, Keys);
        live.Add(Line(1, "tools/call", new JsonObject { ["name"] = "team_git" }));
        live.Add(Line(1, "tools/call", new JsonObject { ["name"] = "team_list" })); // a duplicate id while 1 is in flight
        await WaitForAsync(() => live.Reply(1) is not null);
        Assert.Equal(-32600, live.Reply(1)!["error"]!["code"]!.GetValue<int>());
        await WaitForAsync(() => fake.Methods.Contains(CompanionProtocol.Call));

        live.Add(Note("notifications/cancelled", new JsonObject { ["requestId"] = 1, ["reason"] = "user" }));
        live.Add(Line(2, "tools/call", new JsonObject { ["name"] = "team_list" }));
        await Task.Delay(100, Ct);
        slow.SetResult(FakeConnector.Result("late"));
        await WaitForAsync(() => live.Reply(2) is not null);
        Assert.Equal("teams: alpha", ToolText(live.Reply(2)!));
        await live.EndAsync();
        Assert.DoesNotContain(live.Lines(), l => l["id"]?.GetValue<int>() == 1 && l["result"] is not null); // the cancelled call: no reply
        Assert.Equal(1, fake.Connects); // the connection was kept
        Assert.Equal(1, fake.Disposed); // closed once, at the end

        // a cancel that arrives right behind its request still finds it
        var never = new TaskCompletionSource<JsonObject?>();
        var quick = new FakeConnector { HandleAsync = (m, _) => m == CompanionProtocol.Call ? never.Task : Task.FromResult<JsonObject?>(null) };
        var s = await RunAsync(quick, null, Line(5, "tools/call", new JsonObject { ["name"] = "team_git" }), Note("notifications/cancelled", new JsonObject { ["requestId"] = 5 }));
        Assert.Empty(s.Replies);
        never.TrySetResult(null);
    }

    [Fact]
    public async Task McpToolListChangesAreAnnounced()
    {
        Keys.Save("cc", new string('k', 43));
        var tools = Tools();
        var fake = new FakeConnector
        {
            Handle = (m, _, _) => m switch
            {
                CompanionProtocol.Tools => tools.DeepClone().AsObject(),
                CompanionProtocol.Call => FakeConnector.Result("ok"),
                _ => null,
            },
        };
        await using var live = Live.Start(fake, Keys);
        live.Add(Line(1, "tools/list"));
        await WaitForAsync(() => live.Reply(1) is not null);

        // the app restarts with the same tools: nothing to announce
        fake.BreakNext = true;
        live.Add(Line(2, "tools/call", new JsonObject { ["name"] = "team_list" }));
        await WaitForAsync(() => live.Reply(2) is not null);
        Assert.DoesNotContain(live.Lines(), l => l["method"] is not null);

        // re-paired at another level while it was away: the reconnect finds other tools and says so
        tools["tools"]!.AsArray().Add(new JsonObject { ["name"] = "tell", ["description"] = "Tell.", ["inputSchema"] = new JsonObject { ["type"] = "object" } });
        fake.BreakNext = true;
        live.Add(Line(3, "tools/call", new JsonObject { ["name"] = "team_list" }));
        await WaitForAsync(() => live.Reply(3) is not null);
        Assert.Single(live.Lines(), l => l["method"]?.GetValue<string>() == "notifications/tools/list_changed");
        Assert.Equal(3, fake.Connects);
        await live.EndAsync();
    }

    [Fact]
    public async Task McpAHungCallDoesNotBlockTheNextOne()
    {
        Keys.Save("cc", new string('k', 43));
        var never = new TaskCompletionSource<JsonObject?>();
        var fake = new FakeConnector
        {
            HandleAsync = (m, p) => m == CompanionProtocol.Call && p!["tool"]!.GetValue<string>() == "team_git"
                ? never.Task
                : Task.FromResult<JsonObject?>(m == CompanionProtocol.Call ? FakeConnector.Result("teams: alpha") : null),
        };
        await using var live = Live.Start(fake, Keys, callLimit: TimeSpan.FromMilliseconds(300));
        live.Add(Line(1, "tools/call", new JsonObject { ["name"] = "team_git" }));
        await WaitForAsync(() => live.Reply(1) is not null);
        Assert.Contains("did not answer in time", ToolText(live.Reply(1)!));

        // the app still works the hung call on that connection; the next call goes over a fresh one
        live.Add(Line(2, "tools/call", new JsonObject { ["name"] = "team_list" }));
        await WaitForAsync(() => live.Reply(2) is not null);
        Assert.Equal("teams: alpha", ToolText(live.Reply(2)!));
        Assert.Equal(2, fake.Connects);
        never.TrySetResult(null);
        await live.EndAsync();
    }

    [Fact]
    public async Task McpWhileOnlyTheStatusToolIsListedTheAppComingUpIsAnnounced()
    {
        Keys.Save("cc", new string('k', 43));
        var fake = new FakeConnector { NotRunning = true, Handle = (m, _, _) => m == CompanionProtocol.Tools ? Tools() : null };
        await using var live = Live.Start(fake, Keys, probeInterval: TimeSpan.FromMilliseconds(50));
        live.Add(Line(1, "tools/list"));
        await WaitForAsync(() => live.Reply(1) is not null);
        Assert.Equal("yawble_status", live.Reply(1)!["result"]!["tools"]![0]!["name"]!.GetValue<string>());
        await Task.Delay(300, Ct); // probes while the app is down: nothing is sent
        Assert.DoesNotContain(live.Lines(), l => l["method"] is not null);

        // the app starts: with no request from the client, it is told once that the list changed
        fake.NotRunning = false;
        await WaitForAsync(() => live.Lines().Any(l => l["method"]?.GetValue<string>() == "notifications/tools/list_changed"));
        await Task.Delay(300, Ct);
        Assert.Single(live.Lines(), l => l["method"]?.GetValue<string>() == "notifications/tools/list_changed");
        live.Add(Line(2, "tools/list"));
        await WaitForAsync(() => live.Reply(2) is not null);
        Assert.Equal("team_list", live.Reply(2)!["result"]!["tools"]![0]!["name"]!.GetValue<string>());
        await live.EndAsync();
    }

    /// <summary>A running `yawble mcp` the test feeds line by line.</summary>
    private sealed class Live : IAsyncDisposable
    {
        private readonly BlockingLines _in = new();
        private readonly StringWriter _out = new();
        private readonly TextWriter _sync;
        private readonly Task<int> _run;
        private bool _ended;

        private Live(FakeConnector connector, Y.KeyFiles keys, TimeSpan? callLimit, TimeSpan? probeInterval)
        {
            _sync = TextWriter.Synchronized(_out);
            _run = new Y.McpStdio(connector, keys, null, _in, _sync, TextWriter.Null, null, callLimit, probeInterval).RunAsync(Ct);
        }

        public static Live Start(FakeConnector connector, Y.KeyFiles keys, TimeSpan? callLimit = null, TimeSpan? probeInterval = null) =>
            new(connector, keys, callLimit, probeInterval);

        public void Add(string line) => _in.Add(line);

        public List<JsonObject> Lines()
        {
            string text;
            lock (_sync) text = _out.ToString();
            return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject())];
        }

        public JsonObject? Reply(int id) => Lines().FirstOrDefault(l => l["id"] is JsonValue v && v.GetValue<int>() == id);

        public async Task EndAsync()
        {
            _ended = true;
            _in.End();
            Assert.Equal(0, await _run.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        }

        public async ValueTask DisposeAsync()
        {
            if (_ended) return;
            _in.End();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(30), Ct); } catch (TimeoutException) { }
        }
    }

    [Fact]
    public void TheMcpWaitComesFromYawbleMcpWait()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), Y.Cli.McpWait(_ => null));
        Assert.Equal(TimeSpan.FromSeconds(5), Y.Cli.McpWait(n => n == "YAWBLE_MCP_WAIT" ? "5" : null));
        Assert.Equal(TimeSpan.Zero, Y.Cli.McpWait(n => n == "YAWBLE_MCP_WAIT" ? "0" : null));
        Assert.Equal(TimeSpan.FromSeconds(60), Y.Cli.McpWait(n => n == "YAWBLE_MCP_WAIT" ? "soon" : null));
        Assert.Equal(TimeSpan.FromSeconds(60), Y.Cli.McpWait(n => n == "YAWBLE_MCP_WAIT" ? "-3" : null));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25, Ct);
        Assert.True(condition());
    }

    /// <summary>A stdin that hands out lines as a test adds them, and ends when told.</summary>
    private sealed class BlockingLines : TextReader
    {
        private readonly System.Threading.Channels.Channel<string?> _lines = System.Threading.Channels.Channel.CreateUnbounded<string?>();

        public void Add(string line) => _lines.Writer.TryWrite(line);

        public void End() => _lines.Writer.TryWrite(null);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => await _lines.Reader.ReadAsync(cancellationToken);

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
}
