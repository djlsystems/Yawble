using System.Text;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// WHAT EVERY <see cref="ISiteStore"/> DOES, on SQLite and on SQL Server alike. The existing SQLite
/// tests reach the store through the Host and plugins; the behaviours are the interface's remarks and
/// the SQLite store's: a site unique by name in its team, a team folding case, a publish that takes the
/// next number and forgets the pruned, every site change landing with its tenant row or not at all,
/// and data limits checked in the write's own transaction. Plus the providers' differences made into
/// rules: a trailing space, and two writers at once.
/// </summary>
public abstract class SiteStoreContract : IdentityContract
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    protected async Task<ISiteStore> CreateAsync() => (await DatabaseAsync()).Sites();

    private static SiteRow Site(string team, string name) => new(team, name, null, 1, At, "person@example.test");

    private static SiteVersionRow Version(int version) =>
        new(version, At.AddMinutes(version), "person@example.test", "/data/documents/Alpha/site", version * 10, version * 1000L);

    private static TriggerAudit Published(string name) => Audit(TenantActions.SitePublished, name);

    private async Task<ISiteStore> StoreWithASiteAsync()
    {
        var store = await CreateAsync();
        Assert.True(await store.CreateAsync(Site("Alpha", "blog"), Audit(TenantActions.SiteCreated, "blog"), Ct));
        return store;
    }

    private Task<SiteWriteOutcome> PutAsync(ISiteStore store, string id, string json, string collection = "items", string site = "blog") =>
        store.PutDocumentAsync("Alpha", site, collection, id, json, "alpha/dev", At, Ct);

    private async Task<long> TenantRowsAsync(string action) =>
        await (await DatabaseAsync()).LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{action}'");

    [Fact]
    public async Task A_site_is_created_once_per_team_and_name_with_its_tenant_row()
    {
        var store = await StoreWithASiteAsync();

        Assert.False(await store.CreateAsync(Site("ALPHA", "blog"), Audit(TenantActions.SiteCreated, "blog"), Ct));
        Assert.True(await store.CreateAsync(Site("Alpha", "notes"), Audit(TenantActions.SiteCreated, "notes"), Ct));

        Assert.Equal(2, await TenantRowsAsync(TenantActions.SiteCreated));
        var found = (await store.FindAsync("alpha", "blog", Ct))!;
        Assert.Equal(new SiteRow("Alpha", "blog", null, 1, At, "person@example.test"), found);
    }

    [Fact]
    public async Task Site_names_differing_only_in_case_are_two_sites()
    {
        var store = await StoreWithASiteAsync();

        Assert.True(await store.CreateAsync(Site("Alpha", "Blog"), Audit(TenantActions.SiteCreated, "Blog"), Ct));

        Assert.Equal(["Blog", "blog"], (await store.ListAsync("Alpha", Ct)).Select(s => s.Name));
        Assert.Null(await store.FindAsync("Alpha", "BLOG", Ct));
    }

    [Fact]
    public async Task Lists_order_by_team_then_name_and_one_team_folds_case()
    {
        var store = await CreateAsync();
        foreach (var (team, name) in new[] { ("beta", "a"), ("Alpha", "z"), ("Alpha", "m"), ("Gamma", "b") })
        {
            Assert.True(await store.CreateAsync(Site(team, name), Audit(TenantActions.SiteCreated, name), Ct));
        }

        Assert.Equal(["Alpha/m", "Alpha/z", "beta/a", "Gamma/b"], (await store.ListAsync(null, Ct)).Select(s => $"{s.Team}/{s.Name}"));
        Assert.Equal(["m", "z"], (await store.ListAsync("ALPHA", Ct)).Select(s => s.Name));
        Assert.Empty(await store.ListAsync("Delta", Ct));
    }

    [Fact]
    public async Task A_publish_records_the_version_makes_it_live_takes_the_next_number_and_forgets_the_pruned()
    {
        var store = await StoreWithASiteAsync();

        await store.RecordPublishAsync("Alpha", "blog", Version(1), [], Published("blog"), Ct);
        await store.RecordPublishAsync("alpha", "blog", Version(2), [], Published("blog"), Ct);
        await store.RecordPublishAsync("Alpha", "blog", Version(3), [1], Published("blog"), Ct);

        var site = (await store.FindAsync("Alpha", "blog", Ct))!;
        Assert.Equal((3, 4), (site.LiveVersion, site.NextVersion));
        Assert.Equal([Version(3), Version(2)], await store.VersionsAsync("ALPHA", "blog", Ct));
        Assert.Equal(3, await TenantRowsAsync(TenantActions.SitePublished));
    }

    [Fact]
    public async Task A_publish_of_an_older_number_keeps_the_next_number_past_the_newest()
    {
        var store = await StoreWithASiteAsync();
        await store.RecordPublishAsync("Alpha", "blog", Version(5), [], Published("blog"), Ct);
        await store.RecordPublishAsync("Alpha", "blog", Version(2), [], Published("blog"), Ct);

        var site = (await store.FindAsync("Alpha", "blog", Ct))!;
        Assert.Equal((2, 6), (site.LiveVersion, site.NextVersion));
    }

    [Fact]
    public async Task A_publish_to_a_missing_site_is_refused_and_records_nothing()
    {
        var store = await StoreWithASiteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordPublishAsync("Alpha", "missing", Version(1), [], Published("missing"), Ct));

        Assert.Empty(await store.VersionsAsync("Alpha", "missing", Ct));
        Assert.Equal(0, await TenantRowsAsync(TenantActions.SitePublished));
    }

    [Fact]
    public async Task Unpublishing_and_rolling_back_set_the_live_version_with_a_tenant_row_each()
    {
        var store = await StoreWithASiteAsync();
        await store.RecordPublishAsync("Alpha", "blog", Version(1), [], Published("blog"), Ct);
        await store.RecordPublishAsync("Alpha", "blog", Version(2), [], Published("blog"), Ct);

        await store.SetLiveAsync("Alpha", "blog", null, Audit(TenantActions.SiteUnpublished, "blog"), Ct);
        Assert.Null((await store.FindAsync("Alpha", "blog", Ct))!.LiveVersion);

        await store.SetLiveAsync("ALPHA", "blog", 1, Audit(TenantActions.SiteRolledBack, "blog"), Ct);
        var site = (await store.FindAsync("Alpha", "blog", Ct))!;
        Assert.Equal((1, 3), (site.LiveVersion, site.NextVersion));
        Assert.Equal(1, await TenantRowsAsync(TenantActions.SiteUnpublished));
        Assert.Equal(1, await TenantRowsAsync(TenantActions.SiteRolledBack));
    }

    [Fact]
    public async Task Deleting_a_site_removes_its_versions_and_data_and_a_missing_one_is_false()
    {
        var store = await StoreWithASiteAsync();
        Assert.True(await store.CreateAsync(Site("Alpha", "notes"), Audit(TenantActions.SiteCreated, "notes"), Ct));
        await store.RecordPublishAsync("Alpha", "blog", Version(1), [], Published("blog"), Ct);
        Assert.True((await PutAsync(store, "d1", "{}")).Accepted);
        Assert.True((await PutAsync(store, "d1", "{}", site: "notes")).Accepted);

        Assert.True(await store.DeleteAsync("alpha", "blog", Audit(TenantActions.SiteDeleted, "blog"), Ct));
        Assert.False(await store.DeleteAsync("Alpha", "blog", Audit(TenantActions.SiteDeleted, "blog"), Ct));

        Assert.Null(await store.FindAsync("Alpha", "blog", Ct));
        Assert.Empty(await store.VersionsAsync("Alpha", "blog", Ct));
        Assert.Equal(new SiteDataUsage(0, 0), await store.UsageAsync("Alpha", "blog", Ct));
        Assert.Equal(new SiteDataUsage(1, 2), await store.UsageAsync("Alpha", "notes", Ct));
        Assert.Equal(1, await TenantRowsAsync(TenantActions.SiteDeleted));
    }

    [Fact]
    public async Task Deleting_a_team_removes_every_site_with_a_tenant_row_each_and_any_rows_left_behind()
    {
        var store = await CreateAsync();
        var database = await DatabaseAsync();
        foreach (var (team, name) in new[] { ("Alpha", "b"), ("Alpha", "a"), ("Beta", "a") })
        {
            Assert.True(await store.CreateAsync(Site(team, name), Audit(TenantActions.SiteCreated, name), Ct));
            Assert.True((await store.PutDocumentAsync(team, name, "items", "d1", "{}", "dev", At, Ct)).Accepted);
        }

        // Data whose site row is already gone: never listed, never counted, and removed with the team.
        await database.ExecuteAsync(
            $"INSERT INTO site_documents (team, site, collection, id, doc, bytes, updated_at, updated_by) VALUES ('Alpha', 'gone', 'items', 'd1', '{{}}', 2, '{At:O}', 'dev')");

        var deleted = await store.DeleteTeamAsync("ALPHA", name => Audit(TenantActions.SiteDeleted, name), Ct);

        Assert.Equal(["a", "b"], deleted);
        Assert.Empty(await store.ListAsync("Alpha", Ct));
        Assert.Equal(new SiteDataUsage(0, 0), await store.UsageAsync("Alpha", "gone", Ct));
        Assert.Equal(new SiteDataUsage(1, 2), await store.UsageAsync("Beta", "a", Ct));
        Assert.Equal(2, await TenantRowsAsync(TenantActions.SiteDeleted));
    }

    [Fact]
    public async Task A_site_change_is_not_made_when_its_tenant_row_cannot_be()
    {
        var store = await StoreWithASiteAsync();
        await store.RecordPublishAsync("Alpha", "blog", Version(1), [], Published("blog"), Ct);
        await (await DatabaseAsync()).ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(Site("Alpha", "new"), Audit(TenantActions.SiteCreated, "new"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.RecordPublishAsync("Alpha", "blog", Version(2), [1], Published("blog"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SetLiveAsync("Alpha", "blog", null, Audit(TenantActions.SiteUnpublished, "blog"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync("Alpha", "blog", Audit(TenantActions.SiteDeleted, "blog"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteTeamAsync("Alpha", name => Audit(TenantActions.SiteDeleted, name), Ct));

        Assert.Null(await store.FindAsync("Alpha", "new", Ct));
        var site = (await store.FindAsync("Alpha", "blog", Ct))!;
        Assert.Equal((1, 2), (site.LiveVersion, site.NextVersion));
        Assert.Equal([Version(1)], await store.VersionsAsync("Alpha", "blog", Ct));
    }

    [Fact]
    public async Task Documents_round_trip_list_by_id_and_name_their_collections_once_in_order()
    {
        var store = await StoreWithASiteAsync();
        Assert.True((await PutAsync(store, "b", """{"n":2}""")).Accepted);
        Assert.True((await PutAsync(store, "a", """{"n":1}""")).Accepted);
        Assert.True((await PutAsync(store, "x", "{}", collection: "cards")).Accepted);
        Assert.True((await store.PutDocumentAsync("Alpha", "blog", "items", "a", """{"n":3}""", "alpha/other", At.AddMinutes(1), Ct)).Accepted);

        Assert.Equal(["a", "b"], (await store.ListDocumentsAsync("ALPHA", "blog", "items", Ct)).Select(d => d.Id));
        Assert.Equal(["cards", "items"], await store.CollectionsAsync("alpha", "blog", Ct));
        Assert.Equal(
            new SiteDocument("items", "a", """{"n":3}""", At.AddMinutes(1), "alpha/other"),
            await store.GetDocumentAsync("Alpha", "blog", "items", "a", Ct));
        Assert.Null(await store.GetDocumentAsync("Alpha", "blog", "items", "A", Ct));
        Assert.Equal(new SiteDataUsage(3, 7 + 7 + 2), await store.UsageAsync("Alpha", "blog", Ct));

        Assert.True(await store.DeleteDocumentAsync("Alpha", "blog", "items", "b", Ct));
        Assert.False(await store.DeleteDocumentAsync("Alpha", "blog", "items", "b", Ct));
        Assert.Equal(new SiteDataUsage(2, 9), await store.UsageAsync("Alpha", "blog", Ct));
    }

    /// <summary>REVIEW FOCUS 4: unicode, a <c>'</c> and a document near the limit, back byte for byte.</summary>
    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await StoreWithASiteAsync();
        var body = string.Concat(Enumerable.Repeat("O'Brien's 🚀 café 日本語 ", 2_000));
        var json = $$"""{"text":"{{body}}"}""";
        Assert.True(Encoding.UTF8.GetByteCount(json) is > 50_000 and <= SiteRules.MaxDocumentBytes);

        Assert.True((await store.PutDocumentAsync("Alpha", "blog", "items", "u", json, "O'Brien 🧪", At, Ct)).Accepted);

        var read = (await store.GetDocumentAsync("Alpha", "blog", "items", "u", Ct))!;
        Assert.Equal((json, "O'Brien 🧪"), (read.Json, read.UpdatedBy));
        Assert.Equal(Encoding.UTF8.GetByteCount(json), (await store.UsageAsync("Alpha", "blog", Ct)).Bytes);
    }

    [Fact]
    public async Task A_document_over_the_size_limit_is_refused_and_not_written()
    {
        var store = await StoreWithASiteAsync();
        var json = $$"""{"text":"{{new string('x', SiteRules.MaxDocumentBytes)}}"}""";

        var refused = await PutAsync(store, "big", json);

        Assert.Equal((false, 413, SiteRules.DocumentTooLarge(Encoding.UTF8.GetByteCount(json))), (refused.Accepted, refused.Status, refused.Refusal));
        Assert.Null(await store.GetDocumentAsync("Alpha", "blog", "items", "big", Ct));
    }

    [Fact]
    public async Task A_full_collection_refuses_a_new_document_and_takes_a_rewrite()
    {
        var store = await StoreWithASiteAsync();
        var database = await DatabaseAsync();

        // Rows inserted in batches the two dialects share, to the collection's limit.
        for (var batch = 0; batch < SiteRules.MaxDocumentsPerCollection / 500; batch++)
        {
            var values = string.Join(", ", Enumerable.Range(batch * 500, 500).Select(i =>
                $"('Alpha', 'blog', 'items', 'd{i}', '{{}}', 2, '{At:O}', 'dev')"));
            await database.ExecuteAsync($"INSERT INTO site_documents (team, site, collection, id, doc, bytes, updated_at, updated_by) VALUES {values}");
        }

        var refused = await PutAsync(store, "one-more", "{}");
        Assert.Equal((false, 413), (refused.Accepted, refused.Status));
        Assert.Contains($"already holds {SiteRules.MaxDocumentsPerCollection} documents", refused.Refusal);

        Assert.True((await PutAsync(store, "d0", """{"rewritten":true}""")).Accepted);
        Assert.True((await PutAsync(store, "one-more", "{}", collection: "other")).Accepted);
    }

    [Fact]
    public async Task A_write_past_the_sites_data_limit_is_refused_and_a_rewrite_counts_its_old_bytes_once()
    {
        var store = await StoreWithASiteAsync();
        var database = await DatabaseAsync();
        var held = SiteRules.MaxSiteDataBytes - 10;
        await database.ExecuteAsync(
            $"INSERT INTO site_documents (team, site, collection, id, doc, bytes, updated_at, updated_by) VALUES ('Alpha', 'blog', 'items', 'held', '{{}}', {held}, '{At:O}', 'dev')");

        var refused = await PutAsync(store, "new", """{"twelve":1}""");
        Assert.Equal((false, 413), (refused.Accepted, refused.Status));
        Assert.Contains($"it holds {held} bytes now", refused.Refusal);

        Assert.True((await PutAsync(store, "small", "{\"a\":1}")).Accepted);
        Assert.True((await PutAsync(store, "small", "{\"a\":2}")).Accepted);
        Assert.Equal(new SiteDataUsage(2, held + 7), await store.UsageAsync("Alpha", "blog", Ct));
    }

    /// <summary>
    /// RULING 5: A TRAILING SPACE DECIDES A COMPARISON, as SQLite's <c>=</c> does: a team, a site, a
    /// collection and a document id with one are other keys, whose rows do not exist.
    /// </summary>
    [Fact]
    public async Task A_key_with_a_trailing_space_is_another_key()
    {
        var store = await StoreWithASiteAsync();
        Assert.True((await PutAsync(store, "d1", "{}")).Accepted);

        Assert.Null(await store.FindAsync("Alpha", "blog ", Ct));
        Assert.Null(await store.FindAsync("Alpha ", "blog", Ct));
        Assert.Empty(await store.ListAsync("Alpha ", Ct));
        Assert.Null(await store.GetDocumentAsync("Alpha", "blog", "items", "d1 ", Ct));
        Assert.Empty(await store.ListDocumentsAsync("Alpha", "blog", "items ", Ct));
        Assert.Empty(await store.CollectionsAsync("Alpha", "blog ", Ct));
        Assert.Equal(new SiteDataUsage(0, 0), await store.UsageAsync("Alpha", "blog ", Ct));
        Assert.False(await store.DeleteDocumentAsync("Alpha", "blog", "items", "d1 ", Ct));
        Assert.False(await store.DeleteAsync("Alpha", "blog ", Audit(TenantActions.SiteDeleted, "blog "), Ct));

        Assert.NotNull(await store.GetDocumentAsync("Alpha", "blog", "items", "d1", Ct));
        Assert.NotNull(await store.FindAsync("Alpha", "blog", Ct));
    }

    /// <summary>REVIEW FOCUS 2: eight creates of one site at once. One is the site; the rest answer false.</summary>
    [Fact]
    public async Task Concurrent_creates_of_one_site_make_one()
    {
        var store = await CreateAsync();

        for (var round = 0; round < 12; round++)
        {
            var name = $"raced{round}";
            var created = await RaceAsync(8, i =>
                store.CreateAsync(Site(i % 2 == 0 ? "Alpha" : "alpha", name), Audit(TenantActions.SiteCreated, name), Ct));

            Assert.Single(created, made => made);
            Assert.Single(await store.ListAsync("Alpha", Ct), s => s.Name == name);
        }

        Assert.Equal(12, await TenantRowsAsync(TenantActions.SiteCreated));
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight writes to a site with room for one, at once. Exactly one is taken: a write
    /// counts the site's bytes with the others' rows, never before them.
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_cannot_both_pass_the_sites_data_limit()
    {
        var store = await StoreWithASiteAsync();
        var database = await DatabaseAsync();
        var json = $$"""{"text":"{{new string('x', 40_000)}}"}""";
        var room = SiteRules.MaxSiteDataBytes - Encoding.UTF8.GetByteCount(json) - 100;

        for (var round = 0; round < 6; round++)
        {
            await database.ExecuteAsync("DELETE FROM site_documents");
            await database.ExecuteAsync(
                $"INSERT INTO site_documents (team, site, collection, id, doc, bytes, updated_at, updated_by) VALUES ('Alpha', 'blog', 'items', 'held', '{{}}', {room}, '{At:O}', 'dev')");

            var written = await RaceAsync(8, i => PutAsync(store, $"w{i}", json));

            Assert.Single(written, w => w.Accepted);
            Assert.Equal(2, (await store.UsageAsync("Alpha", "blog", Ct)).Documents);
        }
    }
}

public sealed class SqliteSiteStoreTests : SiteStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerSiteStoreTests : SiteStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
