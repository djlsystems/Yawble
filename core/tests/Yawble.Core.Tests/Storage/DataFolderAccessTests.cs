using Yawble.Core.Companion;
using Yawble.Core.Ipc;
using Yawble.Core.Logs;
using Yawble.Core.Storage;
using Xunit;

// The mode checks run only on Linux and macOS (each POSIX test skips on Windows first).
#pragma warning disable CA1416

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// The data folder is user-only on Linux and macOS: what the core makes is 0700/0600, and at start an existing folder is
/// tightened (group and other bits off, links never followed) with a log line saying what changed. Temp folders only;
/// the mode checks are POSIX's and skip on Windows.
/// </summary>
public sealed class DataFolderAccessTests : IDisposable
{
    private const UnixFileMode Rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Wide = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string _root = Directory.CreateTempSubdirectory("yawble-dfa-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes are Linux's and macOS's");

    private static UnixFileMode Mode(string path) => File.GetUnixFileMode(path);

    private string Make(string relative, UnixFileMode mode, bool dir = false)
    {
        var path = Path.Combine(_root, relative);
        if (dir) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        File.SetUnixFileMode(path, mode);
        return path;
    }

    [Fact]
    public void AnExistingDataFolderIsTightened_AndTheChangeIsLogged()
    {
        SkipOnWindows();
        var data = Make("data", Rwx | Wide, dir: true);
        var db = Make("data/yawble.db", Rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var wal = Make("data/yawble.db-wal", Rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var logs = Make("data/logs", Rwx | Wide, dir: true);
        var log = Make("data/logs/core-2026-11-14.log", Rw | UnixFileMode.OtherRead);
        var backup = Make("data/backups/2026-11-14-daily.db", Rw | UnixFileMode.GroupRead);
        var transcript = Make("data/transcripts/t/m/run-1.log", Rw | UnixFileMode.OtherRead);
        var tool = Make("data/bin/helper", Rwx | Wide); // an executable keeps its user's execute bit
        var already = Make("data/cloud-link.json", Rw);
        var lines = new List<string>();

        var changed = DataFolderAccess.Secure(data, appRoot: null, lines.Add);

        Assert.Equal(Rwx, Mode(data));
        Assert.Equal(Rwx, Mode(logs));
        Assert.Equal(Rwx, Mode(Path.GetDirectoryName(backup)!));
        Assert.Equal(Rwx, Mode(Path.GetDirectoryName(transcript)!));
        foreach (var f in new[] { db, wal, log, backup, transcript, already }) Assert.Equal(Rw, Mode(f));
        Assert.Equal(Rwx, Mode(tool));
        Assert.True(changed >= 9, $"changed {changed}");
        var line = Assert.Single(lines);
        Assert.Contains("user-only", line);
        Assert.Contains($"{changed} path(s)", line);

        // A second start finds nothing to change and says nothing.
        lines.Clear();
        Assert.Equal(0, DataFolderAccess.Secure(data, null, lines.Add));
        Assert.Empty(lines);
    }

    [Fact]
    public void ALinkInTheDataFolderIsNeitherFollowedNorChanged()
    {
        SkipOnWindows();
        var data = Make("data", Rwx, dir: true);
        Make("data/yawble.db", Rw);
        var outside = Make("outside/shared.txt", Rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var outsideDir = Path.GetDirectoryName(outside)!;
        File.SetUnixFileMode(outsideDir, Rwx | Wide);
        File.CreateSymbolicLink(Path.Combine(data, "file-link"), outside);
        Directory.CreateSymbolicLink(Path.Combine(data, "dir-link"), outsideDir);

        DataFolderAccess.Secure(data, null, _ => { });

        Assert.Equal(Rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead, Mode(outside));
        Assert.Equal(Rwx | Wide, Mode(outsideDir));
    }

    [Fact]
    public void AFolderThatIsNotYawbles_IsLeftAsItIs()
    {
        SkipOnWindows();
        // A --data-dir pointed at the home or a shared folder by mistake: other files and no database.
        var other = Make("someones-home", Rwx | Wide, dir: true);
        var file = Make("someones-home/notes.txt", Rw | UnixFileMode.OtherRead);
        var lines = new List<string>();
        Assert.Equal(0, DataFolderAccess.Secure(other, null, lines.Add));
        Assert.Equal(Rwx | Wide, Mode(other));
        Assert.Equal(Rw | UnixFileMode.OtherRead, Mode(file));
        Assert.Contains("left as they are", Assert.Single(lines));
    }

    [Fact]
    public void ANewDataFolderIsMadeUserOnly_AndTheAppRootFolderItselfIsTightened()
    {
        SkipOnWindows();
        // Named as the shell's own app folder (app-folder.ts): Yawble on macOS and Windows, yawble on Linux.
        var appRoot = Make("Yawble", Rwx | Wide, dir: true);
        var teams = Make("Yawble/teams", Rwx | Wide, dir: true);
        var clone = Make("Yawble/teams/t/members/m/repo/README.md", Rw | UnixFileMode.OtherRead);
        var data = Path.Combine(appRoot, "data");

        DataFolderAccess.Secure(data, appRoot, _ => { });

        Assert.Equal(Rwx, Mode(data));
        Assert.Equal(Rwx, Mode(appRoot));
        // Only the app root folder itself: the members' clones keep their own modes.
        Assert.Equal(Rwx | Wide, Mode(teams));
        Assert.Equal(Rw | UnixFileMode.OtherRead, Mode(clone));
    }

    [Fact]
    public void WhatTheCoreCreatesIsUserOnly()
    {
        SkipOnWindows();
        var data = Path.Combine(_root, "data");
        UserOnlyFile.CreatePrivateDirectory(data);
        Assert.Equal(Rwx, Mode(data));

        var log = new DiagnosticLog(data, "core", new FixedClock());
        log.Write("hello");
        Assert.Equal(Rwx, Mode(log.Folder));
        var file = Assert.Single(Directory.GetFiles(log.Folder));
        Assert.Equal(Rw, Mode(file));
        Assert.Contains("hello", File.ReadAllText(file));

        var db = Path.Combine(data, "yawble.db");
        UserOnlyFile.CreateEmptyIfMissing(db);
        Assert.Equal(Rw, Mode(db));
        UserOnlyFile.CreateEmptyIfMissing(db); // there already: left alone

        // SQLite makes the -wal and -shm with the database file's own mode.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (x); INSERT INTO t VALUES (1);";
            cmd.ExecuteNonQuery();
            Assert.Equal(Rw, Mode(db + "-wal"));
            Assert.Equal(Rw, Mode(db + "-shm"));
        }
    }

    [Fact]
    public void ABackupAndItsSidecarAreUserOnly()
    {
        SkipOnWindows();
        var data = Path.Combine(_root, "data");
        UserOnlyFile.CreatePrivateDirectory(data);
        var db = Path.Combine(data, "yawble.db");
        Yawble.Core.Storage.SchemaMigrator.Migrate($"Data Source={db};Pooling=False");
        var backups = new BackupService(data, log: _ => { });
        var info = backups.BackupNow("manual");
        var file = Path.Combine(backups.BackupsDir, info.Name);
        Assert.Equal(Rwx, Mode(backups.BackupsDir));
        Assert.Equal(Rw, Mode(file));
        Assert.Equal(Rw, Mode(Path.ChangeExtension(file, ".json")));
    }

    [Fact]
    public async Task AnExistingSocketFolderThatIsWiderIsTightened_ASharedOneIsNot()
    {
        SkipOnWindows();
        var runtime = Make("run", Rwx | Wide, dir: true);
        var address = Path.Combine(runtime, "core.sock");
        await IpcEndpoint.Create(address).DisposeAsync();
        Assert.Equal(Rwx, Mode(runtime));

        var shared = Make("shared", Rwx | Wide, dir: true);
        Make("shared/notes.txt", Rw);
        await IpcEndpoint.Create(Path.Combine(shared, "core.sock")).DisposeAsync();
        Assert.Equal(Rwx | Wide, Mode(shared));
    }

    [Fact]
    public void OnWindows_NothingIsChanged()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows case");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "yawble.db"), "");
        Assert.Equal(0, DataFolderAccess.Secure(data, null, _ => throw new InvalidOperationException("nothing to say")));
        Assert.False(UserOnlyFile.Tighten(data));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    }
}
