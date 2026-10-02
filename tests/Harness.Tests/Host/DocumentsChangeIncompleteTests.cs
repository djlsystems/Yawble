using System.Net;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;
using static Harness.Tests.Host.DocumentsChangeSupport;

namespace Harness.Tests.Host;

/// <summary>
/// Nothing is renamed, moved or copied when its row cannot be written. A Host of its own, because
/// the only honest way to make the row unwritable is to take the table away.
/// </summary>
public sealed class DocumentsChangeUnrecordedTests
{
    [Theory]
    [InlineData("rename", "The rename could not be recorded, so nothing was renamed.")]
    [InlineData("move", "The move could not be recorded, so nothing was moved.")]
    [InlineData("copy", "The copy could not be recorded, so nothing was copied.")]
    public async Task Nothing_is_renamed_moved_or_copied_when_its_row_cannot_be_written(string verb, string sentence)
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "kept/a.md", "a");
        own.Docs.Write("Alpha", "kept/tree/b.md", "b");
        await own.DropTenantLogAsync();

        var response = verb == "rename"
            ? await own.Client.RenameAsync("Alpha", ("kept/a.md", "c.md"), ("kept/tree", "wood"))
            : await own.Client.TransferAsync(verb, "Alpha", "Beta", "", "kept/a.md", "kept/tree");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(sentence, await response.ErrorAsync());
        Assert.Equal("a", File.ReadAllText(own.Docs.At("Alpha", "kept/a.md")));
        Assert.Equal("b", File.ReadAllText(own.Docs.At("Alpha", "kept/tree/b.md")));
        Assert.False(File.Exists(own.Docs.At("Alpha", "kept/c.md")));
        Assert.False(Path.Exists(own.Docs.At("Beta", "a.md")));
        Assert.False(Path.Exists(own.Docs.At("Beta", "tree")));
    }
}

