using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// THE LOG POSITION AN INSTANT FALLS AT. <c>LastSeqBeforeAsync</c> halves the seq range rather than
/// scanning <c>occurred_at</c>, so it must answer what a scan would: the highest seq written strictly
/// before the instant, 0 for none, across an empty log, an instant before every row, rows sharing
/// one stamp, and the gaps deletion leaves.
/// </summary>
public sealed class LastSeqBeforeTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2001, 2, 3, 4, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-lastseq-{Guid.NewGuid():N}");
    private readonly string _database;

    public LastSeqBeforeTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(_database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task An_empty_log_answers_zero()
    {
        var store = new SqliteMessageStore(_database);

        Assert.Equal(0, await store.LastSeqBeforeAsync(T0, Ct));
    }

    [Fact]
    public async Task An_instant_before_every_row_answers_zero_and_one_after_every_row_the_head()
    {
        await RowsAsync((1, 10), (2, 20), (3, 30));
        var store = new SqliteMessageStore(_database);

        Assert.Equal(0, await store.LastSeqBeforeAsync(T0, Ct));
        Assert.Equal(0, await store.LastSeqBeforeAsync(T0.AddSeconds(10), Ct));
        Assert.Equal(3, await store.LastSeqBeforeAsync(T0.AddSeconds(31), Ct));
    }

    [Fact]
    public async Task A_row_stamped_at_the_instant_is_not_before_it()
    {
        await RowsAsync((1, 10), (2, 20), (3, 20), (4, 20), (5, 30));
        var store = new SqliteMessageStore(_database);

        Assert.Equal(1, await store.LastSeqBeforeAsync(T0.AddSeconds(20), Ct));
        Assert.Equal(4, await store.LastSeqBeforeAsync(T0.AddSeconds(21), Ct));
    }

    [Fact]
    public async Task Gaps_left_by_deletion_answer_what_a_scan_would()
    {
        // Every seq from 1 to 200, one second apart; then all but a scattered few are deleted.
        await RowsAsync([.. Enumerable.Range(1, 200).Select(seq => ((long)seq, seq))]);
        long[] kept = [3, 4, 57, 58, 120, 199];
        await ExecuteAsync($"DELETE FROM messages WHERE seq NOT IN ({string.Join(", ", kept)})");
        var store = new SqliteMessageStore(_database);

        for (var second = 0; second <= 202; second++)
        {
            var at = T0.AddSeconds(second);
            var scan = kept.Where(seq => T0.AddSeconds(seq) < at).DefaultIfEmpty(0).Max();

            Assert.Equal(scan, await store.LastSeqBeforeAsync(at, Ct));
        }
    }

    /// <summary>Writes rows at these seqs, stamped this many seconds after <see cref="T0"/>.</summary>
    private async Task RowsAsync(params (long Seq, int Seconds)[] rows)
    {
        await using var connection = new SqliteConnection($"Data Source={_database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var transaction = connection.BeginTransaction();

        foreach (var (seq, seconds) in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO messages (seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at)
                VALUES ($seq, 'test.mark', '{}', 'console', $seq, NULL, 0, $at)
                """;
            insert.Parameters.AddWithValue("$seq", seq);
            insert.Parameters.AddWithValue("$at", T0.AddSeconds(seconds).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
