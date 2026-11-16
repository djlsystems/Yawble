using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Link;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// Read-only Cloud Link viewers (B025): each call's access ("full" | "read"), the read/change table next to the allow
/// list, the Visitor refusal and its audit line, the hello's capability, and core.getInfo's remoteAccess. Fakes only:
/// the link's call handler is driven directly with a fake dispatcher and a fake send (no socket, no network).
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

    [Theory]
    [InlineData(null)]
    [InlineData("full")]
    [InlineData("FULL")]
    [InlineData("Read")]
    [InlineData("readonly")]
    [InlineData("")]
    public void OnlyTheTextRead_IsReadOnly(string? sent)
    {
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(sent is null ? null : JsonValue.Create(sent)));
        Assert.Equal(RemoteAccess.Read, RemoteAccess.AccessOf(JsonValue.Create("read")));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(JsonValue.Create(5)));
        Assert.Equal(RemoteAccess.Full, RemoteAccess.AccessOf(new JsonArray("read")));
    }

    [Theory]
    [MemberData(nameof(ChangeMethods))]
    public void AFullViewer_IsCheckedAsBefore(string method) =>
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
    public async Task AMissingAccess_BehavesAsFull()
    {
        await using var fx = new Fixture();
        var reply = await fx.CallAsync("v1", 1, "bus.tellManager", new JsonObject { ["teamId"] = 2, ["text"] = "hi" }, access: null);
        Assert.Null(reply["error"]);
        Assert.Equal(["bus.tellManager"], fx.Calls);
        Assert.Equal(RemoteAccess.Full, fx.Link.SessionAccess("v1"));
        var audit = await File.ReadAllLinesAsync(fx.Link.AuditFile, Ct);
        Assert.Contains(audit, l => l.Contains("\tvisitor@example.com\tbus.tellManager\tdone"));
    }

    [Fact]
    public async Task EachSessionKeepsItsAccess()
    {
        await using var fx = new Fixture();
        await fx.CallAsync("visitor", 1, "app.overview", null, "read");
        await fx.CallAsync("member", 1, "app.overview", null, "full");
        Assert.Equal(RemoteAccess.Read, fx.Link.SessionAccess("visitor"));
        Assert.Equal(RemoteAccess.Full, fx.Link.SessionAccess("member"));
        Assert.Equal(RemoteAccess.Full, fx.Link.SessionAccess("never-called"));
    }

    [Theory]
    [InlineData("read", "read")]
    [InlineData("full", "full")]
    [InlineData(null, "full")]
    [InlineData("owner", "full")]
    public async Task CoreGetInfo_OverTheLink_SaysTheViewersAccess(string? sent, string expected)
    {
        await using var fx = new Fixture();
        var reply = await fx.CallAsync("v1", 7, "core.getInfo", null, sent);
        Assert.Equal(expected, reply["result"]!["remoteAccess"]!.GetValue<string>());
        Assert.Equal("Yawble", reply["result"]!["product"]!.GetValue<string>());
        // The window's own answer (the dispatcher's) carries no remoteAccess.
        Assert.Null(((JsonObject)Fixture.Info())["remoteAccess"]);
    }

    /// <summary>A CloudLink with a fake dispatcher that echoes the method, answering calls through a fake send.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDb _dir = new();
        public List<string> Calls { get; } = [];
        public CloudLink Link { get; }

        public Fixture()
        {
            Link = new CloudLink(_dir.Dir, (method, _, _) =>
            {
                lock (Calls) Calls.Add(method);
                return Task.FromResult<JsonNode?>(method == "core.getInfo" ? Info() : new JsonObject { ["echo"] = method });
            }, new AppEvents());
        }

        public static JsonNode Info() => new JsonObject { ["product"] = "Yawble", ["version"] = "test" };

        public async Task<JsonObject> CallAsync(string session, int id, string method, JsonObject? p, string? access)
        {
            var call = new JsonObject
            {
                ["type"] = "call", ["session"] = session, ["id"] = id, ["method"] = method, ["params"] = p, ["user"] = "visitor@example.com",
            };
            if (access is not null) call["access"] = access;
            JsonObject? sent = null;
            await Link.AnswerAsync(call, m => { sent = m; return Task.CompletedTask; }, Ct);
            Assert.NotNull(sent);
            Assert.Equal("result", sent!["type"]!.GetValue<string>());
            Assert.Equal(session, sent["session"]!.GetValue<string>());
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
