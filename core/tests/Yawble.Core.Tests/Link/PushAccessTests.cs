using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Link;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// Every push the core sends up the Cloud Link says the least access a viewer needs to receive it: "read" only for the
/// events listed as reads, "full" for the rest and for any name the table does not know.
/// </summary>
public class PushAccessTests
{
    /// <summary>Every event name the core declares (read from its sources) is in the table, as read or full.</summary>
    [Fact]
    public void EveryEventTheCoreDeclares_IsClassified()
    {
        var declared = DeclaredEvents();
        Assert.True(declared.Count >= 15, $"only {declared.Count} event names found in the core's sources");
        var missing = declared.Where(e => !PushAccess.Levels.ContainsKey(e)).ToList();
        Assert.True(missing.Count == 0, "not classified as read or full in PushAccess: " + string.Join(", ", missing));
        // ...and the table names only real events (no typo silently left out).
        var stray = PushAccess.Levels.Keys.Where(e => !declared.Contains(e)).ToList();
        Assert.True(stray.Count == 0, "in PushAccess but not an event the core declares: " + string.Join(", ", stray));
        Assert.All(PushAccess.Levels.Values, a => Assert.Contains(a, new[] { RemoteAccess.Read, RemoteAccess.Full }));
    }

    /// <summary>An event is published by a declared name, never by a text written at the call: the check above would not see it.</summary>
    [Fact]
    public void NoEventIsPublishedByALiteralName()
    {
        var literal = new Regex("""\bPublish\(\s*\$?"[^"]*""");
        var hits = Sources().SelectMany(f => literal.Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}")).ToList();
        Assert.True(hits.Count == 0, "published by a literal name: " + string.Join("; ", hits));
    }

    [Theory]
    [InlineData("terminal.output")]
    [InlineData("terminal.state")]
    [InlineData("link.state")]
    [InlineData("connections.changed")]
    [InlineData("tools.installProgress")]
    [InlineData("companion.changed")]
    [InlineData("agents.testChanged")]
    [InlineData("website.request")]
    [InlineData("app.removed")]
    // not in the table at all
    [InlineData("hostTerminal.output")]
    [InlineData("credentials.changed")]
    [InlineData("some.newEvent")]
    [InlineData("board.changed ")]
    [InlineData("Board.changed")]
    [InlineData("")]
    [InlineData(null)]
    public void TerminalsAndAnythingNotListedAsARead_AreFull(string? name) => Assert.Equal(RemoteAccess.Full, PushAccess.Of(name));

    [Theory]
    [InlineData("board.changed")]
    [InlineData("lease.changed")]
    [InlineData("network.asksChanged")]
    [InlineData("tools.asksChanged")]
    [InlineData("folder.asksChanged")]
    [InlineData("concierge.turn")]
    [InlineData("concierge.update")]
    [InlineData("org.summaryChanged")]
    [InlineData("logs.tailEntry")]
    [InlineData("code.changed")]
    public void TheRefreshAndStateEventsAReadOnlyViewMayRead_AreRead(string name) => Assert.Equal(RemoteAccess.Read, PushAccess.Of(name));

    [Fact]
    public void TheReadEvents_AreExactlyTheTenListed() =>
        Assert.Equal(10, PushAccess.Levels.Count(l => l.Value == RemoteAccess.Read));

    [Fact]
    public void APushGoesUpMarkedAsAnEvent_WithItsLeastAccess()
    {
        var terminal = CloudLink.PushMessage(new JsonObject { ["event"] = "terminal.output", ["data"] = new JsonObject { ["channel"] = "member:3", ["data"] = "aGk=" } })!;
        Assert.Equal("event", terminal["type"]!.GetValue<string>());
        Assert.Equal("full", terminal["access"]!.GetValue<string>());
        Assert.Equal("member:3", terminal["data"]!["channel"]!.GetValue<string>());
        Assert.Equal("full", CloudLink.PushMessage(new JsonObject { ["event"] = "terminal.state", ["data"] = new JsonObject { ["channel"] = "host:1" } })!["access"]!.GetValue<string>());

        var board = CloudLink.PushMessage(new JsonObject { ["event"] = "board.changed", ["data"] = null })!;
        Assert.Equal("event", board["type"]!.GetValue<string>());
        Assert.Equal("read", board["access"]!.GetValue<string>());

        // An event the table does not know, an event name that is not text, and a message with none: full.
        Assert.Equal("full", CloudLink.PushMessage(new JsonObject { ["event"] = "some.newEvent", ["data"] = null })!["access"]!.GetValue<string>());
        Assert.Equal("full", CloudLink.PushMessage(new JsonObject { ["data"] = null })!["access"]!.GetValue<string>());
    }

    [Fact]
    public void APushThatStaysOnTheComputer_IsNotSentAtAll()
    {
        Assert.Null(CloudLink.PushMessage(new JsonObject { ["event"] = "companion.changed", ["data"] = null }));
        Assert.Null(CloudLink.PushMessage(new JsonObject { ["event"] = "agents.testChanged", ["data"] = null }));
        Assert.Null(CloudLink.PushMessage(new JsonObject { ["event"] = "terminal.output", ["data"] = new JsonObject { ["channel"] = "agent-test:claude" } }));
        // code.changed: only for a session a cloud viewer opened, and only while members' code is shown; then it is a read.
        JsonObject Code(bool cloud) => new() { ["event"] = "code.changed", ["data"] = new JsonObject { ["cloud"] = cloud } };
        Assert.Null(CloudLink.PushMessage(Code(cloud: false)));
        Assert.Null(CloudLink.PushMessage(Code(cloud: true), showCode: false));
        Assert.Equal("read", CloudLink.PushMessage(Code(cloud: true))!["access"]!.GetValue<string>());
    }

    /// <summary>A push marked read carries only what a Visitor may call for: no terminal event is a read.</summary>
    [Fact]
    public void NoTerminalCompanionCredentialOrLinkEvent_IsARead()
    {
        foreach (var (name, access) in PushAccess.Levels)
        {
            if (access != RemoteAccess.Read) continue;
            Assert.False(name.StartsWith("terminal.", StringComparison.Ordinal) || name.StartsWith("hostTerminal.", StringComparison.Ordinal)
                || name.StartsWith("companion.", StringComparison.Ordinal) || name.StartsWith("credentials.", StringComparison.Ordinal)
                || name.StartsWith("link.", StringComparison.Ordinal) || name.StartsWith("connections.", StringComparison.Ordinal), name);
        }
    }

    private static HashSet<string> DeclaredEvents()
    {
        // public const string ChangedEvent = "board.changed"; (also OutputEvent, EventName, ...)
        var pattern = new Regex("""const\s+string\s+\w*Event\w*\s*=\s*"([a-z][A-Za-z]*(?:\.[A-Za-z]+)+)"\s*;""");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Sources())
            foreach (Match m in pattern.Matches(File.ReadAllText(file))) names.Add(m.Groups[1].Value);
        return names;
    }

    private static IEnumerable<string> Sources()
    {
        var src = Path.Combine(RepoRoot(), "core", "src");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "core", "src", "Yawble.Core", "Ipc", "RpcDispatcher.cs"))) return dir.FullName;
        throw new InvalidOperationException("the repository's sources were not found above " + AppContext.BaseDirectory);
    }
}
