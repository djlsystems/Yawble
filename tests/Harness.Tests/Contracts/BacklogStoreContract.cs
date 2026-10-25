using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// WHAT EVERY <see cref="IBacklogStore"/> DOES, on SQLite and on SQL Server alike. Taken from
/// <c>BacklogAuditTransactionTests</c> (same names, same values: a create, a delete, an archive and a
/// dispatch land with their tenant rows in one transaction, and with the row refused nothing changes)
/// and from the interface's and the SQLite store's remarks: the citation minted and never reused, the
/// archive as a second axis, a move touching one row, a renumber keeping the order, the execution record
/// going with a delete, a tip replaced, a start kept, a missed start's reason moving until it is stopped,
/// landed recorded once. Plus the providers' differences made into rules: a repository's case and its
/// trailing space, and two writers at once.
/// </summary>
public abstract class BacklogStoreContract : IdentityContract
{
    private const string Person = "person@example.test";

    private static TriggerAudit Row(string action) => new("person-1", Person, action, "B001", "Crawl openings", null);

    /// <summary>The store with the provider's own tenant row writer.</summary>
    protected async Task<IBacklogStore> CreateAsync()
    {
        var database = await DatabaseAsync();
        return database.Backlog(database.TenantRowAsync);
    }

    private async Task<IBacklogStore> RefusingAsync() =>
        (await DatabaseAsync()).Backlog((_, _, _, _) => throw new IOException("the tenant log is full"));

    private async Task<IBacklogStore> RecordingAsync(List<string> actions)
    {
        var database = await DatabaseAsync();
        return database.Backlog((connection, transaction, row, ct) =>
        {
            actions.Add(row.Action);
            return database.TenantRowAsync(connection, transaction, row, ct);
        });
    }

    private async Task<long> CountAsync(string sql) => await (await DatabaseAsync()).LongAsync(sql);

    private static string Stamp() => DateTimeOffset.UtcNow.ToString("O");

