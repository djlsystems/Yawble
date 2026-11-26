using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// A message the projector cannot apply does not stop the board: it is skipped with a warning that names its seq,
/// what it had written is undone, and the messages after it are applied. The message here is a run's end whose
/// "activity" is a number: the projector has moved the card before it reads the activity and throws.
/// </summary>
public class BoardProjectionGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusStore Bus { get; private set; } = null!;
        public BusEngine Engine { get; private set; } = null!;
        public BoardStore Store { get; private set; } = null!;
        public BoardService Board { get; private set; } = null!;

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig();
            rig.Bus = new BusStore(rig.Db.ConnectionString);
            rig.Engine = new BusEngine(rig.Bus, new ScriptedRunner(), new BusOptions { IdleOffer = false });
            await rig.Engine.CreateTeamAsync("payments", ct: Ct);
            await rig.Engine.AddMemberAsync("payments", "Ada", ct: Ct);
            rig.Store = new BoardStore(rig.Db.ConnectionString);
            rig.Board = new BoardService(rig.Store, rig.Engine);
            return rig;
        }

        public long Tell(string text) =>
            Bus.Append(new NewMessage(MessageTypes.InstructionPrefix + "payments/Ada", $$"""{"text":"{{text}}"}""", "payments/Manager", null)).Seq;

        public long Say(long deliverable, string type, string payload) =>
            Bus.Append(new NewMessage(type, payload, "payments/Ada", deliverable)).Seq;

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private const string BadRunEnd = """{"reason":"ok","activity":5}""";

    [Fact]
    public async Task AMessageTheProjectorCannotApply_IsSkippedWithAWarning_AndTheRestOfThePageIsApplied()
    {
        var entries = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (entries) entries.Add(e); });
        await using var rig = await Rig.CreateAsync();
        var first = rig.Tell("Add a README");
        rig.Say(first, MessageTypes.Progress, """{"status":"writing"}""");
        var bad = rig.Say(first, MessageTypes.Completed, BadRunEnd);
        var second = rig.Tell("Add a licence");
        var last = rig.Say(second, MessageTypes.Handback, """{"delivered":"LICENSE on the branch"}""");

        var read = await rig.Board.ProjectAsync(Ct);

        Assert.Equal(5, read);
        Assert.Equal(last, rig.Store.Cursor());
        var cards = rig.Store.Cards("payments").ToDictionary(c => c.Title);
        // The bad message left nothing: the first card is as the progress report left it, with no run-ended trail line.
        Assert.Equal(("running", Lanes.InProgress), (cards["Add a README"].Status, cards["Add a README"].Lane));
        Assert.DoesNotContain(rig.Store.Trail(cards["Add a README"].Id), t => t.Kind == "run ended");
        // The messages after it reached the board.
        Assert.Equal(("handback", Lanes.Done), (cards["Add a licence"].Status, cards["Add a licence"].Lane));

        DiagEntry warning;
        lock (entries) warning = Assert.Single(entries, e => e.Area == "board");
        Assert.Equal(LogLevels.Warning, warning.Level);
        Assert.Equal(bad.ToString(), warning.Field("seq"));
        Assert.Equal(MessageTypes.Completed, warning.Field("type"));

        // Nothing is left to read, and the board goes on with what comes next.
        Assert.Equal(0, await rig.Board.ProjectAsync(Ct));
        rig.Say(first, MessageTypes.Handback, """{"delivered":"README on the branch"}""");
        Assert.Equal(1, await rig.Board.ProjectAsync(Ct));
        Assert.Equal("handback", rig.Store.Card(cards["Add a README"].Id)!.Status);
        lock (entries) Assert.Single(entries, e => e.Area == "board");
    }

    [Fact]
    public void AUnitThatThrows_IsUndoneAlone_AndTheBatchGoesOn()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);

        store.InBatch(() =>
        {
            store.AddCard("payments", "Ada", null, "Before", "", "queued", Lanes.Todo, 1);
            Assert.Throws<InvalidOperationException>(() => store.InUnit("message", () =>
            {
                store.AddCard("payments", "Ada", null, "Half applied", "", "queued", Lanes.Todo, 2);
                throw new InvalidOperationException("the projector could not go on");
            }));
            store.InUnit("message", () => store.AddCard("payments", "Ada", null, "After", "", "queued", Lanes.Todo, 3));
            store.SetCursor(9);
        });

        Assert.Equal(["After", "Before"], store.Cards("payments").Select(c => c.Title).Order());
        Assert.Equal(9, store.Cursor());
    }
}
