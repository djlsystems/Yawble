using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Yawble.Core.Skills;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Tests.Workflows.Engine;

/// <summary>file.move, file.copy and file.list: bytes, names on a clash, grants at both ends, the dry run and the listing.</summary>
public sealed class WorkflowFileMoveListTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Bytes = [.. Enumerable.Range(0, 256).Select(i => (byte)i), 0xFF, 0xFE, 0x00, 0xC3, 0x28, 0x0D, 0x0A];
    private readonly TempFolder _temp = new();
    private readonly FakeClock _clock = new(T0);
    private readonly RecordingJournal _journal = new();

    public void Dispose() => _temp.Dispose();

    private string Data => _temp.Sub("data");

    private WorkflowRunRequest Request(WorkflowDocument doc) => new()
    {
        Document = doc,
        Actions = new FakeActions(),
        DataFolder = Data,
        Clock = _clock,
        Input = JsonNode.Parse("""{ "triggerId": "t1", "kind": "once", "firedAt": "2026-11-23T17:59:00Z" }"""),
    };

    private Task<WorkflowRunResult> Run(WorkflowRunRequest request) =>
        WorkflowEngine.RunAsync(request with { Clock = _clock }, _journal, TestContext.Current.CancellationToken);

    private static void Succeeded(WorkflowRunResult r) =>
        Assert.True(r.Status == WorkflowRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");

    private JsonArray Output(string stepId) =>
        Assert.IsType<JsonArray>(_journal.Completed.Last(c => c.Record.StepId == stepId).Record.Output);

    private string DataFile(string relative, byte[]? bytes = null)
    {
        var path = Path.Combine(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? Bytes);
        return path;
    }

    private static WorkflowDocument OneStep(string step, string variables = """[ { "name": "to", "type": "string" } ]""", string settings = "[]") =>
        WorkflowSamples.Workflow($$"""[ {{step}}, { "id": "z", "type": "log", "text": "{vars.to}", "next": null } ]""", variables, settings);

    // ---- move and copy ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMoveKeepsEveryByte_IntoAFolderEndingInASeparator_AndGivesTheFinalPath()
    {
        var source = DataFile("in/photo.jpg");
        var r = await Run(Request(OneStep("""{ "id": "a", "type": "file.move", "path": "in/photo.jpg", "to": "out/", "into": "to", "next": "z" }""")));

        Succeeded(r);
        var target = Path.Combine(Data, "out", "photo.jpg");
        Assert.Equal(target, r.Log[^1]);
        Assert.False(File.Exists(source));
        Assert.Equal(Bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task ACopyKeepsEveryByte_AndLeavesTheSource()
    {
        var source = DataFile("in/photo.heic");
        var r = await Run(Request(OneStep("""{ "id": "a", "type": "file.copy", "path": "in/photo.heic", "to": "copies/renamed.heic", "into": "to", "next": "z" }""")));

        Succeeded(r);
        var target = Path.Combine(Data, "copies", "renamed.heic");
        Assert.Equal(target, r.Log[^1]);
        Assert.Equal(Bytes, File.ReadAllBytes(source));
        Assert.Equal(Bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task ToNamingAFolderThatExists_MeansTheSameNameInsideIt()
    {
        DataFile("in/a.png");
        Directory.CreateDirectory(Path.Combine(Data, "sorted"));
        var r = await Run(Request(OneStep("""{ "id": "a", "type": "file.move", "path": "in/a.png", "to": "sorted", "into": "to", "next": "z" }""")));

        Succeeded(r);
        Assert.Equal(Path.Combine(Data, "sorted", "a.png"), r.Log[^1]);
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Data, "sorted", "a.png")));
    }

    [Theory]
    [InlineData("file.move")]
    [InlineData("file.copy")]
    public async Task ANameAlreadyThere_TakesTheFirstFreeSuffix_AndTheOtherFileStays(string type)
    {
        DataFile("in/a.jpg");
        DataFile("out/a.jpg", [1]);
        DataFile("out/a (1).jpg", [2]);
        var r = await Run(Request(OneStep($$"""{ "id": "a", "type": "{{type}}", "path": "in/a.jpg", "to": "out/a.jpg", "into": "to", "next": "z" }""")));

        Succeeded(r);
        Assert.Equal(Path.Combine(Data, "out", "a (2).jpg"), r.Log[^1]);
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Data, "out", "a (2).jpg")));
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(Data, "out", "a.jpg")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(Data, "out", "a (1).jpg")));
        Assert.Equal(type == "file.copy", File.Exists(Path.Combine(Data, "in", "a.jpg")));
    }

    [Theory]
    [InlineData("file.move")]
    [InlineData("file.copy")]
    public async Task OverwriteReplacesTheFileThere(string type)
    {
        DataFile("in/a.jpg");
        DataFile("out/a.jpg", [1]);
        var r = await Run(Request(OneStep($$"""{ "id": "a", "type": "{{type}}", "path": "in/a.jpg", "to": "out/", "overwrite": true, "into": "to", "next": "z" }""")));

        Succeeded(r);
        Assert.Equal(Path.Combine(Data, "out", "a.jpg"), r.Log[^1]);
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Data, "out", "a.jpg")));
        Assert.False(File.Exists(Path.Combine(Data, "out", "a (1).jpg")));
    }

    [Fact]
    public async Task AMissingSourceFailsTheStep()
    {
        var r = await Run(Request(OneStep("""{ "id": "a", "type": "file.copy", "path": "nope.jpg", "to": "out/", "next": "z" }""")));
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("'nope.jpg' does not exist", r.Reason);
    }

    [Theory]
    [InlineData("file.move", "source")]
    [InlineData("file.move", "target")]
    [InlineData("file.copy", "source")]
    [InlineData("file.copy", "target")]
    public async Task AReadOnlyGrant_IsRefusedAtEitherEnd(string type, string readOnlyEnd)
    {
        var writable = _temp.Sub("granted-rw");
        var readOnly = _temp.Sub("granted-ro");
        var sourceFolder = readOnlyEnd == "source" ? readOnly : writable;
        var targetFolder = readOnlyEnd == "target" ? readOnly : writable;
        var source = Path.Combine(sourceFolder, "a.jpg");
        File.WriteAllBytes(source, Bytes);
        var doc = OneStep($$"""{ "id": "a", "type": "{{type}}", "path": "{settings.from}", "to": "{settings.to}", "next": "z" }""",
            settings: """[ { "name": "from", "type": "string" }, { "name": "to", "type": "string" } ]""");
        var settings = new JsonObject { ["from"] = source, ["to"] = Path.Combine(targetFolder, "moved.jpg") };

        var r = await Run(Request(doc) with { Settings = settings, AllowedFolders = [new(writable, true), new(readOnly, false)] });

        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("granted read-only", r.Reason);
        Assert.Equal(Bytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(targetFolder, "moved.jpg")));
    }

    [Fact]
    public async Task BetweenTwoWritableGrants_AMoveWorks()
    {
        var one = _temp.Sub("granted-1");
        var two = _temp.Sub("granted-2");
        var source = Path.Combine(one, "a.jpg");
        File.WriteAllBytes(source, Bytes);
        var doc = OneStep("""{ "id": "a", "type": "file.move", "path": "{settings.from}", "to": "{settings.to}", "into": "to", "next": "z" }""",
            settings: """[ { "name": "from", "type": "string" }, { "name": "to", "type": "string" } ]""");
        var settings = new JsonObject { ["from"] = source, ["to"] = two + Path.DirectorySeparatorChar };

        var r = await Run(Request(doc) with { Settings = settings, AllowedFolders = [new(one, true), new(two, true)] });

        Succeeded(r);
        Assert.Equal(Path.Combine(two, "a.jpg"), r.Log[^1]);
        Assert.False(File.Exists(source));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(two, "a.jpg")));
    }

    [Theory]
    [InlineData("file.move", "../outside.jpg")]
    [InlineData("file.copy", "../outside.jpg")]
    [InlineData("file.copy", "{settings.abs}")]
    public async Task ATargetOutsideTheDataFolderAndGrants_IsRefused(string type, string to)
    {
        var source = DataFile("a.jpg");
        var doc = OneStep($$"""{ "id": "a", "type": "{{type}}", "path": "a.jpg", "to": "{{to}}", "next": "z" }""",
            settings: """[ { "name": "abs", "type": "string" } ]""");
        var abs = Path.Combine(_temp.Sub("elsewhere"), "outside.jpg");

        var r = await Run(Request(doc) with { Settings = new JsonObject { ["abs"] = abs } });

        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("outside the member's data folder and the team's folder grants", r.Reason);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "outside.jpg")));
        Assert.False(File.Exists(abs));
    }

    // ---- dry run ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADryRunLogsTheMoveAndCopy_ChangesNoRealFile_AndLaterStepsSeeWhatItDid()
    {
        var source = DataFile("in/a.jpg");
        DataFile("in/b.jpg");
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "file.move", "path": "in/a.jpg", "to": "out/", "into": "to", "next": "b" },
              { "id": "b", "type": "file.copy", "path": "in/b.jpg", "to": "out/", "next": "c" },
              { "id": "c", "type": "file.exists", "path": "in/a.jpg", "next": "d" },
              { "id": "d", "type": "file.exists", "path": "out/a.jpg", "next": "e" },
              { "id": "e", "type": "file.list", "path": "in", "into": "inList", "next": "f" },
              { "id": "f", "type": "file.list", "path": "out", "into": "outList", "next": "g" },
              { "id": "g", "type": "file.copy", "path": "out/a.jpg", "to": "out/", "next": "h" },
              { "id": "h", "type": "log", "text": "{steps.c} {steps.d} {count(vars.inList)} {count(vars.outList)} {vars.inList[0].name} {steps.g}", "next": null } ]
            """, variables: """[ { "name": "to", "type": "string" }, { "name": "inList", "type": "list<any>" }, { "name": "outList", "type": "list<any>" } ]""");
        var dry = Path.Combine(_temp.Path, "dry");

        var r = await Run(Request(doc) with { DryRun = true, DryRunFolder = dry });

        Succeeded(r);
        var outA = Path.Combine(Data, "out", "a.jpg");
        Assert.Contains($"would move {source} -> {outA}", r.Log);
        Assert.Contains($"would copy {Path.Combine(Data, "in", "b.jpg")} -> {Path.Combine(Data, "out", "b.jpg")}", r.Log);
        Assert.Equal($"false true 1 2 b.jpg {Path.Combine(Data, "out", "a (1).jpg")}", r.Log[^1]);
        Assert.Equal(Bytes, File.ReadAllBytes(source));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Data, "in", "b.jpg")));
        Assert.False(Directory.Exists(Path.Combine(Data, "out")));
    }

    // ---- list ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AListTakesThePatterns_AnyCase_SortedByPath_WithEachFilesFields()
    {
        DataFile("photos/b.png", [1, 2, 3]);
        DataFile("photos/a.JPG", [1]);
        DataFile("photos/notes.txt", [1]);
        DataFile("photos/sub/c.jpeg", [1, 2]);
        var when = new DateTime(2026, 11, 20, 9, 15, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(Data, "photos", "b.png"), when);
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "file.list", "path": "photos", "pattern": "*.jpg;*.jpeg; *.heic;*.png", "into": "flat", "next": "b" },
              { "id": "b", "type": "file.list", "path": "photos", "pattern": "*.jpg;*.jpeg;*.heic;*.png", "recursive": true, "into": "deep", "next": "c" },
              { "id": "c", "type": "file.list", "path": "photos", "into": "all", "next": null } ]
            """, variables: """[ { "name": "flat", "type": "list<any>" }, { "name": "deep", "type": "list<any>" }, { "name": "all", "type": "list<any>" } ]""");

        var r = await Run(Request(doc));

        Succeeded(r);
        var flat = Output("a");
        Assert.Equal(["a.JPG", "b.png"], flat.Select(f => (string?)f!["name"]));
        var b = flat[1]!;
        Assert.Equal(Path.Combine(Data, "photos", "b.png"), (string?)b["path"]);
        Assert.Equal(3, (long?)b["size"]);
        Assert.Equal("2026-11-20T09:15:00Z", (string?)b["modifiedAt"]);
        var deep = Output("b");
        Assert.Equal([Path.Combine(Data, "photos", "a.JPG"), Path.Combine(Data, "photos", "b.png"), Path.Combine(Data, "photos", "sub", "c.jpeg")],
            deep.Select(f => (string?)f!["path"]));
        Assert.Equal(["a.JPG", "b.png", "notes.txt"], Output("c").Select(f => (string?)f!["name"]));
    }

    [Fact]
    public async Task ListingAFolderThatIsNotThereFails()
    {
        var r = await Run(Request(WorkflowSamples.Workflow("""[ { "id": "a", "type": "file.list", "path": "nowhere", "next": null } ]""")));
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("the folder 'nowhere' does not exist", r.Reason);
    }

    [Fact]
    public async Task AReadOnlyGrantCanBeListed_ButNotOutsideTheGrants()
    {
        var readOnly = _temp.Sub("granted-ro");
        File.WriteAllBytes(Path.Combine(readOnly, "a.jpg"), Bytes);
        var elsewhere = _temp.Sub("elsewhere");
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "file.list", "path": "{settings.ro}", "next": "b" },
              { "id": "b", "type": "file.list", "path": "{settings.other}", "next": null } ]
            """, settings: """[ { "name": "ro", "type": "string" }, { "name": "other", "type": "string" } ]""");

        var r = await Run(Request(doc) with
        {
            Settings = new JsonObject { ["ro"] = readOnly, ["other"] = elsewhere },
            AllowedFolders = [new(readOnly, false)],
        });

        Assert.Equal("b", r.FailedStepId);
        Assert.Contains("outside the member's data folder and the team's folder grants", r.Reason);
    }

    // ---- the skill's example ------------------------------------------------------------------------------------------

    internal static string PhotoExample()
    {
        var body = Assert.Single(SkillCatalog.BuiltIns, s => s.Name == "workflows").Body.Replace("\r\n", "\n", StringComparison.Ordinal);
        var m = Regex.Match(body, @"## Example: moves new photos[^\n]*\n.*?```json\n(?<json>.*?)\n```", RegexOptions.Singleline);
        Assert.True(m.Success, "the skill has the photos example");
        return m.Groups["json"].Value;
    }

    [Fact]
    public async Task TheSkillsPhotoExample_MovesThePhotosIntoSmokeSessions_KeepingBothOnAClash()
    {
        var folder = _temp.Sub("photos");
        File.WriteAllBytes(Path.Combine(folder, "IMG_1.jpg"), Bytes);
        File.WriteAllBytes(Path.Combine(folder, "IMG_2.HEIC"), Bytes);
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [1]);
        Directory.CreateDirectory(Path.Combine(folder, "SmokeSessions"));
        File.WriteAllBytes(Path.Combine(folder, "SmokeSessions", "IMG_1.jpg"), [9]);
        var doc = WorkflowDocument.Parse(PhotoExample());

        var r = await Run(Request(doc) with { Settings = new JsonObject { ["folder"] = folder }, AllowedFolders = [new(folder, true)] });

        Succeeded(r);
        Assert.Equal("2 photos moved into SmokeSessions", r.Log[^1]);
        Assert.Equal(["notes.txt"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(["IMG_1 (1).jpg", "IMG_1.jpg", "IMG_2.HEIC"],
            Directory.GetFiles(Path.Combine(folder, "SmokeSessions")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(folder, "SmokeSessions", "IMG_1 (1).jpg")));
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(folder, "SmokeSessions", "IMG_1.jpg")));
    }
}
