using System.Collections.Concurrent;
using System.Globalization;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// The copies taken before a schema step keep the last 30 days plus the newest 10, by the stamp in
/// their names; a migration's own copy always survives; and nothing in the backups folder that is
/// not a before-step copy is ever touched.
/// </summary>
public sealed class BeforeStepBackupsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-before-step-{Guid.NewGuid():N}");

    public BeforeStepBackupsTests() => Directory.CreateDirectory(Backups);

    private string Database => Path.Combine(_directory, "messages.db");

    private string Backups => Path.Combine(_directory, "backups");

    [Fact]
    public void Twenty_eight_copies_over_sixty_days_keep_the_last_thirty_days_plus_the_newest_ten_and_nothing_else_is_touched()
    {
        // Newest first: copy i was taken i * 60/27 days ago, so 0 to 60 days.
        var copies = Enumerable.Range(0, 28)
            .Select(i => (Path: BeforeStep(Now.AddDays(-i * 60.0 / 27), $"auth-{i:000}"), Age: i * 60.0 / 27))
            .ToArray();

        var daily = Write("messages-20260801-030000-daily.db");
        var manual = Write("messages-20260701-000000-manual.db");
        var unrelated = Write("notes.txt");
        var skills = Path.Combine(Backups, "skills-before-builtins");
        Directory.CreateDirectory(skills);
        var skill = Path.Combine(skills, "old-skill.md");
        File.WriteAllText(skill, "kept");

        var pruned = BeforeStepBackups.Prune(Backups, Now);

        var expectedKept = copies.Where((copy, index) => index < 10 || copy.Age < 30).Select(c => c.Path).ToArray();
        var expectedDeleted = copies.Select(c => c.Path).Except(expectedKept).ToArray();

        Assert.Equal(14, expectedKept.Length);
        Assert.All(expectedKept, path => Assert.True(File.Exists(path), path));
        Assert.All(expectedDeleted, path => Assert.False(File.Exists(path), path));
        Assert.Equal(expectedDeleted.Order(), pruned.Deleted.Order());
        Assert.Equal(14, pruned.Kept);

        Assert.True(File.Exists(daily));
        Assert.True(File.Exists(manual));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(skill));
    }

    [Fact]
    public void With_every_copy_older_than_thirty_days_exactly_the_newest_ten_are_kept()
    {
        var copies = Enumerable.Range(0, 15).Select(i => BeforeStep(Now.AddDays(-31 - i), "auth-001")).ToArray();

        var pruned = BeforeStepBackups.Prune(Backups, Now);

        Assert.Equal(copies.Take(10), BeforeStepBackups.Existing(Backups).Select(c => c.Path));
        Assert.Equal(5, pruned.Deleted.Count);
        Assert.Equal(10, pruned.Kept);
    }

    [Fact]
    public void Keep_three_with_no_age_rule_keeps_exactly_the_newest_three()
    {
        var copies = Enumerable.Range(0, 8).Select(i => BeforeStep(Now.AddHours(-i), "messages-002")).ToArray();

        var pruned = BeforeStepBackups.Prune(Backups, Now, keep: 3, keepDays: 0);

        Assert.Equal(copies.Take(3), BeforeStepBackups.Existing(Backups).Select(c => c.Path));
        Assert.Equal(5, pruned.Deleted.Count);
        Assert.Equal(3, pruned.Kept);
    }

    [Fact]
    public async Task A_migrations_own_copy_survives_keep_one_and_its_copy_prunes_the_older_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var older = BeforeStep(DateTimeOffset.UtcNow.AddDays(-90), "auth-001");

        // A copy stamped AFTER the migration's own would otherwise be "the newest one" and win the
        // only place - the migration's copy must survive anyway.
        var future = BeforeStep(DateTimeOffset.UtcNow.AddDays(1), "auth-001");

        var migrator = new SchemaMigrator(Database, Backups, keepBeforeStep: 1, keepBeforeStepDays: 0);
        var taken = await migrator.ApplyAsync(SchemaModules.All, ct);

        Assert.NotNull(taken);
        Assert.True(File.Exists(taken));
        Assert.True(File.Exists(future));
        Assert.False(File.Exists(older));
        Assert.NotNull(migrator.LastPrune);
        Assert.Equal([older], migrator.LastPrune.Deleted);
        Assert.Equal(2, migrator.LastPrune.Kept);
    }

    [Fact]
    public async Task The_daily_backup_prunes_the_before_step_copies_and_leaves_manual_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var old = Enumerable.Range(0, 4).Select(i => BeforeStep(Now.AddDays(-40 - i), "auth-001")).ToArray();
        var manual = Write("messages-20200101-000000-manual.db");

        var backup = new DailyBackup(Database, Backups, keepBeforeStep: 2);
        await backup.TakeAsync(Now, ct);

        Assert.Equal(old.Take(2), BeforeStepBackups.Existing(Backups).Select(c => c.Path));
        Assert.Equal(old.Skip(2).Order(), backup.LastBeforeStepPrune!.Deleted.Order());
        Assert.True(File.Exists(manual));
        Assert.Single(backup.Existing());
    }

    [Fact]
    public void Age_is_read_from_the_name_so_a_recent_modification_time_does_not_keep_an_old_copy()
    {
        var newest = BeforeStep(Now.AddDays(-1), "auth-002");
        var old = BeforeStep(Now.AddDays(-100), "auth-001");
        File.SetLastWriteTimeUtc(old, Now.UtcDateTime);

        BeforeStepBackups.Prune(Backups, Now, keep: 1, keepDays: 30);

        Assert.True(File.Exists(newest));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void A_name_that_does_not_parse_is_left_alone()
    {
        BeforeStep(Now, "auth-002");
        var noStamp = Write("messages-before-auth-001.db");
        var badDate = Write("messages-20261399-000000-before-auth-001.db");
        var shortStamp = Write("messages-2020-before-auth-001.db");
        var otherSuffix = Write("messages-20200101-000000-before-auth-001.db.bak");

        var pruned = BeforeStepBackups.Prune(Backups, Now, keep: 1, keepDays: 0);

        Assert.Empty(pruned.Deleted);
        Assert.All([noStamp, badDate, shortStamp, otherSuffix], path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void The_log_line_names_each_copy_deleted_and_how_many_are_kept()
    {
        var kept = Enumerable.Range(0, 10).Select(i => BeforeStep(Now.AddDays(-31 - i), "auth-001")).ToArray();
        var gone = Enumerable.Range(10, 2).Select(i => BeforeStep(Now.AddDays(-31 - i), "auth-001")).ToArray();

        var line = BeforeStepBackups.Prune(Backups, Now).Describe();

        Assert.Equal(
            "Before-step backups: deleted 2 older than 30 days beyond the newest 10 ("
            + $"{Path.GetFileName(gone[0])}, {Path.GetFileName(gone[1])}); 10 kept.",
            line);
        Assert.All(kept, path => Assert.DoesNotContain(Path.GetFileName(path), line));
        Assert.Equal(
            "Before-step backups: none deleted older than 30 days beyond the newest 10; 10 kept.",
            BeforeStepBackups.Prune(Backups, Now).Describe());
    }

    [Fact]
    public async Task The_daily_runner_logs_the_prune_line()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var gone = BeforeStep(DateTimeOffset.UtcNow.AddDays(-45), "auth-001");
        BeforeStep(DateTimeOffset.UtcNow.AddDays(-1), "auth-002");

        var lines = new ConcurrentQueue<string>();
        using var runner = new DailyBackupRunner(
            new DailyBackup(Database, Backups, keepBeforeStep: 1),
            TimeSpan.FromHours(1),
            new LineLogger(lines));

        await runner.StartAsync(ct);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!lines.Any(l => l.StartsWith("Before-step backups:", StringComparison.Ordinal))
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }

        await runner.StopAsync(ct);

        Assert.Contains(
            $"Before-step backups: deleted 1 older than 30 days beyond the newest 1 ({Path.GetFileName(gone)}); 1 kept.",
            lines);
    }

    private string BeforeStep(DateTimeOffset taken, string step) =>
        Write($"messages-{taken.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-before-{step}.db");

    private string Write(string name)
    {
        var path = Path.Combine(Backups, name);
        File.WriteAllText(path, "copy");
        return path;
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE notes (body TEXT); INSERT INTO notes VALUES ('row');";
        await command.ExecuteNonQueryAsync(ct);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private sealed class LineLogger(ConcurrentQueue<string> lines) : ILogger<DailyBackupRunner>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception));
    }
}
