using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// B019 gap tests (card 91, TesterWren): the edges of the verify-card rule that BoardTesterDoneTests does not pin -
/// the person's own move, a developer's move, which member counts as a tester, what counts as the tester having run,
/// and the comment's wording.
/// </summary>
public class BoardTesterDoneGapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BoardStore Store { get; private set; } = null!;
        public BusEngine Bus { get; private set; } = null!;
        public BoardService Service { get; private set; } = null!;
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

        public async Task InRunOf(string member, Func<RunContext, BoardService, Task> act)
        {
            _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _act = run => act(run, Service);
            await Bus.PersonTellAsync("payments", member, "go", ct: Ct);
            await _done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            _act = null;
        }

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
    public async Task ADevelopersMove_OfAnUntestedTesterCardToDone_IsUnchanged()
    {
        await using var rig = await Rig.StartAsync(("DeveloperPriya", ""), ("TesterWren", ""));
        var card = rig.Card("TesterWren");

        Assert.StartsWith("ok:", await rig.Move("DeveloperPriya", card.Id, "done"));
        Assert.DoesNotContain(rig.Store.Trail(card.Id), t => t.Kind == "comment");
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

    [Theory]
    [InlineData("testerkai", "", true)]             // the name starts with "Tester", any case
    [InlineData("TesterQa", "Developer", true)]      // the name alone is enough
    [InlineData("Kai", "tester, end to end", true)]  // the job starts with the word Tester, any case
    [InlineData("Kai", "  Tester: checks", true)]    // leading spaces in the job
    [InlineData("Kai", "Testing lead", false)]       // "Testing" is not the word Tester
    [InlineData("Kai", "Testers' guild", false)]     // nor is "Testers"
    [InlineData("Kai", "Developer and Tester", false)] // the job must start with it
    [InlineData("DeveloperTester", "", false)]       // the name must start with it
    public async Task WhoCountsAsATester(string name, string job, bool tester)
    {
        await using var rig = await Rig.StartAsync((name, job));
        var card = rig.Card(name);

        var said = await rig.Move("Manager", card.Id, "done");
        if (tester) Assert.StartsWith("refused needs_reason:", said);
        else Assert.StartsWith("ok:", said);
    }

    [Fact]
    public async Task TheTestersOwnBoardComment_CountsAsHavingRun()
    {
        await using var rig = await Rig.StartAsync(("TesterWren", ""));
        var card = rig.Card("TesterWren");

        Assert.StartsWith("ok:", await rig.Tool("TesterWren", new JsonObject { ["action"] = "comment", ["card"] = card.Id, ["text"] = "ran Board tests: 12 passed" }));
        Assert.StartsWith("ok:", await rig.Move("Manager", card.Id, "done"));
        Assert.DoesNotContain(rig.Store.Trail(card.Id), t => t.Kind == "comment" && t.Source == "Manager");
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
