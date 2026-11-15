using System.Text.Json.Nodes;
using Yawble.Core.Link;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// B025 verify (card 156): the tester's break tests for read-only Cloud Link viewers. Fakes only: CloudLink's call
/// handler is driven directly with a fake dispatcher and a fake send (no socket, no network, no system programs).
/// </summary>
public class RemoteAccessBreakTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Visitor = "You have read-only access to this computer (Visitor). Ask a workgroup Member or an Owner for more.";

    // ------------------------------------------------------------------ access values

    public static TheoryData<string> NotQuiteRead() => ["READ", "Read", "", " read", "read ", "read\n", "rea", "readonly", "visitor", "full", "FULL", "null"];

    [Theory]
    [MemberData(nameof(NotQuiteRead))]
    public void AnyTextOtherThanExactRead_IsFull(string sent) =>
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonValue.Create(sent)));

    [Fact]
    public void NonTextAccessValues_AreFull()
    {
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(null));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonValue.Create(0)));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonValue.Create(1)));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonValue.Create(1.5)));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonValue.Create(true)));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(new JsonObject { ["access"] = "read" }));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(new JsonArray("read")));
        // As parsed off the wire, too (a JsonElement-backed value, not a created one).
        Assert.Equal(RemoteAccess.Read, RemoteAccess.AccessOf(JsonNode.Parse("""{"a":"read"}""")!["a"]));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonNode.Parse("""{"a":"READ"}""")!["a"]));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonNode.Parse("""{"a":null}""")!["a"]));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonNode.Parse("""{"a":42}""")!["a"]));
    }

    [Theory]
    [InlineData("""{"access":"READ"}""")]
    [InlineData("""{"access":""}""")]
    [InlineData("""{"access":null}""")]
    [InlineData("""{"access":5}""")]
    [InlineData("""{"access":["read"]}""")]
    [InlineData("""{}""")]
    public async Task OverTheLink_AChangeCall_RunsWhenAccessIsNotExactlyRead(string extra)
    {
        await using var fx = new Fixture();
        var call = fx.Call("s1", 1, "board.move", new JsonObject { ["id"] = 3, ["lane"] = "done" });
        foreach (var kv in (JsonObject)JsonNode.Parse(extra)!) call[kv.Key] = kv.Value?.DeepClone();
        var reply = await fx.AnswerAsync(call);
        Assert.Null(reply["error"]);
        Assert.Equal(["board.move"], fx.Calls);
        Assert.Equal(RemoteAccess.Full, fx.Link.SessionAccess("s1"));
    }

    [Fact]
    public async Task OverTheLink_ExactRead_RefusesTheSameChange()
    {
        await using var fx = new Fixture();
        var reply = await fx.AnswerAsync(fx.Call("s1", 1, "board.move", new JsonObject { ["id"] = 3, ["lane"] = "done" }, "read"));
        Assert.Equal("remote_refused", reply["error"]!["code"]!.GetValue<string>());
        Assert.Equal(Visitor, reply["error"]!["message"]!.GetValue<string>());
        Assert.Empty(fx.Calls);
    }

    // ------------------------------------------------------------------ allow list vs the table

    [Theory]
    [InlineData("board.listEverything")]
    [InlineData("backlog.newThing")]
    [InlineData("logs.export")]
    [InlineData("network.forget")]
    [InlineData("concierge.pin")]
    [InlineData("integrations.list2")]
    [InlineData("runs.listAndStop")]
    [InlineData("agents.get")]
    [InlineData("guardrail.reset")]
    [InlineData("tasks.list.all")]
    public void AnAllowListedMethodMissingFromTheTable_IsRefusedForRead_NotForFull(string method)
    {
        Assert.True(RemoteAccess.OnAllowList(method), $"{method} should be on the allow list (a prefix)");
        Assert.False(RemoteAccess.Kinds.ContainsKey(method));
        Assert.Equal(Visitor, RemoteAccess.Refusal(method, null, allowHostTerminals: true, RemoteAccess.Read));
        Assert.NotEqual(Visitor, RemoteAccess.Refusal(method, null, allowHostTerminals: true, RemoteAccess.Full));
    }

    [Fact]
    public void EveryUiScreenPrefix_HasNoReadByDefault()
    {
        // A screen prefix itself, or a name under it the table does not list, is never a read.
        foreach (var prefix in UiRpc.Prefixes)
        {
            Assert.False(RemoteAccess.Reads(prefix, null), prefix);
            Assert.False(RemoteAccess.Reads(prefix + "zzzNew", null), prefix + "zzzNew");
        }
    }

    [Theory]
    [InlineData("board.list ")]
    [InlineData(" board.list")]
    [InlineData("Board.list")]
    [InlineData("board.List")]
    [InlineData("board.list\u0000")]
    [InlineData("board..list")]
    [InlineData("board.list.move")]
    [InlineData("")]
    public void ANameThatOnlyLooksLikeARead_IsNotARead(string method) =>
        Assert.Equal(Visitor, RemoteAccess.Refusal(method, null, allowHostTerminals: true, RemoteAccess.Read));

    // ------------------------------------------------------------------ terminals and read-looking changes

    [Theory]
    [InlineData("terminal.openInteractive", "member:3")]
    [InlineData("terminal.input", "member:3")]
    [InlineData("terminal.input", "host:1")]
    [InlineData("terminal.resize", "member:3")]
    [InlineData("terminal.attach", "member:3")]
    [InlineData("terminal.stop", "member:3")]
    public void AnInteractiveTerminal_IsRefusedForRead_EvenWithHostTerminalsAllowed(string method, string channel)
    {
        var p = new JsonObject { ["channel"] = channel, ["data"] = "ls\r" };
        Assert.Equal(Visitor, RemoteAccess.Refusal(method, p, allowHostTerminals: true, RemoteAccess.Read));
        Assert.Equal(Visitor, RemoteAccess.Refusal("hostTerminal.start", new JsonObject(), allowHostTerminals: true, RemoteAccess.Read));
    }

    /// <summary>Changes whose verb is one the audit log treats as a read (check, test, preview, attach, input...).</summary>
    [Theory]
    [InlineData("tasks.preview")]
    [InlineData("integrations.mcpCheck")]
    [InlineData("integrations.connectionTest")]
    [InlineData("tools.checkAgain")]
    [InlineData("terminal.attach")]
    [InlineData("terminal.input")]
    [InlineData("agents.checkForUpdates")]
    [InlineData("integrations.detect")]
    [InlineData("integrations.discover")]
    [InlineData("triggers.runNow")]
    [InlineData("backlog.restore")]
    [InlineData("board.restore")]
    [InlineData("logs.prune")]
    [InlineData("logs.setRetention")]
    [InlineData("credentials.setMode")]
    public void AChangeThatLooksLikeARead_IsRefusedForRead(string method)
    {
        Assert.Equal(RemoteAccess.Kind.Changes, RemoteAccess.Kinds[method]);
        Assert.Equal(Visitor, RemoteAccess.Refusal(method, new JsonObject { ["teamId"] = 1 }, allowHostTerminals: true, RemoteAccess.Read));
    }

    [Theory]
    [InlineData("""{"teamId":1,"on":false}""")]
    [InlineData("""{"teamId":1,"on":true}""")]
    [InlineData("""{"teamId":1,"on":"yes"}""")]
    [InlineData("""{"teamId":1,"on":0}""")]
    public void AGetOrSetCall_WithAnyOnValue_IsAChange(string json)
    {
        var p = (JsonObject)JsonNode.Parse(json)!;
        Assert.Equal(Visitor, RemoteAccess.Refusal("teams.push", p, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Equal(Visitor, RemoteAccess.Refusal("teams.syncToBase", (JsonObject)p.DeepClone(), allowHostTerminals: false, RemoteAccess.Read));
    }

    [Fact]
    public void AReadOnlyViewer_GetsTheVisitorSentence_ForWindowOnlyChangesToo()
    {
        foreach (var method in new[] { "link.settings", "credentials.setAnthropicApiKey", "ops.restore", "git.install", "tools.install", "companion.approve" })
            Assert.Equal(Visitor, RemoteAccess.Refusal(method, null, allowHostTerminals: true, RemoteAccess.Read));
    }

    [Fact]
    public void AReadStillGetsTheChecksEveryViewerHas()
    {
        // Code hidden by the person: refused for a reader with the code reason, not let through and not the Visitor sentence.
        var refusal = RemoteAccess.Refusal("code.read", null, allowHostTerminals: false, RemoteAccess.Read, showCode: false);
        Assert.NotNull(refusal);
        Assert.NotEqual(Visitor, refusal);
        // An agent's own terminal sessions are window-only for everyone.
        Assert.NotEqual(Visitor, RemoteAccess.Refusal("agents.testSessions", null, allowHostTerminals: true, RemoteAccess.Read));
        Assert.NotNull(RemoteAccess.Refusal("agents.testSessions", null, allowHostTerminals: true, RemoteAccess.Read));
    }

    // ------------------------------------------------------------------ audit

    public static TheoryData<string> SomeChanges() =>
    [
        "bus.tellManager", "bus.answer", "teams.delete", "docs.write", "board.move", "backlog.dispatch", "network.answer",
        "integrations.approve", "tasks.approve", "policy.saveGlobal", "guardrail.start", "terminal.input", "hostTerminal.start",
        "board.somethingNew", "link.settings",
    ];

    [Theory]
    [MemberData(nameof(SomeChanges))]
    public async Task EachRefusal_IsAuditedWithTheExactMessage(string method)
    {
        await using var fx = new Fixture();
        var reply = await fx.AnswerAsync(fx.Call("s1", 9, method, new JsonObject { ["teamId"] = 1, ["channel"] = "member:1" }, "read", user: "vis\titor@example.com"));
        Assert.Equal(Visitor, reply["error"]!["message"]!.GetValue<string>());
        Assert.Empty(fx.Calls);
        var lines = await File.ReadAllLinesAsync(fx.Link.AuditFile, Ct);
        var line = Assert.Single(lines);
        var fields = line.Split('\t');
        Assert.Equal(4, fields.Length);
        Assert.True(DateTimeOffset.TryParse(fields[0], out _), fields[0]);
        Assert.Equal("vis itor@example.com", fields[1]);
        Assert.Equal(method, fields[2]);
        Assert.Equal("refused: " + Visitor, fields[3]);
    }

    [Fact]
    public async Task AReadOnlyViewersReads_AreNotAudited_AndItsRefusalsAreEachAudited()
    {
        await using var fx = new Fixture();
        await fx.AnswerAsync(fx.Call("s1", 1, "board.list", null, "read"));
        await fx.AnswerAsync(fx.Call("s1", 2, "board.move", null, "read"));
        await fx.AnswerAsync(fx.Call("s1", 3, "app.overview", null, "read"));
        await fx.AnswerAsync(fx.Call("s1", 4, "board.move", null, "read"));
        var lines = await File.ReadAllLinesAsync(fx.Link.AuditFile, Ct);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.EndsWith("\tboard.move\trefused: " + Visitor, l));
        Assert.Equal(["board.list", "app.overview"], fx.Calls);
    }

    // ------------------------------------------------------------------ pushes

    [Fact]
    public async Task Pushes_AreForwardedTheSame_AfterAReadOnlyViewerCalls()
    {
        var pushes = new[]
        {
            new JsonObject { ["event"] = "app.changed", ["data"] = null },
            new JsonObject { ["event"] = "terminal.output", ["data"] = new JsonObject { ["channel"] = "member:3", ["data"] = "x" } },
            new JsonObject { ["event"] = "terminal.state", ["data"] = new JsonObject { ["channel"] = "member:3" } },
        };
        var before = pushes.Select(p => CloudLink.Forwards(p)).ToArray();
        Assert.All(before, Assert.True);
        await using var fx = new Fixture();
        await fx.AnswerAsync(fx.Call("visitor", 1, "app.overview", null, "read"));
        await fx.AnswerAsync(fx.Call("visitor", 2, "board.move", null, "read"));
        Assert.Equal(RemoteAccess.Read, fx.Link.SessionAccess("visitor"));
        // The push filter takes no access at all: a read session's pushes are the same as everyone's.
        Assert.Equal(before, pushes.Select(p => CloudLink.Forwards(p)).ToArray());
        Assert.Equal(before, pushes.Select(p => CloudLink.Forwards(p, showCode: true)).ToArray());
    }

    // ------------------------------------------------------------------ per session

    [Fact]
    public async Task AccessDoesNotLeak_BetweenTwoSessions_EvenInterleaved()
    {
        await using var fx = new Fixture();
        var gate = new TaskCompletionSource();
        fx.Hold = gate.Task; // the dispatcher waits, so the two sessions' calls overlap
        var memberCall = fx.AnswerAsync(fx.Call("member", 1, "board.move", null, "full", user: "member@example.com"));
        var visitorCall = fx.AnswerAsync(fx.Call("visitor", 1, "board.move", null, "read", user: "visitor@example.com"));
        var visitorReply = await visitorCall; // refused at once, while the member's call is still running
        gate.SetResult();
        var memberReply = await memberCall;
        Assert.Null(memberReply["error"]);
        Assert.Equal(Visitor, visitorReply["error"]!["message"]!.GetValue<string>());
        Assert.Equal(["board.move"], fx.Calls);
        Assert.Equal(RemoteAccess.Full, fx.Link.SessionAccess("member"));
        Assert.Equal(RemoteAccess.Read, fx.Link.SessionAccess("visitor"));

        // A session id that is not text, or none at all, records nothing and changes no other session.
        var noSession = fx.Call("x", 2, "board.move", null, "full");
        noSession.Remove("session");
        await fx.AnswerAsync(noSession, checkSession: false);
        var numberSession = fx.Call("x", 3, "board.move", null, "full");
        numberSession["session"] = 5;
        await fx.AnswerAsync(numberSession, checkSession: false);
        Assert.Equal(RemoteAccess.Read, fx.Link.SessionAccess("visitor"));
        Assert.Equal(RemoteAccess.Full, fx.Link.SessionAccess("5"));

        // Each call is checked by its own access, not the session's last one.
        var r = await fx.AnswerAsync(fx.Call("member", 4, "board.move", null, "read", user: "member@example.com"));
        Assert.Equal(Visitor, r["error"]!["message"]!.GetValue<string>());
        Assert.Equal(RemoteAccess.Read, fx.Link.SessionAccess("member"));
        Assert.Equal(RemoteAccess.Read, fx.Link.SessionAccess("visitor"));
    }

    [Fact]
    public async Task CoreGetInfo_SaysEachSessionsOwnAccess()
    {
        await using var fx = new Fixture();
        var a = await fx.AnswerAsync(fx.Call("visitor", 1, "core.getInfo", null, "read"));
        var b = await fx.AnswerAsync(fx.Call("member", 1, "core.getInfo", null, "full"));
        var c = await fx.AnswerAsync(fx.Call("old-site", 1, "core.getInfo", null, null));
        Assert.Equal("read", a["result"]!["remoteAccess"]!.GetValue<string>());
        Assert.Equal("full", b["result"]!["remoteAccess"]!.GetValue<string>());
        Assert.Equal("full", c["result"]!["remoteAccess"]!.GetValue<string>());
        // The wire form is exactly the string, as the shell's CoreInfo.remoteAccess reads it.
        Assert.Contains("\"remoteAccess\":\"read\"", a["result"]!.ToJsonString());
    }

    /// <summary>A CloudLink with a fake dispatcher that records and echoes the method, answering through a fake send.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDb _dir = new();
        public List<string> Calls { get; } = [];
        public CloudLink Link { get; }
        public Task? Hold { get; set; }

        public Fixture()
        {
            Link = new CloudLink(_dir.Dir, async (method, _, _) =>
            {
                if (Hold is { } h) await h;
                lock (Calls) Calls.Add(method);
                return method == "core.getInfo"
                    ? new JsonObject { ["product"] = "Yawble", ["version"] = "test" }
                    : new JsonObject { ["echo"] = method };
            }, new AppEvents());
        }

        public JsonObject Call(string session, int id, string method, JsonObject? p, string? access = null, string user = "visitor@example.com")
        {
            var call = new JsonObject
            {
                ["type"] = "call", ["session"] = session, ["id"] = id, ["method"] = method, ["params"] = p, ["user"] = user,
            };
            if (access is not null) call["access"] = access;
            return call;
        }

        public async Task<JsonObject> AnswerAsync(JsonObject call, bool checkSession = true)
        {
            JsonObject? sent = null;
            var session = call["session"]?.ToJsonString();
            await Link.AnswerAsync(call, m => { sent = m; return Task.CompletedTask; }, Ct);
            Assert.NotNull(sent);
            Assert.Equal("result", sent!["type"]!.GetValue<string>());
            if (checkSession) Assert.Equal(session, sent["session"]?.ToJsonString());
            return sent;
        }

        public async ValueTask DisposeAsync()
        {
            await Link.DisposeAsync();
            _dir.Dispose();
        }
    }
}
