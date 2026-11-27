using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Tests.Workflows.Engine;

/// <summary>"docs:/..." paths: every file step in the team's Documents folder, escapes refused, the dry run, and the check's issues.</summary>
public sealed class WorkflowDocsPathTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Bytes = [.. Enumerable.Range(0, 256).Select(i => (byte)i), 0xC3, 0x28, 0x0D, 0x0A];
    private readonly TempFolder _temp = new();
    private readonly FakeClock _clock = new(T0);
    private readonly RecordingJournal _journal = new();

    public void Dispose() => _temp.Dispose();

    private string Data => _temp.Sub("data");
    private string Docs => _temp.Sub(Path.Combine("team", "documents"));

    private WorkflowRunRequest Request(WorkflowDocument doc, bool docs = true) => new()
    {
        Document = doc,
        Actions = new FakeActions(),
        DataFolder = Data,
        TeamDocumentsFolder = docs ? Docs : null,
        Clock = _clock,
        Input = JsonNode.Parse("""{ "triggerId": "t1", "kind": "once", "firedAt": "2026-11-27T17:59:00Z" }"""),
    };

    private Task<WorkflowRunResult> Run(WorkflowRunRequest request) =>
        WorkflowEngine.RunAsync(request with { Clock = _clock }, _journal, TestContext.Current.CancellationToken);

    private static void Succeeded(WorkflowRunResult r) =>
        Assert.True(r.Status == WorkflowRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");

    private static void FailedWith(WorkflowRunResult r, string text)
    {
        Assert.Equal(WorkflowRunStatus.Failed, r.Status);
        Assert.Contains(text, r.Reason ?? "", StringComparison.Ordinal);
    }

    private string DocsFile(string relative, byte[]? bytes = null)
    {
        var path = Path.GetFullPath(Path.Combine(Docs, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? Bytes);
        return path;
    }

    private static WorkflowDocument Steps(string steps, string variables = "[]") => WorkflowSamples.Workflow(steps, variables);

    // ---- the engine ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WriteAppendReadLinesExistsAndDelete_AllWorkInTheDocumentsFolder()
    {
        var doc = Steps("""
            [ { "id": "a", "type": "file.writeAllText", "path": "docs:/smoke/smokesessions.json", "text": "[1]", "next": "b" },
              { "id": "b", "type": "file.appendLine", "path": "docs:/smoke/notes.txt", "text": "one", "next": "c" },
              { "id": "c", "type": "file.readAllText", "path": "docs:/smoke/smokesessions.json", "into": "text", "next": "d" },
              { "id": "d", "type": "file.readLines", "path": "docs:/smoke/notes.txt", "into": "lines", "next": "e" },
              { "id": "e", "type": "file.exists", "path": "docs:/smoke/notes.txt", "into": "there", "next": "f" },
              { "id": "f", "type": "file.delete", "path": "docs:/smoke/notes.txt", "next": "g" },
              { "id": "g", "type": "file.exists", "path": "docs:/smoke/notes.txt", "into": "gone", "next": "h" },
              { "id": "h", "type": "log", "text": "{vars.text} {vars.lines[0]} {vars.there} {vars.gone}", "next": null } ]
            """, """
            [ { "name": "text", "type": "string" }, { "name": "lines", "type": "list<string>" },
              { "name": "there", "type": "bool" }, { "name": "gone", "type": "bool" } ]
            """);

        var r = await Run(Request(doc));

        Succeeded(r);
        Assert.Equal("[1] one true false", r.Log[^1]);
        Assert.Equal("[1]", File.ReadAllText(Path.Combine(Docs, "smoke", "smokesessions.json")));
        Assert.False(File.Exists(Path.Combine(Docs, "smoke", "notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(Data, "smoke")));
    }

    [Fact]
    public async Task MoveAndCopy_TakeDocsPathsAtBothEnds_ToAndFromTheDataFolder()
    {
        DocsFile("in/a.jpg");
        DocsFile("in/b.jpg");
        var doc = Steps("""
            [ { "id": "a", "type": "file.move", "path": "docs:/in/a.jpg", "to": "docs:/out/", "into": "moved", "next": "b" },
              { "id": "b", "type": "file.copy", "path": "docs:/in/b.jpg", "to": "local/b.jpg", "next": "c" },
              { "id": "c", "type": "file.copy", "path": "local/b.jpg", "to": "docs:/out/b.jpg", "next": "d" },
              { "id": "d", "type": "file.copy", "path": "docs:/out/b.jpg", "to": "docs:/out", "next": "e" },
              { "id": "e", "type": "log", "text": "{vars.moved}|{steps.d}", "next": null } ]
            """, """[ { "name": "moved", "type": "string" } ]""");

        var r = await Run(Request(doc));

        Succeeded(r);
        Assert.Equal($"{Path.Combine(Docs, "out", "a.jpg")}|{Path.Combine(Docs, "out", "b (1).jpg")}", r.Log[^1]);
        Assert.False(File.Exists(Path.Combine(Docs, "in", "a.jpg")));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Docs, "out", "a.jpg")));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Data, "local", "b.jpg")));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Docs, "out", "b.jpg")));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Docs, "out", "b (1).jpg")));
    }

    [Fact]
    public async Task AListOfDocsOrAFolderInIt_GivesTheRealPaths()
    {
        var a = DocsFile("smoke/a.json", [1]);
        DocsFile("smoke/sub/b.json", [1, 2]);
        DocsFile("top.md", [1]);
        var doc = Steps("""
            [ { "id": "a", "type": "file.list", "path": "docs:/smoke", "pattern": "*.json", "into": "flat", "next": "b" },
              { "id": "b", "type": "file.list", "path": "docs:/", "recursive": true, "into": "all", "next": "c" },
              { "id": "c", "type": "log", "text": "{count(vars.flat)} {vars.flat[0].path} {count(vars.all)}", "next": null } ]
            """, """[ { "name": "flat", "type": "list<any>" }, { "name": "all", "type": "list<any>" } ]""");

        var r = await Run(Request(doc));

        Succeeded(r);
        Assert.Equal($"1 {a} 3", r.Log[^1]);
    }

    [Theory]
    [InlineData("docs:/../outside.txt", "cannot go up with '..'")]
    [InlineData("docs:/smoke/../../outside.txt", "cannot go up with '..'")]
    [InlineData("docs:/C:/Windows/x.txt", "not an absolute path")]
    [InlineData("docs://server/share/x.txt", "not an absolute path")]
    [InlineData("docs:\\x.txt", "is not a Documents path")]
    [InlineData("docs:x.txt", "is not a Documents path")]
    [InlineData("docs:/", "is a folder, not a file")]
    public async Task APathThatLeavesOrIsMalformed_FailsTheStep_AndWritesNothing(string path, string why)
    {
        var doc = Steps($$"""[ { "id": "a", "type": "file.writeAllText", "path": "{{path.Replace("\\", "\\\\")}}", "text": "x", "next": null } ]""");

        var r = await Run(Request(doc));

        FailedWith(r, why);
        Assert.Equal("a", r.FailedStepId);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "team", "outside.txt")));
        Assert.Empty(Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ADocsPathMadeByAnExpression_IsCheckedWhenItRuns()
    {
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "file.move", "path": "docs:/in/a.jpg", "to": "{settings.target}", "next": null } ]
            """, settings: """[ { "name": "target", "type": "string" } ]""");
        DocsFile("in/a.jpg");

        var r = await Run(Request(doc) with { Settings = new JsonObject { ["target"] = "docs:/in/../../a.jpg" } });

        FailedWith(r, "cannot go up with '..'");
        Assert.True(File.Exists(Path.Combine(Docs, "in", "a.jpg")));
    }

    [Fact]
    public async Task AnAbsolutePathIntoTheDocumentsFolder_IsAllowedAsTheSameRoot()
    {
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "file.writeAllText", "path": "{settings.file}", "text": "hi", "next": null } ]
            """, settings: """[ { "name": "file", "type": "string" } ]""");

        var r = await Run(Request(doc) with { Settings = new JsonObject { ["file"] = Path.Combine(Docs, "abs.txt") } });

        Succeeded(r);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(Docs, "abs.txt")));
    }

    [Fact]
    public async Task ARunWithoutATeamDocumentsFolder_FailsADocsStepClearly()
    {
        var doc = Steps("""[ { "id": "a", "type": "file.writeAllText", "path": "docs:/smoke/smokesessions.json", "text": "x", "next": null } ]""");

        var r = await Run(Request(doc, docs: false));

        FailedWith(r, "'docs:/smoke/smokesessions.json' is in the team's Documents folder, and this run has none");
    }

    [Fact]
    public async Task ADryRun_WritesItsOwnCopy_LogsTheRealPath_AndLaterStepsSeeIt()
    {
        DocsFile("in/a.jpg");
        var doc = Steps("""
            [ { "id": "a", "type": "file.writeAllText", "path": "docs:/smoke/smokesessions.json", "text": "[2]", "next": "b" },
              { "id": "b", "type": "file.readAllText", "path": "docs:/smoke/smokesessions.json", "into": "text", "next": "c" },
              { "id": "c", "type": "file.move", "path": "docs:/in/a.jpg", "to": "docs:/out/", "next": "d" },
              { "id": "d", "type": "file.list", "path": "docs:/", "recursive": true, "into": "all", "next": "e" },
              { "id": "e", "type": "file.list", "path": ".", "recursive": true, "into": "data", "next": "f" },
              { "id": "f", "type": "log", "text": "{vars.text} {count(vars.all)} {count(vars.data)}", "next": null } ]
            """, """[ { "name": "text", "type": "string" }, { "name": "all", "type": "list<any>" }, { "name": "data", "type": "list<any>" } ]""");
        var dry = Path.Combine(_temp.Path, "dry");
        Directory.CreateDirectory(Data);

        var r = await Run(Request(doc) with { DryRun = true, DryRunFolder = dry });

        Succeeded(r);
        var real = Path.Combine(Docs, "smoke", "smokesessions.json");
        var copy = Path.Combine(dry, "team-docs", "smoke", "smokesessions.json");
        Assert.Contains($"would write {real} (dry run: {copy})", r.Log);
        Assert.Contains($"would move {Path.Combine(Docs, "in", "a.jpg")} -> {Path.Combine(Docs, "out", "a.jpg")}", r.Log);
        // The dry run sees its own write and the move; the data folder's listing does not show the Documents copies.
        Assert.Equal("[2] 2 0", r.Log[^1]);
        Assert.False(File.Exists(real));
        Assert.True(File.Exists(Path.Combine(Docs, "in", "a.jpg")));
        Assert.False(Directory.Exists(Path.Combine(Docs, "out")));
        Assert.Equal("[2]", File.ReadAllText(copy));
    }

    // ---- the check ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("file.writeAllText", "path", "docs:smoke.json", "is not a Documents path")]
    [InlineData("file.readAllText", "path", "docs:/../x.json", "cannot go up with '..'")]
    [InlineData("file.appendLine", "path", "docs:/C:/x.txt", "not an absolute path")]
    [InlineData("file.delete", "path", "docs:/", "is the Documents folder itself, not a file")]
    [InlineData("file.list", "path", "docs:/a/../../b", "cannot go up with '..'")]
    [InlineData("file.move", "to", "docs:/../out/", "cannot go up with '..'")]
    [InlineData("file.copy", "path", "DOCS:/", "is the Documents folder itself, not a file")]
    [InlineData("file.copy", "to", "docs:/../{settings.x}", "cannot go up with '..'")]
    [InlineData("file.copy", "to", "docs:{settings.x}", "is not a Documents path")]
    public void AMalformedDocsPath_IsABadDocsPathIssue(string type, string property, string path, string why)
    {
        var fields = new JsonObject { ["id"] = "a", ["type"] = type, ["path"] = "docs:/in/a.jpg", ["next"] = null };
        if (type is "file.writeAllText" or "file.appendLine") fields["text"] = "x";
        if (type is "file.move" or "file.copy") fields["to"] = "docs:/out/";
        fields[property] = path;

        var result = WorkflowChecker.Check(
            WorkflowSamples.Workflow($"[ {fields.ToJsonString()} ]", settings: """[ { "name": "x", "type": "string" } ]"""),
            new WorkflowSchemaSet([]), FakeCatalog.MailAndSms());

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == WorkflowCheckCodes.BadDocsPath && i.StepId == "a"
            && i.Message.Contains($"{type}'s '{property}'", StringComparison.Ordinal) && i.Message.Contains(why, StringComparison.Ordinal));
    }

    [Fact]
    public void WellFormedDocsPaths_AndExpressions_CheckClean()
    {
        var result = WorkflowChecker.Check(WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "file.writeAllText", "path": "docs:/smoke/smokesessions.json", "text": "x", "next": "b" },
              { "id": "b", "type": "file.list", "path": "docs:/", "into": "all", "next": "c" },
              { "id": "c", "type": "file.move", "path": "docs:/in/{settings.x}.jpg", "to": "docs:/out/", "next": "d" },
              { "id": "d", "type": "file.copy", "path": "{settings.x}", "to": "docs:/", "next": null } ]
            """, """[ { "name": "all", "type": "list<any>" } ]""", settings: """[ { "name": "x", "type": "string" } ]"""),
            new WorkflowSchemaSet([]), FakeCatalog.MailAndSms());

        Assert.True(result.Ok, string.Join("\n", result.Issues));
    }
}
