using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// Next to TeamDeliverableTests' declaration rules: deliverable_complete is refused (tester_fail) while a card's
/// latest tester hand-back in the deliverable starts with FAIL. A later PASS clears it; so does the person's yes to a
/// needs_decision tied to the card (card, kind accept-fail or wont-do), and the declaration then lists the card in
/// closedOverFail. Fake runners only.
/// </summary>
public class TesterFailDeclarationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string FailLine = "FAIL: card 61 the Save button does nothing";

    /// <summary>One declaration try: "ok" with the DeliverableCompleted message, or the refusal.</summary>
    private sealed record Outcome(string Code, string Text, Message? Declared);

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public readonly ScriptedRunner Runner = new();
        public readonly List<Outcome> Outcomes = [];
        public BusEngine Engine = null!;

        public async Task StartAsync(params string[] members)
        {
            Engine = new BusEngine(Db.OpenStore(), Runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200), Retros = false });
            await Engine.CreateTeamAsync("Alpha", ct: Ct);
            foreach (var m in members) await Engine.AddMemberAsync("Alpha", m, ct: Ct);
            await Engine.StartAsync(Ct);
        }

        /// <summary>The Manager's try at declaring: a busy refusal (the tester's run still ending) is not recorded.</summary>
        public async Task<Outcome?> TryDeclare(RunContext run, CancellationToken ct)
        {
            Outcome o;
            try { o = new Outcome("ok", "", await run.DeliverableCompleteAsync("card 61 delivered", ct)); }
            catch (BusRefusal r) when (r.Code == "busy") { return null; }
            catch (BusRefusal r) { o = new Outcome(r.Code, r.Message, null); }
            lock (Outcomes) Outcomes.Add(o);
            return o;
        }

        public int Count(string code) { lock (Outcomes) return Outcomes.Count(o => o.Code == code); }

        public Outcome Declared() { lock (Outcomes) return Outcomes.Single(o => o.Code == "ok"); }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static bool IsRoot(RunContext run) => run.Items.Any(i => i.Source == MessageTypes.PersonSource && i.CausationSeq is null);

    private static JsonArray? ClosedOverFail(Message declared) => JsonNode.Parse(declared.Payload)!["closedOverFail"] as JsonArray;

    [Fact]
    public async Task AFailOutstandingIsRefused_NamingTheCard_AndALaterPassAllowsIt()
    {
        await using var rig = new Rig();
        var garyRuns = 0;
        rig.Runner.On("Alpha/TesterGary", async (run, ct) =>
        {
            var n = Interlocked.Increment(ref garyRuns);
            await run.HandbackAsync(n == 1 ? FailLine + "\n\n- clicked Save, nothing stored" : "PASS: card 61 verified, Save stores the row", ct);
            return RunResult.Ok;
        });
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (IsRoot(run)) { await run.TellAsync("TesterGary", "verify card 61", 61, ct); return RunResult.Ok; }
            if (await rig.TryDeclare(run, ct) is { Code: "tester_fail" } && rig.Count("tester_fail") == 1)
                await run.TellAsync("TesterGary", "fixed: verify card 61 again", 61, ct);
            return RunResult.Ok;
        });
        await rig.StartAsync("TesterGary");
        var root = await rig.Engine.PersonTellAsync("Alpha", "Manager", "ship card 61", ct: Ct);
        using var reader = rig.Db.OpenStore();
        await Wait.UntilAsync(() => reader.DeliverableDeclared(root.Seq), "the declaration after the PASS");

        Outcome refused;
        lock (rig.Outcomes) refused = rig.Outcomes.First(o => o.Code != "ok");
        Assert.Equal("tester_fail", refused.Code);
        Assert.Contains("card 61", refused.Text);
        Assert.Contains($"TesterGary handed back \"{FailLine}\"", refused.Text); // the first line, quoted
        Assert.DoesNotContain("nothing stored", refused.Text);
        Assert.Contains("have the tester verify card 61 again", refused.Text);
        Assert.Contains("needs_decision, card 61 and kind accept-fail", refused.Text);
        Assert.Contains("accept and close?", refused.Text);
        Assert.Contains("won't-do", refused.Text);
        Assert.Contains("also needs the person's yes", refused.Text);
        Assert.Null(ClosedOverFail(rig.Declared().Declared!)); // a PASS clears it: nothing closed over a FAIL
        Assert.Equal(1, rig.Count("tester_fail"));
    }

    [Fact]
    public async Task ThePersonsYesAllowsIt_AndThePayloadRecordsIt()
    {
        await using var rig = new Rig();
        string? noFail = null;
        rig.Runner.On("Alpha/TesterGary", async (run, ct) => { await run.HandbackAsync("  **fail**: card 61 the Save button does nothing", ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (IsRoot(run)) { await run.TellAsync("TesterGary", "verify card 61", 61, ct); return RunResult.Ok; }
            if (await rig.TryDeclare(run, ct) is { Code: "tester_fail" } && rig.Count("tester_fail") == 1)
            {
                try { await run.NeedsDecisionAsync(DecisionAsk.Parse(null, "Accept card 62?", null, null, 62, "accept-fail", "x"), ct); }
                catch (BusRefusal r) { noFail = r.Code; }
                await run.NeedsDecisionAsync(DecisionAsk.Parse(null,
                    "Gary failed card 61 on the Save button; I think the criterion is too strict because Save is out of scope: accept and close?",
                    null, "Save lands in card 63.", 61, "accept-fail", "Save is out of scope for card 61"), ct);
            }
            return RunResult.Ok;
        });
        await rig.StartAsync("TesterGary");
        var root = await rig.Engine.PersonTellAsync("Alpha", "Manager", "ship card 61", ct: Ct);
        IReadOnlyList<Message> open = [];
        await Wait.UntilAsync(async () => (open = await rig.Engine.OpenDecisionsAsync("Alpha", Ct)).Count == 1, "the Manager's question");

        Assert.Equal("no_fail", noFail); // a card with no FAIL has nothing to close over
        var ask = DecisionAsk.Read(open[0].Payload);
        Assert.Equal(new CloseOverFail(61, CloseOverFail.Accepted, "Save is out of scope for card 61"), ask.Close);
        Assert.Equal(DecisionActions.CloseOverFail, ask.Options![0].Action); // the app's yes, the no has no action
        Assert.Null(ask.Options[1].Action);

        await rig.Engine.PersonAnswerAsync("Alpha", "Manager", ask.Options[0].Reply, open[0].Seq, ct: Ct, action: DecisionActions.CloseOverFail);
        using var reader = rig.Db.OpenStore();
        await Wait.UntilAsync(() => reader.DeliverableDeclared(root.Seq), "the declaration after the yes");

        var answer = reader.Deliverable(root.Seq).Single(m => m.Source == MessageTypes.PersonSource && m.CausationSeq == open[0].Seq);
        Assert.Equal(open[0].Seq, JsonNode.Parse(answer.Payload)!["closeOverFail"]!["decision"]!.GetValue<long>());
        var closed = Assert.Single(ClosedOverFail(rig.Declared().Declared!)!)!.AsObject();
        Assert.Equal(61, closed["card"]!.GetValue<long>());
        Assert.Equal("Alpha/TesterGary", closed["tester"]!.GetValue<string>());
        Assert.Equal("**fail**: card 61 the Save button does nothing", closed["failLine"]!.GetValue<string>());
        Assert.Equal("accepted", closed["kind"]!.GetValue<string>());
        Assert.Equal("Save is out of scope for card 61", closed["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task WontDoNeedsAYes()
    {
        // A won't-do needs a reason, and kind goes with card; the close action needs a card.
        Assert.Equal("bad_decision", Assert.Throws<BusRefusal>(() => DecisionAsk.Parse(null, "Drop card 61?", null, null, 61, "wont-do", null)).Code);
        Assert.Equal("bad_decision", Assert.Throws<BusRefusal>(() => DecisionAsk.Parse(null, "Drop it?", null, null, null, "wont-do", "x")).Code);
        Assert.Equal("bad_decision", Assert.Throws<BusRefusal>(() => DecisionAsk.Parse(null, "Drop card 61?", null, null, 61, "maybe", "x")).Code);
        var yesWithoutCard = new JsonArray(
            new JsonObject { ["label"] = "Yes", ["reply"] = "yes", ["action"] = DecisionActions.CloseOverFail },
            new JsonObject { ["label"] = "No", ["reply"] = "no" });
        Assert.Equal("bad_decision", Assert.Throws<BusRefusal>(() => DecisionAsk.Parse(null, "Close?", yesWithoutCard, null)).Code);
        var noYes = new JsonArray(new JsonObject { ["label"] = "A", ["reply"] = "a" }, new JsonObject { ["label"] = "B", ["reply"] = "b" });
        Assert.Equal("bad_decision", Assert.Throws<BusRefusal>(() => DecisionAsk.Parse(null, "Close?", noYes, null, 61, "wont-do", "x")).Code);

        await using var rig = new Rig();
        rig.Runner.On("Alpha/TesterGary", async (run, ct) => { await run.HandbackAsync(FailLine, ct); return RunResult.Ok; });
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (IsRoot(run)) { await run.TellAsync("TesterGary", "verify card 61", 61, ct); return RunResult.Ok; }
            // Every refusal: ask for the won't-do (again, after a "no").
            if (await rig.TryDeclare(run, ct) is { Code: "tester_fail" })
                await run.NeedsDecisionAsync(DecisionAsk.Parse(null, "Card 61 is won't-do: the Save button moves to card 63. Agree?",
                    null, null, 61, "wont-do", "Save moves to card 63"), ct);
            return RunResult.Ok;
        });
        await rig.StartAsync("TesterGary");
        var root = await rig.Engine.PersonTellAsync("Alpha", "Manager", "ship card 61", ct: Ct);
        IReadOnlyList<Message> open = [];
        await Wait.UntilAsync(async () => (open = await rig.Engine.OpenDecisionsAsync("Alpha", Ct)).Count == 1, "the won't-do question");
        var first = DecisionAsk.Read(open[0].Payload);
        Assert.Equal(CloseOverFail.WontDo, first.Close!.Kind);

        // The person says no (the option without the action): still refused, and asked again.
        await rig.Engine.PersonAnswerAsync("Alpha", "Manager", first.Options![1].Reply, open[0].Seq, ct: Ct, action: first.Options[1].Action);
        var firstSeq = open[0].Seq;
        await Wait.UntilAsync(async () => (open = await rig.Engine.OpenDecisionsAsync("Alpha", Ct)).Count == 1 && open[0].Seq != firstSeq, "the second question");
        Assert.Equal(2, rig.Count("tester_fail"));
        using var reader = rig.Db.OpenStore();
        Assert.False(reader.DeliverableDeclared(root.Seq));

        // Text alone (as the Concierge answers) is not the yes either: only the picked yes option is.
        await rig.Engine.PersonAnswerAsync("Alpha", "Manager", "Yes: card 61 is won't-do. Close the deliverable.", open[0].Seq, ct: Ct);
        var secondSeq = open[0].Seq;
        await Wait.UntilAsync(async () => (open = await rig.Engine.OpenDecisionsAsync("Alpha", Ct)).Count == 1 && open[0].Seq != secondSeq, "the third question");
        Assert.Equal(3, rig.Count("tester_fail"));

        await rig.Engine.PersonAnswerAsync("Alpha", "Manager", "Yes", open[0].Seq, ct: Ct, action: DecisionActions.CloseOverFail);
        await Wait.UntilAsync(() => reader.DeliverableDeclared(root.Seq), "the declaration after the yes");
        var closed = Assert.Single(ClosedOverFail(rig.Declared().Declared!)!)!.AsObject();
        Assert.Equal("wont-do", closed["kind"]!.GetValue<string>());
        Assert.Equal("Save moves to card 63", closed["reason"]!.GetValue<string>());
        Assert.Equal(FailLine, closed["failLine"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADeliverableWithNoTesterCardIsUnaffected()
    {
        await using var rig = new Rig();
        rig.Runner.Default = async (run, ct) => { await run.HandbackAsync(FailLine, ct); return RunResult.Ok; };
        rig.Runner.On("Alpha/Manager", async (run, ct) =>
        {
            if (IsRoot(run))
            {
                await run.TellAsync("Ada", "build card 61", 61, ct);       // a developer's FAIL on a card is no tester verdict
                await run.TellAsync("TesterGary", "look around", ct);         // a tester's FAIL with no card belongs to no card
                return RunResult.Ok;
            }
            await rig.TryDeclare(run, ct);
            return RunResult.Ok;
        });
        await rig.StartAsync("Ada", "TesterGary");
        var root = await rig.Engine.PersonTellAsync("Alpha", "Manager", "ship card 61", ct: Ct);
        using var reader = rig.Db.OpenStore();
        await Wait.UntilAsync(() => reader.DeliverableDeclared(root.Seq), "the declaration");

        Assert.Equal(0, rig.Count("tester_fail"));
        Assert.Null(ClosedOverFail(rig.Declared().Declared!));
    }

    [Theory]
    [InlineData("PASS: all good", "PASS")]
    [InlineData("  pass - all good", "PASS")]
    [InlineData("Passed: card 61", "PASS")]
    [InlineData("FAIL", "FAIL")]
    [InlineData("**FAIL**: card 61", "FAIL")]
    [InlineData("## Failed on card 61", "FAIL")]
    [InlineData("> FAILED: card verify", "FAIL")]
    [InlineData("`fail` card verify", "FAIL")]
    [InlineData("__Fail__ card verify", "FAIL")]
    [InlineData("# PASS", "PASS")]
    [InlineData("Verified: one FAIL found", null)]
    [InlineData("Passing the work back", null)]
    [InlineData("Failure analysis", null)]
    [InlineData("Card 61: FAIL", null)]
    [InlineData("", null)]
    public void TheVerdictIsHowTheHandBackStarts(string delivered, string? verdict) => Assert.Equal(verdict, TesterVerdicts.Of(delivered));
}
