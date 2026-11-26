using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// A move of a tester's card to Done by anyone but that tester, before the tester has tested it, gives the reason in
/// note, and the reason is a comment on the card; without one the move is refused. The person's own move, a team with
/// no tester and a member's move of its own card need no reason. Which member is a tester is in BoardTesterRuleTests.
/// </summary>
public class BoardTesterDoneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A team "payments" with the given members; an action runs inside the named member's run.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BoardStore Store { get; private set; } = null!;
        public BusEngine Bus { get; private set; } = null!;
        public BoardService Service { get; private set; } = null!;
        private Func<RunContext, Task<bool>>? _act;
        private TaskCompletionSource? _done;

        public static async Task<Rig> StartAsync(params (string Name, string Job)[] members)
        {
            var rig = new Rig();
            rig.Store = new BoardStore(rig.Db.ConnectionString);
            var runner = new ScriptedRunner();
            foreach (var who in members.Select(m => m.Name).Prepend("Manager"))
                runner.On("payments/" + who, async (run, ct) =>
                {
                    try { if (rig._act is { } a && await a(run)) rig._done?.TrySetResult(); }
                    catch (Exception e) { rig._done?.TrySetException(e); }
                    return RunResult.Ok;
                });
            rig.Bus = new BusEngine(rig.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            rig.Service = new BoardService(rig.Store, rig.Bus);
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
            _act = async run => { if (run.Member.Name != member) return false; await act(run, Service); return true; }; // only the named member's run
            await Bus.PersonTellAsync("payments", member, "go", ct: Ct);
            await _done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            _act = null;
        }

        /// <summary>A board tool call made in the member's run: the result, or "refused code: message".</summary>
        public async Task<string> Tool(string member, JsonObject args)
        {
            string? said = null;
            await InRunOf(member, async (run, service) =>
            {
                try { said = await service.BoardToolAsync(run, args, Ct); }
                catch (BusRefusal e) { said = $"refused {e.Code}: {e.Message}"; }
            });
            return said!;
        }

        public Task<string> Move(string member, long card, string lane, string? note = null)
        {
            var args = new JsonObject { ["action"] = "move", ["card"] = card, ["lane"] = lane };
            if (note is not null) args["note"] = note;
            return Tool(member, args);
        }

        public CardRow Card(string? member, string lane = Lanes.InProgress) =>
            Store.AddCard("payments", member, null, "Check the login page", "spec", "queued", lane, null);

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
        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "blocked")); // other lanes need none
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
        // Its hand-back (not a progress note) is what counts.
        await rig.InRunOf("TesterWren", (run, _) => run.HandbackAsync("checked the login page: 3 passed", Ct));
        await rig.InRunOf("Manager", (_, service) => service.ProjectAsync(Ct));
        var card = rig.Store.Cards("payments").Single(c => c.Member == "TesterWren");
        rig.Store.Save(card with { Lane = Lanes.InProgress, Placed = true });
        Assert.Contains(rig.Store.Trail(card.Id), t => t.Source == "TesterWren");

        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done"));
        Assert.Equal(Lanes.Done, rig.Store.Card(card.Id)!.Lane);
        Assert.DoesNotContain(rig.Store.Trail(card.Id), t => t.Kind == "comment");
    }

    [Fact]
    public async Task ATeamWithoutATester_AndAMembersOwnMove_NeedNoReason()
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

    [Fact]
    public async Task ThePersonsOwnMove_OfAnUntestedTesterCardToDone_NeedsNoReason()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var card = rig.Card("TesterWren");

        await rig.Service.DispatchAsync("board.move", new JsonObject { ["id"] = card.Id, ["lane"] = "done" }, Ct);

        Assert.Equal(Lanes.Done, rig.Store.Card(card.Id)!.Lane);
        var trail = rig.Store.Trail(card.Id);
        Assert.Contains(trail, t => t.Kind == "moved" && t.Source == "you");
        Assert.DoesNotContain(trail, t => t.Kind == "comment");
    }

    [Fact]
    public async Task ADevelopersMove_OfAnUntestedTesterCardToDone_IsRefusedWithoutAReason()
    {
        // Not only the Manager - any mover but the tester itself.
        await using var rig = await Rig.StartAsync(("DeveloperPriya", ""), ("TesterWren", ""));
        var card = rig.Card("TesterWren");

        var said = await rig.Move("DeveloperPriya", card.Id, "done");
        Assert.StartsWith("refused needs_reason:", said);
        Assert.Contains($"card {card.Id} is TesterWren's", said);
        Assert.Equal(Lanes.InProgress, rig.Store.Card(card.Id)!.Lane);
        Assert.DoesNotContain(rig.Store.Trail(card.Id), t => t.Kind == "moved");

        Assert.StartsWith("ok:", await rig.Move("DeveloperPriya", card.Id, "done", "the person asked to skip testing"));
        var comment = Assert.Single(rig.Store.Trail(card.Id), t => t.Kind == "comment");
        Assert.Equal(("DeveloperPriya", "Done without TesterWren's test: the person asked to skip testing"), (comment.Source, comment.Text));
    }

    [Fact]
    public async Task AnotherTestersMove_OfThisTestersCard_NeedsAReasonToo()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""), ("TesterKai", ""));
        var card = rig.Card("TesterWren");
        Assert.StartsWith("refused needs_reason:", await rig.Move("TesterKai", card.Id, "done"));
        Assert.StartsWith("ok:", await rig.Move("TesterWren", card.Id, "done")); // its own move
    }

    [Fact]
    public async Task TheRefusal_NamesTheTester_AndSaysToGiveItThem()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var card = rig.Card("TesterWren");

        var said = await rig.Move("Manager", card.Id, "done");
        Assert.StartsWith("refused needs_reason:", said);
        Assert.Contains($"card {card.Id} is TesterWren's", said);
        Assert.Contains($"Give it to TesterWren with tell (card: {card.Id})", said);
    }

    [Fact]
    public async Task TheComment_SaysWhoseTestWasSkipped_AndTheTrimmedReason()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var card = rig.Card("TesterWren");

        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done", "  no tester on this team  "));
        var comment = Assert.Single(rig.Store.Trail(card.Id), t => t.Kind == "comment");
        Assert.Equal("Manager", comment.Source);
        Assert.Equal("Done without TesterWren's test: no tester on this team", comment.Text);
    }

    [Fact]
    public async Task TheTestersOwnBoardComment_DoesNotCountAsHavingTested()
    {
        // Only a hand-back or a finished run after the latest assignment counts; a comment does not.
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var card = rig.Card("TesterWren");

        Assert.StartsWith("ok:", await rig.Tool("TesterWren", new JsonObject { ["action"] = "comment", ["card"] = card.Id, ["text"] = "ran Board tests: 12 passed" }));
        Assert.StartsWith("refused needs_reason:", await rig.Move("Manager", card.Id, "done"));

        rig.Store.AddTrail(card.Id, "TesterWren", "handed back", "12 passed");
        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done"));
        Assert.DoesNotContain(rig.Store.Trail(card.Id), t => t.Kind == "comment" && t.Source == "Manager");
    }

    /// <summary>The tester's run that was cut off (completed, exit -1, no hand-back) does not let the Manager
    /// move the card to Done without a reason; one that handed back does.</summary>
    [Fact]
    public async Task ATestersRunCutOffWithoutAHandBack_StillNeedsAReasonForDone()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var card = rig.Card("TesterWren");
        rig.Store.AddTrail(card.Id, "Manager", "assigned", "Assigned to TesterWren: verify");
        rig.Store.AddTrail(card.Id, "TesterWren", "run ended",
            TesterRule.RunEndedText(JsonNode.Parse("""{"handedBack":false,"run":{"exitCode":-1}}""")!.AsObject()));

        Assert.StartsWith("refused needs_reason:", await rig.Move("Manager", card.Id, "done"));

        rig.Store.AddTrail(card.Id, "TesterWren", "run ended", TesterRule.RunEndedText(JsonNode.Parse("""{"handedBack":true}""")!.AsObject()));
        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done"));
    }

    [Fact]
    public async Task ATestersHandBackBeforeTheCardWasReassigned_DoesNotLetTheManagerCloseIt()
    {
        await using var rig = await Rig.StartAsync(("DeveloperPriya", ""), ("TesterWren", ""));
        var card = rig.Card("DeveloperPriya");
        rig.Store.AddTrail(card.Id, "Manager", "assigned", "Assigned to TesterWren: verify");
        rig.Store.AddTrail(card.Id, "TesterWren", "handed back", "FAILED");
        rig.Store.AddTrail(card.Id, "TesterWren", "run ended", "run ended");
        rig.Store.AddTrail(card.Id, "Manager", "assigned", "Assigned to DeveloperPriya: fix it");
        rig.Store.AddTrail(card.Id, "DeveloperPriya", "handed back", "fixed");

        Assert.StartsWith("refused needs_reason:", await rig.Move("Manager", card.Id, "done"));
    }

    [Fact]
    public async Task TheTestersRunOnAnotherCard_DoesNotCountForThisOne()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var other = rig.Card("TesterWren");
        rig.Store.AddTrail(other.Id, "TesterWren", "progress", "checking the other card");
        var card = rig.Card("TesterWren");
        rig.Store.AddTrail(card.Id, "Manager", "assigned", "Assigned to TesterWren: check it");
        rig.Store.AddTrail(card.Id, "you", "comment", "TesterWren, please look"); // the tester's name in text is not the tester

        Assert.StartsWith("refused needs_reason:", await rig.Move("Manager", card.Id, "done"));
    }

    [Fact]
    public async Task ACardAlreadyInDone_AndAnUnassignedCard_NeedNoReason()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var done = rig.Card("TesterWren", Lanes.Done);
        Assert.StartsWith("ok:", await rig.Move("Manager", done.Id, "done"));

        var unassigned = rig.Card(null);
        Assert.StartsWith("ok:", await rig.Move("Manager", unassigned.Id, "done"));
    }

    [Fact]
    public async Task ATesterOnAnotherTeam_DoesNotMakeThisTeamsCardATesterCard()
    {
        await using var rig = await Rig.StartAsync(("DeveloperPriya", ""));
        var otherTeam = await rig.Bus.CreateTeamAsync("search", "Manager", "claude", null, Ct);
        await rig.Bus.AddMemberAsync(otherTeam, "TesterWren", "claude", Ct);
        var card = rig.Card("DeveloperPriya");
        rig.Store.AddTrail(card.Id, "Manager", "assigned", "Assigned to TesterWren: check it");

        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done"));
    }
}
