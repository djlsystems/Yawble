using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// needs_decision with summary, options and details; an option's allowlisted action
/// (team.allowLocalhostMac) is applied only when the person answers with that option (bus.answer optionIndex), through
/// teams.localhost (its rights and audit); bus.decisions says whether this caller may apply it; a member with an open
/// question shows "Waiting on you" with the decision's id. Fakes only: the bus tools go through McpBusServer in process.
/// </summary>
public class DecisionOptionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonArray LocalhostOptions() =>
    [
        new JsonObject { ["label"] = "Allow localhost", ["reply"] = "Allowed: run the tests again.", ["action"] = DecisionActions.AllowLocalhostMac },
        new JsonObject { ["label"] = "Keep it off", ["reply"] = "Keep it off; run the tests as the executable." },
    ];

    // ------------------------------------------------------------------ parsing and the stored payload

    [Fact]
    public void ThePayload_RoundTrips_WithTheNewFields()
    {
        var ask = DecisionAsk.Parse(null, "Should the team's runs reach localhost on macOS?", LocalhostOptions(), "- VSTest needs a port\n- MTP never works");
        var payload = ask.ToPayload();
        Assert.Equal("Should the team's runs reach localhost on macOS?", payload["question"]!.GetValue<string>()); // the summary stands in
        Assert.Equal("Should the team's runs reach localhost on macOS?", payload["summary"]!.GetValue<string>());
        Assert.Equal("- VSTest needs a port\n- MTP never works", payload["details"]!.GetValue<string>());
        var options = payload["options"]!.AsArray();
        Assert.Equal(2, options.Count);
        Assert.Equal(DecisionActions.AllowLocalhostMac, options[0]!["action"]!.GetValue<string>());
        Assert.False(options[1]!.AsObject().ContainsKey("action"));

        var back = DecisionAsk.Read(payload.ToJsonString());
        Assert.Equal(ask.Question, back.Question);
        Assert.Equal(ask.Summary, back.Summary);
        Assert.Equal(ask.Details, back.Details);
        Assert.Equal(ask.Options, back.Options);
    }

    [Fact]
    public void OptionsSentAsJsonText_AreRead()
    {
        var ask = DecisionAsk.Parse("Which?", null, JsonValue.Create(LocalhostOptions().ToJsonString()), null);
        Assert.Equal(2, ask.Options!.Count);
    }

    [Fact]
    public void AQuestionOnly_IsStoredAsBefore()
    {
        var ask = DecisionAsk.Parse("JSON or YAML?", null, null, null);
        Assert.Equal("""{"question":"JSON or YAML?"}""", ask.ToPayload().ToJsonString());
        Assert.Equal("JSON or YAML?", DecisionAsk.Read("""{"question":"JSON or YAML?"}""").Headline);
        Assert.Null(DecisionAsk.Read("""{"question":"JSON or YAML?"}""").Options);
        Assert.Equal("not json", DecisionAsk.Read("not json").Question);
    }

    public static TheoryData<string, string> BadOptions => new()
    {
        { """[{"label":"Only one","reply":"x"}]""", "2 to 4" },
        { """[{"label":"a","reply":"1"},{"label":"b","reply":"2"},{"label":"c","reply":"3"},{"label":"d","reply":"4"},{"label":"e","reply":"5"}]""", "2 to 4" },
        { """[{"label":"This label is far too long for a button, really","reply":"x"},{"label":"b","reply":"y"}]""", "at most 40" },
        { """[{"label":"","reply":"x"},{"label":"b","reply":"y"}]""", "needs a label" },
        { """[{"label":"a"},{"label":"b","reply":"y"}]""", "needs a reply" },
        { """[{"label":"a","reply":"x","action":"team.deleteEverything"},{"label":"b","reply":"y"}]""", "not one the app knows" },
        { """[{"label":"a","reply":"x","action":"team.allowLocalhostMac"},{"label":"b","reply":"y","action":"team.allowLocalhostMac"}]""", "at most one option" },
        { """[{"label":"a","reply":"x","run":"rm -rf /"},{"label":"b","reply":"y"}]""", "unknown field" },
        { """[{"label":"Same","reply":"x"},{"label":"same","reply":"y"}]""", "its own label" },
        { """{"label":"a"}""", "must be an array" },
    };

    [Theory]
    [MemberData(nameof(BadOptions))]
    public void BadOptions_AreRefused(string options, string says)
    {
        var e = Assert.Throws<BusRefusal>(() => DecisionAsk.Parse("Which?", null, JsonNode.Parse(options), null));
        Assert.Equal("bad_decision", e.Code);
        Assert.Contains(says, e.Message);
    }

    [Fact]
    public void NeitherQuestionNorSummary_IsRefused() =>
        Assert.Equal("bad_decision", Assert.Throws<BusRefusal>(() => DecisionAsk.Parse(" ", null, LocalhostOptions(), "details")).Code);

    [Fact]
    public void TheToolSchema_OffersSummaryOptionsAndDetails_AndOnlyTheAllowlistedAction()
    {
        var tool = McpBusServer.ToolList().Single(t => t!["name"]!.GetValue<string>() == "needs_decision")!;
        var props = tool["inputSchema"]!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("summary") && props.ContainsKey("options") && props.ContainsKey("details") && props.ContainsKey("question"));
        Assert.Empty(tool["inputSchema"]!["required"]!.AsArray());
        Assert.Equal([DecisionActions.AllowLocalhostMac, DecisionActions.CloseOverFail], props["options"]!["items"]!["properties"]!["action"]!["enum"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Contains("Never number findings", tool["description"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ the tool, the answer, the setting

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public UiRpc Ui { get; private set; } = null!;
        public AuditLog Audit { get; private set; } = null!;
        public long TeamId { get; private set; }
        public List<string> ToolReplies { get; } = [];
        public List<string> AdaSaw { get; } = [];

        /// <summary>Ada's script: an instruction "ask" calls needs_decision with <paramref name="ask"/> through the MCP tool;
        /// "finish" declares the workflow complete; anything else (an answer) is only recorded.</summary>
        public static async Task<Rig> StartAsync(JsonObject? ask = null, bool mac = true)
        {
            var rig = new Rig();
            var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
            {
                var text = Payload.Field(run.Items[0], "text") ?? "";
                rig.AdaSaw.Add(text);
                if (text == "ask") rig.ToolReplies.Add(await CallToolAsync(run, "needs_decision", ask!, ct));
                else if (text == "finish") await run.WorkflowCompleteAsync("the flaky test is fixed", ct);
                return RunResult.Ok;
            });
            rig.Engine = new BusEngine(new BusStore(rig.Db.ConnectionString), runner,
                new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await rig.Engine.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            rig.Ui = new UiRpc(rig.Engine, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384)
            { IsMacOS = () => mac };
            rig.Audit = new AuditLog(rig.Db.ConnectionString);
            rig.Rpc = new RpcDispatcher(new SandboxPlatformInfo(), () => [], ui: rig.Ui, audit: rig.Audit);
            // As Program wires it: the action is the setting's own call, through the dispatcher, as the current caller.
            rig.Ui.Dispatch = (m, p, ct) => rig.Rpc.DispatchAsAsync(Actor.Current ?? Actor.Window, m, p, ct);
            rig.TeamId = (await rig.Engine.TeamsAsync(Ct)).Single().Id;
            return rig;
        }

        private static async Task<string> CallToolAsync(RunContext run, string tool, JsonObject args, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args.DeepClone() },
            };
            var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), ct);
            var result = JsonNode.Parse(reply.Json!)!["result"]!;
            var text = result["content"]![0]!["text"]!.GetValue<string>();
            return result["isError"]?.GetValue<bool>() == true ? "ERROR: " + text : text;
        }

        public async Task TellAdaAsync(string text)
        {
            await Engine.PersonTellAsync("Alpha", "Ada", text, ct: Ct);
            await Wait.UntilAsync(() => Engine.IsQuiescentAsync(Ct), "quiet");
        }

        public async Task<JsonNode?> Call(string method, object? args = null, Actor? actor = null) =>
            await Rpc.DispatchAsAsync(actor ?? Actor.Window, method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

        public async Task<bool> LocalhostOn() => (await Call("teams.localhost", new { teamId = TeamId }))!["on"]!.GetValue<bool>();

        public async Task<JsonObject> OnlyDecision(Actor? actor = null) => Assert.Single((await Call("bus.decisions", new { teamId = TeamId }, actor))!.AsArray())!.AsObject();

        public async Task<IReadOnlyList<Message>> Decisions() => await Engine.OpenDecisionsAsync("Alpha", Ct);

        public IReadOnlyList<AuditLog.Row> LocalhostAudit() => Audit.Recent().Where(r => r.Method == "teams.localhost").ToList();

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static JsonObject LocalhostAsk() => new()
    {
        ["summary"] = "Should the team's runs reach localhost on macOS?",
        ["options"] = LocalhostOptions(),
        ["details"] = "| runner | works |\n|---|---|\n| VSTest | only with it on |",
    };

    [Fact]
    public async Task TheTool_StoresTheNewFieldsExactly_AndChangesNothingUntilThePersonAnswers()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        Assert.StartsWith("ok", rig.ToolReplies.Single());
        var stored = JsonNode.Parse(Assert.Single(await rig.Decisions()).Payload)!.AsObject();
        Assert.Equal(DecisionAsk.Parse(null, "Should the team's runs reach localhost on macOS?", LocalhostOptions(),
            "| runner | works |\n|---|---|\n| VSTest | only with it on |").ToPayload().ToJsonString(), stored.ToJsonString());
        // Asking changed nothing: the setting is off, nothing was audited.
        Assert.False(await rig.LocalhostOn());
        Assert.Empty(rig.LocalhostAudit());
    }

    [Fact]
    public async Task TheTool_WithAQuestionOnly_StoresItAsBefore()
    {
        await using var rig = await Rig.StartAsync(new JsonObject { ["question"] = "JSON or YAML?" });
        await rig.TellAdaAsync("ask");
        Assert.Equal("""{"question":"JSON or YAML?"}""", Assert.Single(await rig.Decisions()).Payload);
        var row = await rig.OnlyDecision();
        Assert.Equal("JSON or YAML?", row["question"]!.GetValue<string>());
        Assert.Null(row["summary"]);
        Assert.False(row.ContainsKey("options"));
    }

    [Fact]
    public async Task TheTool_RefusesAnUnknownAction_AndStoresNothing()
    {
        var ask = LocalhostAsk();
        ask["options"]![0]!["action"] = "team.allowEverything";
        await using var rig = await Rig.StartAsync(ask);
        await rig.TellAdaAsync("ask");
        Assert.StartsWith("ERROR: refused (bad_decision)", rig.ToolReplies.Single());
        Assert.Empty(await rig.Decisions());
        using var store = rig.Db.OpenStore();
        Assert.DoesNotContain(store.MessagesFrom(0, 1000), m => m.Type == MessageTypes.NeedsDecision);
    }

    [Fact]
    public async Task TheTool_RefusesBadOptionCountsAndLabels()
    {
        foreach (var options in new[]
        {
            new JsonArray(new JsonObject { ["label"] = "Yes", ["reply"] = "yes" }),
            new JsonArray(new JsonObject { ["label"] = new string('x', 41), ["reply"] = "yes" }, new JsonObject { ["label"] = "No", ["reply"] = "no" }),
        })
        {
            await using var rig = await Rig.StartAsync(new JsonObject { ["summary"] = "Go?", ["options"] = options });
            await rig.TellAdaAsync("ask");
            Assert.StartsWith("ERROR: refused (bad_decision)", rig.ToolReplies.Single());
            Assert.Empty(await rig.Decisions());
        }
    }

    [Fact]
    public async Task TheDecision_IsServedWithItsOptions_AndTheActionsAvailability()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var row = await rig.OnlyDecision();
        Assert.Equal("Should the team's runs reach localhost on macOS?", row["summary"]!.GetValue<string>());
        Assert.StartsWith("| runner |", row["details"]!.GetValue<string>());
        var options = row["options"]!.AsArray();
        Assert.Equal("Allow localhost", options[0]!["label"]!.GetValue<string>());
        Assert.Equal(DecisionActions.AllowLocalhostMac, options[0]!["action"]!.GetValue<string>());
        Assert.True(options[0]!["actionAvailable"]!.GetValue<bool>());
        Assert.Null(options[0]!["actionUnavailableReason"]);
        Assert.False(options[1]!.AsObject().ContainsKey("actionAvailable")); // no action, no availability

        // A Visitor reads it with the action disabled and why.
        var visitor = (await rig.OnlyDecision(Actor.CloudViewer("vic@example.com", "read")))["options"]![0]!;
        Assert.False(visitor["actionAvailable"]!.GetValue<bool>());
        Assert.Equal(UiRpc.VisitorActionReason, visitor["actionUnavailableReason"]!.GetValue<string>());
        // A full cloud viewer may.
        Assert.True((await rig.OnlyDecision(Actor.CloudViewer("pat@example.com")))["options"]![0]!["actionAvailable"]!.GetValue<bool>());
        // The Concierge (an agent) never may.
        Assert.Equal(UiRpc.NotThePersonActionReason,
            (await rig.OnlyDecision(new Actor(Actor.Concierge, "pat")))["options"]![0]!["actionUnavailableReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnsweringWithTheActionOption_TurnsTheSettingOn_AuditsIt_AndSendsTheReply()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());

        var result = (await rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 }))!.AsObject();
        Assert.Equal(DecisionActions.AllowLocalhostMac, result["applied"]!.GetValue<string>());
        Assert.True(await rig.LocalhostOn());
        var audit = Assert.Single(rig.LocalhostAudit());
        Assert.Equal((Actor.App, "done"), (audit.Via, audit.Outcome));
        Assert.Contains("team=Alpha", audit.Target);

        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");
        Assert.Empty(await rig.Decisions());
        using var store = rig.Db.OpenStore();
        var answer = store.Find(result["seq"]!.GetValue<long>())!;
        Assert.Equal(question.Seq, answer.CausationSeq);
        Assert.Equal("Allowed: run the tests again.", Payload.Field(answer, "text"));
        Assert.Contains("Allowed: run the tests again.", rig.AdaSaw);
    }

    [Fact]
    public async Task AnsweringWithAnOptionWithoutAction_SendsItsReply_AndChangesNothing()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        var result = (await rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 1, text = "Thanks." }))!.AsObject();
        Assert.False(result.ContainsKey("applied"));
        Assert.False(await rig.LocalhostOn());
        Assert.Empty(rig.LocalhostAudit());
        using var store = rig.Db.OpenStore();
        Assert.Equal("Keep it off; run the tests as the executable.\n\nThanks.", Payload.Field(store.Find(result["seq"]!.GetValue<long>())!, "text"));
    }

    [Fact]
    public async Task AVisitor_IsRefused_AndNothingIsSentOrChanged()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        // The Cloud Link already refuses bus.answer for a Visitor (RemoteAccess)...
        Assert.Equal(Yawble.Core.Link.RemoteAccess.ReadOnlyRefusal, Yawble.Core.Link.RemoteAccess.Refusal("bus.answer",
            new JsonObject { ["teamId"] = rig.TeamId, ["seq"] = question.Seq, ["optionIndex"] = 0 }, false, Yawble.Core.Link.RemoteAccess.Read));
        // ...and the core refuses the action itself when a Visitor's call reaches it anyway.
        var e = await Assert.ThrowsAsync<RpcException>(() =>
            rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 }, Actor.CloudViewer("vic@example.com", "read")));
        Assert.Equal("forbidden", e.Code);
        Assert.Equal(UiRpc.VisitorActionReason, e.Message);
        Assert.False(await rig.LocalhostOn());
        Assert.Empty(rig.LocalhostAudit());
        Assert.Single(await rig.Decisions()); // still open: nothing was sent
    }

    [Fact]
    public async Task OffAMac_TheActionIsUnavailable_AndAnsweringWithItIsRefused()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk(), mac: false);
        await rig.TellAdaAsync("ask");
        var option = (await rig.OnlyDecision())["options"]![0]!;
        Assert.False(option["actionAvailable"]!.GetValue<bool>());
        Assert.Equal(UiRpc.MacOnlyActionReason, option["actionUnavailableReason"]!.GetValue<string>());
        var question = Assert.Single(await rig.Decisions());
        var e = await Assert.ThrowsAsync<RpcException>(() => rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 }));
        Assert.Equal("forbidden", e.Code);
        Assert.False(await rig.LocalhostOn());
        Assert.Single(await rig.Decisions());
    }

    [Fact]
    public async Task ABadOptionIndex_IsRefused()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        foreach (var bad in new object[] { 2, -1, "first" })
            Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() =>
                rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = bad }))).Code);
        Assert.Single(await rig.Decisions());
    }

    [Fact]
    public async Task AQuestionOnlyDecision_RefusesAnOptionIndex_AndIsAnsweredWithTextAsBefore()
    {
        await using var rig = await Rig.StartAsync(new JsonObject { ["question"] = "JSON or YAML?" });
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() =>
            rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 }))).Code);
        await rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, text = "YAML" });
        Assert.Empty(await rig.Decisions());
    }

    // ------------------------------------------------------------------ the tester's checks

    [Fact]
    public async Task TheShellsAnswer_TextEqualToTheReply_IsSentOnce_AndStillAppliesTheAction()
    {
        // The shell's option buttons send bus.answer { teamId, seq, text: reply, optionIndex }.
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        var result = (await rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0, text = "Allowed: run the tests again." }))!.AsObject();
        Assert.Equal(DecisionActions.AllowLocalhostMac, result["applied"]!.GetValue<string>());
        Assert.True(await rig.LocalhostOn());
        Assert.Single(rig.LocalhostAudit());
        using var store = rig.Db.OpenStore();
        Assert.Equal("Allowed: run the tests again.", Payload.Field(store.Find(result["seq"]!.GetValue<long>())!, "text"));
    }

    [Fact]
    public async Task ATextOnlyAnswer_AsTheConciergeSendsIt_NeverAppliesTheAction()
    {
        // The Concierge's answer tool sends { teamId, seq, text } and no optionIndex: even the action option's exact reply
        // is only text, the setting stays off and nothing is audited.
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        var result = (await rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, text = "Allowed: run the tests again." },
            new Actor(Actor.Concierge, "pat")))!.AsObject();
        Assert.False(result.ContainsKey("applied"));
        Assert.False(await rig.LocalhostOn());
        Assert.Empty(rig.LocalhostAudit());
    }

    // ------------------------------------------------------------------ fixes from review

    [Fact]
    public async Task ARefusedAnswer_ToAnArchivedTeam_LeavesAllowLocalhostOff_AndSendsNothing()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        await rig.Engine.ArchiveTeamAsync("Alpha", Ct);

        var e = await Assert.ThrowsAsync<RpcException>(() => rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 }));
        Assert.Equal("archived", e.Code);
        Assert.False(await rig.LocalhostOn());
        Assert.Empty(rig.LocalhostAudit());
        Assert.Single(await rig.Decisions()); // still open: nothing was sent
    }

    [Fact]
    public async Task AnOptionWithTypedWords_ReachesTheAgent_AsTheReplyThenTheWords_Once()
    {
        // The shell sends only the typed words with optionIndex; the core puts the option's reply first, once.
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        var result = (await rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0, text = "  Then close the card.  " }))!.AsObject();
        Assert.Equal(DecisionActions.AllowLocalhostMac, result["applied"]!.GetValue<string>());
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");

        const string expected = "Allowed: run the tests again.\n\nThen close the card.";
        using var store = rig.Db.OpenStore();
        Assert.Equal(expected, Payload.Field(store.Find(result["seq"]!.GetValue<long>())!, "text"));
        var saw = Assert.Single(rig.AdaSaw, t => t.Contains("Then close the card."));
        Assert.Equal(expected, saw);
        Assert.Equal(1, saw.Split("Allowed: run the tests again.").Length - 1);
    }

    [Fact]
    public async Task TwoConcurrentAnswers_OneAppliesAndSends_TheOtherIsRefusedAsAnsweredAlready()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());

        // The first answer's action is held in the setting's call (a fake gate on the dispatcher) while the second arrives.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = 0;
        var real = rig.Ui.Dispatch!;
        rig.Ui.Dispatch = async (m, p, ct) =>
        {
            if (m == "teams.localhost" && Interlocked.Increment(ref actions) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
            return await real(m, p, ct);
        };

        var first = rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Single(await rig.Decisions()); // still open while the first is applying: the second passes UiRpc's own look-up
        var second = await Assert.ThrowsAsync<RpcException>(() => rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 }));
        Assert.Equal("no_such_decision", second.Code);
        Assert.Equal(BusEngine.AnsweredAlready, second.Message);
        release.SetResult();
        var result = (await first)!.AsObject();

        Assert.Equal(DecisionActions.AllowLocalhostMac, result["applied"]!.GetValue<string>());
        Assert.Equal(1, actions);
        Assert.True(await rig.LocalhostOn());
        Assert.Single(rig.LocalhostAudit());
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");
        Assert.Empty(await rig.Decisions());
        using var store = rig.Db.OpenStore();
        Assert.Single(store.MessagesFrom(0, 1000), m => m.CausationSeq == question.Seq && m.Source == MessageTypes.PersonSource);
        Assert.Single(rig.AdaSaw, t => t == "Allowed: run the tests again.");
    }

    [Fact]
    public async Task AFailedAction_ReleasesTheDecision_SoItCanBeAnsweredAgain()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        var real = rig.Ui.Dispatch!;
        rig.Ui.Dispatch = (m, p, ct) => m == "teams.localhost" ? throw new RpcException("unavailable", "the setting could not be saved") : real(m, p, ct);
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<RpcException>(() =>
            rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 }))).Code);
        Assert.Single(await rig.Decisions());

        rig.Ui.Dispatch = real;
        await rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 0 });
        Assert.True(await rig.LocalhostOn());
        Assert.Empty(await rig.Decisions());
    }

    // ------------------------------------------------------------------ Waiting on you

    [Fact]
    public async Task AMemberThatDeclaredComplete_ThenAsks_ShowsWaitingOnYou_UntilAnswered()
    {
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("finish");
        Assert.StartsWith("declared complete", (await rig.Engine.TeamStatusAsync("Alpha", Ct)).Single(s => s.Name == "Ada").StatusLine);

        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        await AssertWaiting(rig, question.Seq);

        await rig.Call("bus.answer", new { teamId = rig.TeamId, seq = question.Seq, optionIndex = 1 });
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");
        var card = (await rig.Engine.AgentCardsAsync(rig.TeamId, Ct)).Single(c => c.Member.Name == "Ada");
        Assert.Null(card.DecisionId);
        Assert.DoesNotContain(BusEngine.WaitingOnYou, card.StatusLine ?? "");
    }

    [Fact]
    public async Task AnOpenQuestion_WinsOverADeclarationMadeAfterIt_InAnotherWorkflow()
    {
        // The bug: the card's line is the member's last report, so declaring another workflow complete after asking
        // showed "declared complete" while the question still waited on the person.
        await using var rig = await Rig.StartAsync(LocalhostAsk());
        await rig.TellAdaAsync("ask");
        var question = Assert.Single(await rig.Decisions());
        await rig.TellAdaAsync("finish");
        await AssertWaiting(rig, question.Seq);

        await rig.Engine.PersonTellAsync("Alpha", "Ada", "Keep it off.", question.Seq, Ct);
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");
        var status = (await rig.Engine.TeamStatusAsync("Alpha", Ct)).Single(s => s.Name == "Ada");
        Assert.Null(status.DecisionId);
        Assert.StartsWith("declared complete", status.StatusLine); // falls back as before
    }

    private static async Task AssertWaiting(Rig rig, long decisionId)
    {
        var card = (await rig.Engine.AgentCardsAsync(rig.TeamId, Ct)).Single(c => c.Member.Name == "Ada");
        Assert.Equal(decisionId, card.DecisionId);
        Assert.Equal("Waiting on you: Should the team's runs reach localhost on macOS?", card.StatusLine);
        var status = (await rig.Engine.TeamStatusAsync("Alpha", Ct)).Single(s => s.Name == "Ada");
        Assert.Equal(decisionId, status.DecisionId);
        Assert.StartsWith(BusEngine.WaitingOnYou, status.StatusLine);
        // teams.agents (the shell's cards) carries both.
        var agent = (await rig.Call("teams.agents", new { teamId = rig.TeamId }))!.AsArray().Single(a => a!["member"]!["name"]!.GetValue<string>() == "Ada")!;
        Assert.Equal(decisionId, agent["decisionId"]!.GetValue<long>());
        Assert.StartsWith(BusEngine.WaitingOnYou, agent["statusLine"]!.GetValue<string>());
        Assert.Equal("decision", agent["attention"]!.GetValue<string>());
    }
}
