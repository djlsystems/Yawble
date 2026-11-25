using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// docs.moveToBacklog: a team's text document becomes a pending backlog item of that team (backlog.add's answer) and
/// leaves the documents folder, all or nothing. Fakes only: a temp database and folder, no runner, a failing delete fake.
/// </summary>
public sealed class DocsMoveToBacklogTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly BusEngine _bus;
    private readonly TeamRepos _repos;
    private readonly UiRpc _ui;

    public DocsMoveToBacklogTests()
    {
        _repos = new TeamRepos(_db.Dir, _db.ConnectionString);
        _bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { Repos = _repos, IdleOffer = false });
        _bus.Board = new BoardService(new BoardStore(_db.ConnectionString), _bus);
        _ui = new UiRpc(_bus, new PolicyStore(_db.ConnectionString), null!, null!, null!, null!, null!);
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private async Task<long> TeamAsync(string name = "Alpha")
    {
        await _bus.CreateTeamAsync(name, ct: Ct);
        return (await _bus.TeamsAsync(Ct)).Single(t => t.Name == name).Id;
    }

    private string DocPath(string team, string relative) => Path.Combine(_repos.TeamRoot(team), TeamDocuments.Folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private void WriteDoc(string team, string relative, string text)
    {
        var file = DocPath(team, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private Task<JsonNode?> Move(long teamId, string path) =>
        _ui.DispatchAsync("docs.moveToBacklog", new JsonObject { ["teamId"] = teamId, ["path"] = path }, Ct);

    private async Task<JsonArray> Backlog(bool archived = false) =>
        (await _bus.Board!.DispatchAsync("backlog.list", new JsonObject { ["archived"] = archived }, Ct))!.AsArray();

    [Fact]
    public async Task AHeadingDocument_BecomesAPendingItemOfItsTeam_AndLeavesTheFolder()
    {
        await TeamAsync("Other");
        var teamId = await TeamAsync();
        const string text = "Written after the review.\n\n## Login form  \n\nPasswords are hashed.\n# A later heading\n";
        WriteDoc("Alpha", "specs/login.md", text);

        var item = (await Move(teamId, "specs/login.md"))!.AsObject();

        Assert.Equal("Login form", item["title"]!.GetValue<string>());
        Assert.Equal(text, item["body"]!.GetValue<string>());
        Assert.Equal("Alpha", item["team"]!.GetValue<string>());
        Assert.Equal("pending", item["state"]!.GetValue<string>());
        Assert.StartsWith("B", item["label"]!.GetValue<string>());
        Assert.False(File.Exists(DocPath("Alpha", "specs/login.md")));
        Assert.True(Directory.Exists(DocPath("Alpha", "specs"))); // only the document goes, as docs.delete does
        // The answer is backlog.add's: the same fields, and the item is on the backlog as listed.
        var added = (await _bus.Board!.DispatchAsync("backlog.add", new JsonObject { ["title"] = "x" }, Ct))!.AsObject();
        Assert.Equal(added.Select(kv => kv.Key).Order(), item.Select(kv => kv.Key).Order());
        var listed = Assert.Single(await Backlog(), i => i!["id"]!.GetValue<long>() == item["id"]!.GetValue<long>());
        Assert.Equal("Login form", listed!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task NoHeading_TheFileNameWithoutItsExtensionIsTheTitle()
    {
        var teamId = await TeamAsync();
        WriteDoc("Alpha", "release-plan.txt", "Ship on Friday.\n#not a heading\n```\n# inside a code fence\n```\n");

        var item = (await Move(teamId, "release-plan.txt"))!.AsObject();

        Assert.Equal("release-plan", item["title"]!.GetValue<string>());
        Assert.False(File.Exists(DocPath("Alpha", "release-plan.txt")));
    }

    [Fact]
    public async Task ALongHeading_IsClippedToTheBacklogsTitleLimit()
    {
        var teamId = await TeamAsync();
        WriteDoc("Alpha", "long.md", "### " + new string('a', 300) + "\n");
        Assert.Equal(200, (await Move(teamId, "long.md"))!["title"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData("# Title", "Title")]
    [InlineData("  ###### Deep one ##", "Deep one")]
    [InlineData("#\n# \nText\n## Second", "Second")]
    [InlineData("####### seven\n", "notes")]
    [InlineData("    # indented code", "notes")]
    [InlineData("~~~\n# fenced\n~~~\nplain", "notes")]
    public void TheTitle_IsTheFirstMarkdownHeadingsText(string text, string title) =>
        Assert.Equal(title, UiRpc.BacklogTitle("notes.md", text));

    [Theory]
    [InlineData("specs", "is not a file")]
    [InlineData("blob.bin", "is not a text document")]
    [InlineData("picture.png", "is not a text document")]
    [InlineData("gone.md", "is not there")]
    [InlineData("../outside.md", "must be a path inside")]
    public async Task AFolderABinaryOrAMissingPath_IsRefused_AndNothingChanges(string path, string reason)
    {
        var teamId = await TeamAsync();
        Directory.CreateDirectory(DocPath("Alpha", "specs"));
        WriteDoc("Alpha", "specs/inside.md", "# Inside");
        File.WriteAllBytes(DocPath("Alpha", "blob.bin"), [0x01, 0x00, 0x02, 0x00]);
        File.WriteAllBytes(DocPath("Alpha", "picture.png"), [0x89, 0x50, 0x4E, 0x47]);

        var e = await Assert.ThrowsAsync<RpcException>(() => Move(teamId, path));

        Assert.Contains(reason, e.Message);
        Assert.Empty(await Backlog());
        Assert.Empty(await Backlog(archived: true));
        Assert.True(File.Exists(DocPath("Alpha", "specs/inside.md")));
        Assert.True(File.Exists(DocPath("Alpha", "blob.bin")));
        Assert.True(File.Exists(DocPath("Alpha", "picture.png")));
    }

    [Fact]
    public async Task ADeleteThatFails_LeavesNoItem_AndTheDocumentStays()
    {
        var teamId = await TeamAsync();
        WriteDoc("Alpha", "plan.md", "# Plan\n\nSteps.");
        _ui.DeleteDocument = (_, _) => throw new IOException("the file is open in another program");

        var e = await Assert.ThrowsAsync<RpcException>(() => Move(teamId, "plan.md"));

        Assert.Contains("the file is open in another program", e.Message);
        Assert.Empty(await Backlog());
        Assert.Empty(await Backlog(archived: true));
        Assert.Equal("# Plan\n\nSteps.", File.ReadAllText(DocPath("Alpha", "plan.md")));
    }

    [Fact]
    public async Task AnUnknownTeam_IsRefused()
    {
        var e = await Assert.ThrowsAsync<RpcException>(() => Move(999, "plan.md"));
        Assert.Equal("no_such_team", e.Code);
        Assert.Empty(await Backlog());
    }

    [Fact]
    public void AVisitorIsRefused_AFullViewerIsNot()
    {
        var p = new JsonObject { ["teamId"] = 1, ["path"] = "plan.md" };
        Assert.True(RemoteAccess.OnAllowList("docs.moveToBacklog"));
        Assert.Equal(RemoteAccess.Kind.Changes, RemoteAccess.Kinds["docs.moveToBacklog"]);
        Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal("docs.moveToBacklog", p, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("docs.moveToBacklog", p, allowHostTerminals: false, RemoteAccess.Full));
    }

    [Fact]
    public void TheMoveIsAudited_AsAChange()
    {
        Assert.True(UiRpc.Handles("docs.moveToBacklog"));
        Assert.False(AuditLog.IsReadOnly("docs.moveToBacklog"));
        Assert.False(AuditLog.IsReadOnly("docs.moveToBacklog", new JsonObject { ["teamId"] = 1, ["path"] = "plan.md" }));
    }
}
