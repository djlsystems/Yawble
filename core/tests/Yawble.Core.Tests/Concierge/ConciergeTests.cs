using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The Concierge: chats kept as conversations, each turn one headless run of the chosen agent with the whole context
/// in its prompt and the Concierge's tools at its gateway; the reply read from the CLI's output and streamed.
/// </summary>
public class ConciergeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Feed(ConciergeOutput o, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += 5) o.Write(bytes.AsMemory(i, Math.Min(5, bytes.Length - i)));
    }

    private const string ClaudeStream =
        "{\"type\":\"system\",\"subtype\":\"init\"}\n" +
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"ToolSearch\",\"input\":{\"query\":\"x\"}}]}}\n" +
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Let me look.\"},{\"type\":\"tool_use\",\"name\":\"mcp__yawble-bus__team_status\",\"input\":{\"team\":\"alpha\"}}]}}\n" +
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"...\"}]}}\n" +
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"**alpha** is idle.\"}]}}\n" +
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"**alpha** is idle.\"}\n";

    [Fact]
    public void ClaudesStreamGivesTheReplyAsItComes_TheToolsItUsed_AndTheFinalAnswer()
    {
        var o = new ConciergeOutput(AgentCli.Claude);
        Feed(o, ClaudeStream[..ClaudeStream.IndexOf("{\"type\":\"user\"", StringComparison.Ordinal)]);
        Assert.Equal("Let me look.", o.Text);
        Assert.False(o.HasFinal);
        Feed(o, ClaudeStream[ClaudeStream.IndexOf("{\"type\":\"user\"", StringComparison.Ordinal)..]);
        Assert.True(o.HasFinal);
        Assert.Equal("**alpha** is idle.", o.Text);
        var tool = Assert.Single(o.Tools); // the CLI's own ToolSearch step is not shown
        Assert.Equal("team_status", tool.Name);
        Assert.Equal("team: alpha", tool.Detail);
        Assert.Null(o.Error);
    }

    [Fact]
    public void AClaudeErrorResultIsTheError()
    {
        var o = new ConciergeOutput(AgentCli.Claude);
        Feed(o, "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"result\":\"API Error: 401\"}\n");
        Assert.Equal("API Error: 401", o.Error);
    }

    [Fact]
    public void GroksSummaryTextIsTheAnswer_AndPlainTextClisGiveTheirOutputWithoutEscapes()
    {
        var grok = new ConciergeOutput(AgentCli.Grok);
        Feed(grok, "{\n  \"text\": \"All quiet.\",\n  \"usage\": { \"input_tokens\": 3 },\n  \"num_turns\": 2\n}\n");
        Assert.Equal("All quiet.", grok.Text);
        var codex = new ConciergeOutput(AgentCli.Codex);
        Feed(codex, "\u001b[1mHello\u001b[0m from codex\r\nline two");
        codex.Complete();
        Assert.Equal("Hello from codex\nline two", codex.Text);
    }

    [Fact]
    public void ThePromptCarriesTheRole_TheTeams_TheFocus_TheNotes_TheConversation_AndTheNewMessage()
    {
        var history = new List<ConciergeMessageRow>
        {
            Row(1, "user", "first question"), Row(2, "assistant", "first answer"), Row(3, "user", "and now this"),
        };
        var p = ConciergePrompt.Build("- alpha: 2 agents", "alpha", "the person likes short answers", history, DateTimeOffset.Now);
        Assert.Contains("You are the Concierge", p);
        Assert.Contains("- alpha: 2 agents", p);
        Assert.Contains("alpha (the person has this team open", p);
        Assert.Contains("likes short answers", p);
        Assert.True(p.IndexOf("**Person:** first question", StringComparison.Ordinal) < p.IndexOf("**You:** first answer", StringComparison.Ordinal));
        Assert.EndsWith("and now this\n", p.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("**Person:** and now this", p);
    }

    [Fact]
    public void ALongConversationStaysUnderTheCommandLineLimit_KeepingTheNewestMessages()
    {
        var history = Enumerable.Range(1, 200).Select(i => Row(i, i % 2 == 1 ? "user" : "assistant", $"message {i} " + new string('x', 2500))).ToList();
        history.Add(Row(201, "user", "the latest"));
        var p = ConciergePrompt.Build("- alpha", null, new string('n', 50_000), history, DateTimeOffset.Now);
        Assert.True(p.Length <= ConciergePrompt.MaxChars, $"{p.Length} characters");
        Assert.Contains("message 200 ", p);
        Assert.DoesNotContain("message 2 x", p);
        Assert.Contains("(earlier messages omitted)", p);
        Assert.Contains("the latest", p);
    }

    [Theory]
    [InlineData("What is every team doing?", "What is every team doing?")]
    [InlineData("   \nline one\nline two", "line one")]
    [InlineData("", "New chat")]
    public void ATitleIsTheFirstLine(string text, string title) => Assert.Equal(title, ConciergePrompt.Title(text));

    [Fact]
    public void ConversationsMessagesAndNotesAreKept()
    {
        using var db = new TempDb();
        var store = new ConciergeStore(db.ConnectionString);
        store.Create("a1", "First", "claude", null);
        store.Create("b2", "Second", "grok", 7);
        store.AddMessage("a1", "user", "hello");
        store.AddMessage("a1", "assistant", "hi", "done", null, [new ConciergeToolUse("team_list", null)], "claude", "r-1");
        Assert.Equal(["a1", "b2"], store.List().Select(c => c.Id)); // a1 was updated last
        var messages = store.Messages("a1");
        Assert.Equal(["user", "assistant"], messages.Select(m => m.Role));
        Assert.Equal("team_list", Assert.Single(messages[1].Tools).Name);
        store.Rename("b2", "Renamed");
        store.SetTeam("b2", null);
        Assert.Equal(("Renamed", (long?)null), (store.Get("b2")!.Title, store.Get("b2")!.TeamId));
        store.SaveNotes("remember this");
        store.SaveNotes("remember that");
        Assert.Equal("remember that", store.Notes());
        Assert.True(store.Delete("a1"));
        Assert.Empty(store.Messages("a1"));
        Assert.Null(store.Get("a1"));
    }

    [Fact]
    public async Task ATurnRunsTheChosenAgentWithTheConciergeTools_StreamsItsReply_AndKeepsTheAnswer()
    {
        using var db = new TempDb();
        await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner());
        await bus.StartAsync(Ct);
        var alpha = await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        var events = new AppEvents();
        var pushes = new List<JsonObject>();
        using var _ = events.Subscribe(m => { lock (pushes) pushes.Add(m); return Task.CompletedTask; });
        var registry = new RunRegistry();
        var uiCalls = new List<(string Method, JsonObject? P)>();
        LaunchRequest? launched = null;
        string? statusSeen = null;

        async Task<LaunchOutcome> Launch(LaunchRequest request, CancellationToken ct)
        {
            launched = request;
            // The agent calls its tools through the run's gateway, then prints its answer.
            statusSeen = ToolText(await request.Tools!.HandleAsync(Call("team_status", new JsonObject { ["team"] = "alpha" }), ct));
            await request.Tools.HandleAsync(Call("tell", new JsonObject { ["team"] = "ALPHA", ["instruction"] = "Add a README" }), ct);
            var process = new FakeProcess(RunKind.Headless);
            var output = new RunOutputBuffer();
            var live = new LiveRun("r-test", null, request.Team, request.Member, RunKind.Headless, process,
                new RunSupervisor(process, 60, 0, new RunActivity(TimeProvider.System), output), output);
            registry.Add(live);
            output.Write(Encoding.UTF8.GetBytes(ClaudeStream));
            registry.Remove(live);
            return new LaunchOutcome("r-test", RunEnd.Completed, null, null, []);
        }

        var service = new ConciergeService(new ConciergeStore(db.ConnectionString), bus, Launch, registry, events,
            (method, p, ct) =>
            {
                lock (uiCalls) uiCalls.Add((method, p));
                return Task.FromResult<JsonNode?>(new JsonObject { ["seq"] = 42, ["workflow"] = 42 });
            },
            _ => Task.FromResult("claude"));

        var sent = await service.DispatchAsync("concierge.send", new JsonObject { ["text"] = "Get alpha a README", ["teamId"] = alpha }, Ct);
        var id = sent!["id"]!.GetValue<string>();
        JsonNode? conversation = null;
        await Wait.UntilAsync(async () =>
        {
            conversation = await service.DispatchAsync("concierge.get", new JsonObject { ["id"] = id }, Ct);
            return conversation!["messages"]!.AsArray().Count == 2;
        }, "the answer kept");

        Assert.Equal(ConciergeService.Team, launched!.Team);
        Assert.Equal(ConciergeService.MemberOf(id), launched.Member);
        Assert.Equal(AgentCli.Claude, launched.Subject!.Cli);
        Assert.Contains("Team in focus\nalpha", launched.Prompt!.ReplaceLineEndings("\n"));
        Assert.Contains("Get alpha a README", launched.Prompt);
        Assert.Contains("Manager (manager, claude)", statusSeen);
        var tell = Assert.Single(uiCalls, c => c.Method == "bus.tellManager");
        Assert.Equal(alpha, tell.P!["teamId"]!.GetValue<long>());

        Assert.Equal("Get alpha a README", conversation!["title"]!.GetValue<string>());
        Assert.Equal("alpha", conversation["team"]!.GetValue<string>());
        var answer = conversation["messages"]![1]!;
        Assert.Equal("assistant", answer["role"]!.GetValue<string>());
        Assert.Equal("**alpha** is idle.", answer["text"]!.GetValue<string>());
        Assert.Equal("done", answer["state"]!.GetValue<string>());
        Assert.Equal("team_status", answer["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Null(conversation["turn"]);
        await Wait.UntilAsync(() => { lock (pushes) return pushes.Any(p => p["data"]?["state"]?.GetValue<string>() == "done"); }, "the done push");
        lock (pushes) Assert.Contains(pushes, p => p["event"]!.GetValue<string>() == ConciergeService.TurnEvent && p["data"]!["state"]!.GetValue<string>() == "running");

        var list = (await service.DispatchAsync("concierge.list", null, Ct))!.AsArray();
        Assert.Equal(id, Assert.Single(list)!["id"]!.GetValue<string>());
        Assert.True((await service.DispatchAsync("concierge.delete", new JsonObject { ["id"] = id }, Ct))!["deleted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AFailedRunIsKeptAsAFailedAnswerWithTheReason()
    {
        using var db = new TempDb();
        await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner());
        await bus.StartAsync(Ct);
        var service = new ConciergeService(new ConciergeStore(db.ConnectionString), bus,
            (_, _) => Task.FromResult(new LaunchOutcome("r-x", RunEnd.StartFailed, "claude is not signed in on this computer", null, [])),
            new RunRegistry(), new AppEvents(), (_, _, _) => Task.FromResult<JsonNode?>(null), _ => Task.FromResult("claude"));
        var id = (await service.DispatchAsync("concierge.send", new JsonObject { ["text"] = "hello" }, Ct))!["id"]!.GetValue<string>();
        JsonNode? c = null;
        await Wait.UntilAsync(async () => (c = await service.DispatchAsync("concierge.get", new JsonObject { ["id"] = id }, Ct))!["messages"]!.AsArray().Count == 2, "the failed answer");
        var answer = c!["messages"]![1]!;
        Assert.Equal("failed", answer["state"]!.GetValue<string>());
        Assert.Contains("not signed in", answer["error"]!.GetValue<string>());
        // The conversation goes on: the next message is a new turn.
        await service.DispatchAsync("concierge.send", new JsonObject { ["id"] = id, ["text"] = "again" }, Ct);
    }

    private static ConciergeMessageRow Row(long id, string role, string text) => new(id, "c", role, text, "", role == "assistant" ? "done" : null, null, [], null, null);

    private static byte[] Call(string tool, JsonObject args) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
    }.ToJsonString());

    private static string ToolText(Yawble.Core.Gateway.BusReply reply) =>
        JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
    [Fact]
    public void ThePromptSaysWhatItCanSeeAndDo_AndHowToAnswerStatusAndRefusals()
    {
        var p = ConciergePrompt.Build("- alpha", null, "", [], DateTimeOffset.Now);
        Assert.Contains("never say you cannot see", p);
        Assert.Contains("ONE recommended next step", p);
        Assert.Contains("Never work around", p);
        Assert.Contains("say which tool, and why", p);
        Assert.Contains("closest thing", p);
        Assert.Contains("doctor_fix", p);
    }

    [Fact]
    public void TheDoctorFixToolIsOffered_AndIsLocalOnly()
    {
        Assert.Contains("doctor_fix", ConciergeActions.ToolNames);
        Assert.Contains("doctor_fix", ConciergeActions.LocalOnly);
        Assert.Contains(ConciergeActions.Tools().OfType<JsonObject>(), t => t["name"]?.GetValue<string>() == "doctor_fix");
    }

    [Fact]
    public async Task TheConciergesCallsReachTheBoardForBacklogAndTheScreensForTheRest()
    {
        var calls = new List<string>();
        var route = ConciergeService.Route(
            (m, _, _) => { calls.Add("ui:" + m); return Task.FromResult<JsonNode?>(null); },
            () => (m, _, _) => { calls.Add("board:" + m); return Task.FromResult<JsonNode?>(new JsonObject { ["label"] = "B006" }); });

        var added = await route("backlog.add", new JsonObject { ["title"] = "t" }, Ct);
        await route("board.list", null, Ct);
        await route("teams.repos", null, Ct);

        Assert.Equal(["board:backlog.add", "board:board.list", "ui:teams.repos"], calls);
        Assert.Equal("B006", added!["label"]!.GetValue<string>());
    }
}
