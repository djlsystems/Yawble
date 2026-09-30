using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// A CURSOR IS REMOVED, NEVER ZEROED.
///
/// Retention may purge what is behind every cursor. A deleted container's cursor left in place -
/// or zeroed - holds the slowest position down forever, so retention is stuck behind a subscriber
/// nobody will ever restore.
/// </summary>
public sealed class CursorRemovalTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-cursor-{Guid.NewGuid():N}");

    private readonly SqliteMessageStore _store;

    public CursorRemovalTests()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _store = new SqliteMessageStore(database);
    }

    [Fact]
    public async Task A_forgotten_cursor_no_longer_holds_retention_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var gone = new ContainerId("Alpha", "Gone");
        var live = new ContainerId("Alpha", "Live");

        await _store.AdvanceAsync(gone, 5, ct);
        await _store.AdvanceAsync(live, 9, ct);
        Assert.Equal(5, await _store.SlowestAsync(ct));

        await _store.ForgetAsync(gone, ct);

        Assert.Equal(9, await _store.SlowestAsync(ct));
    }

    [Fact]
    public async Task Forgetting_the_last_cursor_leaves_no_position_rather_than_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var only = new ContainerId("Alpha", "Only");

        await _store.AdvanceAsync(only, 7, ct);
        await _store.ForgetAsync(only, ct);

        Assert.Null(await _store.SlowestAsync(ct));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
