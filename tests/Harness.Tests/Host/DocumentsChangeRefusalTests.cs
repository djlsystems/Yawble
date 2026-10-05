using System.Net;
using System.Net.Http.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;
using static Harness.Tests.Host.DocumentsChangeSupport;

namespace Harness.Tests.Host;

/// <summary>A Host with a copy limit of five, a gone team's folder and a retired one.</summary>
public sealed class DocumentsRefusalHost : IAsyncLifetime
{
    internal DocumentsChangeHost Host { get; private set; } = null!;

    public string Gone { get; private set; } = "";

    public string Retired { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        Host = await DocumentsChangeHost.StartAsync(copyLimit: 5);
        Gone = await GoneTeamAsync(Host.Services, "Gone", "was.md");
        Retired = await RetiredFolderAsync(Host.Services, "Earlier", "was.md");
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}

/// <summary>
/// EVERY REFUSAL, FOR EVERY VERB IT APPLIES TO, as a checklist rather than whichever class happened
/// to need it: the status, the exact sentence, the disk unchanged, no `documents.*` row and no
/// folder notice. A refusal is decided while planning, before anything is recorded.
/// </summary>
public sealed class DocumentsChangeRefusalTests(DocumentsRefusalHost fixture) : IClassFixture<DocumentsRefusalHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One refusal: what to set up under its own prefix, what to send, and what comes back.</summary>
    private sealed record Case(
        string Verb, Func<Setup, object> Arrange, HttpStatusCode Status, Func<Setup, string> Sentence, string Team = "Alpha");

    /// <summary>A case's own prefix in Alpha, and the means to put documents there.</summary>
    private sealed record Setup(string P, TeamDocuments Docs, string Gone, string Retired)
    {
        public string File(string relative, string folder = "Alpha") => Docs.Write(folder, $"{P}/{relative}");

        public string Dir(string relative, string folder = "Alpha") => Docs.Folder(folder, $"{P}/{relative}");

        public void Link(string relative, string target) =>
            System.IO.File.CreateSymbolicLink(Docs.At("Alpha", $"{P}/{relative}"), target);

        public void FolderLink(string relative, string target) =>
            Directory.CreateSymbolicLink(Docs.At("Alpha", $"{P}/{relative}"), target);
    }

    private static object Rename(params (string Path, string Name)[] items) =>
        new { items = items.Select(item => new { path = item.Path, name = item.Name }) };

    private static object Transfer(string folder, string? path, params object[] items) =>
        new { to = new { folder, path }, items = items.Select(item => item is string p ? new { path = p } : item) };

    private const string Through = " is reached through a link, and a link is never followed.";
    private const string IsLink = " is a link. A link is never followed, renamed, moved or copied.";
    private const string GoneAdd = "Gone no longer exists. Its documents can be read, copied or moved out, and deleted, but nothing can be added to them.";
    private const string RetiredAdd = "This folder belongs to an earlier team called Earlier. Its documents can be read, copied or moved out, and deleted, but nothing can be added to them.";

