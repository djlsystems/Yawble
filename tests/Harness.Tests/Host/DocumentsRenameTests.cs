using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;
using static Harness.Tests.Host.DocumentsChangeSupport;

namespace Harness.Tests.Host;

/// <summary>
/// Renaming in the Documents dialog, through the real Host: in place, in a batch, never onto a name
/// already there, never the marker or the folder itself, nothing in a gone or retired folder;
/// recorded first, and a live team's watches told the old and the new paths.
/// </summary>
public sealed class DocumentsRenameTests(HostFixture host) : IClassFixture<HostFixture>
{
    private TeamDocuments Docs => host.Services.GetRequiredService<TeamDocuments>();

    [Fact]
    public async Task A_file_and_a_folder_are_renamed_in_place_in_one_batch()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "batch/a.md", "one");
        Docs.Write(host.Alpha, "batch/tree/inner.md", "two");

        var response = await client.RenameAsync(host.Alpha, ("batch/a.md", "b.md"), ("batch/tree", "grove"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [("batch/a.md", "batch/b.md", "done", (string?)null), ("batch/tree", "batch/grove", "done", null)],
            (await response.BodyAsync()).Results());
        Assert.Equal("one", File.ReadAllText(Docs.At(host.Alpha, "batch/b.md")));
        Assert.Equal("two", File.ReadAllText(Docs.At(host.Alpha, "batch/grove/inner.md")));
        Assert.False(File.Exists(Docs.At(host.Alpha, "batch/a.md")));
        Assert.False(Directory.Exists(Docs.At(host.Alpha, "batch/tree")));
    }

    [Fact]
    public async Task A_case_only_rename_is_not_a_clash()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "cases/notes.md");

        var response = await client.RenameAsync(host.Alpha, ("cases/notes.md", "Notes.md"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Notes.md"], Directory.GetFiles(Docs.At(host.Alpha, "cases")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_rename_onto_an_existing_name_is_refused_and_nothing_changes()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "taken/a.md", "a");
        Docs.Write(host.Alpha, "taken/b.md", "b");

        var response = await client.RenameAsync(host.Alpha, ("taken/a.md", "b.md"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("There is already b.md in taken.", await response.ErrorAsync());
        Assert.Equal("a", File.ReadAllText(Docs.At(host.Alpha, "taken/a.md")));
        Assert.Equal("b", File.ReadAllText(Docs.At(host.Alpha, "taken/b.md")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    public async Task A_bad_name_is_refused_with_a_sentence(string name)
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "named/a.md");

        var response = await client.RenameAsync(host.Alpha, ("named/a.md", name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("A name cannot be empty, \".\" or \"..\", or contain / \\ or :.", await response.ErrorAsync());
        Assert.True(File.Exists(Docs.At(host.Alpha, "named/a.md")));
    }

    [Fact]
    public async Task The_marker_is_neither_renamed_nor_a_name_to_rename_to()
    {
        using var client = await host.PersonAsync();
        Docs.EnsureFor(host.Alpha);
        Docs.Write(host.Alpha, "marked/a.md");

        var source = await client.RenameAsync(host.Alpha, (TeamPaths.MarkerFileName, "free.md"));
        Assert.Equal(HttpStatusCode.BadRequest, source.StatusCode);
        Assert.Equal("That file is this folder's own marker, not a document.", await source.ErrorAsync());
        Assert.True(File.Exists(TeamPaths.MarkerIn(Docs.RootFor(host.Alpha))));

        var target = await client.RenameAsync(host.Alpha, ("marked/a.md", TeamPaths.MarkerFileName));
        Assert.Equal(HttpStatusCode.BadRequest, target.StatusCode);
        Assert.Equal($"{TeamPaths.MarkerFileName} is reserved for the folder's marker.", await target.ErrorAsync());
        Assert.True(File.Exists(Docs.At(host.Alpha, "marked/a.md")));
    }

    [Fact]
    public async Task The_documents_folder_itself_cannot_be_renamed()
    {
        using var client = await host.PersonAsync();
        Docs.EnsureFor(host.Alpha);

        var response = await client.RenameAsync(host.Alpha, ("", "Elsewhere"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The documents folder itself cannot be renamed or moved.", await response.ErrorAsync());
        Assert.True(Directory.Exists(Docs.RootFor(host.Alpha)));
    }

    [Fact]
    public async Task Nothing_in_a_gone_or_retired_folder_can_be_renamed()
    {
        using var client = await host.PersonAsync();
        var gone = await GoneTeamAsync(host.Services, "RenameGone", "a.md");
        var retired = await RetiredFolderAsync(host.Services, "RenameRetired", "a.md");

        var fromGone = await client.RenameAsync(gone, ("a.md", "b.md"));
        Assert.Equal(HttpStatusCode.Conflict, fromGone.StatusCode);
        Assert.Equal(
            "RenameGone no longer exists. Its documents can be read, copied or moved out, and deleted, but nothing in them can be renamed.",
            await fromGone.ErrorAsync());
        Assert.True(File.Exists(Docs.At(gone, "a.md")));

        var fromRetired = await client.RenameAsync(retired, ("a.md", "b.md"));
        Assert.Equal(HttpStatusCode.Conflict, fromRetired.StatusCode);
        Assert.Equal(
            "This folder belongs to an earlier team called RenameRetired. Its documents can be read, copied or moved out, and deleted, but nothing in them can be renamed.",
            await fromRetired.ErrorAsync());
        Assert.True(File.Exists(Docs.At(retired, "a.md")));
    }

    [Fact]
    public async Task A_path_outside_the_folder_and_a_path_through_a_link_are_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need privileges on Windows.");
        using var client = await host.PersonAsync();
        Docs.Write(host.Beta, "theirs.md");
        var real = Docs.Folder(host.Alpha, "linked-real");
        File.WriteAllText(Path.Combine(real, "in.md"), "x");
        Directory.CreateSymbolicLink(Docs.At(host.Alpha, "linked"), real);

        var outside = await client.RenameAsync(host.Alpha, ($"../{host.Beta}/theirs.md", "mine.md"));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        Assert.Equal("That path is outside this team's documents.", await outside.ErrorAsync());

        var through = await client.RenameAsync(host.Alpha, ("linked/in.md", "out.md"));
        Assert.Equal(HttpStatusCode.Conflict, through.StatusCode);
        Assert.Equal("linked/in.md is reached through a link, and a link is never followed.", await through.ErrorAsync());
        Assert.True(File.Exists(Path.Combine(real, "in.md")));
    }

    [Fact]
    public async Task A_rename_to_the_current_name_is_skipped_changes_nothing_and_is_recorded_unchanged()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "same/a.md", "kept");
        var mark = await NoticeMarkAsync(host.Services);

        var response = await client.RenameAsync(host.Alpha, ("same/a.md", "a.md"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [("same/a.md", "same/a.md", "skipped", (string?)"same/a.md is already named a.md.")],
            (await response.BodyAsync()).Results());
        Assert.Equal(["a.md"], Directory.GetFiles(Docs.At(host.Alpha, "same")).Select(Path.GetFileName));
        Assert.Equal("kept", File.ReadAllText(Docs.At(host.Alpha, "same/a.md")));
        Assert.DoesNotContain(await NoticesSinceAsync(host.Services, mark), n => n.Team == host.Alpha);

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentsRenamed, host.Alpha, TestContext.Current.CancellationToken);
        Assert.NotNull(row);
        Assert.Equal("same/a.md", row.SubjectName);
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal(
            [("same/a.md", "same/a.md", 0)],
            detail.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => (i.GetProperty("from").GetString(), i.GetProperty("to").GetString(), i.GetProperty("files").GetInt32())));
    }

    [Fact]
    public async Task Every_rename_appends_documents_renamed_naming_each_old_and_new_path()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "rowed/a.md");
        Docs.Write(host.Alpha, "rowed/sub/b.md");

        Assert.Equal(HttpStatusCode.OK, (await client.RenameAsync(host.Alpha, ("rowed/a.md", "c.md"), ("rowed/sub", "deeper"))).StatusCode);

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentsRenamed, host.Alpha, TestContext.Current.CancellationToken);
        Assert.NotNull(row);
        Assert.Equal("person@example.test", row.ActorEmail);
        Assert.Equal("rowed/a.md", row.SubjectName);
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal(host.Alpha, detail.RootElement.GetProperty("folder").GetString());
        Assert.Equal(
            [("rowed/a.md", "rowed/c.md"), ("rowed/sub", "rowed/deeper")],
            detail.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => (i.GetProperty("from").GetString(), i.GetProperty("to").GetString())));
    }

    [Fact]
    public async Task A_rename_in_a_live_teams_folder_announces_old_and_new_paths_once_per_folder()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "told/a.md");
        Docs.Write(host.Alpha, "told/tree/x.md");
        Docs.Write(host.Alpha, "told/tree/deep/y.md");
        var mark = await NoticeMarkAsync(host.Services);

        Assert.Equal(HttpStatusCode.OK, (await client.RenameAsync(host.Alpha, ("told/a.md", "b.md"), ("told/tree", "wood"))).StatusCode);

        var notices = (await NoticesSinceAsync(host.Services, mark)).Where(n => n.Team == host.Alpha).ToList();
        Assert.Equal(["told", "told/tree", "told/tree/deep", "told/wood", "told/wood/deep"], notices.Select(n => n.Folder).Order(StringComparer.Ordinal));
        Assert.Equal(["told/a.md", "told/b.md"], notices.Single(n => n.Folder == "told").Changed);
        Assert.Equal(["told/tree/x.md"], notices.Single(n => n.Folder == "told/tree").Changed);
        Assert.Equal(["told/wood/deep/y.md"], notices.Single(n => n.Folder == "told/wood/deep").Changed);
    }
}
