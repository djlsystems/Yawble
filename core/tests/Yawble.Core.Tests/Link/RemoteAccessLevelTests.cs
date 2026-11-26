using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Link;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// Read-only Cloud Link viewers: each call's access ("full" | "read"), the read/change table next to the allow
/// list, the Visitor refusal and its audit line, the hello's capability, and core.getInfo's remoteAccess; then the
/// values and names that only look right (an access that is not exactly "full" or "read", a method that only looks
/// like a read). Fakes only: the link's call handler is driven directly with a fake dispatcher and a fake send (no
/// socket, no network, no system programs).
/// </summary>
public class RemoteAccessLevelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Visitor = "You have read-only access to this computer (Visitor). Ask a workgroup Member or an Owner for more.";

    [Fact]
    public void TheRefusalIsTheVisitorsSentence() => Assert.Equal(Visitor, RemoteAccess.ReadOnlyRefusal);

    /// <summary>Every method the core dispatches (read from its handlers' sources) that the allow list lets through is in the table.</summary>
    [Fact]
    public void EveryMethodOnTheAllowList_IsClassified()
    {
        var dispatched = DispatchedMethods();
        Assert.True(dispatched.Count > 150, $"only {dispatched.Count} methods found in the core's handlers");
        var allowed = dispatched.Where(RemoteAccess.OnAllowList).ToList();
        var missing = allowed.Where(m => !RemoteAccess.Kinds.ContainsKey(m)).ToList();
        Assert.True(missing.Count == 0, "not classified as read or change: " + string.Join(", ", missing));
        // ...and the table names only real methods on the allow list (no typo silently left as a change).
        var stray = RemoteAccess.Kinds.Keys.Where(m => !dispatched.Contains(m) || !RemoteAccess.OnAllowList(m)).ToList();
        Assert.True(stray.Count == 0, "in the table but not an allowed method: " + string.Join(", ", stray));
    }

    public static TheoryData<string> ReadMethods() => [.. RemoteAccess.Kinds.Where(k => k.Value != RemoteAccess.Kind.Changes).Select(k => k.Key)];

    public static TheoryData<string> ChangeMethods() => [.. RemoteAccess.Kinds.Where(k => k.Value == RemoteAccess.Kind.Changes).Select(k => k.Key)];

    [Theory]
    [MemberData(nameof(ReadMethods))]
    public void AReadCall_PassesForAReadOnlyViewer_AsForAFullOne(string method)
    {
        Assert.True(RemoteAccess.Reads(method, null));
        // Whatever refuses it for every viewer (window-only methods) still does; the access never does.
        Assert.Equal(RemoteAccess.Refusal(method, null, allowHostTerminals: true, RemoteAccess.Full),
            RemoteAccess.Refusal(method, null, allowHostTerminals: true, RemoteAccess.Read));
        Assert.NotEqual(Visitor, RemoteAccess.Refusal(method, null, allowHostTerminals: true, RemoteAccess.Read));
    }

    [Theory]
    [InlineData("app.overview")]
    [InlineData("teams.list")]
    [InlineData("docs.read")]
    [InlineData("board.list")]
    [InlineData("backlog.item")]
    [InlineData("monitor.messages")]
    [InlineData("logs.query")]
    [InlineData("policy.getGlobal")]
    [InlineData("code.read")]
    [InlineData("core.getInfo")]
    public void TheScreensData_IsReadable(string method) =>
        Assert.Null(RemoteAccess.Refusal(method, null, allowHostTerminals: false, RemoteAccess.Read));

    [Theory]
    [MemberData(nameof(ChangeMethods))]
    public void AChangeCall_IsRefusedForAReadOnlyViewer_WithTheVisitorSentence(string method)
    {
        Assert.False(RemoteAccess.Reads(method, null));
        Assert.Equal(Visitor, RemoteAccess.Refusal(method, null, allowHostTerminals: true, RemoteAccess.Read));
    }

    [Theory]
    [InlineData("terminal.input", "member:3")]
    [InlineData("terminal.attach", "member:3")]
    [InlineData("terminal.resize", "member:3")]
    [InlineData("terminal.input", "host:shell")]
    [InlineData("terminal.attach", "agent-test:claude")]
    public void Terminals_AreChanges_HostOrAgent(string method, string channel)
    {
        var p = new JsonObject { ["channel"] = channel };
        Assert.Equal(Visitor, RemoteAccess.Refusal(method, p, allowHostTerminals: true, RemoteAccess.Read));
        Assert.Equal(Visitor, RemoteAccess.Refusal("hostTerminal.start", null, allowHostTerminals: true, RemoteAccess.Read));
    }

    [Fact]
    public void AMethodMissingFromTheTable_IsAChange()
    {
        Assert.False(RemoteAccess.Kinds.ContainsKey("teams.somethingNew"));
        Assert.True(RemoteAccess.OnAllowList("teams.somethingNew"));
        Assert.False(RemoteAccess.Reads("teams.somethingNew", null));
        Assert.Equal(Visitor, RemoteAccess.Refusal("teams.somethingNew", null, allowHostTerminals: true, RemoteAccess.Read));
        // A full viewer is not refused by the table (the handler answers it).
        Assert.Null(RemoteAccess.Refusal("teams.somethingNew", null, allowHostTerminals: true, RemoteAccess.Full));
    }

    [Theory]
    [InlineData("teams.push")]
    [InlineData("teams.syncToBase")]
    public void AGetOrSetCall_ReadsWithoutOn_ChangesWithIt(string method)
    {
        Assert.Null(RemoteAccess.Refusal(method, new JsonObject { ["teamId"] = 1 }, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Equal(Visitor, RemoteAccess.Refusal(method, new JsonObject { ["teamId"] = 1, ["on"] = true }, allowHostTerminals: false, RemoteAccess.Read));
    }

    [Fact]
    public void TheDeliverableExpirySetting_ReadsWithoutDays_ChangesWithThem_AndExtendIsAChange()
    {
        // teams.deliverableExpiry is a get-or-set on "days"; bus.deliverableExtend changes the deliverable.
        Assert.True(RemoteAccess.OnAllowList("teams.deliverableExpiry"));
        Assert.Null(RemoteAccess.Refusal("teams.deliverableExpiry", new JsonObject { ["teamId"] = 1 }, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("teams.deliverableExpiry", new JsonObject { ["teamId"] = 1, ["on"] = true }, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Equal(Visitor, RemoteAccess.Refusal("teams.deliverableExpiry", new JsonObject { ["teamId"] = 1, ["days"] = 7 }, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("teams.deliverableExpiry", new JsonObject { ["teamId"] = 1, ["days"] = 7 }, allowHostTerminals: false, RemoteAccess.Full));

        Assert.True(RemoteAccess.OnAllowList("bus.deliverableExtend"));
        Assert.Equal(RemoteAccess.Kind.Changes, RemoteAccess.Kinds["bus.deliverableExtend"]);
        Assert.Equal(Visitor, RemoteAccess.Refusal("bus.deliverableExtend", new JsonObject { ["teamId"] = 1, ["deliverable"] = 4 }, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("bus.deliverableExtend", new JsonObject { ["teamId"] = 1, ["deliverable"] = 4 }, allowHostTerminals: false, RemoteAccess.Full));
        Assert.False(RemoteAccess.Kinds.ContainsKey("teams.setDeliverableExpiry")); // the setting has one method, a get-or-set
    }

    [Theory]
    [InlineData(null)]
    [InlineData("full")]
    [InlineData("FULL")]
    [InlineData("Read")]
    [InlineData("readonly")]
    [InlineData("")]
    public void OnlyTheTextsFullAndRead_AreAnAccess(string? sent)
    {
        Assert.Equal(sent == "full" ? RemoteAccess.Full : null, RemoteAccess.AccessOf(sent is null ? null : JsonValue.Create(sent)));
        Assert.Equal(RemoteAccess.Read, RemoteAccess.AccessOf(JsonValue.Create("read")));
        Assert.Null(RemoteAccess.AccessOf(JsonValue.Create(5)));
        Assert.Null(RemoteAccess.AccessOf(new JsonArray("read")));
    }

    [Theory]
    [MemberData(nameof(ChangeMethods))]
    public void AFullViewer_IsCheckedAsAViewerWithNoAccessLevelGiven(string method) =>
        Assert.Equal(RemoteAccess.Refusal(method, null, allowHostTerminals: false),
            RemoteAccess.Refusal(method, null, allowHostTerminals: false, RemoteAccess.Full));

    [Fact]
    public void TheHello_CarriesTheAccessCapability_AndItsFields()
    {
        var hello = CloudLink.Hello();
        Assert.Equal("hello", hello["type"]!.GetValue<string>());
        Assert.Equal(["access", "summary", "logsTail"], hello["caps"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.NotNull(hello["name"]);
        Assert.NotNull(hello["os"]);
        Assert.NotNull(hello["version"]);
    }

    [Fact]
    public async Task AReadOnlyViewersChangeCall_IsRefused_NotRun_AndAudited()
    {
        await using var fx = new Fixture();
        var reply = await fx.CallAsync("v1", 1, "bus.tellManager", new JsonObject { ["teamId"] = 2, ["text"] = "hi" }, "read");
        Assert.Equal("remote_refused", reply["error"]!["code"]!.GetValue<string>());
        Assert.Equal(Visitor, reply["error"]!["message"]!.GetValue<string>());
        Assert.Null(reply["result"]);
        Assert.Empty(fx.Calls);
        var audit = await File.ReadAllLinesAsync(fx.Link.AuditFile, Ct);
        Assert.Contains(audit, l => l.Contains("\tvisitor@example.com\tbus.tellManager\trefused: " + Visitor));
    }

    [Fact]
    public async Task AReadOnlyViewersReadCall_IsRun()
    {
        await using var fx = new Fixture();
        var reply = await fx.CallAsync("v1", 1, "board.list", new JsonObject { ["team"] = "web" }, "read");
        Assert.Null(reply["error"]);
        Assert.Equal("board.list", reply["result"]!["echo"]!.GetValue<string>());
        Assert.Equal(["board.list"], fx.Calls);
    }

    [Fact]
    public async Task AMissingAccess_IsRefused_AndNothingRuns()
    {
        await using var fx = new Fixture();
        var reply = await fx.CallAsync("v1", 1, "bus.tellManager", new JsonObject { ["teamId"] = 2, ["text"] = "hi" }, access: null);
        Assert.Equal("remote_refused", reply["error"]!["code"]!.GetValue<string>());
        Assert.Equal(RemoteAccess.NoAccessRefusal, reply["error"]!["message"]!.GetValue<string>());
        Assert.Empty(fx.Calls);
        Assert.Null(fx.Link.SessionAccess("v1"));
        var audit = await File.ReadAllLinesAsync(fx.Link.AuditFile, Ct);
        Assert.Contains(audit, l => l.Contains("\tvisitor@example.com\tbus.tellManager\trefused: "));
    }

    [Fact]
    public async Task EachSessionKeepsItsAccess()
    {
        await using var fx = new Fixture();
        await fx.CallAsync("visitor", 1, "app.overview", null, "read");
        await fx.CallAsync("member", 1, "app.overview", null, "full");
        Assert.Equal(RemoteAccess.Read, fx.Link.SessionAccess("visitor"));
        Assert.Equal(RemoteAccess.Full, fx.Link.SessionAccess("member"));
        Assert.Null(fx.Link.SessionAccess("never-called"));
    }

    [Theory]
    [InlineData("read", "read")]
    [InlineData("full", "full")]
    [InlineData(null, null)]
    [InlineData("owner", null)]
    public async Task CoreGetInfo_OverTheLink_SaysTheViewersAccess_AndIsRefusedWithoutOne(string? sent, string? expected)
    {
        await using var fx = new Fixture();
        var reply = await fx.CallAsync("v1", 7, "core.getInfo", null, sent);
        if (expected is null)
        {
            Assert.Null(reply["result"]);
            Assert.Equal(RemoteAccess.NoAccessRefusal, reply["error"]!["message"]!.GetValue<string>());
            return;
        }
        Assert.Equal(expected, reply["result"]!["remoteAccess"]!.GetValue<string>());
        Assert.Equal("Yawble", reply["result"]!["product"]!.GetValue<string>());
        // The window's own answer (the dispatcher's) carries no remoteAccess.
        Assert.Null(((JsonObject)Fixture.Info())["remoteAccess"]);
    }

    // ------------------------------------------------------------------ access values

    public static TheoryData<string> NotQuiteAnAccess() =>
        ["READ", " read", "read ", "read\n", "rea", "visitor", "Full", " full", "full ", "ful", "fullest", "admin", "owner", "null"];

    [Theory]
    [MemberData(nameof(NotQuiteAnAccess))]
    public void AnyTextOtherThanExactFullOrRead_IsNoAccess(string sent) =>
        Assert.Null(RemoteAccess.AccessOf(JsonValue.Create(sent)));

    [Fact]
    public void NonTextAccessValues_AreNoAccess()
    {
        Assert.Null(RemoteAccess.AccessOf(null));
        Assert.Null(RemoteAccess.AccessOf(JsonValue.Create(0)));
        Assert.Null(RemoteAccess.AccessOf(JsonValue.Create(1)));
        Assert.Null(RemoteAccess.AccessOf(JsonValue.Create(1.5)));
        Assert.Null(RemoteAccess.AccessOf(JsonValue.Create(true)));
        Assert.Null(RemoteAccess.AccessOf(new JsonObject { ["access"] = "read" }));
        Assert.Null(RemoteAccess.AccessOf(new JsonArray("read")));
        Assert.Null(RemoteAccess.AccessOf(new JsonArray("full")));
        // As parsed off the wire, too (a JsonElement-backed value, not a created one).
        Assert.Equal(RemoteAccess.Read, RemoteAccess.AccessOf(JsonNode.Parse("""{"a":"read"}""")!["a"]));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonNode.Parse("""{"a":"full"}""")!["a"]));
        Assert.Null(RemoteAccess.AccessOf(JsonNode.Parse("""{"a":"READ"}""")!["a"]));
        Assert.Null(RemoteAccess.AccessOf(JsonNode.Parse("""{"a":"FULL"}""")!["a"]));
        Assert.Null(RemoteAccess.AccessOf(JsonNode.Parse("""{"a":null}""")!["a"]));
        Assert.Null(RemoteAccess.AccessOf(JsonNode.Parse("""{"a":42}""")!["a"]));
    }

    [Theory]
    [InlineData("""{"access":"READ"}""")]
    [InlineData("""{"access":""}""")]
    [InlineData("""{"access":null}""")]
    [InlineData("""{"access":5}""")]
    [InlineData("""{"access":["read"]}""")]
    [InlineData("""{"access":"FULL"}""")]
    [InlineData("""{"access":"owner"}""")]
    [InlineData("""{"access":true}""")]
    [InlineData("""{}""")]
    public async Task OverTheLink_ACallWhoseAccessIsNotExactlyFullOrRead_IsRefused_AChangeAndARead(string extra)
    {
        await using var fx = new Fixture();
        var id = 0;
        foreach (var method in new[] { "board.move", "board.list", "core.getInfo" })
        {
            var call = fx.Call("s1", ++id, method, new JsonObject { ["id"] = 3, ["lane"] = "done" });
            foreach (var kv in (JsonObject)JsonNode.Parse(extra)!) call[kv.Key] = kv.Value?.DeepClone();
            var reply = await fx.AnswerAsync(call);
            Assert.Equal("remote_refused", reply["error"]!["code"]!.GetValue<string>());
            Assert.Equal(RemoteAccess.NoAccessRefusal, reply["error"]!["message"]!.GetValue<string>());
            Assert.Null(reply["result"]);
        }
        Assert.Empty(fx.Calls);
        Assert.Null(fx.Link.SessionAccess("s1"));
        // Written down like any other refusal.
        var audit = await File.ReadAllLinesAsync(fx.Link.AuditFile, Ct);
        Assert.Contains(audit, l => l.Contains("\tboard.move\trefused: ") && l.Contains("full or read"));
    }

    [Fact]
    public async Task OverTheLink_ACallWithNoValidAccess_DoesNotChangeWhatTheSessionSaid()
    {
        await using var fx = new Fixture();
        await fx.AnswerAsync(fx.Call("s1", 1, "app.overview", null, "read"));
        await fx.AnswerAsync(fx.Call("s1", 2, "board.move", null));
        Assert.Equal(RemoteAccess.Read, fx.Link.SessionAccess("s1"));
        Assert.Equal(["app.overview"], fx.Calls);
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
    [InlineData("terminal.input", "host:1")]
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
        // Whether a push goes up does not depend on who is viewing: each push says the least access that receives it instead.
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
        Assert.Null(fx.Link.SessionAccess("5"));

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
        var c = await fx.AnswerAsync(fx.Call("says-nothing", 1, "core.getInfo", null, null));
        Assert.Equal("read", a["result"]!["remoteAccess"]!.GetValue<string>());
        Assert.Equal("full", b["result"]!["remoteAccess"]!.GetValue<string>());
        Assert.Null(c["result"]);
        Assert.Equal(RemoteAccess.NoAccessRefusal, c["error"]!["message"]!.GetValue<string>());
        // The wire form is exactly the string, as the shell's CoreInfo.remoteAccess reads it.
        Assert.Contains("\"remoteAccess\":\"read\"", a["result"]!.ToJsonString());
    }

    /// <summary>A CloudLink with a fake dispatcher that records and echoes the method, answering calls through a fake send.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDb _dir = new();
        public List<string> Calls { get; } = [];
        public CloudLink Link { get; }

        /// <summary>When set, the dispatcher waits for it before it answers, so calls can overlap.</summary>
        public Task? Hold { get; set; }

        public Fixture()
        {
            Link = new CloudLink(_dir.Dir, async (method, _, _) =>
            {
                if (Hold is { } h) await h;
                lock (Calls) Calls.Add(method);
                return method == "core.getInfo" ? Info() : new JsonObject { ["echo"] = method };
            }, new AppEvents());
        }

        public static JsonNode Info() => new JsonObject { ["product"] = "Yawble", ["version"] = "test" };

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

        public async Task<JsonObject> CallAsync(string session, int id, string method, JsonObject? p, string? access)
        {
            var sent = await AnswerAsync(Call(session, id, method, p, access));
            Assert.Equal(id, sent["id"]!.GetValue<int>());
            return sent;
        }

        public async ValueTask DisposeAsync()
        {
            await Link.DisposeAsync();
            _dir.Dispose();
        }
    }

    /// <summary>
    /// The method names the core's handlers dispatch, read from their sources ("x.y" =&gt; / case "x.y": / "x.y" or "x.z"
    /// in a match): what a new handler adds shows up here without being listed by hand.
    /// </summary>
    private static HashSet<string> DispatchedMethods()
    {
        var src = Path.Combine(RepoRoot(), "core", "src");
        var pattern = new Regex("""(?:case |^\s*|\bor |\bis )"([a-z][A-Za-z]*(?:\.[A-Za-z]+)+)"(?=(?:\s+or\s+"[^"]+")*\s*(?:=>|:|when\b|or\b|;|\)))""", RegexOptions.Multiline);
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in pattern.Matches(File.ReadAllText(file))) methods.Add(m.Groups[1].Value);
        }
        return methods;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "core", "src", "Yawble.Core", "Ipc", "RpcDispatcher.cs"))) return dir.FullName;
        throw new InvalidOperationException("the repository's sources were not found above " + AppContext.BaseDirectory);
    }
}
