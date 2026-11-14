using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// B019: the Manager moving a tester's card to Done before the tester has run on it gives the reason in note, and the
/// reason is a comment on the card; without one the move is refused. Teams with no tester, and members' moves, are as before.
/// </summary>
public class BoardTesterDoneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A team "payments" with the given members; <paramref name="act"/> runs inside the named member's run.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BoardStore Store { get; private set; } = null!;
        public BusEngine Bus { get; private set; } = null!;
        private BoardService _service = null!;
        private Func<RunContext, Task>? _act;
        private TaskCompletionSource? _done;

        public static async Task<Rig> StartAsync(params (string Name, string Job)[] members)
        {
            var rig = new Rig();
            rig.Store = new BoardStore(rig.Db.ConnectionString);
            var runner = new ScriptedRunner();
            foreach (var who in members.Select(m => m.Name).Prepend("Manager"))
                runner.On("payments/" + who, async (run, ct) =>
                {
                    try { if (rig._act is { } a) await a(run); rig._done?.TrySetResult(); }
                    catch (Exception e) { rig._done?.TrySetException(e); }
                    return RunResult.Ok;
                });
            rig.Bus = new BusEngine(rig.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            rig._service = new BoardService(rig.Store, rig.Bus);
            var teamId = await rig.Bus.CreateTeamAsync("payments", "Manager", "claude", null, Ct);
            foreach (var (name, job) in members)
            {
                var row = await rig.Bus.AddMemberAsync(teamId, name, "claude", Ct);
                if (job.Length > 0) await rig.Bus.UpdateMemberAsync(row.Id, row.Cli, job, row.Model, row.Credential, Ct);
            }
            await rig.Bus.StartAsync(Ct);
            return rig;
        }

        /// <summary>Runs <paramref name="act"/> in one run of <paramref name="member"/>, woken by the person.</summary>
        public async Task InRunOf(string member, Func<RunContext, BoardService, Task> act)
        {
            _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _act = run => act(run, _service);
            await Bus.PersonTellAsync("payments", member, "go", ct: Ct);
            await _done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            _act = null;
        }

        /// <summary>The Manager's board move, as its tool call: the result, or "refused code: message".</summary>
        public async Task<string> Move(string member, long card, string lane, string? note = null)
        {
            string? said = null;
            await InRunOf(member, async (run, service) =>
            {
                var args = new JsonObject { ["action"] = "move", ["card"] = card, ["lane"] = lane };
                if (note is not null) args["note"] = note;
                try { said = await service.BoardToolAsync(run, args, Ct); }
                catch (BusRefusal e) { said = $"refused {e.Code}: {e.Message}"; }
            });
            return said!;
        }

        public CardRow Card(string member) =>
            Store.AddCard("payments", member, null, "Check the login page", "spec", "queued", Lanes.InProgress, null);

        public async ValueTask DisposeAsync()
        {
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task AnUntestedTesterCard_MovedToDoneByTheManager_IsRefusedWithoutAReason()
    {
        await using var rig = await Rig.StartAsync(("DeveloperPriya", ""), ("TesterWren", ""));
        var card = rig.Card("TesterWren");

        var said = await rig.Move("Manager", card.Id, "done");
        Assert.StartsWith("refused needs_reason:", said);
        Assert.Contains($"tell (card: {card.Id})", said);
        Assert.Contains("note", said);
        Assert.Contains("no tester on this team", said);
        Assert.Equal(Lanes.InProgress, rig.Store.Card(card.Id)!.Lane);
        Assert.DoesNotContain(rig.Store.Trail(card.Id), t => t.Kind == "moved");

        Assert.StartsWith("refused needs_reason:", await rig.Move("Manager", card.Id, "done", "   ")); // blank is no reason
        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "blocked")); // other lanes: unchanged
    }

    [Fact]
    public async Task WithAReason_TheMoveIsAllowed_AndTheReasonIsACommentOnTheCard()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var card = rig.Card("TesterWren");

        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done", "the person asked to skip testing"));
        Assert.Equal(Lanes.Done, rig.Store.Card(card.Id)!.Lane);
        var trail = rig.Store.Trail(card.Id);
        Assert.Contains(trail, t => t.Kind == "moved" && t.Source == "Manager" && t.Text.Contains("the person asked to skip testing"));
        Assert.Contains(trail, t => t.Kind == "comment" && t.Source == "Manager" && t.Text.Contains("the person asked to skip testing"));
    }

    [Fact]
    public async Task ACardOnceAssignedToATester_ByJobText_NeedsAReasonToo()
    {
        // Not named Tester, but its job is: and the card was given to it, then to someone else.
        await using var rig = await Rig.StartAsync(("Wren", "Tester: checks the developers' work"), ("DeveloperPriya", ""));
        var card = rig.Card("DeveloperPriya");
        rig.Store.AddTrail(card.Id, "Manager", "assigned", "Assigned to Wren: check card");
        Assert.StartsWith("refused needs_reason:", await rig.Move("Manager", card.Id, "done"));
    }

    [Fact]
    public async Task OnceTheTesterHasRunOnTheCard_TheMoveNeedsNoReason()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        // The tester is given work (its card) and runs on it: the projector writes its report on the trail under its name.
        await rig.InRunOf("TesterWren", (run, _) => run.ProgressAsync("checking the login page", Ct));
        await rig.InRunOf("Manager", (_, service) => service.ProjectAsync(Ct));
        var card = rig.Store.Cards("payments").Single(c => c.Member == "TesterWren");
        rig.Store.Save(card with { Lane = Lanes.InProgress, Placed = true });
        Assert.Contains(rig.Store.Trail(card.Id), t => t.Source == "TesterWren");

        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done"));
        Assert.Equal(Lanes.Done, rig.Store.Card(card.Id)!.Lane);
        Assert.DoesNotContain(rig.Store.Trail(card.Id), t => t.Kind == "comment");
    }

    [Fact]
    public async Task ATeamWithoutATester_AndAMembersOwnMove_AreUnchanged()
    {
        await using (var rig = await Rig.StartAsync(("DeveloperPriya", "Developer, desktop core")))
        {
            var card = rig.Card("DeveloperPriya");
            Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done"));
            Assert.DoesNotContain(rig.Store.Trail(card.Id), t => t.Kind == "comment");
        }
        await using (var rig = await Rig.StartAsync(("TesterWren", "")))
        {
            var card = rig.Card("TesterWren");
            Assert.StartsWith("ok:", await rig.Move("TesterWren", card.Id, "done")); // the tester's own move
        }
        await using (var rig = await Rig.StartAsync(("DeveloperPriya", ""), ("TesterWren", "")))
        {
            var card = rig.Card("DeveloperPriya"); // a developer's card on a team with a tester
            Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done"));
        }
    }
}
