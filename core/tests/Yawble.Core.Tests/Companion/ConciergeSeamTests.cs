using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// Companion task 2: the additive seams the companion uses to call the Concierge's own tools (acting actor, CallToolAsync,
/// public definitions, a book's shown plans, ForTurn with another book). Fakes only: ui, board and repos are fakes; no git
/// or gh is ever run here.
/// </summary>
public class ConciergeSeamTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("yawble-cseam-").FullName;
    private readonly TempDb _db = new();

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static ConciergeTurn ExternalTurn() => new("external:cc", byPerson: true, remote: true);

    private sealed class Rig
    {
        public required BusEngine Bus;
        public required ConciergeStore Store;
        public required ConciergeGit Git;
        public List<(string Method, JsonObject? P)> Ui { get; } = [];
        public List<(string Method, JsonObject? P)> Board { get; } = [];
        public ConfirmationBook Book = new();
        public AuditLog? Audit;

        public async Task<JsonNode?> UiAsync(string method, JsonObject? p, CancellationToken ct)
        {
            lock (Ui) Ui.Add((method, p));
            if (method == "bus.tellManager")
            {
                // As UiRpc does: the person's tell, stamped with whoever is acting now.
                var m = await Bus.PersonTellAsync("alpha", "Manager", p!["text"]!.GetValue<string>(), null, ct);
                return new JsonObject { ["seq"] = m.Seq, ["workflow"] = m.CorrelationId };
            }
            return new JsonObject();
        }

        public ConciergeActions Actions(ConciergeTurn turn) => new(Bus, UiAsync,
            (m, p, _) => { lock (Board) Board.Add((m, p)); return Task.FromResult<JsonNode?>(new JsonObject { ["label"] = "B001" }); },
            Git, (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([]), Book, gh: null, audit: Audit, turn: turn);

        public ConciergeTools Tools(ConciergeTurn turn, Actor? actingFor = null, bool withActions = true) =>
            new(Bus, Store, UiAsync, () => null, null, null, withActions ? Actions(turn) : null, turn.Remote, turn, actingFor);
    }

    private async Task<Rig> MakeRigAsync()
    {
        var bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await bus.StartAsync(Ct);
        await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        var git = new ConciergeGit(new HostGit(Path.Combine(_root, "empty.gitconfig")), new PersonGitPush(globalConfig: Path.Combine(_root, "empty.gitconfig")));
        return new Rig { Bus = bus, Store = new ConciergeStore(_db.ConnectionString), Git = git };
    }

    private static string Token(string text) => System.Text.RegularExpressions.Regex.Match(text, "confirm=\"([0-9a-f]+)\"").Groups[1].Value;

    private static async Task<(string Text, bool IsError)> McpCall(ConciergeTools tools, string name, JsonObject args)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() } };
        var reply = JsonNode.Parse((await tools.HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct)).Json!)!["result"]!;
        return (reply["content"]![0]!["text"]!.GetValue<string>(), reply["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ToolsActAsTheGivenActor()
    {
        var rig = await MakeRigAsync();

        var (said, isError) = await rig.Tools(ExternalTurn(), Actor.ExternalClient("cc")).CallToolAsync("tell",
            new JsonObject { ["team"] = "alpha", ["instruction"] = "Add a README" }, Ct);
        Assert.False(isError, said);
        Assert.StartsWith("ok: told the Manager of alpha", said);
        var told = Assert.Single(await rig.Bus.RecentTeamMessagesAsync("alpha", 10, Ct), m => m.Source == MessageTypes.PersonSource);
        var p = _db.OpenStore().ProvenanceOf(told.Seq)!;
        Assert.Equal(Actor.External, p.Via);
        Assert.Equal(Actor.LocalUser, p.User);

        // Without an actor given, the tools still act as the Concierge for the local account (as before).
        await rig.Tools(ExternalTurn()).CallToolAsync("tell", new JsonObject { ["team"] = "alpha", ["instruction"] = "Add a licence" }, Ct);
        var second = (await rig.Bus.RecentTeamMessagesAsync("alpha", 10, Ct)).Where(m => m.Source == MessageTypes.PersonSource).MaxBy(m => m.Seq)!;
        Assert.Equal(Actor.Concierge, _db.OpenStore().ProvenanceOf(second.Seq)!.Via);
    }

    [Fact]
    public async Task ActionAuditUsesTheGivenActor()
    {
        var rig = await MakeRigAsync();
        rig.Audit = new AuditLog(_db.ConnectionString);

        var (said, isError) = await rig.Tools(ExternalTurn(), Actor.ExternalClient("cc")).CallToolAsync("board_move",
            new JsonObject { ["card"] = 38, ["lane"] = "done" }, Ct);
        Assert.False(isError, said);
        Assert.Equal("board.move", Assert.Single(rig.Board).Method);

        var row = Assert.Single(rig.Audit.Recent(), r => r.Method == "concierge.board_move");
        Assert.Equal(Actor.External, row.Via);
        Assert.Equal(Actor.LocalUser, row.User);
        Assert.Equal("cc", row.Client);
        Assert.Equal("ok", row.Outcome);
    }

    [Fact]
    public void IssuedInReturnsThePlanAndTokenOfThatTurnOnly()
    {
        var book = new ConfirmationBook();
        var first = new ConciergeTurn("c1", byPerson: true, remote: false);
        var other = new ConciergeTurn("c1", byPerson: true, remote: false);
        var a = book.Issue(first, "team_archive", "k1", "Archive team alpha");
        var b = book.Issue(other, "team_delete", "k2", "DELETE team beta");
        var c = book.Issue(first, "git_prune", "k3");

        var mine = book.IssuedIn(first);
        Assert.Equal(2, mine.Count);
        Assert.Contains(new ConfirmationBook.IssuedConfirmation(a, "team_archive", "Archive team alpha"), mine);
        Assert.Contains(new ConfirmationBook.IssuedConfirmation(c, "git_prune", ""), mine);
        Assert.Equal([new ConfirmationBook.IssuedConfirmation(b, "team_delete", "DELETE team beta")], book.IssuedIn(other));
        Assert.Empty(book.IssuedIn(new ConciergeTurn("c1", byPerson: true, remote: false)));

        // A redeemed token is no longer listed.
        var later = new ConciergeTurn("c1", byPerson: true, remote: false);
        Assert.Equal(ConfirmationBook.Outcome.Ok, book.Redeem(later, "team_archive", "k1", a));
        Assert.Equal([new ConfirmationBook.IssuedConfirmation(c, "git_prune", "")], book.IssuedIn(first));
    }

    [Fact]
    public async Task ATokenFromTheSharedBookIsUnknownInAnotherBook()
    {
        var rig = await MakeRigAsync();
        var companionBook = new ConfirmationBook();
        var first = new ConciergeTurn("c1", byPerson: true, remote: false);

        // Shown through the Concierge's own book: the token and the plan are there, not in the companion's book.
        var shown = await rig.Actions(first).TryCallAsync("team_archive", new JsonObject { ["team"] = "alpha" }, Ct);
        Assert.StartsWith("NOT DONE", shown);
        var token = Token(shown!);
        var issued = Assert.Single(rig.Book.IssuedIn(first));
        Assert.Equal(token, issued.Token);
        Assert.Equal("team_archive", issued.Tool);
        Assert.StartsWith("Archive team alpha:", issued.Plan);
        Assert.Contains(issued.Plan, shown);
        Assert.Empty(companionBook.IssuedIn(first));

        // The same yes, redeemed through actions bound to the companion's own book: unknown, nothing runs; a new list is shown there.
        var next = new ConciergeTurn("c1", byPerson: true, remote: false);
        var viaCompanion = await rig.Actions(first).ForTurn(next, companionBook).TryCallAsync("team_archive", new JsonObject { ["team"] = "alpha", ["confirm"] = token }, Ct);
        Assert.StartsWith("NOT DONE", viaCompanion);
        Assert.Contains("unknown or expired", viaCompanion);
        Assert.DoesNotContain(rig.Ui, u => u.Method == "teams.archive");
        var fresh = Assert.Single(companionBook.IssuedIn(next));
        Assert.NotEqual(token, fresh.Token);
        Assert.Equal(issued.Plan, fresh.Plan);

        // ForTurn without a book keeps the actions' own book: there the yes is still good.
        var third = new ConciergeTurn("c1", byPerson: true, remote: false);
        var ran = await rig.Actions(first).ForTurn(third).TryCallAsync("team_archive", new JsonObject { ["team"] = "alpha", ["confirm"] = token }, Ct);
        Assert.StartsWith("ok:", ran);
        Assert.Single(rig.Ui, u => u.Method == "teams.archive");
    }

    [Fact]
    public async Task CallToolAsyncMatchesTheMcpPath()
    {
        var rig = await MakeRigAsync();
        var turn = ExternalTurn();
        var cases = new (string Name, JsonObject Args)[]
        {
            ("team_list", []),
            ("team_status", new() { ["team"] = "alpha" }),
            ("team_status", new() { ["team"] = "nobody" }),
            ("git_prune", new() { ["team"] = "alpha" }),
            ("no_such_tool", []),
        };
        foreach (var (name, args) in cases)
        {
            var mcp = await McpCall(rig.Tools(turn), name, args);
            var direct = await rig.Tools(turn).CallToolAsync(name, args.DeepClone().AsObject(), Ct);
            Assert.Equal(mcp, direct);
        }
        Assert.True((await rig.Tools(turn).CallToolAsync("no_such_tool", [], Ct)).IsError);
        // git_prune is local-only: the remote turn's refusal comes back the same way on both paths.
        Assert.Contains("only for the person at this computer", (await rig.Tools(turn).CallToolAsync("git_prune", new JsonObject { ["team"] = "alpha" }, Ct)).Text);
    }

    [Fact]
    public async Task DefinitionsAreTheToolsListOfTheMcpPath()
    {
        var rig = await MakeRigAsync();
        foreach (var withActions in new[] { false, true })
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" };
            var listed = JsonNode.Parse((await rig.Tools(ExternalTurn(), withActions: withActions).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct)).Json!)!["result"]!["tools"]!;
            var defs = ConciergeTools.Definitions(withActions);
            Assert.Equal(listed.ToJsonString(), defs.ToJsonString());
            Assert.Equal(withActions, defs.Any(t => t!["name"]!.GetValue<string>() == "board_move"));
            Assert.Contains(defs, t => t!["name"]!.GetValue<string>() == "team_git");
            // Deep-cloned: changing the copy changes nothing for the next caller.
            defs[0]!["description"] = "changed";
            Assert.NotEqual("changed", ConciergeTools.Definitions(withActions)[0]!["description"]!.GetValue<string>());
        }
    }
}