/// <summary>
/// A rename, move or copy the Host cannot finish, through the real Host: never an unhandled error,
/// but 409 with a sentence naming each item not done and why, every item's result, and the rows
/// `documents.&lt;verb&gt;` then `documents.&lt;verb&gt;-incomplete`. The Host's own moves, copies
/// and removals refuse the named paths, the way an agent's owner-only directory refuses it.
/// </summary>
public sealed class DocumentsChangeIncompleteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> ChangeAsync(DocumentsChangeHost own, string verb, params string[] paths) =>
        verb == "rename"
            ? own.Client.RenameAsync("Alpha", [.. paths.Select(path => (path, Path.GetFileName(path) + ".renamed"))])
            : own.Client.TransferAsync(verb, "Alpha", "Beta", "", [.. paths]);

    [Theory]
    [InlineData("rename", "renamed", "Rename")]
    [InlineData("move", "moved", "Move")]
    [InlineData("copy", "copied", "Copy")]
    public async Task A_batch_that_partly_fails_answers_409_path_by_path_and_logs_the_row_then_incomplete(string verb, string verbed, string again)
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "free.md");
        var locked = own.Docs.Write("Alpha", "locked.md");
        own.Refused[locked] = new UnauthorizedAccessException(locked);

        var response = await ChangeAsync(own, verb, "free.md", "locked.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal(
            $"The {verb} did not finish: 1 of 2 {verbed}. Not {verbed}: locked.md (permission denied). {again} them again once that is fixed.",
            body.GetProperty("error").GetString());
        Assert.Equal(["done", "failed"], body.Results().Select(r => r.Outcome));
        Assert.True(File.Exists(locked));

        var rows = await RowsAsync(own.Services, "Alpha");
        Assert.Equal([$"documents.{verbed}", $"documents.{verb}-incomplete"], rows.Select(r => r.Action));
        using var detail = JsonDocument.Parse(rows[1].Detail!);
        Assert.Equal(["free.md"], detail.RootElement.GetProperty("done").EnumerateArray().Select(d => d.GetString()));
        var failed = Assert.Single(detail.RootElement.GetProperty("failed").EnumerateArray());
        Assert.Equal("locked.md", failed.GetProperty("from").GetString());
        Assert.Equal("permission denied", failed.GetProperty("reason").GetString());

        Assert.Empty(await own.Services.GetRequiredService<IUnfinishedRemovals>().ListAsync(Ct));
    }

    [Theory]
    [InlineData("rename", "Nothing was renamed.")]
    [InlineData("move", "Nothing was moved.")]
    [InlineData("copy", "Nothing was copied.")]
    public async Task A_batch_where_nothing_went_says_so(string verb, string start)
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        var locked = own.Docs.Write("Alpha", "locked.md");
        own.Refused[locked] = new UnauthorizedAccessException(locked);

        var response = await ChangeAsync(own, verb, "locked.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.StartsWith(start, await response.ErrorAsync());
    }

    public static TheoryData<string, string> Failures => new()
    {
        { "permission", "permission denied" },
        { "in use", "in use" },
        { "another drive", "on another drive" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Each_failure_names_its_reason(string failure, string reason)
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        var locked = own.Docs.Folder("Alpha", "stuck");
        own.Docs.Write("Alpha", "stuck/a.md");
        own.Docs.Folder("Alpha", "into");
        own.Refused[locked] = failure switch
        {
            "permission" => new UnauthorizedAccessException(locked),
            "in use" => new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020)),
            _ => new IOException("Invalid cross-device link", 18),
        };

        var response = await own.Client.MoveAsync("Alpha", "Alpha", "into", "stuck");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.BodyAsync();
        Assert.Equal(reason, Assert.Single(body.Results()).Reason);
        Assert.Contains($"stuck ({reason})", body.GetProperty("error").GetString());
        Assert.True(File.Exists(own.Docs.At("Alpha", "stuck/a.md")));
    }

    [Fact]
    public async Task When_the_incomplete_row_cannot_be_written_the_sentence_says_so()
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "free.md");
        var locked = own.Docs.Write("Alpha", "locked.md");

        // The first row lands; the move of free.md then takes the table away, so the second cannot.
        own.Refused[locked] = new UnauthorizedAccessException(locked);
        own.Before[own.Docs.At("Alpha", "free.md")] = () => own.DropTenantLogAsync().GetAwaiter().GetResult();

        var response = await own.Client.MoveAsync("Alpha", "Beta", "", "free.md", "locked.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith(" The record of what was left could not be written.", await response.ErrorAsync());
    }

    [Fact]
    public async Task A_clash_that_appears_after_planning_is_never_overwritten()
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "a.md", "mine");
        var first = own.Docs.Write("Alpha", "first.md");

        // The first item's move is where something with the second item's name appears in Beta.
        own.Before[first] = () => File.WriteAllText(own.Docs.At("Beta", "a.md"), "appeared");

        var response = await own.Client.MoveAsync("Alpha", "Beta", "", "first.md", "a.md");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var results = (await response.BodyAsync()).Results();
        Assert.Equal(("a.md", "a.md", "failed", "something with that name appeared in Beta"), results[1]);
        Assert.Equal("appeared", File.ReadAllText(own.Docs.At("Beta", "a.md")));
        Assert.Equal("mine", File.ReadAllText(own.Docs.At("Alpha", "a.md")));
    }

    [Fact]
    public async Task A_copy_that_fails_part_way_leaves_nothing_half_made()
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "tree/a.md");
        own.Docs.Write("Alpha", "tree/b.md");
        own.Docs.Write("Alpha", "tree/sub/c.md");
        var stuck = own.Docs.At("Alpha", "tree/sub/c.md");
        own.Refused[stuck] = new UnauthorizedAccessException(stuck);
        var mark = await NoticeMarkAsync(own.Services);

        var response = await own.Client.CopyAsync("Alpha", "Beta", "", "tree");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(("tree", "tree", "failed", "permission denied"), Assert.Single((await response.BodyAsync()).Results()));
        Assert.False(Path.Exists(own.Docs.At("Beta", "tree")));
        Assert.Empty(await NoticesSinceAsync(own.Services, mark));
    }

    [Fact]
    public async Task A_replace_whose_removal_leaves_something_fails_that_item_and_keeps_the_source()
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "tree/new.md", "new");
        own.Docs.Write("Beta", "tree/old.md", "old");
        var held = own.Docs.At("Beta", "tree/old.md");
        own.Refused[held] = new UnauthorizedAccessException(held);

        var response = await own.Client.MoveAsync("Alpha", "Beta", "", Item("tree", "replace"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var result = Assert.Single((await response.BodyAsync()).Results());
        Assert.Equal("failed", result.Outcome);
        Assert.StartsWith("tree could not be replaced: permission denied", result.Reason);
        Assert.Equal("new", File.ReadAllText(own.Docs.At("Alpha", "tree/new.md")));
        Assert.Equal("old", File.ReadAllText(held));
        Assert.Empty(await own.Services.GetRequiredService<IUnfinishedRemovals>().ListAsync(Ct));
    }

    [Fact]
    public async Task Only_what_changed_is_announced()
    {
        await using var own = await DocumentsChangeHost.StartAsync();
        own.Docs.Write("Alpha", "went.md");
        var locked = own.Docs.Write("Alpha", "stayed.md");
        own.Refused[locked] = new UnauthorizedAccessException(locked);
        var mark = await NoticeMarkAsync(own.Services);

        Assert.Equal(HttpStatusCode.Conflict, (await own.Client.MoveAsync("Alpha", "Beta", "", "went.md", "stayed.md")).StatusCode);

        var notices = await NoticesSinceAsync(own.Services, mark);
        Assert.Equal(["went.md"], notices.Single(n => n.Team == "Alpha").Changed);
        Assert.Equal(["went.md"], notices.Single(n => n.Team == "Beta").Changed);
    }
}
