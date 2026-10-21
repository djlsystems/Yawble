using System.Text.Json;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// A backlog item's create, delete, archive and dispatch land with their tenant rows in one
/// transaction: when the row cannot be written, nothing changes.
/// </summary>
public sealed class BacklogAuditTransactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-backlog-audit-{Guid.NewGuid():N}");
    private readonly string _database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BacklogAuditTransactionTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(_database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static TriggerAudit Row(string action) => new("person-1", "person@example.test", action, "B001", "Crawl openings", null);

    private SqliteBacklogStore Refusing() => new(_database, audit: (_, _, _, _) => throw new IOException("the tenant log is full"));

    private SqliteBacklogStore Recording(List<string> actions) => new(_database, audit: (connection, transaction, row, ct) =>
    {
        actions.Add(row.Action);
        return TenantAuditRow.AppendAsync(connection, transaction, row, ct);
    });

    [Fact]
    public async Task A_create_whose_row_cannot_be_written_creates_nothing()
    {
        var store = Refusing();

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(null, "Crawl openings", "spec", "person@example.test", Ct, audit: Row(TenantActions.BacklogItemCreated)));

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM backlog_items"));
    }

    [Fact]
    public async Task A_delete_an_archive_and_a_dispatch_whose_row_cannot_be_written_change_nothing()
    {
        var actions = new List<string>();
        var item = await Recording(actions).CreateAsync(null, "Crawl openings", "spec", "person@example.test", Ct, audit: Row(TenantActions.BacklogItemCreated));
        var store = Refusing();

        await Assert.ThrowsAsync<IOException>(() => store.SetArchivedAsync(item.Id, DateTimeOffset.UtcNow.ToString("O"), Ct, audit: Row(TenantActions.BacklogItemArchived)));
        await Assert.ThrowsAsync<IOException>(() => store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, "person@example.test", Ct, audit: Row(TenantActions.BacklogItemDispatched)));
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
        var store = Recording(actions);

        var item = await store.CreateAsync(null, "Crawl openings", "spec", "person@example.test", Ct, audit: Row(TenantActions.BacklogItemCreated));
        await store.AddDispatchAsync(item.Id, "alpha", "Alpha", 7, "person@example.test", Ct, audit: Row(TenantActions.BacklogItemDispatched));
        await store.SetArchivedAsync(item.Id, DateTimeOffset.UtcNow.ToString("O"), Ct, audit: Row(TenantActions.BacklogItemArchived));
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
        var store = new SqliteBacklogStore(_database, audit: (connection, transaction, row, ct) =>
        {
            rows.Add(row);
            return TenantAuditRow.AppendAsync(connection, transaction, row, ct);
        });

        var item = await store.CreateAsync("alpha", "Crawl openings", "spec", "person@example.test", Ct,
            audit: new("person-1", "person@example.test", TenantActions.BacklogItemCreated, null, "Crawl openings", """{"team":"alpha"}"""));

        var row = Assert.Single(rows);
        Assert.Equal(PlatformBacklogId.Format(item.Id), row.Subject);
        Assert.Equal("Crawl openings", row.SubjectName);
        var detail = JsonDocument.Parse(row.Detail!).RootElement;
        Assert.Equal(item.Id, detail.GetProperty("id").GetInt64());
        Assert.Equal("alpha", detail.GetProperty("team").GetString());
    }

    [Fact]
    public async Task A_change_with_no_row_asked_for_still_lands_as_before()
    {
        var store = new SqliteBacklogStore(_database);

        var item = await store.CreateAsync(null, "Crawl openings", "spec", "person@example.test", Ct);
        await store.SetArchivedAsync(item.Id, DateTimeOffset.UtcNow.ToString("O"), Ct);

        Assert.NotNull((await store.GetAsync(item.Id, Ct))?.ArchivedAt);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new SqliteConnection(SqliteOpen.ConnectionString(_database));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Ct) ?? 0L);
    }
}
