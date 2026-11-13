using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Credentials;

namespace Yawble.Core.Storage;

/// <summary>A Connection as a backup's sidecar records it: names only, never a value.</summary>
/// <paramref name="SecretFieldsUnknown"/>: secret field keys whose store could not say whether they are set without reading the value.
public sealed record BackupConnection(long Id, string Name, string Kind, IReadOnlyList<string> SecretFields, IReadOnlyList<string>? SecretFieldsUnknown = null)
{
    /// <summary>
    /// Lists which of <paramref name="fields"/> have an entry under connection/&lt;storeKey&gt;/&lt;field&gt; in the
    /// credential store, with its existence check only: no value is read.
    /// </summary>
    public static BackupConnection Probe(long id, string name, string kind, string storeKey, IEnumerable<string> fields, Ops.ICredentialIndex store) =>
        new(id, name, kind, fields.Where(f => store.Has($"connection/{storeKey}/{f}")).ToArray());
}

/// <summary>The outcome of a staged restore at start. <see cref="Reason"/> says why when it is not <see cref="Restored"/>.</summary>
public sealed record RestoreResult(string Backup, string State, string? Reason, DateTimeOffset At)
{
    public const string Restored = "restored", Pending = "pending", Failed = "failed";
}

/// <summary>One line of ops.backups.</summary>
public sealed record BackupInfo(string Name, long Size, string Reason, DateTimeOffset Time);

/// <summary>
/// Backups of the app's database:<data dir>/backups/yawble-&lt;UTC yyyyMMdd-HHmmss&gt;-&lt;reason&gt;.db,
/// made with SQLite's VACUUM INTO (consistent while the app runs; never a raw copy of a WAL database), each with a
/// sidecar .json (versions, migrations, Connections by name and secret FIELD NAMES). Secrets and website sessions are not
/// copied. Old backups are deleted by <see cref="Prune"/>.
/// </summary>
public sealed class BackupService
{
    public const int KeepBeforeUpdate = 5, KeepManual = 5, DefaultKeepDaily = 7;
    public static readonly TimeSpan DailySkipWithin = TimeSpan.FromHours(20);
    public const string KeepDailySetting = "backups.keepDaily";

    private static readonly Regex NamePattern = new(@"^yawble-(\d{8}-\d{6})-([a-z0-9][a-z0-9_.-]*)\.db$", RegexOptions.Compiled);
    private static readonly string[] SqlSuffixes = ["", "-wal", "-shm"];

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<IReadOnlyList<BackupConnection>> _connections;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(string dataDir, Func<DateTimeOffset>? now = null, Func<IReadOnlyList<BackupConnection>>? connections = null, Action<string>? log = null)
    {
        DataDir = dataDir;
        _dbPath = Path.Combine(dataDir, "yawble.db");
        _connectionString = $"Data Source={_dbPath};Pooling=False";
        BackupsDir = Path.Combine(dataDir, "backups");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _connections = connections ?? (() => []);
        _log = log ?? (_ => { });
    }

    public string DataDir { get; }
    public string BackupsDir { get; }

    /// <summary>Raised after a restore has replaced the database: the app must restart (set by the host).</summary>
    public Action? RestartRequested { get; set; }

    public static string FileName(DateTimeOffset utc, string reason) =>
        $"yawble-{utc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{reason}.db";

    /// <summary>The hook for <see cref="SchemaMigrator.Migrate(string, Action{SqliteConnection, string})"/>.</summary>
    public Action<SqliteConnection, string> BeforeMigration => (connection, first) => BackupFrom(connection, "before-" + first);

    /// <summary>Backs up through an already open connection. Throws when the backup could not be made; leaves no partial file.</summary>
    public BackupInfo BackupFrom(SqliteConnection connection, string reason)
    {
        _gate.Wait();
        try { return BackupLocked(connection, reason); }
        finally { _gate.Release(); }
    }

