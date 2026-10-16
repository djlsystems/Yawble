using System.Collections.Concurrent;
using System.Globalization;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void Copies_that_share_a_second_are_ordered_by_their_numeric_suffix()
    {
        var second = Now.AddDays(-40);
        var first = BeforeStep(second, "auth-001");
        var two = BeforeStep(second, "auth-001-2");
        var ten = BeforeStep(second, "auth-001-10");
        var later = BeforeStep(Now.AddDays(-39), "auth-002");

        Assert.Equal([later, ten, two, first], BeforeStepBackups.Existing(Backups).Select(c => c.Path));

        // The boundary falls inside that second: the later copy and -10 stay, -2 and the unsuffixed go.
        var pruned = BeforeStepBackups.Prune(Backups, Now, keep: 2, keepDays: 0);

        Assert.Equal([later, ten], BeforeStepBackups.Existing(Backups).Select(c => c.Path));
        Assert.Equal(new[] { first, two }.Order(), pruned.Deleted.Order());
    }

    [Fact]
    public void A_deletion_that_fails_is_reported_and_the_rest_are_still_deleted()
    {
        var copies = Enumerable.Range(0, 5).Select(i => BeforeStep(Now.AddDays(-40 - i), "auth-001")).ToArray();
        var stuck = copies[3];

        var pruned = BeforeStepBackups.Prune(
            Backups, Now, keep: 1, keepDays: 0,
            delete: path =>
            {
                if (path == stuck) throw new IOException("the file is in use");
                File.Delete(path);
            });

        Assert.Equal(new[] { copies[1], copies[2], copies[4] }.Order(), pruned.Deleted.Order());
        Assert.Equal([$"{Path.GetFileName(stuck)} (the file is in use)"], pruned.Failed);
        Assert.True(File.Exists(copies[0]));
        Assert.True(File.Exists(stuck));
        Assert.False(File.Exists(copies[1]));
        Assert.False(File.Exists(copies[4]));
        Assert.EndsWith(
            $"; 1 kept. Could not delete 1: {Path.GetFileName(stuck)} (the file is in use).",
            pruned.Describe());
    }

    [Fact]
    public async Task With_a_configured_backups_directory_a_migrations_copy_lands_there_and_the_daily_run_prunes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var dataRoot = Path.Combine(_directory, "data");
        var elsewhere = Path.Combine(_directory, "elsewhere");
        Directory.CreateDirectory(dataRoot);
        var database = Path.Combine(dataRoot, "messages.db");

        var settings = BackupSettings.From(Configuration(elsewhere), dataRoot);
        var taken = await settings.MigratorFor(database).ApplyAsync(SchemaModules.All, ct);

        Assert.NotNull(taken);
        Assert.Equal(elsewhere, Path.GetDirectoryName(taken));
        Assert.False(Directory.Exists(BackupSettings.DefaultDirectory(dataRoot)));

        var older = Path.Combine(elsewhere, Path.GetFileName(BeforeStep(DateTimeOffset.UtcNow.AddDays(-2), "auth-001")));
        File.Move(Path.Combine(Backups, Path.GetFileName(older)), older);

        var daily = settings.DailyBackupFor(database);
        await daily.TakeAsync(DateTimeOffset.UtcNow, ct);

        Assert.Equal(elsewhere, daily.Directory);
        Assert.Equal([older], daily.LastBeforeStepPrune!.Deleted);
        Assert.True(File.Exists(taken));
        Assert.Single(daily.Existing());
    }

    [Fact]
    public async Task The_operator_migrate_writes_and_prunes_by_the_configured_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        var dataRoot = Path.Combine(_directory, "data");
        var elsewhere = Path.Combine(_directory, "elsewhere");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(elsewhere);

        var old = new[] { -3, -2 }
            .Select(days => BeforeStep(DateTimeOffset.UtcNow.AddDays(days), "auth-001"))
            .Select(path =>
            {
                var moved = Path.Combine(elsewhere, Path.GetFileName(path));
                File.Move(path, moved);
                return moved;
            })
            .ToArray();

        using var output = new StringWriter();
        await OperatorCommands.TryRunAsync(
            ["--list-users"], dataRoot, output, ct, BackupSettings.From(Configuration(elsewhere), dataRoot));

        var remaining = BeforeStepBackups.Existing(elsewhere).Select(c => c.Path).ToArray();

        Assert.Single(remaining);
        Assert.DoesNotContain(remaining[0], old);
        Assert.All(old, path => Assert.False(File.Exists(path), path));
        Assert.Contains("Before-step backups: deleted 2 beyond the newest 1 (", output.ToString());
        Assert.False(Directory.Exists(BackupSettings.DefaultDirectory(dataRoot)));
    }

    [Fact]
    public void A_before_restore_copy_is_kept_forever_and_not_counted_toward_the_newest_ten()
    {
        var ordinary = Enumerable.Range(0, 15).Select(i => BeforeStep(Now.AddDays(-31 - i), "auth-001")).ToArray();

        // Newer than every ordinary copy, so counting it would push the tenth out; and one far older,
        // with a same-second suffix, that the rule would delete if it were a before-step copy.
        var restored = BeforeStep(Now.AddDays(-30.5), "restore");
        var restoredAgain = Write(
            $"messages-{Now.AddDays(-60).UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-before-restore-2.db");

        var pruned = BeforeStepBackups.Prune(Backups, Now);

        Assert.True(File.Exists(restored));
        Assert.True(File.Exists(restoredAgain));
        Assert.Equal(ordinary.Take(10), BeforeStepBackups.Existing(Backups).Select(c => c.Path));
        Assert.Equal(ordinary.Skip(10).Order(), pruned.Deleted.Order());
        Assert.Equal(10, pruned.Kept);
    }

    [Fact]
    public async Task With_a_configured_backups_directory_the_manual_and_before_restore_copies_land_there()
    {
        var ct = TestContext.Current.CancellationToken;
        var dataRoot = Path.Combine(_directory, "data");
        var elsewhere = Path.Combine(_directory, "elsewhere");
        Directory.CreateDirectory(dataRoot);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(dataRoot, "messages.db")};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE notes (body TEXT);";
            await command.ExecuteNonQueryAsync(ct);
        }

        var settings = BackupSettings.From(Configuration(elsewhere), dataRoot);

        using var backup = new StringWriter();
        Assert.Equal(
            OperatorOutcome.Completed,
            await OperatorCommands.TryRunAsync(["--backup"], dataRoot, backup, ct, settings));

        var manual = Assert.Single(Directory.GetFiles(elsewhere, "messages-*-manual.db"));
        Assert.Contains($"Wrote {manual}", backup.ToString());

        using var restore = new StringWriter();
        Assert.Equal(
            OperatorOutcome.Completed,
            await OperatorCommands.TryRunAsync(["--restore", manual], dataRoot, restore, ct, settings));

        var beforeRestore = Assert.Single(Directory.GetFiles(elsewhere, "messages-*-before-restore.db"));
        Assert.Contains($"Backed up the current database to {beforeRestore}", restore.ToString());
        Assert.False(Directory.Exists(BackupSettings.DefaultDirectory(dataRoot)));
    }

    private static IConfiguration Configuration(string directory) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backups:Directory"] = directory,
                ["Backups:KeepBeforeStep"] = "1",
                ["Backups:KeepBeforeStepDays"] = "0",
            })
            .Build();

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
