using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Ui;
using Xunit;
using ClosedOverFailText = Yawble.Core.Board.ClosedOverFail;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// The tester's checks of closing over a FAIL, end to end on a real BusEngine with fake runners: the Manager works only through the MCP bus
/// tools (tell with card, needs_decision with card/kind/reason, deliverable_complete, deliverable_show, deliverable_digest), the
/// person answers through the window's bus.answer (optionIndex) on a real UiRpc, the Concierge's answer is bus.answer with
/// text only as the Concierge actor, and the board is a real BoardService reading the bus. Strings are core/src's own
/// (TesterVerdicts.Refusal, Board.ClosedOverFail).
/// </summary>
public class ClosedOverFailBusVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Fail1 = "FAIL: card verify, the Save button does nothing";
    private const string Fail2 = "FAIL: card verify, Save now stores the row twice";

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public readonly ScriptedRunner Runner = new();
        public BusEngine Engine = null!;
        public RpcDispatcher Rpc = null!;
        public BoardStore Board = null!;
        public BoardService BoardSvc = null!;
        public long TeamId;
        public long Build, Verify;
        public long Root;
        public readonly List<string> Declares = [];   // every deliverable_complete tool reply except busy
        public string? Show, Digest;
        public int TesterRuns;
        private int _handledTester;

        public async Task StartAsync(params string[] members)
        {
            Engine = new BusEngine(Db.OpenStore(), Runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false, Retros = false });
            await Engine.CreateTeamAsync("Alpha", ct: Ct);
            foreach (var m in members) await Engine.AddMemberAsync("Alpha", m, ct: Ct);
            await Engine.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(Engine, new PolicyStore(Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, Engine), terminals, 16_384);
            Rpc = new RpcDispatcher(new SandboxPlatformInfo(), () => [], ui: ui);
            ui.Dispatch = (m, p, ct) => Rpc.DispatchAsAsync(Actor.Current ?? Actor.Window, m, p, ct);
            TeamId = (await Engine.TeamsAsync(Ct)).Single().Id;
            Board = new BoardStore(Db.ConnectionString);
            BoardSvc = new BoardService(Board, Engine);
            Build = Board.AddCard("Alpha", null, "B004", "Build the Save button", "spec", "queued", Lanes.Todo, null).Id;
            Verify = Board.AddCard("Alpha", null, "B004", "Verify the Save button", "spec", "queued", Lanes.Todo, null).Id;
        }

        public static async Task<string> Tool(RunContext run, string tool, object args, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
            };
            var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), ct);
            var result = JsonNode.Parse(reply.Json!)!["result"]!;
            var text = result["content"]![0]!["text"]!.GetValue<string>();
            return result["isError"]?.GetValue<bool>() == true ? "ERROR: " + text : text;
        }

        /// <summary>deliverable_complete through the tool; null when busy (a run still ending), else the reply (recorded). On ok, reads deliverable_show and deliverable_digest.</summary>
        public async Task<string?> Declare(RunContext run, CancellationToken ct)
        {
            var text = await Tool(run, "deliverable_complete", new { delivered = "the Save button shipped" }, ct);
            if (text.StartsWith("ERROR: refused (busy)", StringComparison.Ordinal)) return null;
            lock (Declares) Declares.Add(text);
            if (!text.StartsWith("ERROR", StringComparison.Ordinal))
            {
                Show = await Tool(run, "deliverable_show", new { }, ct);
                Digest = await Tool(run, "deliverable_digest", new { }, ct);
            }
            return text;
        }

        /// <summary>True once per new tester hand-back (the Manager may be woken more than once for one).</summary>
        public bool NewTesterVerdict()
        {
            var n = Volatile.Read(ref TesterRuns);
            if (n <= _handledTester) return false;
            _handledTester = n;
            return true;
        }

        /// <summary>A busy refusal: the verdict is handled on the next wake instead.</summary>
        public void NewTesterVerdictReset() => _handledTester--;

        public int Refusals(string code) { lock (Declares) return Declares.Count(d => d.StartsWith($"ERROR: refused ({code})", StringComparison.Ordinal)); }

        public bool Declared() { using var s = Db.OpenStore(); return s.DeliverableDeclared(Root); }

        public Message Completed() { using var s = Db.OpenStore(); return s.Deliverable(Root).Single(m => m.Type == MessageTypes.DeliverableCompleted); }

        public async Task<(Message Ask, DecisionAsk Parsed)> OpenDecision(long? notSeq = null)
        {
            IReadOnlyList<Message> open = [];
            await Wait.UntilAsync(async () => (open = await Engine.OpenDecisionsAsync("Alpha", Ct)).Count == 1 && open[0].Seq != notSeq, "an open decision");
            return (open[0], DecisionAsk.Read(open[0].Payload));
        }

        public async Task<JsonNode?> Answer(long seq, int? optionIndex, string? text, Actor? actor = null)
        {
            var p = new JsonObject { ["teamId"] = TeamId, ["seq"] = seq };
            if (optionIndex is { } i) p["optionIndex"] = i;
            if (text is not null) p["text"] = text;
            return await Rpc.DispatchAsAsync(actor ?? Actor.Window, "bus.answer", p, Ct);
        }

        public async Task<CardRow> CardAfterProjection(long id)
        {
            await BoardSvc.ProjectAsync(Ct);
            return Board.Card(id)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static bool IsRoot(RunContext run) => run.Items.Any(i => i.Source == MessageTypes.PersonSource && i.CausationSeq is null);

    private static bool FromAda(RunContext run) => run.Items.Any(i => i.Type == MessageTypes.Handback && i.Source == "Alpha/Ada");

    private static bool Answered(RunContext run) => run.Items.Any(i => i.Source == MessageTypes.PersonSource && i.CausationSeq is not null);

    private static string RefusalStart(long card, string failLine) =>
        $"ERROR: refused (tester_fail): deliverable completion refused: card {card}'s latest test is a FAIL: TesterGary handed back \"{failLine}\"";

    /// <summary>The Manager's usual opening: Ada builds the build card; on her hand-back the tester verifies the verify card.</summary>
    private static async Task<bool> Opening(Rig rig, RunContext run, CancellationToken ct)
    {
        if (IsRoot(run)) { await Rig.Tool(run, "tell", new { member = "Ada", instruction = "build the Save button", card = rig.Build }, ct); return true; }
        if (FromAda(run)) { await Rig.Tool(run, "tell", new { member = "TesterGary", instruction = "verify the Save button", card = rig.Verify }, ct); return true; }
        return false;
    }

    private static async Task Ship(Rig rig)
    {
        await rig.StartAsync("Ada", "TesterGary");
        rig.Root = (await rig.Engine.PersonTellAsync("Alpha", "Manager", "ship the Save button", ct: Ct)).Seq;
    }

    // ------------------------------------------------------------------ 1 and 2: FAIL refused, a later PASS allows

    [Fact]
    public async Task AFail_IsRefusedNamingTheCardAndQuotingTheLine_AndALaterPassAllowsIt_TheBoardIsPlain()
    {
        await using var rig = new Rig();
        rig.Runner.On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("done: the Save button is built", ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/TesterGary", async (run, ct) =>
        {
            var n = Interlocked.Increment(ref rig.TesterRuns);
            await run.HandbackAsync(n == 1 ? "**" + Fail1 + "**\n\n- clicked Save: nothing stored" : "PASS: card verify, Save stores the row", ct);
            return RunResult.Ok;
        });
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (await Opening(rig, run, ct)) return RunResult.Ok;
            if (!rig.NewTesterVerdict()) return RunResult.Ok;
            var t = await rig.Declare(run, ct);
            if (t is null) { rig.NewTesterVerdictReset(); return RunResult.Ok; }
            if (t.StartsWith("ERROR: refused (tester_fail)", StringComparison.Ordinal))
                await Rig.Tool(run, "tell", new { member = "Ada", instruction = "fix: Save stores nothing", card = rig.Build }, ct);
            return RunResult.Ok;
        });
        await Ship(rig);
        await Wait.UntilAsync(rig.Declared, "the declaration after the PASS");

        var refused = rig.Declares.First();
        // The leading ** is part of the first line quoted (FirstLine trims, does not strip Markdown).
        Assert.StartsWith(RefusalStart(rig.Verify, "**" + Fail1 + "**"), refused);
        Assert.DoesNotContain("nothing stored", refused);
        Assert.Contains($"have the tester verify card {rig.Verify} again", refused);
        Assert.Contains($"needs_decision, card {rig.Verify} and kind accept-fail", refused);
        Assert.True(rig.Refusals("tester_fail") >= 1);
        Assert.StartsWith("ok", rig.Declares.Last());
        Assert.Null(JsonNode.Parse(rig.Completed().Payload)![ClosedOverFailText.Field]);

        var verify = await rig.CardAfterProjection(rig.Verify);
        Assert.Equal(Lanes.Done, verify.Lane);
        Assert.DoesNotContain(rig.Board.Trail(rig.Verify), t => t.Kind is ClosedOverFailText.AcceptedTrailKind or ClosedOverFailText.WontDoTrailKind);
        Assert.DoesNotContain("closed over a FAIL", rig.Digest!);
        Assert.DoesNotContain("Closed over a FAIL", rig.Show!);
        Assert.Equal(("done", "**Alpha** finished: the Save button shipped"), ConciergeWatcher.Describe(rig.Completed(), "Alpha"));
    }

    // ------------------------------------------------------------------ 3: accept-fail, the person picks the yes

    [Fact]
    public async Task AcceptFail_ThePersonsPickedYesAllowsIt_BoardDigestShowAndConciergeSayIt()
    {
        await using var rig = new Rig();
        string? askReply = null, stillRefused = null;
        rig.Runner.On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("done: the Save button is built", ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/TesterGary", async (run, ct) => { Interlocked.Increment(ref rig.TesterRuns); await run.HandbackAsync(Fail1 + "\n\ndetails", ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (await Opening(rig, run, ct)) return RunResult.Ok;
            if (Answered(run)) { await rig.Declare(run, ct); return RunResult.Ok; }
            if (!rig.NewTesterVerdict()) return RunResult.Ok;
            if (await rig.Declare(run, ct) is null) { rig.NewTesterVerdictReset(); return RunResult.Ok; }
            askReply = await Rig.Tool(run, "needs_decision", new
            {
                summary = $"TesterGary failed card {rig.Verify} on the Save button; Save is out of scope: accept and close?",
                card = rig.Verify, kind = "accept-fail", reason = "Save is out of scope here",
            }, ct);
            // The question open, no yes yet: still refused.
            stillRefused = await Rig.Tool(run, "deliverable_complete", new { delivered = "the Save button shipped" }, ct);
            return RunResult.Ok;
        });
        await Ship(rig);
        var (ask, parsed) = await rig.OpenDecision();
        Assert.StartsWith("ok", askReply);
        Assert.StartsWith(RefusalStart(rig.Verify, Fail1), rig.Declares[0]);
        await Wait.UntilAsync(() => Volatile.Read(ref stillRefused) is not null, "the Manager's try with the question open");
        Assert.StartsWith(RefusalStart(rig.Verify, Fail1), stillRefused);
        Assert.Equal(new CloseOverFail(rig.Verify, CloseOverFail.Accepted, "Save is out of scope here"), parsed.Close);
        var yes = parsed.Options!.Select((o, i) => (o, i)).Single(x => x.o.Action == DecisionActions.CloseOverFail).i;

        var applied = await rig.Answer(ask.Seq, yes, null);
        Assert.Equal(DecisionActions.CloseOverFail, applied!["applied"]!.GetValue<string>());
        await Wait.UntilAsync(rig.Declared, "the declaration after the yes");

        var entry = Assert.Single((JsonNode.Parse(rig.Completed().Payload)![ClosedOverFailText.Field] as JsonArray)!)!;
        Assert.Equal(rig.Verify, entry["card"]!.GetValue<long>());
        Assert.Equal(Fail1, entry["failLine"]!.GetValue<string>());
        Assert.Equal("accepted", entry["kind"]!.GetValue<string>());

        var line = ClosedOverFailText.AcceptedPrefix + Fail1;
        var verify = await rig.CardAfterProjection(rig.Verify);
        Assert.Equal(Lanes.Done, verify.Lane);
        Assert.Single(rig.Board.Trail(rig.Verify), t => t.Kind == ClosedOverFailText.AcceptedTrailKind && t.Text == line);
        Assert.Equal(Lanes.Done, (await rig.CardAfterProjection(rig.Build)).Lane);

        Assert.Contains("== closed over a FAIL (accepted by the person) ==", rig.Digest!);
        var digestLine = rig.Digest!.Split('\n').Single(l => l.StartsWith($"- card {rig.Verify} (tester TesterGary): ", StringComparison.Ordinal));
        Assert.Contains(ClosedOverFailText.AcceptedPrefix, digestLine);
        Assert.Contains(Fail1, digestLine);
        Assert.Contains($"card {rig.Verify}: {line}", rig.Show!);
        Assert.Equal(("done", $"**Alpha** finished, {ClosedOverFailText.ConciergeWords}: the Save button shipped"), ConciergeWatcher.Describe(rig.Completed(), "Alpha"));
    }

    // ------------------------------------------------------------------ 4: won't-do

    [Fact]
    public async Task WontDo_RefusedWithoutAYes_AfterANo_AfterATypedYes_AndAConciergeYes_AllowedAfterThePickedYes()
    {
        await using var rig = new Rig();
        string? noReason = null;
        const string Reason = "Save moves to card 70";
        rig.Runner.On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("done: the Save button is built", ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/TesterGary", async (run, ct) => { Interlocked.Increment(ref rig.TesterRuns); await run.HandbackAsync(Fail1, ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (await Opening(rig, run, ct)) return RunResult.Ok;
            if (!Answered(run) && !rig.NewTesterVerdict()) return RunResult.Ok;
            var t = await rig.Declare(run, ct);
            if (t is null) { rig.NewTesterVerdictReset(); return RunResult.Ok; }
            if (!t.StartsWith("ERROR: refused (tester_fail)", StringComparison.Ordinal)) return RunResult.Ok;
            noReason ??= await Rig.Tool(run, "needs_decision", new { summary = $"Card {rig.Verify} is won't-do?", card = rig.Verify, kind = "wont-do" }, ct);
            await Rig.Tool(run, "needs_decision", new { summary = $"Card {rig.Verify} is won't-do: Save moves to card 70. Agree?", card = rig.Verify, kind = "wont-do", reason = Reason }, ct);
            return RunResult.Ok;
        });
        await Ship(rig);

        var (q1, p1) = await rig.OpenDecision();
        Assert.StartsWith("ERROR: refused (bad_decision)", noReason); // a won't-do needs its reason
        Assert.Equal(1, rig.Refusals("tester_fail"));
        Assert.Equal(CloseOverFail.WontDo, p1.Close!.Kind);
        var yes = p1.Options!.Select((o, i) => (o, i)).Single(x => x.o.Action == DecisionActions.CloseOverFail).i;
        var no = p1.Options!.Select((o, i) => (o, i)).First(x => x.o.Action is null).i;

        // The person's "no" (the option without the action): refused, asked again.
        await rig.Answer(q1.Seq, no, null);
        var (q2, _) = await rig.OpenDecision(q1.Seq);
        Assert.Equal(2, rig.Refusals("tester_fail"));

        // The yes option's own reply, typed (no optionIndex): not the yes.
        await rig.Answer(q2.Seq, null, p1.Options![yes].Reply);
        var (q3, _) = await rig.OpenDecision(q2.Seq);
        Assert.Equal(3, rig.Refusals("tester_fail"));

        // The Concierge: may not pick the yes option, and its text answer "Yes" is not the yes.
        var concierge = new Actor(Actor.Concierge, Actor.LocalUser);
        var forbidden = await Assert.ThrowsAsync<RpcException>(() => rig.Answer(q3.Seq, yes, null, concierge));
        Assert.Equal("forbidden", forbidden.Code);
        await rig.Answer(q3.Seq, null, "Yes", concierge);
        var (q4, _) = await rig.OpenDecision(q3.Seq);
        Assert.Equal(4, rig.Refusals("tester_fail"));
        Assert.False(rig.Declared());

        await rig.Answer(q4.Seq, yes, null);
        await Wait.UntilAsync(rig.Declared, "the declaration after the picked yes");
        var entry = Assert.Single((JsonNode.Parse(rig.Completed().Payload)![ClosedOverFailText.Field] as JsonArray)!)!;
        Assert.Equal(("wont-do", Reason), (entry["kind"]!.GetValue<string>(), entry["reason"]!.GetValue<string>()));

        var line = ClosedOverFailText.WontDoPrefix + Reason;
        Assert.Equal(Lanes.Done, (await rig.CardAfterProjection(rig.Verify)).Lane);
        Assert.Single(rig.Board.Trail(rig.Verify), t => t.Kind == ClosedOverFailText.WontDoTrailKind && t.Text == line);
        Assert.Contains($"card {rig.Verify}: {line}", rig.Show!);
        Assert.Contains(ClosedOverFailText.WontDoPrefix, rig.Digest!);
        Assert.Contains(Reason, rig.Digest!);
    }

    // ------------------------------------------------------------------ 5: no tester card, a developer's FAIL

    [Fact]
    public async Task NoTesterCard_ADevelopersFail_AndATesterFailWithoutACard_AreUnaffected()
    {
        await using var rig = new Rig();
        rig.Runner.On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync(Fail1, ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/TesterGary", async (run, ct) => { await run.HandbackAsync(Fail2, ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (IsRoot(run))
            {
                await Rig.Tool(run, "tell", new { member = "Ada", instruction = "build the Save button", card = rig.Build }, ct);
                await Rig.Tool(run, "tell", new { member = "TesterGary", instruction = "look around the app" }, ct); // no card
                return RunResult.Ok;
            }
            await rig.Declare(run, ct);
            return RunResult.Ok;
        });
        await Ship(rig);
        await Wait.UntilAsync(rig.Declared, "the declaration");
        Assert.Equal(0, rig.Refusals("tester_fail"));
        Assert.Null(JsonNode.Parse(rig.Completed().Payload)![ClosedOverFailText.Field]);
        await rig.CardAfterProjection(rig.Build);
        Assert.DoesNotContain(rig.Board.Trail(rig.Build), t => t.Kind is ClosedOverFailText.AcceptedTrailKind or ClosedOverFailText.WontDoTrailKind);
    }

    [Fact]
    public async Task ADeliverableWithNoTesterAtAll_IsUnaffected()
    {
        await using var rig = new Rig();
        await rig.StartAsync("Ada", "Bea");
        rig.Runner.Default = async (run, ct) => { await run.HandbackAsync("FAIL: nothing works", ct); return RunResult.Ok; };
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (IsRoot(run)) { await Rig.Tool(run, "tell", new { member = "Bea", instruction = "verify it", card = rig.Verify }, ct); return RunResult.Ok; }
            await rig.Declare(run, ct);
            return RunResult.Ok;
        });
        rig.Root = (await rig.Engine.PersonTellAsync("Alpha", "Manager", "ship it", ct: Ct)).Seq;
        await Wait.UntilAsync(rig.Declared, "the declaration");
        Assert.Equal(0, rig.Refusals("tester_fail"));
    }

    // ------------------------------------------------------------------ 6: a newer FAIL after a yes needs a new yes

    [Fact]
    public async Task ANewerFailAfterAYes_NeedsANewYes()
    {
        await using var rig = new Rig();
        rig.Runner.On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("done: the Save button is built", ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/TesterGary", async (run, ct) =>
        {
            var n = Interlocked.Increment(ref rig.TesterRuns);
            await run.HandbackAsync(n == 1 ? Fail1 : Fail2, ct);
            return RunResult.Ok;
        });
        var answers = 0;
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (await Opening(rig, run, ct)) return RunResult.Ok;
            if (Answered(run))
            {
                // After the first yes the Manager has the tester look once more; after the second it declares.
                if (Interlocked.Increment(ref answers) == 1)
                    await Rig.Tool(run, "tell", new { member = "TesterGary", instruction = "one more look", card = rig.Verify }, ct);
                else await rig.Declare(run, ct);
                return RunResult.Ok;
            }
            if (!rig.NewTesterVerdict()) return RunResult.Ok;
            var t = await rig.Declare(run, ct);
            if (t is null) { rig.NewTesterVerdictReset(); return RunResult.Ok; }
            if (t.StartsWith("ERROR: refused (tester_fail)", StringComparison.Ordinal))
                await Rig.Tool(run, "needs_decision", new { summary = $"Accept card {rig.Verify}'s FAIL and close?", card = rig.Verify, kind = "accept-fail", reason = "out of scope" }, ct);
            return RunResult.Ok;
        });
        await Ship(rig);

        var (q1, p1) = await rig.OpenDecision();
        var yes = p1.Options!.Select((o, i) => (o, i)).Single(x => x.o.Action == DecisionActions.CloseOverFail).i;
        await rig.Answer(q1.Seq, yes, null);

        // The tester's second FAIL comes after the yes: refused again, quoting the new line.
        var (q2, _) = await rig.OpenDecision(q1.Seq);
        Assert.Equal(2, rig.Refusals("tester_fail"));
        Assert.StartsWith(RefusalStart(rig.Verify, Fail2), rig.Declares[1]);
        Assert.False(rig.Declared());

        await rig.Answer(q2.Seq, yes, null);
        await Wait.UntilAsync(rig.Declared, "the declaration after the second yes");
        var entry = Assert.Single((JsonNode.Parse(rig.Completed().Payload)![ClosedOverFailText.Field] as JsonArray)!)!;
        Assert.Equal(Fail2, entry["failLine"]!.GetValue<string>());
        await rig.CardAfterProjection(rig.Verify);
        Assert.Single(rig.Board.Trail(rig.Verify), t => t.Text == ClosedOverFailText.AcceptedPrefix + Fail2);
    }

    // ------------------------------------------------------------------ the verdict words, through a real hand-back

    [Theory]
    [InlineData("> FAILED: card verify", true)]
    [InlineData("`fail` card verify", true)]
    [InlineData("__Fail__ card verify", true)]
    [InlineData("# PASS", false)]
    [InlineData("Verified: one FAIL found", false)]
    public void TheVerdictWords(string delivered, bool fail) =>
        Assert.Equal(fail ? TesterVerdicts.Fail : delivered.Contains("PASS") ? TesterVerdicts.Pass : null, TesterVerdicts.Of(delivered));
}
