using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;
using static Harness.Tests.Host.DocumentsChangeSupport;

namespace Harness.Tests.Host;

/// <summary>
/// Moving in the Documents dialog, through the real Host: within a team and into another team's
/// folder, out of a gone or retired folder but never into one, never a folder into itself or a link
/// out of its team; a clash is asked about, or kept both, replaced or skipped as chosen; recorded
/// first, and both folders' watches told.
/// </summary>
public sealed class DocumentsMoveTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TeamDocuments Docs => host.Services.GetRequiredService<TeamDocuments>();

    [Fact]
    public async Task A_batch_moves_files_and_folders_within_a_team()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "within/a.md", "a");
        Docs.Write(host.Alpha, "within/tree/b.md", "b");
        Docs.Folder(host.Alpha, "within/into");

        var response = await client.MoveAsync(host.Alpha, host.Alpha, "within/into", "within/a.md", "within/tree");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new (string, string, string, string?)[] { ("within/a.md", "within/into/a.md", "done", null), ("within/tree", "within/into/tree", "done", null) },
            (await response.BodyAsync()).Results());
        Assert.Equal("a", File.ReadAllText(Docs.At(host.Alpha, "within/into/a.md")));
        Assert.Equal("b", File.ReadAllText(Docs.At(host.Alpha, "within/into/tree/b.md")));
        Assert.False(File.Exists(Docs.At(host.Alpha, "within/a.md")));
        Assert.False(Directory.Exists(Docs.At(host.Alpha, "within/tree")));
    }

    [Fact]
    public async Task A_batch_moves_into_another_teams_folder()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "across/a.md", "a");
        Docs.Write(host.Alpha, "across/tree/b.md", "b");
        Docs.EnsureFor(host.Beta);

        var response = await client.MoveAsync(host.Alpha, host.Beta, "", "across/a.md", "across/tree");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["a.md", "tree"], (await response.BodyAsync()).Results().Select(r => r.To));
        Assert.Equal("a", File.ReadAllText(Docs.At(host.Beta, "a.md")));
        Assert.Equal("b", File.ReadAllText(Docs.At(host.Beta, "tree/b.md")));
        Assert.False(Directory.EnumerateFileSystemEntries(Docs.At(host.Alpha, "across")).Any());
    }

    [Fact]
    public async Task Moving_out_of_a_gone_teams_folder_and_a_retired_folder_is_allowed_and_the_folder_and_marker_stay()
    {
        using var client = await host.PersonAsync();
        var gone = await GoneTeamAsync(host.Services, "MoveOutGone", "kept.md");
        var retired = await RetiredFolderAsync(host.Services, "MoveOutRetired", "old.md");
        Docs.Folder(host.Beta, "rescued");

        Assert.Equal(HttpStatusCode.OK, (await client.MoveAsync(gone, host.Beta, "rescued", "kept.md")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.MoveAsync(retired, host.Beta, "rescued", "old.md")).StatusCode);

        Assert.True(File.Exists(Docs.At(host.Beta, "rescued/kept.md")));
        Assert.True(File.Exists(Docs.At(host.Beta, "rescued/old.md")));
        Assert.True(File.Exists(TeamPaths.MarkerIn(Docs.RootFor(gone))));
        Assert.True(File.Exists(TeamPaths.MarkerIn(Docs.RootFor(retired))));
        Assert.Contains(Docs.Folders(), f => f.Folder == gone);
        Assert.Contains(Docs.Folders(), f => f.Folder == retired);
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_itself_or_its_own_subfolder()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "self/tree/deep/a.md");

        foreach (var into in new[] { "self/tree", "self/tree/deep" })
        {
            var response = await client.MoveAsync(host.Alpha, host.Alpha, into, "self/tree");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("self/tree cannot be moved into itself.", await response.ErrorAsync());
        }

        Assert.True(File.Exists(Docs.At(host.Alpha, "self/tree/deep/a.md")));
    }

    [Fact]
    public async Task A_link_is_never_moved_and_a_folder_holding_one_does_not_leave_its_team()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need privileges on Windows.");
        using var client = await host.PersonAsync();
        var target = Docs.Write(host.Alpha, "links/target.md");
        File.CreateSymbolicLink(Docs.At(host.Alpha, "links/pointer.md"), target);
        Docs.Write(host.Alpha, "links/holder/a.md");
        File.CreateSymbolicLink(Docs.At(host.Alpha, "links/holder/inner"), target);

        var link = await client.MoveAsync(host.Alpha, host.Beta, "", "links/pointer.md");
        Assert.Equal(HttpStatusCode.Conflict, link.StatusCode);
        Assert.Equal("links/pointer.md is a link. A link is never followed, renamed, moved or copied.", await link.ErrorAsync());

        var holder = await client.MoveAsync(host.Alpha, host.Beta, "", "links/holder");
        Assert.Equal(HttpStatusCode.Conflict, holder.StatusCode);
        Assert.Equal(
            "links/holder holds a link (links/holder/inner). A link is never moved into another team's documents; delete it first.",
            await holder.ErrorAsync());
        Assert.True(File.Exists(Docs.At(host.Alpha, "links/holder/a.md")));

        // Within its own team a folder holding a link moves as one rename: the link goes with it, unfollowed.
        Docs.Folder(host.Alpha, "links/elsewhere");
        Assert.Equal(HttpStatusCode.OK, (await client.MoveAsync(host.Alpha, host.Alpha, "links/elsewhere", "links/holder")).StatusCode);
        Assert.NotNull(new FileInfo(Docs.At(host.Alpha, "links/elsewhere/holder/inner")).LinkTarget);
    }

    [Fact]
    public async Task A_destination_reached_through_a_link_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need privileges on Windows.");
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "via/a.md");
        Directory.CreateSymbolicLink(Docs.At(host.Alpha, "via/door"), Docs.Folder(host.Beta, "behind"));

        var response = await client.MoveAsync(host.Alpha, host.Alpha, "via/door", "via/a.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("via/door is reached through a link, and a link is never followed.", await response.ErrorAsync());
        Assert.True(File.Exists(Docs.At(host.Alpha, "via/a.md")));
        Assert.False(File.Exists(Docs.At(host.Beta, "behind/a.md")));
    }

    [Fact]
    public async Task Moving_into_its_own_folder_is_skipped()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "home/a.md");

        var response = await client.MoveAsync(host.Alpha, host.Alpha, "home", "home/a.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new (string, string, string, string?)[] { ("home/a.md", "home/a.md", "skipped", "already there") }, (await response.BodyAsync()).Results());
        Assert.True(File.Exists(Docs.At(host.Alpha, "home/a.md")));
    }

    [Fact]
    public async Task A_clash_with_no_choice_is_409_naming_each_and_nothing_moves_and_no_row_is_written()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "ask/from/a.md", "new");
        Docs.Write(host.Alpha, "ask/from/tree/x.md", "new");
        Docs.Write(host.Alpha, "ask/from/free.md", "free");
        Docs.Write(host.Alpha, "ask/to/a.md", "old");
        Docs.Write(host.Alpha, "ask/to/tree/y.md", "old");
        var rows = (await RowsAsync(host.Services, host.Alpha)).Count;

        var response = await client.MoveAsync(host.Alpha, host.Alpha, "ask/to", "ask/from/a.md", "ask/from/tree", "ask/from/free.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal("2 of these are already in ask/to: a.md, tree. Choose Keep both, Replace or Skip for each.", body.GetProperty("error").GetString());
        Assert.Equal(
            [("ask/from/a.md", "ask/to/a.md", false), ("ask/from/tree", "ask/to/tree", true)],
            body.GetProperty("clashes").EnumerateArray().Select(c => (c.GetProperty("from").GetString(), c.GetProperty("to").GetString(), c.GetProperty("isFolder").GetBoolean())));
        Assert.True(File.Exists(Docs.At(host.Alpha, "ask/from/free.md")));
        Assert.Equal("old", File.ReadAllText(Docs.At(host.Alpha, "ask/to/a.md")));
        Assert.Equal(rows, (await RowsAsync(host.Services, host.Alpha)).Count);
    }

    [Fact]
    public async Task Keep_both_names_copy_then_copy_2_before_the_extension_and_for_folders_and_dot_names()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "keep/one/a.md", "first");
        Docs.Write(host.Alpha, "keep/two/a.md", "second");
        Docs.Write(host.Alpha, "keep/one/.env", "env");
        Docs.Write(host.Alpha, "keep/one/tree/x.md");
        Docs.Write(host.Alpha, "keep/to/a.md", "there");
        Docs.Write(host.Alpha, "keep/to/.env", "there");
        Docs.Write(host.Alpha, "keep/to/tree/y.md");

        var first = await client.MoveAsync(host.Alpha, host.Alpha, "keep/to",
            Item("keep/one/a.md", "keep-both"), Item("keep/one/.env", "keep-both"), Item("keep/one/tree", "keep-both"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(["keep/to/a (copy).md", "keep/to/.env (copy)", "keep/to/tree (copy)"], (await first.BodyAsync()).Results().Select(r => r.To));

        var second = await client.MoveAsync(host.Alpha, host.Alpha, "keep/to", Item("keep/two/a.md", "keep-both"));
        Assert.Equal(["keep/to/a (copy 2).md"], (await second.BodyAsync()).Results().Select(r => r.To));
        Assert.Equal("there", File.ReadAllText(Docs.At(host.Alpha, "keep/to/a.md")));
        Assert.Equal("first", File.ReadAllText(Docs.At(host.Alpha, "keep/to/a (copy).md")));
        Assert.Equal("second", File.ReadAllText(Docs.At(host.Alpha, "keep/to/a (copy 2).md")));
        Assert.True(File.Exists(Docs.At(host.Alpha, "keep/to/tree (copy)/x.md")));
    }

    [Fact]
    public async Task Replace_removes_the_existing_entry_through_folder_removal_and_names_it_in_the_row()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "swap/from/a.md", "new");
        Docs.Write(host.Alpha, "swap/from/tree/n.md", "new");
        Docs.Write(host.Alpha, "swap/from/flat", "now a file");
        Docs.Write(host.Beta, "swap/a.md", "old");
        Docs.Write(host.Beta, "swap/tree/old1.md");
        Docs.Write(host.Beta, "swap/tree/sub/old2.md");
        Docs.Write(host.Beta, "swap/flat/was-a-folder.md");
        var mark = await NoticeMarkAsync(host.Services);

        var response = await client.MoveAsync(host.Alpha, host.Beta, "swap",
            Item("swap/from/a.md", "replace"), Item("swap/from/tree", "replace"), Item("swap/from/flat", "replace"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("new", File.ReadAllText(Docs.At(host.Beta, "swap/a.md")));
        Assert.Equal(["n.md"], Directory.GetFileSystemEntries(Docs.At(host.Beta, "swap/tree")).Select(Path.GetFileName));
        Assert.Equal("now a file", File.ReadAllText(Docs.At(host.Beta, "swap/flat")));

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentsMoved, host.Alpha, Ct);
        using var detail = JsonDocument.Parse(row!.Detail!);
        Assert.Equal(["swap/a.md", "swap/tree", "swap/flat"], detail.RootElement.GetProperty("replaced").EnumerateArray().Select(r => r.GetString()));

        var beta = (await NoticesSinceAsync(host.Services, mark)).Where(n => n.Team == host.Beta).ToList();
        Assert.Equal(["swap/tree/n.md", "swap/tree/old1.md"], beta.Single(n => n.Folder == "swap/tree").Changed);
        Assert.Equal(["swap/tree/sub/old2.md"], beta.Single(n => n.Folder == "swap/tree/sub").Changed);
        Assert.Equal(["swap/flat/was-a-folder.md"], beta.Single(n => n.Folder == "swap/flat").Changed);
        Assert.Contains("swap/a.md", beta.Single(n => n.Folder == "swap").Changed);
    }

    [Fact]
    public async Task Skip_leaves_both_and_reports_skipped()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "skip/from/a.md", "mine");
        Docs.Write(host.Alpha, "skip/to/a.md", "theirs");

        var response = await client.MoveAsync(host.Alpha, host.Alpha, "skip/to", Item("skip/from/a.md", "skip"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new (string, string, string, string?)[] { ("skip/from/a.md", "skip/to/a.md", "skipped", "Skipped: skip/to/a.md is already there.") },
            (await response.BodyAsync()).Results());
        Assert.Equal("mine", File.ReadAllText(Docs.At(host.Alpha, "skip/from/a.md")));
        Assert.Equal("theirs", File.ReadAllText(Docs.At(host.Alpha, "skip/to/a.md")));
    }

    [Fact]
    public async Task Every_move_appends_documents_moved_naming_every_source_destination_and_replaced_path()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "rowed/a.md");
        Docs.Write(host.Alpha, "rowed/tree/x.md");
        Docs.Write(host.Alpha, "rowed/tree/y.md");
        Docs.Write(host.Alpha, "rowed/b.md");
        Docs.Write(host.Beta, "rowed-in/b.md");

        Assert.Equal(HttpStatusCode.OK, (await client.MoveAsync(host.Alpha, host.Beta, "rowed-in",
            "rowed/a.md", "rowed/tree", Item("rowed/b.md", "replace"))).StatusCode);

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.DocumentsMoved, host.Alpha, Ct);
        Assert.NotNull(row);
        Assert.Equal("person@example.test", row.ActorEmail);
        Assert.Equal("rowed/a.md", row.SubjectName);
        using var detail = JsonDocument.Parse(row.Detail!);
        var root = detail.RootElement;
        Assert.Equal(host.Alpha, root.GetProperty("folder").GetString());
        Assert.Equal(host.Beta, root.GetProperty("to").GetProperty("folder").GetString());
        Assert.Equal("rowed-in", root.GetProperty("to").GetProperty("path").GetString());
        Assert.Equal(
            [("rowed/a.md", "rowed-in/a.md", 1), ("rowed/tree", "rowed-in/tree", 2), ("rowed/b.md", "rowed-in/b.md", 1)],
            root.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("from").GetString(), i.GetProperty("to").GetString(), i.GetProperty("files").GetInt32())));
        Assert.Equal("replace", root.GetProperty("items")[2].GetProperty("onClash").GetString());
        Assert.Equal(["rowed-in/b.md"], root.GetProperty("replaced").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task A_move_announces_what_left_the_source_and_what_arrived_once_per_folder_and_a_gone_source_nothing()
    {
        using var client = await host.PersonAsync();
        Docs.Write(host.Alpha, "leaving/a.md");
        Docs.Write(host.Alpha, "leaving/b.md");
        Docs.Write(host.Alpha, "leaving/tree/c.md");
        Docs.Folder(host.Beta, "arriving");
        var mark = await NoticeMarkAsync(host.Services);

        Assert.Equal(HttpStatusCode.OK, (await client.MoveAsync(host.Alpha, host.Beta, "arriving", "leaving/a.md", "leaving/b.md", "leaving/tree")).StatusCode);

        var notices = await NoticesSinceAsync(host.Services, mark);
        Assert.Equal(4, notices.Count);
        Assert.Equal(["leaving/a.md", "leaving/b.md"], notices.Single(n => n.Team == host.Alpha && n.Folder == "leaving").Changed);
        Assert.Equal(["leaving/tree/c.md"], notices.Single(n => n.Team == host.Alpha && n.Folder == "leaving/tree").Changed);
        Assert.Equal(["arriving/a.md", "arriving/b.md"], notices.Single(n => n.Team == host.Beta && n.Folder == "arriving").Changed);
        Assert.Equal(["arriving/tree/c.md"], notices.Single(n => n.Team == host.Beta && n.Folder == "arriving/tree").Changed);

        var gone = await GoneTeamAsync(host.Services, "AnnouncedGone", "old.md");
        mark = await NoticeMarkAsync(host.Services);
        Assert.Equal(HttpStatusCode.OK, (await client.MoveAsync(gone, host.Beta, "arriving", "old.md")).StatusCode);
        var fromGone = await NoticesSinceAsync(host.Services, mark);
        Assert.DoesNotContain(fromGone, n => n.Team == gone);
        Assert.Equal(["arriving/old.md"], Assert.Single(fromGone).Changed);
    }

    /// <summary>
    /// ROOT ONLY: making a folder owned by the agent user needs a process that can switch users. A
    /// root process's own rename moves that folder anyway, so the Host's refusal is injected where
    /// the product's Host meets it.
    /// </summary>
    [Fact]
    public async Task An_agent_owned_owner_only_folder_that_cannot_be_moved_is_named_with_why()
    {
        var runAs = AgentLaunchUser.Resolve("nobody");
        if (!runAs.Switches) Assert.Skip($"This process cannot switch users: {runAs.Reason}.");

        await using var own = await DocumentsChangeHost.StartAsync();
        var root = own.Docs.RootFor("Alpha");
        for (var directory = root; directory.Length >= own.DataRoot.Length; directory = Path.GetDirectoryName(directory)!)
        {
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
        }

        var agentDir = Path.Combine(root, "made");
        var start = new System.Diagnostics.ProcessStartInfo(runAs.Prefix[0]);
        foreach (var argument in runAs.Prefix.Skip(1)) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "/bin/sh", "-c", $"umask 077 && mkdir -p '{agentDir}/deep' && echo x > '{agentDir}/deep/a.md' && chmod 700 '{agentDir}' '{agentDir}/deep'" })
        {
            start.ArgumentList.Add(argument);
        }
        using (var process = System.Diagnostics.Process.Start(start)!) process.WaitForExit();
        Assert.True(Directory.Exists(agentDir));
        own.Docs.Folder("Alpha", "into");
        own.Refused[agentDir] = new UnauthorizedAccessException(agentDir);

        var response = await own.Client.MoveAsync("Alpha", "Alpha", "into", "made");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal("Nothing was moved. Not moved: made (permission denied). Move them again once that is fixed.", body.GetProperty("error").GetString());
        Assert.Equal(new (string, string, string, string?)[] { ("made", "into/made", "failed", "permission denied") }, body.Results());
        Assert.True(Directory.Exists(agentDir));
    }

    /// <summary>
    /// SKIPPED AS ROOT: root lists a mode-000 folder anyway. A folder the Host cannot list could hide
    /// a link the move to another team must never carry, so that item fails, naming the folder.
    /// </summary>
    [Fact]
    public async Task A_folder_holding_a_subfolder_the_host_cannot_list_is_not_moved_to_another_team_unchecked()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions.");
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "sealed/open.md");
        own.Docs.Write("Alpha", "sealed/locked/hidden.md");
        own.Docs.Write("Alpha", "free.md");
        var locked = own.Docs.At("Alpha", "sealed/locked");
        own.Lock(locked);
        if (CanList(locked)) Assert.Skip("This process lists a mode-000 folder anyway (it runs as root).");

        var response = await own.Client.MoveAsync("Alpha", "Beta", "", "sealed", "free.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal(
            "The move did not finish: 1 of 2 moved. Not moved: sealed (permission denied (sealed/locked cannot be listed)). Move them again once that is fixed.",
            body.GetProperty("error").GetString());
        Assert.Equal(
            new (string, string, string, string?)[] { ("sealed", "sealed", "failed", "permission denied (sealed/locked cannot be listed)"), ("free.md", "free.md", "done", null) },
            body.Results());
        Assert.True(Directory.Exists(own.Docs.At("Alpha", "sealed/locked")));
        Assert.False(Directory.Exists(own.Docs.At("Beta", "sealed")));
        Assert.Equal(
            [TenantActions.DocumentsMoved, TenantActions.DocumentsMoveIncomplete],
            (await RowsAsync(own.Services, "Alpha")).Select(r => r.Action));

        // Within its own team the same folder moves: one rename, nothing to check.
        own.Docs.Folder("Alpha", "inside");
        Assert.Equal(HttpStatusCode.OK, (await own.Client.MoveAsync("Alpha", "Alpha", "inside", "sealed")).StatusCode);
    }
}
