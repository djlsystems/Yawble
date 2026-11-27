using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Tests.Workflows.Engine;

/// <summary>
/// A tell step's attachments in the engine (a fake teller): the paths it takes (the data folder, a grant, docs:/, an expression's list,
/// file.list's items), the log, the dry run that copies nothing, a missing or out-of-reach file that fails the step, HEIC copied as it is,
/// the copy into a team's documents (Ui.Attachments) and the instruction text that names the copies; and the check's issues.
/// </summary>
public sealed class WorkflowTellAttachmentTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();
    public void Dispose() => _temp.Dispose();

    private string Data => _temp.Sub("data");
    private string Granted => _temp.Sub("photos");
    private string Docs => _temp.Sub(Path.Combine("team", "documents"));

    private sealed class FakeTeller(string? docs = null) : IWorkflowTeller
    {
        public List<WorkflowTellCall> Calls { get; } = [];

        public Task<WorkflowTellSent> TellAsync(WorkflowTellCall call, CancellationToken ct)
        {
            Calls.Add(call);
            var copies = docs is null || call.Attachments is null ? null : Yawble.Core.Ui.Attachments.Copy(docs, call.Attachments);
            return Task.FromResult(new WorkflowTellSent(42, call.Member, call.Team ?? "Support", copies));
        }
    }

    private static string Write(string folder, string name, byte[]? bytes = null)
    {
        var path = Path.GetFullPath(Path.Combine(folder, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3]);
        return path;
    }

    private static WorkflowDocument Tell(string attachments, string variables = "[]") => WorkflowSamples.Workflow($$"""
        [ { "id": "a", "type": "tell", "text": "Look at these.", "attachments": {{attachments}}, "next": null } ]
        """, variables);

    private WorkflowRunRequest Request(WorkflowDocument doc, IWorkflowTeller teller, bool dryRun = false) => new()
    {
        Document = doc, Actions = new FakeActions(), DataFolder = Data, Teller = teller, Team = "Support", Clock = new FakeClock(T0),
        AllowedFolders = [new WorkflowFolderGrant(Granted, CanWrite: false)], TeamDocumentsFolder = Docs,
        DryRunFolder = dryRun ? _temp.Sub("dry") : null, DryRun = dryRun,
    };

    private static Task<WorkflowRunResult> Run(WorkflowRunRequest request) => WorkflowEngine.RunAsync(request, new RecordingJournal(), Ct);

    private static void Succeeded(WorkflowRunResult r) =>
        Assert.True(r.Status == WorkflowRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");

    // ---- the engine ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Attachments_FromTheDataFolderAGrantAndDocs_GoToTheTellerAsRealPaths_AndTheLogNamesTheCopies()
    {
        var local = Write(Data, "in/report.pdf");
        var photo = Write(Granted, "IMG 0001.jpg");
        var doc = Write(Docs, "smoke/notes.txt");
        var teller = new FakeTeller(Docs);

        var r = await Run(Request(Tell($"""[ "in/report.pdf", {System.Text.Json.JsonSerializer.Serialize(photo)}, "docs:/smoke/notes.txt" ]"""), teller));

        Succeeded(r);
        var call = Assert.Single(teller.Calls);
        Assert.Equal([local, photo, doc], call.Attachments!);
        var copies = Directory.GetFiles(Path.Combine(Docs, "attachments")).Order().ToList();
        Assert.Equal(3, copies.Count);
        Assert.Contains(copies, c => c.EndsWith("-IMG-0001.jpg", StringComparison.Ordinal));
        Assert.All(copies, c => Assert.Equal([1, 2, 3], File.ReadAllBytes(c)));
        Assert.Contains(r.Log, l => l.StartsWith($"attached {photo} as {Path.Combine(Docs, "attachments")}", StringComparison.Ordinal));
        Assert.True(File.Exists(photo)); // copied, not moved
    }

    [Fact]
    public async Task AnExpressionsList_AndFileListItems_AreTakenItemByItem()
    {
        Write(Data, "in/a.png");
        Write(Data, "in/b.png");
        var teller = new FakeTeller();
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "file.list", "path": "in", "into": "files", "next": "t" },
              { "id": "t", "type": "tell", "text": "Look.", "attachments": ["{vars.files}", "{vars.names}"], "next": null } ]
            """, """[ { "name": "files", "type": "list<any>" }, { "name": "names", "type": "list<string>", "initial": ["in/a.png"] } ]""");

        var r = await Run(Request(doc, teller));

        Succeeded(r);
        Assert.Equal(string.Join("|", Path.Combine(Data, "in", "a.png"), Path.Combine(Data, "in", "b.png"), Path.Combine(Data, "in", "a.png")),
            string.Join("|", Assert.Single(teller.Calls).Attachments!));
    }

    [Fact]
    public async Task ADryRun_LogsWouldAttach_AndCopiesNothing()
    {
        var photo = Write(Granted, "a.jpg");
        var teller = new FakeTeller(Docs);

        var r = await Run(Request(Tell($"[ {System.Text.Json.JsonSerializer.Serialize(photo)} ]"), teller, dryRun: true));

        Succeeded(r);
        Assert.Empty(teller.Calls);
        Assert.Contains($"would attach {photo}", r.Log);
        Assert.False(Directory.Exists(Path.Combine(Docs, "attachments")));
    }

    [Fact]
    public async Task AMissingFile_FailsTheStepWithTheReason_AndTellsNothing()
    {
        var teller = new FakeTeller(Docs);

        var r = await Run(Request(Tell("""[ "in/gone.pdf" ]"""), teller));

        Assert.Equal((WorkflowRunStatus.Failed, "a"), (r.Status, r.FailedStepId));
        Assert.Contains("'in/gone.pdf' does not exist, so tell cannot attach it", r.Reason);
        Assert.Empty(teller.Calls);
    }

    [Theory]
    [InlineData("outside", "is outside the member's data folder and the team's folder grants")]
    [InlineData("folder", "is a folder; tell attaches files")]
    [InlineData("number", "an attachment is ")]
    public async Task AFileOutOfReachAFolderOrNotAPath_FailsTheStep(string kind, string why)
    {
        var outside = Write(_temp.Sub("elsewhere"), "x.jpg");
        Directory.CreateDirectory(Path.Combine(Data, "sub"));
        var attachments = kind switch
        {
            "outside" => $"[ {System.Text.Json.JsonSerializer.Serialize(outside)} ]",
            "folder" => """[ "sub" ]""",
            _ => "\"{vars.n}\"",
        };
        var teller = new FakeTeller(Docs);

        var r = await Run(Request(Tell(attachments, """[ { "name": "n", "type": "int", "initial": 5 } ]"""), teller));

        Assert.Equal(WorkflowRunStatus.Failed, r.Status);
        Assert.Contains(why, r.Reason);
        Assert.Empty(teller.Calls);
    }

    [Fact]
    public async Task AHeicPhoto_IsCopiedAsItIs_AndTheLogAndTheInstructionSaySo()
    {
        byte[] heic = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c'];
        var photo = Write(Granted, "IMG_0002.HEIC", heic);
        var teller = new FakeTeller(Docs);

        var dry = await Run(Request(Tell($"[ {System.Text.Json.JsonSerializer.Serialize(photo)} ]"), teller, dryRun: true));
        var r = await Run(Request(Tell($"[ {System.Text.Json.JsonSerializer.Serialize(photo)} ]"), teller));

        Succeeded(dry);
        Assert.Contains($"would attach {photo} ({WorkflowTell.HeifNote})", dry.Log);
        Succeeded(r);
        var copy = Assert.Single(Directory.GetFiles(Path.Combine(Docs, "attachments")));
        Assert.EndsWith("-IMG_0002.heic", copy, StringComparison.Ordinal);
        Assert.Equal(heic, File.ReadAllBytes(copy));
        Assert.Contains(r.Log, l => l.StartsWith($"attached {photo} as {copy}", StringComparison.Ordinal) && l.Contains("not converted to JPEG", StringComparison.Ordinal));
        Assert.Contains($"- {copy} ({WorkflowTell.HeifNote})", WorkflowTell.AttachmentsText([copy]));
    }

    [Fact]
    public void TheInstructionText_NamesEachCopy()
    {
        var text = WorkflowTell.AttachmentsText([@"C:\t\documents\attachments\20261127-180000-a.jpg", @"C:\t\documents\attachments\20261127-180000-b.pdf"]);

        Assert.StartsWith("The workflow attached 2 files, copied into the team's documents. Open each with your file-reading tool:", text);
        Assert.EndsWith("\n- C:\\t\\documents\\attachments\\20261127-180000-a.jpg\n- C:\\t\\documents\\attachments\\20261127-180000-b.pdf", text);
        Assert.Equal("", WorkflowTell.AttachmentsText([]));
    }

    // ---- the check ----------------------------------------------------------------------------------------------------

    private static WorkflowCheckResult Check(string attachments) => WorkflowChecker.Check(
        WorkflowSamples.Workflow($$"""[ { "id": "a", "type": "tell", "text": "Look.", "attachments": {{attachments}}, "next": null } ]""",
            """[ { "name": "files", "type": "list<any>" }, { "name": "names", "type": "list<string>" }, { "name": "n", "type": "int" } ]""",
            settings: """[ { "name": "x", "type": "string" } ]"""),
        new WorkflowSchemaSet([]), FakeCatalog.MailAndSms());

    [Theory]
    [InlineData("""[ "in/report.pdf", "C:/Photos/a.jpg", "docs:/smoke/a.png", "{settings.x}", "{vars.files}", "{vars.names}", "in/{settings.x}.jpg" ]""")]
    [InlineData("\"{vars.names}\"")]
    [InlineData("\"{settings.x}\"")]
    [InlineData("[]")]
    public void GoodAttachments_CheckClean(string attachments)
    {
        var result = Check(attachments);
        Assert.True(result.Ok, string.Join("\n", result.Issues));
    }

    [Theory]
    [InlineData("""{ "path": "a.jpg" }""", WorkflowCheckCodes.InvalidDocument, "a list of file paths")]
    [InlineData("5", WorkflowCheckCodes.InvalidDocument, "a list of file paths")]
    [InlineData("""[ 5 ]""", WorkflowCheckCodes.InvalidDocument, "attachments[0] is a file's path")]
    [InlineData("""[ "a.jpg", "" ]""", WorkflowCheckCodes.BadValue, "attachments[1] is empty")]
    [InlineData("""[ "{vars.n}" ]""", WorkflowCheckCodes.TypeMismatch, "attachments[0] is int")]
    [InlineData("\"{vars.n}\"", WorkflowCheckCodes.TypeMismatch, "attachments is int")]
    [InlineData("""[ "docs:/../x.jpg" ]""", WorkflowCheckCodes.BadDocsPath, "cannot go up with '..'")]
    [InlineData("""[ "docs:/" ]""", WorkflowCheckCodes.BadDocsPath, "is the Documents folder itself")]
    [InlineData("""[ "{vars.nope}" ]""", WorkflowCheckCodes.UnknownReference, "")]
    public void BadAttachments_AreIssuesOnTheStep(string attachments, string code, string why)
    {
        var result = Check(attachments);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == code && i.StepId == "a" && i.Message.Contains(why, StringComparison.Ordinal));
    }

    [Fact]
    public void MoreThanTheMostAttachments_IsABadValue()
    {
        var list = "[" + string.Join(",", Enumerable.Range(0, WorkflowTell.MaxAttachments + 1).Select(i => $"\"f{i}.jpg\"")) + "]";
        Assert.Contains(Check(list).Issues, i => i.Code == WorkflowCheckCodes.BadValue && i.Message.Contains("the most is 20", StringComparison.Ordinal));
    }
}
