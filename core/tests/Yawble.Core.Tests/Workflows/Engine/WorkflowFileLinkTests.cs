using System.Text.Json;
using Xunit;
using Yawble.Core.Workflows.Engine;

namespace Yawble.Core.Tests.Workflows.Engine;

/// <summary>
/// A junction inside a folder the file steps reach (the data folder, a granted folder, the team's Documents) is never followed:
/// as a source, a destination, a folder to list or a tell's attachment, the step fails and nothing is read or written through it.
/// </summary>
public sealed class WorkflowFileLinkTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();
    private readonly List<string> _links = [];

    public void Dispose()
    {
        foreach (var link in _links)
            try { Directory.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _temp.Dispose();
    }

    private string Data => _temp.Sub("data");
    private string Granted => _temp.Sub("granted");
    private string Docs => _temp.Sub(Path.Combine("team", "documents"));
    private string Outside => _temp.Sub("host");

    private sealed class FakeTeller : IWorkflowTeller
    {
        public List<WorkflowTellCall> Calls { get; } = [];

        public Task<WorkflowTellSent> TellAsync(WorkflowTellCall call, CancellationToken ct)
        {
            Calls.Add(call);
            return Task.FromResult(new WorkflowTellSent(42, call.Member, call.Team ?? "Support", null));
        }
    }

    /// <summary>
    /// A junction named j in the folder <paramref name="where"/> names, to a folder outside every grant holding secret.txt; gives the
    /// folder it leads to. Where the file system refuses a junction (an agent's sandbox), j is a plain folder the link check is told
    /// to see as a link (<see cref="Yawble.Core.Ops.PathLinks.SeenAsLink"/>), and it leads to itself.
    /// </summary>
    private string Junction(string where)
    {
        var root = where switch { "data" => Data, "granted" => Granted, _ => Docs };
        var link = Path.Combine(root, "j");
        File.WriteAllBytes(Path.Combine(Outside, "secret.txt"), [7, 7, 7]);
        if (OperatingSystem.IsWindows() && TestFiles.TryCreateJunction(link, Outside))
        {
            _links.Add(link);
            return Outside;
        }
        Directory.CreateDirectory(link);
        File.WriteAllBytes(Path.Combine(link, "secret.txt"), [7, 7, 7]);
        Yawble.Core.Ops.PathLinks.SeenAsLink.Value = p => string.Equals(Path.GetFullPath(p), link, StringComparison.OrdinalIgnoreCase);
        return link;
    }

    /// <summary>How a step names <paramref name="inner"/> under the junction: relative in the data folder, absolute in a grant, docs:/ in Documents.</summary>
    private string Through(string where, string inner) => where switch
    {
        "data" => "j/" + inner,
        "granted" => Path.Combine(Granted, "j", inner),
        _ => "docs:/j/" + inner,
    };

    private WorkflowRunRequest Request(string steps, FakeTeller? teller = null) => new()
    {
        Document = WorkflowSamples.Workflow(steps, """[ { "name": "files", "type": "list<any>" } ]"""),
        Actions = new FakeActions(), DataFolder = Data, Teller = teller ?? new FakeTeller(), Team = "Support", Clock = new FakeClock(T0),
        AllowedFolders = [new WorkflowFolderGrant(Granted, CanWrite: true)], TeamDocumentsFolder = Docs,
    };

    private static Task<WorkflowRunResult> Run(WorkflowRunRequest request) => WorkflowEngine.RunAsync(request, new RecordingJournal(), Ct);

    private static void FailedThroughALink(WorkflowRunResult r, string path)
    {
        Assert.Equal((WorkflowRunStatus.Failed, "a"), (r.Status, r.FailedStepId));
        Assert.Contains($"'{path}' goes through a link, which is not followed", r.Reason);
    }

    private static string J(string s) => JsonSerializer.Serialize(s);

    [Theory]
    [InlineData("data", "file.copy")]
    [InlineData("data", "file.move")]
    [InlineData("granted", "file.copy")]
    [InlineData("granted", "file.move")]
    [InlineData("docs", "file.copy")]
    [InlineData("docs", "file.move")]
    public async Task AJunctionAsTheSource_IsRefused_AndNothingIsReadOrMovedThroughIt(string where, string type)
    {
        var behind = Junction(where);
        var source = Through(where, "secret.txt");

        var r = await Run(Request($$"""[ { "id": "a", "type": "{{type}}", "path": {{J(source)}}, "to": "out/secret.txt", "next": null } ]"""));

        FailedThroughALink(r, source);
        Assert.False(File.Exists(Path.Combine(Data, "out", "secret.txt")));
        Assert.Equal([7, 7, 7], File.ReadAllBytes(Path.Combine(behind, "secret.txt"))); // a move deleted nothing
    }

    [Theory]
    [InlineData("data", "file.copy", "copied.txt")]
    [InlineData("data", "file.move", "")]
    [InlineData("granted", "file.copy", "copied.txt")]
    [InlineData("granted", "file.move", "")]
    [InlineData("docs", "file.copy", "copied.txt")]
    [InlineData("docs", "file.move", "")]
    public async Task AJunctionAsTheDestination_OrAsTheFolderTarget_IsRefused_AndNothingIsWrittenThroughIt(string where, string type, string name)
    {
        var behind = Junction(where);
        File.WriteAllBytes(Path.Combine(Data, "a.txt"), [1, 2, 3]);
        // A named file under the junction, or the junction itself as the folder to put the file in.
        var to = name.Length == 0 ? Through(where, "").TrimEnd('/', '\\') : Through(where, name);

        var r = await Run(Request($$"""[ { "id": "a", "type": "{{type}}", "path": "a.txt", "to": {{J(to)}}, "next": null } ]"""));

        Assert.Equal((WorkflowRunStatus.Failed, "a"), (r.Status, r.FailedStepId));
        Assert.Contains("goes through a link, which is not followed", r.Reason);
        Assert.Equal(["secret.txt"], Directory.GetFiles(behind).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(Data, "a.txt")));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("granted")]
    [InlineData("docs")]
    public async Task AJunctionAsTheFolderToList_IsRefused(string where)
    {
        var behind = Junction(where);
        var folder = Through(where, "").TrimEnd('/', '\\');

        var r = await Run(Request($$"""[ { "id": "a", "type": "file.list", "path": {{J(folder)}}, "into": "files", "next": null } ]"""));

        FailedThroughALink(r, folder);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("granted")]
    [InlineData("docs")]
    public async Task AJunctionInATellsAttachment_FailsTheStep_AndTellsNothing(string where)
    {
        var behind = Junction(where);
        var attachment = Through(where, "secret.txt");
        var teller = new FakeTeller();

        var r = await Run(Request($$"""[ { "id": "a", "type": "tell", "text": "Look.", "attachments": [ {{J(attachment)}} ], "next": null } ]""", teller));

        FailedThroughALink(r, attachment);
        Assert.Empty(teller.Calls);
    }

    [Fact]
    public async Task AFreeNameThatIsAJunction_IsRefused_NotWrittenThrough()
    {
        File.WriteAllBytes(Path.Combine(Data, "a.txt"), [1, 2, 3]);
        var taken = Path.Combine(_temp.Sub(Path.Combine("data", "out")), "a.txt");
        File.WriteAllBytes(taken, [9]);
        var link = Path.Combine(Data, "out", "a (1).txt");
        var behind = Outside;
        File.WriteAllBytes(Path.Combine(Outside, "secret.txt"), [7, 7, 7]);
        if (OperatingSystem.IsWindows() && TestFiles.TryCreateJunction(link, Outside)) _links.Add(link);
        else
        {
            Directory.CreateDirectory(link);
            behind = link;
            Yawble.Core.Ops.PathLinks.SeenAsLink.Value = p => string.Equals(Path.GetFullPath(p), link, StringComparison.OrdinalIgnoreCase);
        }

        var r = await Run(Request("""[ { "id": "a", "type": "file.copy", "path": "a.txt", "to": "out/a.txt", "next": null } ]"""));

        Assert.Equal((WorkflowRunStatus.Failed, "a"), (r.Status, r.FailedStepId));
        Assert.Contains("a (1).txt' goes through a link, which is not followed", r.Reason);
        Assert.DoesNotContain(Directory.GetFiles(behind), f => Path.GetFileName(f) == "a.txt");
        Assert.Equal([9], File.ReadAllBytes(taken));
    }

    [Fact]
    public async Task AFileReadThroughAJunctionInDocuments_IsRefused()
    {
        Junction("docs");

        var r = await Run(Request("""[ { "id": "a", "type": "file.readAllText", "path": "docs:/j/secret.txt", "next": null } ]"""));

        FailedThroughALink(r, "docs:/j/secret.txt");
    }
}
