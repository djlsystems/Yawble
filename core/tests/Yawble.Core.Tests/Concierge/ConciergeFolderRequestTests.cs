using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Integrations;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The Concierge and folders: folder_request raises a folder ask for the person and grants nothing; a turn the person
/// did not start is refused; team_status lists the team's folders and its waiting folder requests; a paired program is
/// not offered folder_request. Fakes only: a temporary database, a bus whose runs never start, fictional paths; git is
/// never run.
/// </summary>
public sealed class ConciergeFolderRequestTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly bool Win = OperatingSystem.IsWindows();
    private static readonly string Home = Win ? @"C:\Users\pat" : "/home/pat";
    private static readonly string DataDir = Win ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
    private static readonly string Install = Win ? @"C:\Program Files\Yawble" : "/opt/yawble";
    private static readonly string Sessions = Win ? @"Z:\smokesessions" : "/srv/smokesessions";

    private readonly TempDb _db = new();
    private readonly List<string> _told = [];
    private BusEngine _bus = null!;
    private PolicyStore _policies = null!;
    private FolderAskService _asks = null!;
    private long _team;

    public async ValueTask InitializeAsync()
    {
        _bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await _bus.StartAsync(Ct);
        await _bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        _team = (await _bus.TeamsAsync(Ct)).Single().Id;
        await _bus.AddMemberAsync(_team, "Ines", "claude", Ct);
        _policies = new PolicyStore(_db.ConnectionString);
        var layouts = new MemberLayouts(DataDir, Install, Home);
        _asks = new FolderAskService(_db.ConnectionString, _policies,
            async (memberId, ct) =>
            {
                var everyone = await _bus.AllMemberRowsAsync(ct);
                return layouts.For(everyone.First(m => m.Id == memberId), everyone);
            },
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            (_, member, _, _, _) =>
            {
                lock (_told) _told.Add(member);
                return Task.CompletedTask;
            },
            exists: _ => true);
        _bus.FolderAsks = _asks;
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private ConciergeActions Actions(ConciergeTurn? turn = null) => new(_bus,
        (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()), (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()),
        new ConciergeGit(new HostGit(Path.Combine(_db.Dir, "empty.gitconfig")), new PersonGitPush(globalConfig: Path.Combine(_db.Dir, "empty.gitconfig"))),
        (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([]), new ConfirmationBook(),
        turn: turn ?? new ConciergeTurn("c1", byPerson: true, remote: false));

    private static async Task<string> Call(ConciergeActions a, JsonObject args)
    {
        try { return await a.TryCallAsync("folder_request", args, Ct) ?? throw new InvalidOperationException("not an action tool"); }
        catch (Exception e) when (e is ArgumentException or BusRefusal) { return "REFUSED: " + e.Message; }
    }

    private static JsonObject Request(string path, string access = "read", string? member = "Ines", string team = "alpha")
    {
        var args = new JsonObject { ["team"] = team, ["path"] = path, ["access"] = access, ["reason"] = "the nightly workflow reads the recorded sessions" };
        if (member is not null) args["member"] = member;
        return args;
    }

    [Fact]
    public async Task FolderRequest_RaisesTheAskInNeedsYou_AndGrantsNothing()
    {
        Assert.Contains("folder_request", ConciergeActions.ToolNames);
        var text = await Call(Actions(), Request(Sessions, "readwrite"));
        var ask = Assert.Single(_asks.Asks());
        Assert.StartsWith($"asked (folder request {ask.Id})", text);
        Assert.Contains("Needs you", text);
        Assert.Equal(("alpha/Ines", FolderAskService.ConciergeAsker, Sessions, "readwrite"), (ask.Member, ask.AskedBy, ask.Path, ask.Access));
        // Nothing is granted by the call: no team folder, no member grant, nothing in the global policy.
        Assert.Empty(_policies.TeamFolders(_team));
        Assert.True(_policies.GetMember(ask.MemberId).Loosen.IsEmpty);
        Assert.DoesNotContain(Sessions, _policies.GetGlobal().Filesystem.Readwrite);

        // Over the Cloud Link the person may ask for it too (it only raises the ask); with no member it is the Manager's.
        var remote = await Call(Actions(new ConciergeTurn("c1", byPerson: true, remote: true)), Request(Sessions, "read", member: null));
        Assert.StartsWith("asked (folder request", remote);
        Assert.Equal(2, _asks.Asks().Count);
        Assert.Contains(_asks.Asks(), a => a.Member == "alpha/Manager");

        // The person approves the first for the team; asked again, the Concierge is told it is granted. No agent is woken.
        using (Actor.Use(Actor.Window)) await _asks.AnswerAsync(ask.Id, FolderAccess.ReadWrite, null, Ct, FolderScope.Team);
        Assert.Empty(_told);
        Assert.Contains("already read-write for alpha/Ines", await Call(Actions(), Request(Sessions, "readwrite")));
    }

    [Fact]
    public async Task FolderRequest_IsRefused_ForATurnThePersonDidNotStart_ANeverGrantedFolder_AndAnUnknownTeamOrMember()
    {
        var review = await Call(Actions(new ConciergeTurn("c1", byPerson: false, remote: false)), Request(Sessions));
        Assert.Contains("not started by a message from the person", review);

        var secrets = await Call(Actions(), Request(Win ? Home + @"\.ssh" : Home + "/.ssh"));
        Assert.StartsWith("refused:", secrets);
        Assert.Contains("secrets", secrets);
        Assert.StartsWith("refused:", await Call(Actions(), Request(DataDir)));
        Assert.Contains("no member named 'Nobody' on team 'alpha'", await Call(Actions(), Request(Sessions, member: "Nobody")));
        Assert.StartsWith("REFUSED", await Call(Actions(), Request(Sessions, team: "ghosts")));
        Assert.StartsWith("REFUSED", await Call(Actions(), Request(Sessions, "everything")));
        Assert.Empty(_asks.Asks(openOnly: false));
        Assert.Empty(_policies.TeamFolders(_team));
    }

    [Fact]
    public async Task TeamStatus_ListsTheTeamsFolders_AndItsWaitingFolderRequests()
    {
        var tools = new ConciergeTools(_bus, new ConciergeStore(_db.ConnectionString), (_, _, _) => Task.FromResult<JsonNode?>(null), () => null, "c1", null, Actions());
        async Task<string> Status()
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "team_status", ["arguments"] = new JsonObject { ["team"] = "alpha" } } };
            var reply = JsonNode.Parse((await tools.HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct)).Json!)!["result"]!;
            return reply["content"]![0]!["text"]!.GetValue<string>();
        }

        var none = await Status();
        Assert.Contains("Team folders (outside its own folders, for every member): none.", none);
        Assert.DoesNotContain("Folder requests waiting", none);

        using (Actor.Use(Actor.Window)) await _asks.AddTeamFolderAsync(_team, Sessions, "read", Ct);
        var other = Win ? @"Z:\output" : "/srv/output";
        await Call(Actions(), Request(other, "readwrite"));
        var status = await Status();
        Assert.Contains($"- {Sessions}: read-only (added by {Actor.LocalUser} on ", status);
        Assert.Contains("Settings > Folders", status);
        Assert.Contains("Folder requests waiting for the person", status);
        Assert.Contains($"alpha/Ines asks for read-write access to {other} (asked by the Concierge)", status);
    }

    [Fact]
    public void APairedProgram_IsNotOfferedFolderRequest_AtAnyLevel()
    {
        Assert.Null(CompanionTools.LevelOf("folder_request", []));
        Assert.DoesNotContain(CompanionTools.Definitions(CompanionLevel.Destructive), d => d!["name"]!.GetValue<string>() == "folder_request");
        // The Concierge is offered it, with what it does.
        var tool = ConciergeTools.Definitions(true).Single(d => d!["name"]!.GetValue<string>() == "folder_request")!;
        Assert.Contains("only raises the request", tool["description"]!.GetValue<string>());
        Assert.Equal(["access", "path", "reason", "team"], tool["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order());
        Assert.Contains("folder_request", ConciergePrompt.Role);
    }
}