    private static readonly Dictionary<string, Case> Cases = new()
    {
        // ---- rename
        ["rename R1"] = new("rename", s => Rename(("a.md", "b.md")), HttpStatusCode.NotFound, _ => "No team 'Nobody'.", Team: "Nobody"),
        ["rename R5 gone"] = new("rename", s => Rename(("was.md", "is.md")), HttpStatusCode.Conflict,
            _ => "Gone no longer exists. Its documents can be read, copied or moved out, and deleted, but nothing in them can be renamed.", Team: "<gone>"),
        ["rename R5 retired"] = new("rename", s => Rename(("was.md", "is.md")), HttpStatusCode.Conflict,
            _ => "This folder belongs to an earlier team called Earlier. Its documents can be read, copied or moved out, and deleted, but nothing in them can be renamed.", Team: "<retired>"),
        ["rename R6"] = new("rename", s => Rename(("../Beta/x.md", "y.md")), HttpStatusCode.BadRequest, _ => "That path is outside this team's documents."),
        ["rename R7"] = new("rename", s => Rename(($"{s.P}/none.md", "b.md")), HttpStatusCode.NotFound, s => $"No such document: {s.P}/none.md."),
        ["rename R8"] = new("rename", s => Rename(("", "Other")), HttpStatusCode.BadRequest, _ => "The documents folder itself cannot be renamed or moved."),
        ["rename R9 source"] = new("rename", s => Rename((TeamPaths.MarkerFileName, "b.md")), HttpStatusCode.BadRequest, _ => "That file is this folder's own marker, not a document."),
        ["rename R9 name"] = new("rename", s => { s.File("a.md"); return Rename(($"{s.P}/a.md", TeamPaths.MarkerFileName)); },
            HttpStatusCode.BadRequest, _ => $"{TeamPaths.MarkerFileName} is reserved for the folder's marker."),
        ["rename R10"] = new("rename", s => { s.Link("pointer.md", s.File("target.md")); return Rename(($"{s.P}/pointer.md", "b.md")); },
            HttpStatusCode.Conflict, s => $"{s.P}/pointer.md{IsLink}"),
        ["rename R11"] = new("rename", s => { s.File("real/in.md"); s.FolderLink("door", s.Docs.At("Alpha", $"{s.P}/real")); return Rename(($"{s.P}/door/in.md", "b.md")); },
            HttpStatusCode.Conflict, s => $"{s.P}/door/in.md{Through}"),
        ["rename R15"] = new("rename", s => { s.File("a.md"); return Rename(($"{s.P}/a.md", "x/y")); },
            HttpStatusCode.BadRequest, _ => "A name cannot be empty, \".\" or \"..\", or contain / \\ or :."),
        ["rename R16"] = new("rename", s => { s.File("a.md"); s.File("b.md"); return Rename(($"{s.P}/a.md", "b.md")); },
            HttpStatusCode.Conflict, s => $"There is already b.md in {s.P}."),
        ["rename R19"] = new("rename", s => Rename(), HttpStatusCode.BadRequest, _ => "Name at least one document."),
        ["rename R21 twice"] = new("rename", s => { s.File("a.md"); return Rename(($"{s.P}/a.md", "b.md"), ($"{s.P}/a.md", "c.md")); },
            HttpStatusCode.BadRequest, s => $"{s.P}/a.md is named twice."),
        ["rename R21 onto one"] = new("rename", s => { s.File("a.md"); s.File("b.md"); return Rename(($"{s.P}/a.md", "c.md"), ($"{s.P}/b.md", "c.md")); },
            HttpStatusCode.BadRequest, s => $"{s.P}/a.md and {s.P}/b.md would both become {s.P}/c.md."),

        // ---- move
        ["move R1"] = new("move", s => Transfer("Beta", null, "a.md"), HttpStatusCode.NotFound, _ => "No team 'Nobody'.", Team: "Nobody"),
        ["move R2"] = new("move", s => { s.File("a.md"); return Transfer("Nowhere", null, $"{s.P}/a.md"); }, HttpStatusCode.NotFound, _ => "No documents folder 'Nowhere'."),
        ["move R3"] = new("move", s => { s.File("a.md"); return Transfer(s.Gone, null, $"{s.P}/a.md"); }, HttpStatusCode.Conflict, _ => GoneAdd),
        ["move R4"] = new("move", s => { s.File("a.md"); return Transfer(s.Retired, null, $"{s.P}/a.md"); }, HttpStatusCode.Conflict, _ => RetiredAdd),
        ["move R6 source"] = new("move", s => Transfer("Beta", null, "../Beta/x.md"), HttpStatusCode.BadRequest, _ => "That path is outside this team's documents."),
        ["move R6 destination"] = new("move", s => { s.File("a.md"); return Transfer("Beta", "../Alpha", $"{s.P}/a.md"); }, HttpStatusCode.BadRequest, _ => "That path is outside this team's documents."),
        ["move R7"] = new("move", s => Transfer("Beta", null, $"{s.P}/none.md"), HttpStatusCode.NotFound, s => $"No such document: {s.P}/none.md."),
        ["move R8"] = new("move", s => Transfer("Beta", null, ""), HttpStatusCode.BadRequest, _ => "The documents folder itself cannot be renamed or moved."),
        ["move R9"] = new("move", s => Transfer("Beta", null, TeamPaths.MarkerFileName), HttpStatusCode.BadRequest, _ => "That file is this folder's own marker, not a document."),
        ["move R10"] = new("move", s => { s.Link("pointer.md", s.File("target.md")); return Transfer("Beta", null, $"{s.P}/pointer.md"); },
            HttpStatusCode.Conflict, s => $"{s.P}/pointer.md{IsLink}"),
        ["move R11 source"] = new("move", s => { s.File("real/in.md"); s.FolderLink("door", s.Docs.At("Alpha", $"{s.P}/real")); return Transfer("Beta", null, $"{s.P}/door/in.md"); },
            HttpStatusCode.Conflict, s => $"{s.P}/door/in.md{Through}"),
        ["move R11 destination"] = new("move", s => { s.File("a.md"); s.FolderLink("door", s.Dir("real")); return Transfer("Alpha", $"{s.P}/door", $"{s.P}/a.md"); },
            HttpStatusCode.Conflict, s => $"{s.P}/door{Through}"),
        ["move R12"] = new("move", s => { s.File("holder/a.md"); s.Link("holder/inner", s.File("target.md")); return Transfer("Beta", null, $"{s.P}/holder"); },
            HttpStatusCode.Conflict, s => $"{s.P}/holder holds a link ({s.P}/holder/inner). A link is never moved into another team's documents; delete it first."),
        ["move R13"] = new("move", s => { s.File("tree/deep/a.md"); return Transfer("Alpha", $"{s.P}/tree/deep", $"{s.P}/tree"); },
            HttpStatusCode.Conflict, s => $"{s.P}/tree cannot be moved into itself."),
        ["move R14 missing"] = new("move", s => { s.File("a.md"); return Transfer("Alpha", $"{s.P}/nope", $"{s.P}/a.md"); }, HttpStatusCode.NotFound, s => $"No such folder: {s.P}/nope."),
        ["move R14 a file"] = new("move", s => { s.File("a.md"); s.File("b.md"); return Transfer("Alpha", $"{s.P}/b.md", $"{s.P}/a.md"); },
            HttpStatusCode.Conflict, s => $"{s.P}/b.md is a file, not a folder."),
        ["move R17"] = new("move", s => { s.File("from/a.md"); s.File("to/a.md"); return Transfer("Alpha", $"{s.P}/to", $"{s.P}/from/a.md"); },
            HttpStatusCode.Conflict, s => $"a.md is already in {s.P}/to. Choose Keep both, Replace or Skip."),
        ["move R18"] = new("move", s => { s.File("a.md"); return Transfer("Beta", null, Item($"{s.P}/a.md", "merge")); }, HttpStatusCode.BadRequest, _ => "onClash is keep-both, replace or skip."),
        ["move R19 empty"] = new("move", s => Transfer("Beta", null), HttpStatusCode.BadRequest, _ => "Name at least one document."),
        ["move R19 1001"] = new("move", s => Transfer("Beta", null, [.. Enumerable.Range(0, 1001).Select(i => (object)$"{s.P}/{i}.md")]),
            HttpStatusCode.BadRequest, _ => "At most 1000 documents at a time."),
        ["move R21 spelled two ways"] = new("move", s => { s.File("a.md"); return Transfer("Beta", null, $"{s.P}/a.md", $"{s.P}/./a.md"); },
            HttpStatusCode.BadRequest, s => $"{s.P}/a.md is named twice."),
        ["move R21 twice"] = new("move", s => { s.File("a.md"); return Transfer("Beta", null, $"{s.P}/a.md", $"{s.P}/a.md"); },
            HttpStatusCode.BadRequest, s => $"{s.P}/a.md is named twice."),
        ["move R21 onto one"] = new("move", s => { s.File("x/a.md"); s.File("y/a.md"); s.Dir("to"); return Transfer("Alpha", $"{s.P}/to", $"{s.P}/x/a.md", $"{s.P}/y/a.md"); },
            HttpStatusCode.BadRequest, s => $"{s.P}/x/a.md and {s.P}/y/a.md would both become {s.P}/to/a.md."),
        ["move no destination"] = new("move", s => { s.File("a.md"); return Transfer("", null, $"{s.P}/a.md"); },
            HttpStatusCode.BadRequest, _ => "Name the folder to move them into in to.folder."),
        ["move folder and inside"] = new("move", s => { s.File("f/a.md"); s.Dir("to"); return Transfer("Alpha", $"{s.P}/to", $"{s.P}/f", $"{s.P}/f/a.md"); },
            HttpStatusCode.BadRequest, s => $"{s.P}/f/a.md is inside {s.P}/f, which is named too."),
        ["move replace own holder"] = new("move", s => { s.File("a/a/x.md"); return Transfer("Alpha", s.P, Item($"{s.P}/a/a", "replace")); },
            HttpStatusCode.Conflict, s => $"{s.P}/a/a is inside {s.P}/a, which it would replace."),

        // ---- copy
        ["copy R1"] = new("copy", s => Transfer("Beta", null, "a.md"), HttpStatusCode.NotFound, _ => "No team 'Nobody'.", Team: "Nobody"),
        ["copy R2"] = new("copy", s => { s.File("a.md"); return Transfer("Nowhere", null, $"{s.P}/a.md"); }, HttpStatusCode.NotFound, _ => "No documents folder 'Nowhere'."),
        ["copy R3"] = new("copy", s => { s.File("a.md"); return Transfer(s.Gone, null, $"{s.P}/a.md"); }, HttpStatusCode.Conflict, _ => GoneAdd),
        ["copy R4"] = new("copy", s => { s.File("a.md"); return Transfer(s.Retired, null, $"{s.P}/a.md"); }, HttpStatusCode.Conflict, _ => RetiredAdd),
        ["copy R6"] = new("copy", s => Transfer("Beta", null, "../Beta/x.md"), HttpStatusCode.BadRequest, _ => "That path is outside this team's documents."),
        ["copy R7"] = new("copy", s => Transfer("Beta", null, $"{s.P}/none.md"), HttpStatusCode.NotFound, s => $"No such document: {s.P}/none.md."),
        ["copy R9"] = new("copy", s => Transfer("Beta", null, TeamPaths.MarkerFileName), HttpStatusCode.BadRequest, _ => "That file is this folder's own marker, not a document."),
        ["copy R10"] = new("copy", s => { s.Link("pointer.md", s.File("target.md")); return Transfer("Beta", null, $"{s.P}/pointer.md"); },
            HttpStatusCode.Conflict, s => $"{s.P}/pointer.md{IsLink}"),
        ["copy R11"] = new("copy", s => { s.File("real/in.md"); s.FolderLink("door", s.Docs.At("Alpha", $"{s.P}/real")); return Transfer("Beta", null, $"{s.P}/door/in.md"); },
            HttpStatusCode.Conflict, s => $"{s.P}/door/in.md{Through}"),
        ["copy R13"] = new("copy", s => { s.File("tree/deep/a.md"); return Transfer("Alpha", $"{s.P}/tree/deep", $"{s.P}/tree"); },
            HttpStatusCode.Conflict, s => $"{s.P}/tree cannot be copied into itself."),
        ["copy R14"] = new("copy", s => { s.File("a.md"); return Transfer("Alpha", $"{s.P}/nope", $"{s.P}/a.md"); }, HttpStatusCode.NotFound, s => $"No such folder: {s.P}/nope."),
        ["copy R17"] = new("copy", s => { s.File("from/a.md"); s.File("to/a.md"); s.File("to/b.md"); s.File("from/b.md"); return Transfer("Alpha", $"{s.P}/to", $"{s.P}/from/a.md", $"{s.P}/from/b.md"); },
            HttpStatusCode.Conflict, s => $"2 of these are already in {s.P}/to: a.md, b.md. Choose Keep both, Replace or Skip for each."),
        ["copy R18"] = new("copy", s => { s.File("a.md"); return Transfer("Beta", null, Item($"{s.P}/a.md", "overwrite")); }, HttpStatusCode.BadRequest, _ => "onClash is keep-both, replace or skip."),
        ["copy R19"] = new("copy", s => Transfer("Beta", null), HttpStatusCode.BadRequest, _ => "Name at least one document."),
        ["copy R20"] = new("copy", s => { foreach (var n in Enumerable.Range(0, 6)) s.File($"many/{n}.md"); return Transfer("Beta", null, $"{s.P}/many"); },
            HttpStatusCode.Conflict, _ => "That is 6 files; at most 5 can be copied at a time."),
        ["copy R21"] = new("copy", s => { s.File("a.md"); return Transfer("Beta", null, $"{s.P}/a.md", $"{s.P}/a.md"); },
            HttpStatusCode.BadRequest, s => $"{s.P}/a.md is named twice."),
        ["copy no destination"] = new("copy", s => { s.File("a.md"); return Transfer("", null, $"{s.P}/a.md"); },
            HttpStatusCode.BadRequest, _ => "Name the folder to copy them into in to.folder."),
        ["copy folder and inside"] = new("copy", s => { s.File("f/a.md"); s.Dir("to"); return Transfer("Alpha", $"{s.P}/to", $"{s.P}/f", $"{s.P}/f/a.md"); },
            HttpStatusCode.BadRequest, s => $"{s.P}/f/a.md is inside {s.P}/f, which is named too."),
        ["copy replace own holder"] = new("copy", s => { s.File("a/a/x.md"); return Transfer("Alpha", s.P, Item($"{s.P}/a/a", "replace")); },
            HttpStatusCode.Conflict, s => $"{s.P}/a/a is inside {s.P}/a, which it would replace."),
    };

