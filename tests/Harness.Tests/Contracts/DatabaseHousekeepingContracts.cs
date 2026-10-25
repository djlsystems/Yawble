using System.Data.Common;
using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IPluginRemovalRows"/>, as each provider must keep it: the interface's remarks. The row and
/// the folder move land together or neither does: a move that succeeds commits the row, a move that fails
/// leaves none, and a row that cannot be written moves nothing.
/// </summary>
public abstract class PluginRemovalRowsContract : HostStoreContract
{
    private static TriggerAudit Row => Audit(TenantActions.PluginRemoved, "fixture");

    [Fact]
    public async Task A_move_that_succeeds_commits_its_row()
    {
        var database = await DatabaseAsync();
        var moved = 0;

        Assert.True(await database.RemovalRows().RecordWhileAsync(Row, () => { moved++; return true; }, () => moved--, Ct));

        Assert.Equal(1, moved);
        Assert.Equal([TenantActions.PluginRemoved], await database.ColumnAsync("SELECT action FROM tenant_events"));
    }

    [Fact]
    public async Task A_move_that_fails_leaves_no_row()
    {
        var database = await DatabaseAsync();

        Assert.False(await database.RemovalRows().RecordWhileAsync(Row, () => false, () => throw new InvalidOperationException("No move to undo."), Ct));

        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    [Fact]
    public async Task A_row_that_cannot_be_written_moves_nothing()
    {
        var database = await DatabaseAsync();
        await database.ExecuteAsync("DROP TABLE tenant_events");
        var moved = false;

        await Assert.ThrowsAnyAsync<DbException>(() => database.RemovalRows().RecordWhileAsync(Row, () => moved = true, () => { }, Ct));

        Assert.False(moved);
    }
}

public sealed class SqlitePluginRemovalRowsTests : PluginRemovalRowsContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerPluginRemovalRowsTests : PluginRemovalRowsContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}

/// <summary>
/// <see cref="IDatabaseHealth"/>, as each provider must keep it: a database that opens answers true, and
/// one that cannot be reached answers false, never a throw (<c>/healthz</c> reports it as
/// <c>cannot open</c>).
/// </summary>
public abstract class DatabaseHealthContract : HostStoreContract
{
    [Fact]
    public async Task A_database_that_opens_answers_true()
    {
        Assert.True(await (await DatabaseAsync()).Health().PingAsync(Ct));
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_answers_false_and_never_throws()
    {
        Assert.False(await (await DatabaseAsync()).MissingHealth().PingAsync(Ct));
    }
}

public sealed class SqliteDatabaseHealthTests : DatabaseHealthContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerDatabaseHealthTests : DatabaseHealthContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}

/// <summary>
/// <see cref="IDatabaseMaintenance"/>, as each provider must keep it. A checkpoint on an idle database
/// answers, not busy, and never throws. A provider says what backs it up instead of a copy, or takes the
/// copy: with <c>BackupElsewhere</c> null, <c>CopyToAsync</c> writes the whole database at the path and
/// answers it (SQLite's <c>VACUUM INTO</c>, from <c>DailyBackupTests</c>); with it set, it writes nothing
/// and answers null, and on SQL Server the sentence is that point-in-time restore is the backup.
/// </summary>
public abstract class DatabaseMaintenanceContract : HostStoreContract
{
    [Fact]
    public async Task A_checkpoint_of_an_idle_database_answers_not_busy()
    {
        var result = await (await DatabaseAsync()).Maintenance().CheckpointAsync(Ct);

        Assert.False(result.Busy);
        Assert.True(result.CheckpointedFrames <= result.LogFrames);
    }

    [Fact]
    public async Task A_copy_is_taken_or_what_backs_the_database_up_instead_is_said()
    {
        var database = await DatabaseAsync();
        await database.Teams().SaveTeamAsync(new PersistedTeam("copied", null), Ct);
        var maintenance = database.Maintenance();
        var directory = Directory.CreateTempSubdirectory("harness-copy-").FullName;

        try
        {
            var destination = Path.Combine(directory, "messages-copy.db");
            var written = await maintenance.CopyToAsync(destination, Ct);

            if (maintenance.BackupElsewhere is null)
            {
                Assert.Equal(destination, written);
                Assert.True(new FileInfo(destination).Length > 0);
            }
            else
            {
                Assert.Null(written);
                Assert.False(File.Exists(destination));
                Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class SqliteDatabaseMaintenanceTests : DatabaseMaintenanceContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());

    [Fact]
    public async Task The_sqlite_file_is_copied_by_this_host()
    {
        Assert.Null((await DatabaseAsync()).Maintenance().BackupElsewhere);
    }
}

public sealed class SqlServerDatabaseMaintenanceTests : DatabaseMaintenanceContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();

    [Fact]
    public async Task Point_in_time_restore_is_the_backup_on_sql_server()
    {
        var maintenance = (await DatabaseAsync()).Maintenance();

        Assert.Equal("point-in-time restore is the backup on SQL Server.", maintenance.BackupElsewhere);
        Assert.Equal(SqlServerDatabaseMaintenance.PointInTimeRestore, maintenance.BackupElsewhere);
        Assert.Equal(new CheckpointResult(false, 0, 0), await maintenance.CheckpointAsync(Ct));
    }
}
