using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests;

/// <summary>
/// The daily copy is a database SQLite will open, the newest seven survive, and nothing
/// that is not a daily copy is ever deleted - the pre-migration and manual backups share the folder.
/// </summary>
public sealed class DailyBackupTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 3, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-backup-{Guid.NewGuid():N}");

    public DailyBackupTests() => Directory.CreateDirectory(_directory);

    private string Database => Path.Combine(_directory, "messages.db");

    private string Backups => Path.Combine(_directory, "backups");

    [Fact]
    public async Task The_backup_is_a_valid_database_holding_what_the_live_one_held()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seeded = await SeedAsync("before the backup", ct);

        var written = await new DailyBackup(Database, Backups).TakeAsync(Start, ct);

        await using var copy = new SqliteConnection($"Data Source={written};Mode=ReadOnly;Pooling=False");
        await copy.OpenAsync(ct);

        await using var check = copy.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await check.ExecuteScalarAsync(ct));

        await using var read = copy.CreateCommand();
        read.CommandText = "SELECT body FROM notes;";
        Assert.Equal("before the backup", await read.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Only_the_newest_seven_daily_copies_are_kept_and_other_backups_are_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seeded = await SeedAsync("row", ct);

        Directory.CreateDirectory(Backups);
        var migration = Path.Combine(Backups, "messages-20200101-000000-before-auth-001.db");
        var manual = Path.Combine(Backups, "messages-20200101-000000-manual.db");
        await File.WriteAllTextAsync(migration, "not ours", ct);
        await File.WriteAllTextAsync(manual, "not ours", ct);

        var backup = new DailyBackup(Database, Backups);
        var taken = new List<string>();

        for (var day = 0; day < 10; day++)
        {
            taken.Add(await backup.TakeAsync(Start.AddDays(day), ct));
        }

        Assert.Equal(taken.AsEnumerable().Reverse().Take(7), backup.Existing());
        Assert.True(File.Exists(migration));
        Assert.True(File.Exists(manual));
        Assert.All(taken.Take(3), old => Assert.False(File.Exists(old)));
    }

    [Fact]
    public async Task A_copy_is_due_when_there_is_none_or_the_newest_is_a_day_old()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seeded = await SeedAsync("row", ct);
        var backup = new DailyBackup(Database, Backups);

        Assert.True(backup.IsDue(Start));

        await backup.TakeAsync(Start, ct);

        Assert.False(backup.IsDue(Start.AddHours(23)));
        Assert.True(backup.IsDue(Start.AddDays(1)));
    }

    [Fact]
    public async Task The_checkpoint_truncates_the_wal_to_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        // Held open so closing the last connection does not checkpoint for us.
        await using var held = await SeedAsync("row", ct);
        Assert.True(new FileInfo(Database + "-wal").Length > 0);

        var result = await SqliteMaintenance.CheckpointAsync(Database, ct);

        Assert.False(result.Busy);
        Assert.Equal(0, new FileInfo(Database + "-wal").Length);
    }

    [Fact]
    public async Task The_quiet_sweep_checkpoints_even_when_the_sweep_itself_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var checkpointed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A null sweep throws on every tick, which is the failure being survived.
        using var reaper = new QuietTeamReaper(
            null!,
            () => TimeSpan.FromMinutes(30),
            TimeSpan.FromMilliseconds(20),
            NullLogger<QuietTeamReaper>.Instance,
            _ =>
            {
                checkpointed.TrySetResult();
                return Task.FromResult(new CheckpointResult(false, 0, 0));
            });

        await reaper.StartAsync(ct);
        await checkpointed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await reaper.StopAsync(ct);
    }

    private async Task<SqliteConnection> SeedAsync(string body, CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=WAL; CREATE TABLE notes (body TEXT); INSERT INTO notes VALUES ($body);";
        command.Parameters.AddWithValue("$body", body);
        await command.ExecuteNonQueryAsync(ct);

        return connection;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
