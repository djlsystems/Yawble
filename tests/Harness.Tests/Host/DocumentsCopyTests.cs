using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;
using static Harness.Tests.Host.DocumentsChangeSupport;

namespace Harness.Tests.Host;

/// <summary>
/// Copying in the Documents dialog, through the real Host: within a team and into another team's
/// folder, out of a gone or retired folder but never into one, a whole folder without its marker,
/// links left out and named, never more than the limit, never into itself; a clash kept both,
/// replaced or skipped as chosen; recorded first, and the destination's watches told.
/// </summary>
public sealed class DocumentsCopyTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TeamDocuments Docs => host.Services.GetRequiredService<TeamDocuments>();

    [Fact]
    public async Task A_batch_copies_files_and_folders_within_and_across_teams()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "copies/a.md", "a");
        Docs.Write(host.Alpha, "copies/tree/b.md", "b");
        Docs.Folder(host.Alpha, "copies/tree/empty");
        Docs.Folder(host.Alpha, "copies/into");

        var within = await client.CopyAsync(host.Alpha, host.Alpha, "copies/into", "copies/a.md", "copies/tree");
        Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        Assert.Equal(["copies/into/a.md", "copies/into/tree"], (await within.BodyAsync()).Results().Select(r => r.To));

        var across = await client.CopyAsync(host.Alpha, host.Beta, "", "copies/a.md", "copies/tree");
        Assert.Equal(HttpStatusCode.OK, across.StatusCode);

        foreach (var (team, prefix) in new[] { (host.Alpha, "copies/into/"), (host.Beta, "") })
        {
            Assert.Equal("a", File.ReadAllText(Docs.At(team, prefix + "a.md")));
            Assert.Equal("b", File.ReadAllText(Docs.At(team, prefix + "tree/b.md")));
            Assert.True(Directory.Exists(Docs.At(team, prefix + "tree/empty")));
        }

        Assert.Equal("a", File.ReadAllText(Docs.At(host.Alpha, "copies/a.md")));
        Assert.Equal("b", File.ReadAllText(Docs.At(host.Alpha, "copies/tree/b.md")));
    }

    [Fact]
    public async Task Copying_into_the_same_folder_keeps_both_without_asking()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "dup/a.md", "a");
        Docs.Write(host.Alpha, "dup/tree/x.md", "x");

        var first = await client.CopyAsync(host.Alpha, host.Alpha, "dup", "dup/a.md", "dup/tree");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(["dup/a (copy).md", "dup/tree (copy)"], (await first.BodyAsync()).Results().Select(r => r.To));

        var second = await client.CopyAsync(host.Alpha, host.Alpha, "dup", "dup/a.md");
        Assert.Equal(["dup/a (copy 2).md"], (await second.BodyAsync()).Results().Select(r => r.To));
        Assert.Equal("x", File.ReadAllText(Docs.At(host.Alpha, "dup/tree (copy)/x.md")));
    }

    [Fact]
    public async Task Copying_out_of_a_gone_or_retired_folder_is_allowed_and_into_one_is_refused()
    {
        using var client = await host.PersonAsync();
        var gone = await GoneTeamAsync(host.Services, "CopyGone", "record.md");
        var retired = await RetiredFolderAsync(host.Services, "CopyRetired", "older.md");
        Docs.Folder(host.Beta, "kept");

        Assert.Equal(HttpStatusCode.OK, (await client.CopyAsync(gone, host.Beta, "kept", "record.md")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.CopyAsync(retired, host.Beta, "kept", "older.md")).StatusCode);
        Assert.True(File.Exists(Docs.At(host.Beta, "kept/record.md")));
        Assert.True(File.Exists(Docs.At(gone, "record.md")));

        var intoGone = await client.CopyAsync(host.Beta, gone, "", "kept/older.md");
        Assert.Equal(HttpStatusCode.Conflict, intoGone.StatusCode);
        Assert.Equal(
            "CopyGone no longer exists. Its documents can be read, copied or moved out, and deleted, but nothing can be added to them.",
            await intoGone.ErrorAsync());
        var intoRetired = await client.CopyAsync(host.Beta, retired, "", "kept/record.md");
        Assert.Equal(HttpStatusCode.Conflict, intoRetired.StatusCode);
        Assert.False(File.Exists(Docs.At(gone, "older.md")));
        Assert.False(File.Exists(Docs.At(retired, "record.md")));
    }

    [Fact]
    public async Task A_whole_folder_is_copied_without_its_marker()
    {
        using var client = await host.PersonAsync();
        var gone = await GoneTeamAsync(host.Services, "Whole", "one.md", "sub/two.md");
        Docs.Folder(host.Beta, "archive");

        var response = await client.CopyAsync(gone, host.Beta, "archive", "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new (string, string, string, string?)[] { ("", "archive/Whole", "done", null) }, (await response.BodyAsync()).Results());
        Assert.True(File.Exists(Docs.At(host.Beta, "archive/Whole/one.md")));
        Assert.True(File.Exists(Docs.At(host.Beta, "archive/Whole/sub/two.md")));
        Assert.False(File.Exists(TeamPaths.MarkerIn(Docs.At(host.Beta, "archive/Whole"))));
        Assert.True(File.Exists(TeamPaths.MarkerIn(Docs.RootFor(gone))));
    }

    [Fact]
    public async Task Links_inside_a_copied_folder_are_not_followed_and_are_named()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need privileges on Windows.");
        using var client = await host.PersonAsync();
        var secret = Docs.Write(host.Beta, "secret.md", "not for copying");
        Docs.Write(host.Alpha, "linked/plain.md");
        File.CreateSymbolicLink(Docs.At(host.Alpha, "linked/pointer.md"), secret);

        var response = await client.CopyAsync(host.Alpha, host.Alpha, "", "linked");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.Single((await response.BodyAsync()).GetProperty("results").EnumerateArray());
        var left = Assert.Single(result.GetProperty("notCopied").EnumerateArray());
        Assert.Equal("linked/pointer.md", left.GetProperty("path").GetString());
        Assert.Equal("a link, never followed", left.GetProperty("reason").GetString());
        Assert.True(File.Exists(Docs.At(host.Alpha, "linked (copy)/plain.md")));
        Assert.False(Path.Exists(Docs.At(host.Alpha, "linked (copy)/pointer.md")));
    }

    [Fact]
    public async Task A_folder_cannot_be_copied_into_itself()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "mirror/tree/a.md");

        var response = await client.CopyAsync(host.Alpha, host.Alpha, "mirror/tree", "mirror/tree");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("mirror/tree cannot be copied into itself.", await response.ErrorAsync());
        Assert.Equal(["a.md"], Directory.GetFileSystemEntries(Docs.At(host.Alpha, "mirror/tree")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Clash_choices_keep_both_replace_and_skip_per_item()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "choose/a.md", "new a");
        Docs.Write(host.Alpha, "choose/b.md", "new b");
        Docs.Write(host.Alpha, "choose/c.md", "new c");
        Docs.Write(host.Beta, "choose/a.md", "old a");
        Docs.Write(host.Beta, "choose/b.md", "old b");
        Docs.Write(host.Beta, "choose/c.md", "old c");

        var asked = await client.CopyAsync(host.Alpha, host.Beta, "choose", "choose/a.md");
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.Equal("a.md is already in choose. Choose Keep both, Replace or Skip.", await asked.ErrorAsync());

        var response = await client.CopyAsync(host.Alpha, host.Beta, "choose",
            Item("choose/a.md", "keep-both"), Item("choose/b.md", "replace"), Item("choose/c.md", "skip"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["done", "done", "skipped"], (await response.BodyAsync()).Results().Select(r => r.Outcome));
        Assert.Equal("old a", File.ReadAllText(Docs.At(host.Beta, "choose/a.md")));
        Assert.Equal("new a", File.ReadAllText(Docs.At(host.Beta, "choose/a (copy).md")));
        Assert.Equal("new b", File.ReadAllText(Docs.At(host.Beta, "choose/b.md")));
        Assert.Equal("old c", File.ReadAllText(Docs.At(host.Beta, "choose/c.md")));

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentsCopied, host.Alpha, Ct);
        using var detail = JsonDocument.Parse(row!.Detail!);
        Assert.Equal(["choose/b.md"], detail.RootElement.GetProperty("replaced").EnumerateArray().Select(r => r.GetString()));
    }

    /// <summary>
    /// SKIPPED AS ROOT: root lists a mode-000 folder anyway. Without the check a copy would leave the
    /// unlistable folder out and still say "done".
    /// </summary>
    [Fact]
    public async Task A_folder_holding_a_subfolder_the_host_cannot_list_fails_that_item_naming_it_not_an_unhandled_500()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions.");
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "partial/open.md");
        own.Docs.Write("Alpha", "partial/locked/hidden.md");
        own.Docs.Write("Alpha", "other.md");
        var locked = own.Docs.At("Alpha", "partial/locked");
        own.Lock(locked);
        if (CanList(locked)) Assert.Skip("This process lists a mode-000 folder anyway (it runs as root).");

        var response = await own.Client.CopyAsync("Alpha", "Beta", "", "partial", "other.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal(
            "The copy did not finish: 1 of 2 copied. Not copied: partial (permission denied (partial/locked cannot be listed)). Copy them again once that is fixed.",
            body.GetProperty("error").GetString());
        Assert.Equal(
            new (string, string, string, string?)[] { ("partial", "partial", "failed", "permission denied (partial/locked cannot be listed)"), ("other.md", "other.md", "done", null) },
            body.Results());
        Assert.False(Directory.Exists(own.Docs.At("Beta", "partial")));
        Assert.True(File.Exists(own.Docs.At("Beta", "other.md")));
        Assert.Equal(
            [TenantActions.DocumentsCopied, TenantActions.DocumentsCopyIncomplete],
            (await RowsAsync(own.Services, "Alpha")).Select(r => r.Action));
    }

    [Fact]
    public async Task Every_copy_appends_documents_copied_first()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "copyrow/a.md");
        Docs.Write(host.Alpha, "copyrow/tree/x.md");
        Docs.Write(host.Alpha, "copyrow/tree/y.md");
        Docs.Folder(host.Beta, "copyrow-in");

        Assert.Equal(HttpStatusCode.OK, (await client.CopyAsync(host.Alpha, host.Beta, "copyrow-in", "copyrow/a.md", "copyrow/tree")).StatusCode);

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentsCopied, host.Alpha, Ct);
        Assert.NotNull(row);
        Assert.Equal("person@example.test", row.ActorEmail);
        Assert.Equal("copyrow/a.md", row.SubjectName);
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal(host.Beta, detail.RootElement.GetProperty("to").GetProperty("folder").GetString());
        Assert.Equal(
            [("copyrow/a.md", "copyrow-in/a.md", 1), ("copyrow/tree", "copyrow-in/tree", 2)],
            detail.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => (i.GetProperty("from").GetString(), i.GetProperty("to").GetString(), i.GetProperty("files").GetInt32())));
    }

    [Fact]
    public async Task A_copy_into_a_live_teams_folder_announces_each_new_file_once_per_folder()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "told/a.md");
        Docs.Write(host.Alpha, "told/tree/b.md");
        Docs.Write(host.Alpha, "told/tree/c.md");
        Docs.Folder(host.Beta, "heard");
        var mark = await NoticeMarkAsync(host.Services);

        Assert.Equal(HttpStatusCode.OK, (await client.CopyAsync(host.Alpha, host.Beta, "heard", "told/a.md", "told/tree")).StatusCode);

        var notices = await NoticesSinceAsync(host.Services, mark);
        Assert.Equal(2, notices.Count);
        Assert.DoesNotContain(notices, n => n.Team == host.Alpha);
        Assert.Equal(["heard/a.md"], notices.Single(n => n.Folder == "heard").Changed);
        Assert.Equal(["heard/tree/b.md", "heard/tree/c.md"], notices.Single(n => n.Folder == "heard/tree").Changed);
    }

    [Fact]
    public async Task The_documents_list_names_the_root_an_agent_sees_its_folder_under()
    {
        using var client = await host.PersonAsync();

        using var listed = JsonDocument.Parse(await client.GetStringAsync("/api/documents", Ct));

        Assert.Equal(Path.GetFullPath(Path.Combine(host.DataRoot, "documents")), listed.RootElement.GetProperty("root").GetString());
        Assert.Equal(Docs.RootFor(host.Alpha), Path.Combine(listed.RootElement.GetProperty("root").GetString()!, host.Alpha));
    }
}