    [Fact]
    public async Task A_create_whose_row_cannot_be_written_creates_nothing()
    {
        var store = await RefusingAsync();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(null, "Crawl openings", "spec", Person, Ct, audit: Row(TenantActions.BacklogItemCreated)));

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM backlog_items"));
    }

    [Fact]
    public async Task A_delete_an_archive_and_a_dispatch_whose_row_cannot_be_written_change_nothing()
    {
        var actions = new List<string>();
        var item = await (await RecordingAsync(actions)).CreateAsync(null, "Crawl openings", "spec", Person, Ct, audit: Row(TenantActions.BacklogItemCreated));
        var store = await RefusingAsync();

        await Assert.ThrowsAsync<IOException>(() => store.SetArchivedAsync(item.Id, Stamp(), Ct, audit: Row(TenantActions.BacklogItemArchived)));
        await Assert.ThrowsAsync<IOException>(() => store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct, audit: Row(TenantActions.BacklogItemDispatched)));
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(item.Id, Ct, audit: Row(TenantActions.BacklogItemDeleted)));

        var kept = await store.GetAsync(item.Id, Ct);
        Assert.NotNull(kept);
        Assert.Null(kept.ArchivedAt);
        Assert.Empty(await store.DispatchesAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Each_change_lands_with_its_row_in_the_same_transaction()
    {
        var actions = new List<string>();
        var store = await RecordingAsync(actions);

        var item = await store.CreateAsync(null, "Crawl openings", "spec", Person, Ct, audit: Row(TenantActions.BacklogItemCreated));
        await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct, audit: Row(TenantActions.BacklogItemDispatched));
        await store.SetArchivedAsync(item.Id, Stamp(), Ct, audit: Row(TenantActions.BacklogItemArchived));
        await store.DeleteAsync(item.Id, Ct, audit: Row(TenantActions.BacklogItemDeleted));

        Assert.Equal(
            [TenantActions.BacklogItemCreated, TenantActions.BacklogItemDispatched, TenantActions.BacklogItemArchived, TenantActions.BacklogItemDeleted],
            actions);
        Assert.Equal(4, await CountAsync("SELECT COUNT(*) FROM tenant_events"));
        Assert.Null(await store.GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task A_created_rows_subject_is_the_items_citation_and_its_detail_names_the_id()
    {
        // THE CITATION IS MINTED INSIDE THE TRANSACTION, so the caller cannot name it: the store
        // fills the subject and the id, as the row carried before it moved into the transaction.
        var rows = new List<TriggerAudit>();
        var database = await DatabaseAsync();
        var store = database.Backlog((connection, transaction, row, ct) =>
        {
            rows.Add(row);
            return database.TenantRowAsync(connection, transaction, row, ct);
        });

        var item = await store.CreateAsync("alpha", "Crawl openings", "spec", Person, Ct,
            audit: new("person-1", Person, TenantActions.BacklogItemCreated, null, "Crawl openings", """{"team":"alpha"}"""));

        var row = Assert.Single(rows);
        Assert.Equal(PlatformBacklogId.Format(item.Id), row.Subject);
        Assert.Equal("Crawl openings", row.SubjectName);
        var detail = JsonDocument.Parse(row.Detail!).RootElement;
        Assert.Equal(item.Id, detail.GetProperty("id").GetInt64());
        Assert.Equal("alpha", detail.GetProperty("team").GetString());
        Assert.Equal(PlatformBacklogId.Format(item.Id), await (await DatabaseAsync()).TextAsync("SELECT subject FROM tenant_events"));
    }

    [Fact]
    public async Task A_change_with_no_row_asked_for_still_lands_as_before()
    {
        var store = (await DatabaseAsync()).Backlog(null);

        var item = await store.CreateAsync(null, "Crawl openings", "spec", Person, Ct);
        await store.SetArchivedAsync(item.Id, Stamp(), Ct);

        Assert.NotNull((await store.GetAsync(item.Id, Ct))?.ArchivedAt);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    [Fact]
    public async Task A_row_asked_of_a_store_made_without_a_writer_is_refused_and_nothing_changes()
    {
        var store = (await DatabaseAsync()).Backlog(null);
        var item = await store.CreateAsync(null, "Kept", "spec", Person, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(null, "Lost", "spec", Person, Ct, audit: Row(TenantActions.BacklogItemCreated)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(item.Id, Ct, audit: Row(TenantActions.BacklogItemDeleted)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EditAsync(item.Id, "Changed", null, null, false, null, [Row(TenantActions.BacklogItemEdited)], Ct));

        Assert.Equal("Kept", Assert.Single(await store.ListAsync(null, true, false, Ct)).Title);
    }

    [Fact]
    public async Task An_edit_whose_rows_cannot_be_written_changes_nothing()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "Kept", "spec", Person, Ct);
        await (await DatabaseAsync()).ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<DbException>(() =>
            store.EditAsync(item.Id, "Changed", "new spec", BacklogStates.Ready, true, "o-1", [Row(TenantActions.BacklogItemEdited)], Ct));
        await Assert.ThrowsAnyAsync<DbException>(() => store.CreateAsync(null, "Lost", "spec", Person, Ct, audit: Row(TenantActions.BacklogItemCreated)));

        var kept = (await store.GetAsync(item.Id, Ct))!;
        Assert.Equal(("Kept", "spec", BacklogStates.Pending, (string?)null), (kept.Title, kept.Body, kept.State, kept.OutcomeId));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM backlog_items"));
    }

    [Fact]
    public async Task Items_append_in_gaps_and_a_deleted_items_id_is_never_handed_out_again()
    {
        var store = await CreateAsync();

        var first = await store.CreateAsync(null, "One", "a", Person, Ct);
        var second = await store.CreateAsync(null, "Two", "b", Person, Ct);
        var third = await store.CreateAsync("alpha", "Three", "c", Person, Ct);

        Assert.Equal([(1L, 1024d), (2L, 2048d), (3L, 3072d)], new[] { first, second, third }.Select(i => (i.Id, i.Position)));
        Assert.Equal((BacklogStates.Pending, (string?)null, Person), (third.State, third.ArchivedAt, third.CreatedBy));
        Assert.Equal(third.CreatedAt, third.UpdatedAt);
        Assert.Equal(PlatformBacklogId.Format(3), await (await DatabaseAsync()).TextAsync("SELECT MAX(id) FROM backlog_items"));

        await store.DeleteAsync(third.Id, Ct);
        var fourth = await store.CreateAsync(null, "Four", "d", Person, Ct);

        Assert.Equal((4L, 3072d), (fourth.Id, fourth.Position));
        Assert.Equal(fourth, await store.GetAsync(4, Ct));
        Assert.Null(await store.GetAsync(3, Ct));
    }

    [Fact]
    public async Task The_archive_is_a_second_axis_and_teams_filter_each_list()
    {
        var store = await CreateAsync();
        var unlinked = await store.CreateAsync(null, "Unlinked", "a", Person, Ct);
        var alpha = await store.CreateAsync("alpha", "Alpha's", "b", Person, Ct);
        var beta = await store.CreateAsync("beta", "Beta's", "c", Person, Ct);
        var archived = await store.CreateAsync("alpha", "Archived", "d", Person, Ct);

        await store.UpdateAsync(alpha.Id, state: BacklogStates.Implemented, ct: Ct);
        await store.SetArchivedAsync(archived.Id, "2026-10-01T00:00:00.0000000+00:00", Ct);

        Assert.Equal([unlinked.Id, alpha.Id, beta.Id], (await store.ListAsync(null, false, false, Ct)).Select(i => i.Id));
        Assert.Equal([alpha.Id], (await store.ListAsync(new HashSet<string> { "alpha" }, false, false, Ct)).Select(i => i.Id));
        Assert.Equal([unlinked.Id, alpha.Id], (await store.ListAsync(new HashSet<string> { "alpha" }, true, false, Ct)).Select(i => i.Id));

        var shelved = Assert.Single(await store.ListAsync(null, true, true, Ct));
        Assert.Equal((archived.Id, "2026-10-01T00:00:00.0000000+00:00"), (shelved.Id, shelved.ArchivedAt));
        Assert.Empty(await store.ListAsync(new HashSet<string> { "beta" }, true, true, Ct));

        await store.SetArchivedAsync(archived.Id, null, Ct);
        Assert.Empty(await store.ListAsync(null, true, true, Ct));
        Assert.Equal(archived.Position, (await store.GetAsync(archived.Id, Ct))!.Position);
    }

    [Fact]
    public async Task The_archive_pages_newest_first_below_its_cursor()
    {
        var store = await CreateAsync();

        for (var i = 0; i < 5; i++)
        {
            var item = await store.CreateAsync(i % 2 == 0 ? null : "alpha", $"Item {i}", "spec", Person, Ct);
            await store.SetArchivedAsync(item.Id, Stamp(), Ct);
        }

        Assert.Equal([5L, 4L], (await store.ListArchivedAsync(null, true, null, 2, Ct)).Select(i => i.Id));
        Assert.Equal([3L, 2L], (await store.ListArchivedAsync(null, true, 4, 2, Ct)).Select(i => i.Id));
        Assert.Equal([4L, 2L], (await store.ListArchivedAsync(new HashSet<string> { "alpha" }, false, null, 10, Ct)).Select(i => i.Id));
        Assert.Empty(await store.ListArchivedAsync(null, true, null, 0, Ct));
    }

    [Fact]
    public async Task Titles_are_read_for_every_id_asked_in_one_call()
    {
        var store = await CreateAsync();
        var one = await store.CreateAsync(null, "One", "a", Person, Ct);
        var two = await store.CreateAsync(null, "Two", "b", Person, Ct);

        var titles = await store.TitlesAsync([one.Id, two.Id, two.Id, 999], Ct);

        Assert.Equal(2, titles.Count);
        Assert.Equal(("One", "Two"), (titles[one.Id], titles[two.Id]));
        Assert.Empty(await store.TitlesAsync([], Ct));
    }

    [Fact]
    public async Task An_update_sets_only_the_fields_it_is_given()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "Title", "Body", Person, Ct);

        await store.UpdateAsync(item.Id, title: "Renamed", ct: Ct);
        await store.UpdateAsync(item.Id, state: BacklogStates.Ready, ct: Ct);
        await store.UpdateAsync(item.Id, ct: Ct);

        var read = (await store.GetAsync(item.Id, Ct))!;
        Assert.Equal(("Renamed", "Body", BacklogStates.Ready), (read.Title, read.Body, read.State));
        Assert.True(string.CompareOrdinal(read.UpdatedAt, item.UpdatedAt) >= 0);
        Assert.Equal(item.CreatedAt, read.CreatedAt);
    }

    [Fact]
    public async Task An_edit_sets_and_clears_the_outcome_and_lands_each_of_its_rows()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "Title", "Body", Person, Ct);

        await store.EditAsync(item.Id, null, "New body", null, true, "outcome-1",
            [Row(TenantActions.BacklogItemEdited), Row(TenantActions.BacklogItemImplemented)], Ct);

        var read = (await store.GetAsync(item.Id, Ct))!;
        Assert.Equal(("Title", "New body", "outcome-1"), (read.Title, read.Body, read.OutcomeId));

        await store.EditAsync(item.Id, null, null, null, true, null, [Row(TenantActions.BacklogItemImplemented)], Ct);
        Assert.Null((await store.GetAsync(item.Id, Ct))!.OutcomeId);

        // Nothing to set: the rows still land, and the item is as it was.
        await store.EditAsync(item.Id, null, null, null, false, null, [Row(TenantActions.BacklogItemEdited)], Ct);
        Assert.Equal("New body", (await store.GetAsync(item.Id, Ct))!.Body);

        Assert.Equal(
            [TenantActions.BacklogItemEdited, TenantActions.BacklogItemImplemented, TenantActions.BacklogItemImplemented, TenantActions.BacklogItemEdited],
            await (await DatabaseAsync()).ColumnAsync("SELECT action FROM tenant_events ORDER BY seq"));
    }

    [Fact]
    public async Task A_move_writes_one_row_and_leaves_its_modified_time()
    {
        var store = await CreateAsync();
        var one = await store.CreateAsync(null, "One", "a", Person, Ct);
        var two = await store.CreateAsync(null, "Two", "b", Person, Ct);

        await store.SetPositionAsync(two.Id, 512.5, Ct);

        Assert.Equal([two.Id, one.Id], (await store.ListAsync(null, true, false, Ct)).Select(i => i.Id));
        var moved = (await store.GetAsync(two.Id, Ct))!;
        Assert.Equal((512.5, two.UpdatedAt), (moved.Position, moved.UpdatedAt));
        Assert.Equal(1024d, (await store.GetAsync(one.Id, Ct))!.Position);
    }

    [Fact]
    public async Task A_renumber_spaces_every_item_archived_included_in_its_order()
    {
        var store = await CreateAsync();
        var one = await store.CreateAsync(null, "One", "a", Person, Ct);
        var two = await store.CreateAsync(null, "Two", "b", Person, Ct);
        var three = await store.CreateAsync(null, "Three", "c", Person, Ct);
        await store.SetPositionAsync(three.Id, 1.25, Ct);
        await store.SetPositionAsync(one.Id, 2048, Ct);
        await store.SetArchivedAsync(two.Id, Stamp(), Ct);

        var renumbered = await store.RenumberAsync(Ct);

        // one and two tie at 2048: id decides.
        Assert.Equal([(three.Id, 1024d), (one.Id, 2048d), (two.Id, 3072d)], renumbered.Select(i => (i.Id, i.Position)));
        Assert.NotNull(renumbered[2].ArchivedAt);
        Assert.Equal(3072d, (await store.GetAsync(two.Id, Ct))!.Position);
        Assert.Equal([1024d, 2048d, 3072d], (await store.RenumberAsync(Ct)).Select(i => i.Position));
    }

    [Fact]
    public async Task A_delete_takes_its_dispatches_tips_starts_and_missed_starts_and_nothing_else()
    {
        var store = await CreateAsync();
        var gone = await store.CreateAsync(null, "Gone", "a", Person, Ct);
        var kept = await store.CreateAsync(null, "Kept", "b", Person, Ct);

        foreach (var item in new[] { gone, kept })
        {
            var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 10 + item.Id, Person, Ct);
            await store.RecordTipAsync(dispatch.Id, "repo", "tip", Ct);
            await store.RecordBaseAsync(dispatch.Id, "repo", "default", "team", Ct);
            await store.RecordMissedStartAsync(dispatch.Id, "other", "the fetch from origin failed", null, null, Ct);
        }

        await store.DeleteAsync(gone.Id, Ct);

        Assert.Empty(await store.DispatchesAsync(gone.Id, Ct));
        var left = Assert.Single(await store.DispatchesAsync(kept.Id, Ct));
        Assert.Single(await store.TipsAsync(left.Id, Ct));
        Assert.Single(await store.BasesAsync(left.Id, Ct));
        Assert.Single(await store.MissedStartsAsync(left.Id, Ct));
        foreach (var table in new[] { "backlog_dispatches", "backlog_dispatch_tips", "backlog_dispatch_bases", "backlog_dispatch_missed_starts" })
        {
            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM {table}"));
        }
    }

    [Fact]
    public async Task Dispatches_list_in_order_and_the_current_one_is_each_items_newest()
    {
        var store = await CreateAsync();
        var one = await store.CreateAsync(null, "One", "a", Person, Ct);
        var two = await store.CreateAsync(null, "Two", "b", Person, Ct);

        var first = await store.AddDispatchAsync(one.Id, "alpha", "Alpha", 41, Person, Ct);
        var other = await store.AddDispatchAsync(two.Id, "beta", "Beta", 42, Person, Ct);
        var again = await store.AddDispatchAsync(one.Id, "Alpha", "Alpha", 43, "other@example.test", Ct);
        var newest = await store.AddDispatchAsync(two.Id, "beta", "Beta", 42, Person, Ct);
        await store.SetArchivedAsync(two.Id, Stamp(), Ct);

        Assert.Equal([first, again], await store.DispatchesAsync(one.Id, Ct));
        Assert.Equal((one.Id, "alpha", "Alpha", 41L, Person), (first.Item, first.TeamId, first.TeamName, first.Correlation, first.DispatchedBy));

        var latest = await store.LatestDispatchesAsync(Ct);
        Assert.Equal([(one.Id, again.Id), (two.Id, newest.Id)], latest.Select(d => (d.Item, d.Id)));

        var current = await store.CurrentDispatchesAsync(Ct);
        Assert.Equal(
            [(again.Id, "One", BacklogStates.Pending, false), (newest.Id, "Two", BacklogStates.Pending, true)],
            current.Select(c => (c.Dispatch.Id, c.Title, c.State, c.Archived)));

        Assert.Equal(other, await store.DispatchForCorrelationAsync(42, Ct));
        Assert.Null(await store.DispatchForCorrelationAsync(99, Ct));
    }

    [Fact]
    public async Task A_freeze_sets_and_clears_a_dispatchs_stats()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);
        var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct);

        await store.FreezeDispatchAsync(dispatch.Id, "2026-10-01T00:00:00Z", """{"tokens":12}""", Ct);
        var frozen = Assert.Single(await store.DispatchesAsync(item.Id, Ct));
        Assert.Equal(("2026-10-01T00:00:00Z", """{"tokens":12}"""), (frozen.FrozenAt, frozen.FrozenStats));

        await store.FreezeDispatchAsync(dispatch.Id, null, null, Ct);
        var thawed = Assert.Single(await store.DispatchesAsync(item.Id, Ct));
        Assert.Equal(((string?)null, (string?)null), (thawed.FrozenAt, thawed.FrozenStats));
    }

    [Fact]
    public async Task A_tip_is_replaced_and_a_start_is_kept_each_listed_by_repository()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);
        var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct);

        await store.RecordTipAsync(dispatch.Id, "web", "aaa", Ct);
        await store.RecordTipAsync(dispatch.Id, "web", "bbb", Ct);
        await store.RecordTipAsync(dispatch.Id, "Api", "ccc", Ct);
        await store.RecordBaseAsync(dispatch.Id, "web", "d1", "t1", Ct);
        await store.RecordBaseAsync(dispatch.Id, "web", "d2", "t2", Ct);
        await store.RecordBaseAsync(dispatch.Id, "Api", "d3", null, Ct);

        Assert.Equal([("Api", "ccc"), ("web", "bbb")], (await store.TipsAsync(dispatch.Id, Ct)).Select(t => (t.Repo, t.Sha)));
        Assert.Equal(
            [("Api", "d3", (string?)null), ("web", "d1", (string?)"t1")],
            (await store.BasesAsync(dispatch.Id, Ct)).Select(b => (b.Repo, b.DefaultSha, b.TeamSha)));
        Assert.Equal([dispatch.Id], await store.DispatchesWithStartsAsync(Ct));
    }

    [Fact]
    public async Task A_repository_differing_only_in_case_is_the_same_repository()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);
        var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct);

        await store.RecordTipAsync(dispatch.Id, "Web", "aaa", Ct);
        await store.RecordTipAsync(dispatch.Id, "web", "bbb", Ct);
        await store.RecordBaseAsync(dispatch.Id, "Web", "d1", null, Ct);
        await store.RecordBaseAsync(dispatch.Id, "WEB", "d2", null, Ct);
        await store.RecordMissedStartAsync(dispatch.Id, "Web", "first", null, null, Ct);
        await store.RecordMissedStartAsync(dispatch.Id, "web", "second", null, null, Ct);

        var tip = Assert.Single(await store.TipsAsync(dispatch.Id, Ct));
        Assert.Equal(("Web", "bbb"), (tip.Repo, tip.Sha));
        var start = Assert.Single(await store.BasesAsync(dispatch.Id, Ct));
        Assert.Equal(("Web", "d1"), (start.Repo, start.DefaultSha));
        Assert.Equal("second", Assert.Single(await store.MissedStartsAsync(dispatch.Id, Ct)).Reason);
        Assert.Empty(await store.UnrecordedStartsAsync(Ct));

        await store.StopStartRetryAsync(dispatch.Id, "WEB", "stopped", Ct);
        Assert.Equal("stopped", Assert.Single(await store.MissedStartsAsync(dispatch.Id, Ct)).Reason);
    }

    [Fact]
    public async Task A_repository_with_a_trailing_space_is_another_repository()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);
        var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct);

        await store.RecordTipAsync(dispatch.Id, "web", "aaa", Ct);
        await store.RecordTipAsync(dispatch.Id, "web ", "bbb", Ct);
        await store.RecordBaseAsync(dispatch.Id, "web ", "d1", null, Ct);
        await store.RecordMissedStartAsync(dispatch.Id, "web", "the clone was missing", null, null, Ct);
        await store.RecordMissedStartAsync(dispatch.Id, "web ", "the clone was missing", null, null, Ct);

        Assert.Equal(["aaa", "bbb"], (await store.TipsAsync(dispatch.Id, Ct)).Select(t => t.Sha));
        Assert.Equal("web ", Assert.Single(await store.BasesAsync(dispatch.Id, Ct)).Repo);
        Assert.Equal(2, (await store.MissedStartsAsync(dispatch.Id, Ct)).Count);

        // Only the repository with no start of its own is unrecorded, and a stop names one repository.
        Assert.Equal("web", Assert.Single(await store.UnrecordedStartsAsync(Ct)).Repo);
        await store.StopStartRetryAsync(dispatch.Id, "web ", "stopped", Ct);
        Assert.Equal(
            [("web", (string?)null), ("web ", (string?)"stopped")],
            (await store.MissedStartsAsync(dispatch.Id, Ct)).Select(m => (m.Repo, m.StoppedAt is null ? null : m.Reason)));
    }

    [Fact]
    public async Task A_missed_start_moves_its_reason_until_it_is_stopped()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);
        var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct);

        await store.RecordMissedStartAsync(dispatch.Id, "web", "the default branch was not known", "team-1", "default-1", Ct);
        await store.RecordMissedStartAsync(dispatch.Id, "web", "the fetch from origin failed", "team-2", "default-2", Ct);

        var missed = Assert.Single(await store.MissedStartsAsync(dispatch.Id, Ct));
        Assert.Equal(("the fetch from origin failed", "team-1", "default-1", (string?)null), (missed.Reason, missed.TeamSha, missed.DefaultSha, missed.StoppedAt));

        await store.StopStartRetryAsync(dispatch.Id, "web", "it had commits of its own", Ct);
        await store.StopStartRetryAsync(dispatch.Id, "web", "a second stop", Ct);
        await store.RecordMissedStartAsync(dispatch.Id, "web", "a later failure", null, null, Ct);

        var stopped = Assert.Single(await store.MissedStartsAsync(dispatch.Id, Ct));
        Assert.Equal("it had commits of its own", stopped.Reason);
        Assert.NotNull(stopped.StoppedAt);
    }

    [Fact]
    public async Task Unrecorded_starts_are_the_missed_ones_with_no_start_of_their_own()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);
        var one = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct);
        var two = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 8, Person, Ct);

        await store.RecordMissedStartAsync(two.Id, "b", "missing", null, null, Ct);
        await store.RecordMissedStartAsync(one.Id, "x", "missing", null, null, Ct);
        await store.RecordMissedStartAsync(one.Id, "a", "missing", null, null, Ct);
        await store.RecordBaseAsync(one.Id, "X", "default", null, Ct);

        Assert.Equal([(one.Id, "a"), (two.Id, "b")], (await store.UnrecordedStartsAsync(Ct)).Select(m => (m.Dispatch, m.Repo)));
        Assert.Equal([one.Id], await store.DispatchesWithStartsAsync(Ct));
    }

    [Fact]
    public async Task Landed_is_recorded_once_and_never_moved()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);
        var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, Person, Ct);

        Assert.True(await store.RecordLandedAsync(dispatch.Id, "sha-1", "main", Ct));
        Assert.False(await store.RecordLandedAsync(dispatch.Id, "sha-2", "other", Ct));
        Assert.False(await store.RecordLandedAsync(dispatch.Id + 100, "sha-3", "main", Ct));

        var landed = Assert.Single(await store.DispatchesAsync(item.Id, Ct));
        Assert.Equal(("sha-1", "main"), (landed.LandedSha, landed.LandedBranch));
        Assert.NotNull(landed.LandedAt);
    }

    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await CreateAsync();
        var title = "Crawl O'Brien's openings 🚀 café 日本語";
        var body = LongUnicode("# Spec");

        var item = await store.CreateAsync("Équipe", title, body, "o'brien@example.test", Ct);
        var dispatch = await store.AddDispatchAsync(item.Id, "Équipe", "Équipe 🚀", 7, "o'brien@example.test", Ct);
        await store.RecordTipAsync(dispatch.Id, "dépôt 🚀", "sha", Ct);
        await store.FreezeDispatchAsync(dispatch.Id, "2026-10-01T00:00:00Z", LongUnicode("{\"x\":\"") + "\"}", Ct);

        var read = (await store.GetAsync(item.Id, Ct))!;
        Assert.Equal((title, body, "Équipe", "o'brien@example.test"), (read.Title, read.Body, read.Team, read.CreatedBy));
        Assert.Equal(title, (await store.TitlesAsync([item.Id], Ct))[item.Id]);
        Assert.Equal("Équipe 🚀", Assert.Single(await store.DispatchesAsync(item.Id, Ct)).TeamName);
        Assert.Equal(LongUnicode("{\"x\":\"") + "\"}", Assert.Single(await store.DispatchesAsync(item.Id, Ct)).FrozenStats);
        Assert.Equal("dépôt 🚀", Assert.Single(await store.TipsAsync(dispatch.Id, Ct)).Repo);
    }

    [Fact]
    public async Task Concurrent_creates_take_distinct_ids_and_positions()
    {
        var store = await CreateAsync();

        var created = await RaceAsync(8, i => store.CreateAsync(null, $"Raced {i}", "spec", Person, Ct, audit: Row(TenantActions.BacklogItemCreated)));

        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), created.Select(i => i.Id).Order());
        Assert.Equal(Enumerable.Range(1, 8).Select(i => i * 1024d), created.Select(i => i.Position).Order());
        Assert.Equal(8, await CountAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    [Fact]
    public async Task Concurrent_tips_of_one_repository_make_one_row()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);

        for (var round = 0; round < 10; round++)
        {
            var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", round, Person, Ct);
            await RaceAsync(8, async i =>
            {
                await store.RecordTipAsync(dispatch.Id, i % 2 == 0 ? "web" : "WEB", $"sha-{i}", Ct);
                return i;
            });

            Assert.Single(await store.TipsAsync(dispatch.Id, Ct));
        }
    }

    [Fact]
    public async Task Concurrent_starts_of_one_repository_keep_one()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);

        for (var round = 0; round < 10; round++)
        {
            var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", round, Person, Ct);
            await RaceAsync(8, async i =>
            {
                await store.RecordBaseAsync(dispatch.Id, "web", $"default-{i}", null, Ct);
                await store.RecordMissedStartAsync(dispatch.Id, "web", $"reason-{i}", null, null, Ct);
                return i;
            });

            Assert.Single(await store.BasesAsync(dispatch.Id, Ct));
            Assert.Single(await store.MissedStartsAsync(dispatch.Id, Ct));
        }
    }

    [Fact]
    public async Task Concurrent_landed_proofs_record_one()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "One", "a", Person, Ct);

        for (var round = 0; round < 10; round++)
        {
            var dispatch = await store.AddDispatchAsync(item.Id, "alpha", "Alpha", round, Person, Ct);
            var won = await RaceAsync(8, i => store.RecordLandedAsync(dispatch.Id, $"sha-{i}", "main", Ct));

            Assert.Single(won, w => w);
        }
    }

    [Fact]
    public async Task Concurrent_edits_of_different_fields_keep_both()
    {
        var store = await CreateAsync();
        var item = await store.CreateAsync(null, "Title", "Body", Person, Ct);

        await RaceAsync(2, async i =>
        {
            if (i == 0) await store.UpdateAsync(item.Id, title: "New title", ct: Ct);
            else await store.EditAsync(item.Id, null, "New body", null, false, null, [Row(TenantActions.BacklogItemEdited)], Ct);
            return i;
        });

        var read = (await store.GetAsync(item.Id, Ct))!;
        Assert.Equal(("New title", "New body"), (read.Title, read.Body));
    }
}

public sealed class SqliteBacklogStoreTests : BacklogStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerBacklogStoreTests : BacklogStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