    public BackupInfo BackupNow(string reason = "manual")
    {
        _gate.Wait();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return BackupLocked(connection, reason);
        }
        finally { _gate.Release(); }
    }

    private BackupInfo BackupLocked(SqliteConnection connection, string reason)
    {
        if (!Regex.IsMatch(reason, "^[a-z0-9][a-z0-9_.-]*$")) throw new ArgumentException($"'{reason}' is not a usable backup reason");
        Directory.CreateDirectory(BackupsDir);
        var when = _now().ToUniversalTime();
        var name = FileName(when, reason);
        var target = Path.Combine(BackupsDir, name);
        var partial = Path.Combine(BackupsDir, "~.tmp");
        DeleteIfExists(partial);
        try
        {
            if (File.Exists(target)) throw new IOException($"a backup named {name} already exists");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"VACUUM INTO '{partial.Replace("'", "''")}';";
                cmd.ExecuteNonQuery();
            }
            File.Move(partial, target);
            WriteSidecar(connection, target, when, reason);
        }
        catch
        {
            DeleteIfExists(partial);
            DeleteIfExists(target);
            DeleteIfExists(SidecarOf(target));
            throw;
        }
        var info = new BackupInfo(name, new FileInfo(target).Length, reason, when);
        _log($"backup: {name} ({info.Size} bytes)");
        try { Prune(); } catch (Exception e) { _log($"backup: pruning old backups failed: {e.Message}"); }
        return info;
    }

    private void WriteSidecar(SqliteConnection connection, string target, DateTimeOffset when, string reason)
    {
        var migrations = new List<string>();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM schema_migrations ORDER BY name;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) migrations.Add(r.GetString(0));
        }
        catch (SqliteException) { /* a database with no migrations yet: an empty list */ }
        var sidecar = new
        {
            app = CoreInfo.ProductName,
            version = CoreInfo.Version,
            commit = CoreInfo.Commit,
            reason,
            time = when,
            migrations,
            connections = _connections().Select(c => new { id = c.Id, name = c.Name, kind = c.Kind, secretFields = c.SecretFields, secretFieldsUnknown = c.SecretFieldsUnknown is { Count: > 0 } u ? u : null }).ToArray(),
        };
        File.WriteAllText(SidecarOf(target), JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SidecarOf(string dbFile) => Path.ChangeExtension(dbFile, ".json");

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(BackupsDir)) return [];
        return Directory.EnumerateFiles(BackupsDir, "yawble-*.db")
            .Select(f => Parse(new FileInfo(f)))
            .OfType<BackupInfo>()
            .OrderByDescending(b => b.Time).ThenByDescending(b => b.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static BackupInfo? Parse(FileInfo file)
    {
        var m = NamePattern.Match(file.Name);
        if (!m.Success || !DateTimeOffset.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)) return null;
        return new BackupInfo(file.Name, file.Length, m.Groups[2].Value, time);
    }

    private static string Group(string reason) =>
        reason == "daily" ? "daily" : reason == "manual" ? "manual" : "before";

    public int KeepDaily()
    {
        try
        {
            using var c = new SqliteConnection(_connectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_meta WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", KeepDailySetting);
            if (cmd.ExecuteScalar() is string s && int.TryParse(s, out var n) && n >= 1) return Math.Min(n, 365);
        }
        catch (SqliteException) { }
        return DefaultKeepDaily;
    }

    public void SetKeepDaily(int count)
    {
        if (count < 1 || count > 365) throw new ArgumentOutOfRangeException(nameof(count), "keep between 1 and 365 daily backups");
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO app_meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;";
        cmd.Parameters.AddWithValue("$k", KeepDailySetting);
        cmd.Parameters.AddWithValue("$v", count.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Keeps the newest N of each kind (daily: the setting, before-update: 5, manual: 5; before-restore counts as before-update).</summary>
    public int Prune(int? keepDaily = null)
    {
        var limits = new Dictionary<string, int> { ["daily"] = keepDaily ?? KeepDaily(), ["before"] = KeepBeforeUpdate, ["manual"] = KeepManual };
        var removed = 0;
        foreach (var group in List().GroupBy(b => Group(b.Reason)))
            foreach (var old in group.Skip(limits[group.Key]))
            {
                var path = Path.Combine(BackupsDir, old.Name);
                foreach (var s in SqlSuffixes) DeleteIfExists(path + s); // a before-restore file may have its -wal and -shm
                DeleteIfExists(SidecarOf(path));
                removed++;
            }
        return removed;
    }

    /// <summary>
    /// The daily rule: nothing when any backup is under 20 hours old, otherwise one "daily" backup. Returns what it made,
    /// or null when skipped. Failures are logged, never thrown (a timer calls this).
    /// </summary>
    public BackupInfo? DailyIfDue()
    {
        var newest = List().FirstOrDefault();
        if (newest is not null && _now() - newest.Time < DailySkipWithin) return null;
        if (!File.Exists(_dbPath)) return null;
        try { return BackupNow("daily"); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log($"backup: the daily backup failed: {e.Message}");
            return null;
        }
    }

    /// <summary>First start of the day, then an hourly check, until cancelled.</summary>
    public async Task RunDailyAsync(CancellationToken ct, TimeSpan? every = null)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Run(() => DailyIfDue(), ct).ConfigureAwait(false);
                await Task.Delay(every ?? TimeSpan.FromHours(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Stages a restore: checks the backup and copies it next to the database as yawble.restore.db, then asks the app
    /// to restart. The live database is NOT touched (the running core holds it open); <see cref="ApplyPendingRestore"/> swaps
    /// it in at the next start, before any connection opens and before SchemaMigrator.Migrate. Callers must have checked the actor: only the person in the window may do this.
    /// </summary>
    public BackupInfo Restore(string backup)
    {
        if (backup != Path.GetFileName(backup) || !NamePattern.IsMatch(backup)) throw new ArgumentException($"'{backup}' is not a backup name");
        var source = Path.Combine(BackupsDir, backup);
        if (!File.Exists(source)) throw new FileNotFoundException($"there is no backup named {backup}");
        _gate.Wait();
        try
        {
            CheckIntact(source, backup);
            Directory.CreateDirectory(DataDir);
            var partial = StagedDb + ".part";
            DeleteIfExists(partial);
            DeleteIfExists(StagedNote);
            DeleteIfExists(StagedDb);
            DeleteIfExists(ResultFile); // the last outcome is about an earlier restore
            File.Copy(source, partial, overwrite: false);
            File.Move(partial, StagedDb);
            File.WriteAllText(StagedNote, backup);
        }
        finally { _gate.Release(); }
        _log($"backup: {backup} staged; restarting to restore it");
        RestartRequested?.Invoke();
        return Parse(new FileInfo(source))!;
    }

    private string StagedDb => Path.Combine(DataDir, "yawble.restore.db");
    private string StagedNote => Path.Combine(DataDir, "yawble.restore.txt");

    /// <summary>The backup a restore has staged for the next start, or null.</summary>
    public string? PendingRestore => File.Exists(StagedDb) && File.Exists(StagedNote) ? File.ReadAllText(StagedNote).Trim() : null;

    private static void CheckIntact(string file, string label)
    {
        using var check = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        check.Open();
        using var cmd = check.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        if ((cmd.ExecuteScalar() as string) != "ok") throw new InvalidOperationException($"{label} is damaged; the current database was left alone");
    }

    /// <summary>
    /// At start, before anything opens the database: when a restore is staged, moves the current file (with its -wal and -shm)
    /// aside as yawble-&lt;ts&gt;-before-restore.db (in the backups folder) and puts the staged backup in its place. A staged
    /// file that fails the check is discarded and the current database stays. If the current file cannot be moved (the old core
    /// still holds it), the restore stays staged for the next start (<see cref="RestoreResult.Pending"/>). If the staged file
    /// cannot be put in place, the current file is moved back so the core never starts on an empty database
    /// (<see cref="RestoreResult.Failed"/>). Every outcome is kept in <see cref="LastRestore"/>. Returns the backup restored, or
    /// null. Throws only <see cref="RestoreFatalException"/>: when the current file was moved aside and can be put neither
    /// back nor replaced, so the core must not start (the migrator would make an empty database); every file stays.
    /// </summary>
    public string? ApplyPendingRestore()
    {
        if (!File.Exists(StagedDb)) { try { DeleteIfExists(StagedNote); } catch (IOException) { } return null; }
        var name = "unknown";
        var movedAside = new List<(string From, string To)>();
        string? aside = null;
        try
        {
            if (File.Exists(StagedNote)) name = File.ReadAllText(StagedNote).Trim();
            try { CheckIntact(StagedDb, name); }
            catch (Exception e) when (e is InvalidOperationException or SqliteException)
            {
                _log($"backup: the staged restore of {name} is unusable ({e.Message}); the current database was kept");
                DeleteIfExists(StagedDb); DeleteIfExists(StagedNote);
                Record(name, RestoreResult.Failed, $"the backup copy was damaged ({e.Message}); the database from before was kept");
                return null;
            }
            if (File.Exists(_dbPath))
            {
                try
                {
                    using var c = new SqliteConnection(_connectionString);
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException) { /* a damaged or held database is moved aside as it is, if it can be */ }
                SqliteConnection.ClearAllPools();
                Directory.CreateDirectory(BackupsDir);
                aside = Path.Combine(BackupsDir, FileName(_now(), "before-restore"));
                try
                {
                    // The -wal and -shm go with it, so nothing a failed checkpoint left behind is lost.
                    foreach (var s in SqlSuffixes)
                        if (File.Exists(_dbPath + s)) { MoveFile(_dbPath + s, aside + s); movedAside.Add((_dbPath + s, aside + s)); }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (!PutBack(movedAside)) throw Stranded(name, aside, $"the database could not be moved aside: {e.Message}");
                    _log($"backup: the database is still in use, so the restore of {name} waits for the next start: {e.Message}");
                    Record(name, RestoreResult.Pending, $"the database was still in use, so it was not replaced ({e.Message}); the next start tries again");
                    return null;
                }
                try { File.WriteAllText(SidecarOf(aside), JsonSerializer.Serialize(new { app = CoreInfo.ProductName, version = CoreInfo.Version, commit = CoreInfo.Commit, reason = "before-restore", restoredFrom = name })); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"backup: no sidecar for {aside}: {e.Message}"); }
            }
            else
            {
                foreach (var s in SqlSuffixes.Skip(1)) DeleteIfExists(_dbPath + s); // stale leftovers of a file that is gone
            }
            try { MoveFile(StagedDb, _dbPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!PutBack(movedAside)) throw Stranded(name, aside!, $"the backup could not be put in place: {e.Message}");
                if (aside is not null) { try { DeleteIfExists(SidecarOf(aside)); } catch (IOException) { } }
                try { DeleteIfExists(StagedDb); DeleteIfExists(StagedNote); } catch (IOException) { }
                _log($"backup: could not put the restore of {name} in place ({e.Message}); the database from before was kept");
                Record(name, RestoreResult.Failed, $"the backup could not be put in place ({e.Message}); the database from before was kept");
                return null;
            }
            DeleteIfExists(StagedNote);
            _log($"backup: restored {name}");
            Record(name, RestoreResult.Restored, null);
            return name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"backup: could not apply the staged restore of {name}: {e.Message}");
            Record(name, File.Exists(StagedDb) ? RestoreResult.Pending : RestoreResult.Failed, e.Message);
            return null;
        }
    }

    /// <summary>
    /// Moves files back where they were, the database file first. True when all of them are back. Stops at the first move
    /// that fails, so a database that cannot go back keeps its -wal and -shm beside it rather than leaving them alone
    /// next to the database path (where the next start would take them for stale leftovers and delete them).
    /// </summary>
    private bool PutBack(List<(string From, string To)> moved)
    {
        foreach (var (from, to) in moved)
        {
            try { MoveFile(to, from); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"backup: could not move {to} back to {from}: {e.Message}");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The database from before is neither in place nor could be put back: starting would make a new, empty database
    /// where it was. Recorded, logged, and thrown, with every file left where it is.
    /// </summary>
    private RestoreFatalException Stranded(string name, string aside, string why)
    {
        var fatal = new RestoreFatalException(aside, StagedDb, Path.Combine(BackupsDir, name),
            $"Yawble could not start: restoring the backup {name} failed ({why}), and the database from before could not be moved back. " +
            $"Nothing was deleted and no new database was made. The database from before is at {aside}; " +
            $"the backup being restored is at {StagedDb} (a copy of {Path.Combine(BackupsDir, name)}). " +
            $"Close Yawble, move the database from before back to {_dbPath} (or the backup copy there instead), then start Yawble again.");
        _log("backup: " + fatal.Message);
        Record(name, RestoreResult.Failed, fatal.Message);
        return fatal;
    }

    /// <summary>File moves during a restore. A seam for tests (to make one move fail); the app always uses File.Move.</summary>
    internal Action<string, string> MoveFile { get; init; } = (from, to) => File.Move(from, to);

    private string ResultFile => Path.Combine(DataDir, "yawble.restore-result.json");

    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    /// <summary>How the last staged restore went at start (restored, pending, failed), or null if none ran since the last staging.</summary>
    public RestoreResult? LastRestore
    {
        get
        {
            try { return File.Exists(ResultFile) ? JsonSerializer.Deserialize<RestoreResult>(File.ReadAllText(ResultFile), ResultJson) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    private void Record(string backup, string state, string? reason)
    {
        try { File.WriteAllText(ResultFile, JsonSerializer.Serialize(new RestoreResult(backup, state, reason, _now()), ResultJson)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"backup: could not record the restore result: {e.Message}"); }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>
/// A restore left no database in place and could not put the old one back (BackupService.ApplyPendingRestore). The core
/// must stop before anything opens or migrates the database; the message says where each file is, for the person.
/// </summary>
public sealed class RestoreFatalException(string oldDatabase, string stagedBackup, string backup, string message) : Exception(message)
{
    /// <summary>Where the database from before is now (moved aside, in the backups folder).</summary>
    public string OldDatabase { get; } = oldDatabase;

    /// <summary>The staged copy of the backup being restored, next to where the database belongs.</summary>
    public string StagedBackup { get; } = stagedBackup;

    /// <summary>The backup itself, in the backups folder.</summary>
    public string Backup { get; } = backup;
}