    public static TheoryData<string> Refusals => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Every_refusal_is_its_sentence_and_nothing_is_written_recorded_or_announced(string refusal)
    {
        var host = fixture.Host;
        var @case = Cases[refusal];
        var setup = new Setup($"r{Array.IndexOf([.. Cases.Keys], refusal)}", host.Docs, fixture.Gone, fixture.Retired);
        var body = @case.Arrange(setup);
        var team = @case.Team switch { "<gone>" => fixture.Gone, "<retired>" => fixture.Retired, var named => named };

        var before = Snapshot(host.Docs.TenantRoot);
        var rows = (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 1000, Ct)).Events
            .Count(e => e.Action.StartsWith("documents.", StringComparison.Ordinal));
        var mark = await NoticeMarkAsync(host.Services);

        var response = await host.Client.PostAsync($"/api/teams/{team}/documents/{@case.Verb}", JsonContent.Create(body), Ct);

        Assert.Equal(@case.Status, response.StatusCode);
        Assert.Equal(@case.Sentence(setup), await response.ErrorAsync());
        Assert.Equal(before, Snapshot(host.Docs.TenantRoot));
        Assert.Equal(rows, (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 1000, Ct)).Events
            .Count(e => e.Action.StartsWith("documents.", StringComparison.Ordinal)));
        Assert.Empty(await NoticesSinceAsync(host.Services, mark));
    }

    /// <summary>Every entry under the documents root: a file with its contents, a folder, a link with its target.</summary>
    private static List<string> Snapshot(string root) =>
        [.. new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Select(entry => entry.LinkTarget is { } target
                ? $"{entry.FullName} -> {target}"
                : entry is DirectoryInfo ? $"{entry.FullName}/" : $"{entry.FullName} = {File.ReadAllText(entry.FullName)}")
            .Order(StringComparer.Ordinal)];
}
