using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Credentials;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Provenance;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests;

/// <summary>Backups. Temp data dir, fake clock, in-memory credential store: no real programs, network or OS store.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("b-").FullName;
    private DateTimeOffset _now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
    private string Db => Path.Combine(_dir, "yawble.db");
    private string Cs => $"Data Source={Db};Pooling=False";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private BackupService Service(Func<IReadOnlyList<BackupConnection>>? connections = null) => new(_dir, () => _now, connections);

    private void MigratedDb()
    {
        SchemaMigrator.Migrate(Cs);
        Exec("INSERT INTO app_meta (key, value) VALUES ('marker', 'one');");
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string? Marker(string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_meta WHERE key = 'marker';";
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public void OnlineBackupIsConsistentAndNamedByUtcTimeAndReason()
    {
        MigratedDb();
        var info = Service().BackupNow();
        Assert.Equal("yawble-20261111-080000-manual.db", info.Name);
        Assert.Equal("manual", info.Reason);
        Assert.Equal(_now, info.Time);
        Assert.True(info.Size > 0);
        Assert.Equal("one", Marker(Path.Combine(_dir, "backups", info.Name)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "backups"), "*.partial"));
    }

    [Fact]
    public void MigrationTakesOneBackupBeforeTheFirstPendingMigration()
    {
        // An existing database that has no migrations yet.
        Exec("CREATE TABLE schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
        var svc = Service();
        var applied = SchemaMigrator.Migrate(Cs, svc.BeforeMigration);
        Assert.Equal(SchemaMigrator.Available, applied);
        var list = svc.List();
        var only = Assert.Single(list);
        Assert.Equal("before-" + SchemaMigrator.Available[0], only.Reason);
        // The backup is of the database as it was before: no migration had been applied.
        using var c = new SqliteConnection($"Data Source={Path.Combine(_dir, "backups", only.Name)};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM schema_migrations;";
        Assert.Equal(0L, cmd.ExecuteScalar());

        // Nothing pending: no second backup. A new database: none either.
        SchemaMigrator.Migrate(Cs, svc.BeforeMigration);
        Assert.Single(svc.List());
    }

    [Fact]
    public void NewDatabaseIsNotBackedUp()
    {
        var svc = Service();
        SchemaMigrator.Migrate(Cs, svc.BeforeMigration);
        Assert.Empty(svc.List());
    }

    [Fact]
    public void UpdateIsRefusedWhenTheBackupFails()
    {
        Exec("CREATE TABLE schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
        File.WriteAllText(Path.Combine(_dir, "backups"), "a file where the folder should be");
        var e = Assert.Throws<MigrationBackupException>(() => SchemaMigrator.Migrate(Cs, Service().BeforeMigration));
        Assert.Contains("backup", e.Message);
        using var c = new SqliteConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM schema_migrations;";
        Assert.Equal(0L, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'teams';";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public void DailySkipsUnder20HoursAndRunsAfter()
    {
        MigratedDb();
        var svc = Service();
        Assert.NotNull(svc.DailyIfDue());
        _now = _now.AddHours(19);
        Assert.Null(svc.DailyIfDue());
        _now = _now.AddHours(2); // 21 h after the first
        var second = svc.DailyIfDue();
        Assert.NotNull(second);
        Assert.Equal("daily", second!.Reason);
        Assert.Equal(2, svc.List().Count);
    }

    [Fact]
    public void AnyRecentBackupCountsForTheDailySkip()
    {
        MigratedDb();
        var svc = Service();
        svc.BackupNow("manual");
        _now = _now.AddHours(1);
        Assert.Null(svc.DailyIfDue());
    }

    [Fact]
    public void RetentionKeepsNewestPerKindAndHonoursTheDailySetting()
    {
        MigratedDb();
        var svc = Service();
        for (var i = 0; i < 9; i++) { _now = _now.AddMinutes(1); svc.BackupNow("daily"); }
        for (var i = 0; i < 8; i++) { _now = _now.AddMinutes(1); svc.BackupNow("manual"); }
        for (var i = 0; i < 7; i++) { _now = _now.AddMinutes(1); svc.BackupNow("before-0099_x"); }
        var list = svc.List();
        Assert.Equal(7, list.Count(b => b.Reason == "daily"));
        Assert.Equal(5, list.Count(b => b.Reason == "manual"));
        Assert.Equal(5, list.Count(b => b.Reason.StartsWith("before-")));
        // The newest survive.
        Assert.Equal(_now, list[0].Time);
        // Sidecars of deleted backups are gone too.
        Assert.Equal(list.Count, Directory.GetFiles(Path.Combine(_dir, "backups"), "*.json").Length);

        svc.SetKeepDaily(3);
        Assert.Equal(3, svc.KeepDaily());
        svc.Prune();
        Assert.Equal(3, svc.List().Count(b => b.Reason == "daily"));
    }

    [Fact]
    public void SidecarListsConnectionsAndSecretFieldNamesButNeverValues()
    {
        MigratedDb();
        var store = new Yawble.Core.Tests.Ops.MemoryCredentialStore();
        store.Set("connection/github-main/token", "ghp_SUPERSECRETVALUE");
        store.Set("connection/github-main/password", "hunter2-SECRET");
        var conn = BackupConnection.Probe(7, "GitHub", "api", "github-main", ["token", "password", "refresh"], store);
        Assert.Equal(["token", "password"], conn.SecretFields);

        var info = Service(() => [conn]).BackupNow();
        var json = File.ReadAllText(Path.Combine(_dir, "backups", Path.ChangeExtension(info.Name, ".json")));
        Assert.DoesNotContain("SUPERSECRET", json);
        Assert.DoesNotContain("hunter2", json);
        var doc = JsonNode.Parse(json)!;
        Assert.Equal(CoreInfo.Version, (string?)doc["version"]);
        Assert.NotNull(doc["commit"]);
        Assert.Contains(SchemaMigrator.Available[0], doc["migrations"]!.AsArray().Select(n => (string?)n));
        var c = doc["connections"]!.AsArray().Single()!;
        Assert.Equal("GitHub", (string?)c["name"]);
        Assert.Equal(["token", "password"], c["secretFields"]!.AsArray().Select(n => (string?)n).ToArray());
    }

    [Fact]
    public void RestoreMovesTheCurrentDatabaseAsideAndAsksForARestart()
    {
        MigratedDb();
        var svc = Service();
        var backup = svc.BackupNow();
        Exec("UPDATE app_meta SET value = 'two' WHERE key = 'marker';");
        var restarts = 0;
        svc.RestartRequested = () => restarts++;
        _now = _now.AddMinutes(5);

        svc.Restore(backup.Name);

        // Staged only: the live database is untouched until the next start.
        Assert.Equal(1, restarts);
        Assert.Equal("two", Marker(Db));
        Assert.Equal(backup.Name, svc.PendingRestore);
        Assert.DoesNotContain(svc.List(), b => b.Reason == "before-restore");

        Assert.Equal(backup.Name, Service().ApplyPendingRestore());
        Assert.Equal("one", Marker(Db));
        Assert.Null(svc.PendingRestore);
        var aside = Assert.Single(svc.List(), b => b.Reason == "before-restore");
        Assert.Equal("two", Marker(Path.Combine(_dir, "backups", aside.Name)));
    }

    [Fact]
    public void RestoreWhileTheDatabaseIsHeldOpenLikeTheCoreDoesNeitherFailsNorCorrupts()
    {
        MigratedDb();
        var svc = Service();
        var backup = svc.BackupNow();
        // The core keeps a pooled, WAL-mode connection open for its whole life (as BusStore does).
        using var held = new SqliteConnection($"Data Source={Db};Pooling=True");
        held.Open();
        using (var cmd = held.CreateCommand()) { cmd.CommandText = "PRAGMA journal_mode=WAL; UPDATE app_meta SET value = 'two' WHERE key = 'marker';"; cmd.ExecuteNonQuery(); }

        svc.Restore(backup.Name); // must not need the file free

        using (var cmd = held.CreateCommand()) { cmd.CommandText = "SELECT value FROM app_meta WHERE key = 'marker';"; Assert.Equal("two", cmd.ExecuteScalar()); }
        Assert.Equal("two", Marker(Db));

        // The next start: the core is gone (its connections closed), then the swap runs before the migrator.
        held.Close();
        SqliteConnection.ClearAllPools();
        Assert.Equal(backup.Name, Service().ApplyPendingRestore());
        SchemaMigrator.Migrate(Cs);
        Assert.Equal("one", Marker(Db));
    }

    /// <summary>A restart's BackupService whose file moves go through <paramref name="move"/> (to fail one on purpose).</summary>
    private BackupService Restarted(Action<string, string> move) => new(_dir, () => _now) { MoveFile = move };

    private string Staged => Path.Combine(_dir, "yawble.restore.db");

    [Fact]
    public void AFinalMoveThatFailsPutsTheCurrentDatabaseBack_AndReportsTheFailure()
    {
        MigratedDb();
        var svc = Service();
        var backup = svc.BackupNow();
        Exec("PRAGMA journal_mode=WAL; UPDATE app_meta SET value = 'two' WHERE key = 'marker';");
        _now = _now.AddMinutes(5);
        svc.Restore(backup.Name);
        SqliteConnection.ClearAllPools();

        var restarted = Restarted((from, to) =>
        {
            if (from == Staged && to == Db) throw new IOException("disk said no");
            File.Move(from, to);
        });
        Assert.Null(restarted.ApplyPendingRestore());

        Assert.True(File.Exists(Db));
        Assert.Equal("two", Marker(Db)); // the database from before, never an empty one
        Assert.DoesNotContain(svc.List(), b => b.Reason == "before-restore"); // moved back, not left aside
        var last = restarted.LastRestore!;
        Assert.Equal(backup.Name, last.Backup);
        Assert.Equal(RestoreResult.Failed, last.State);
        Assert.Contains("disk said no", last.Reason);
        Assert.Null(restarted.PendingRestore); // not retried at every start; the person can restore again from the list
    }

    [Fact]
    public void AFinalMoveAndAMoveBackThatBothFailStopTheStart_NamingWhereEverythingIs_AndLeaveEveryFile()
    {
        MigratedDb();
        var svc = Service();
        var backup = svc.BackupNow();
        Exec("UPDATE app_meta SET value = 'two' WHERE key = 'marker';");
        _now = _now.AddMinutes(5);
        svc.Restore(backup.Name);
        SqliteConnection.ClearAllPools();

        var restarted = Restarted((from, to) =>
        {
            if (from == Staged && to == Db) throw new IOException("disk said no");
            if (to == Db) throw new IOException("and no again"); // moving the old database back fails too
            File.Move(from, to);
        });
        var fatal = Assert.Throws<RestoreFatalException>(() => restarted.ApplyPendingRestore());

        var aside = Assert.Single(svc.List(), b => b.Reason == "before-restore");
        var asidePath = Path.Combine(_dir, "backups", aside.Name);
        Assert.Equal(asidePath, fatal.OldDatabase);
        Assert.Equal(Staged, fatal.StagedBackup);
        Assert.Contains(asidePath, fatal.Message);
        Assert.Contains(Staged, fatal.Message);
        Assert.Contains(backup.Name, fatal.Message);
        // Nothing is created or removed: no database in place (so nothing to migrate into), the old one aside, the
        // staged copy and its note still there, and the backup itself untouched.
        Assert.False(File.Exists(Db));
        Assert.Equal("two", Marker(asidePath));
        Assert.True(File.Exists(Staged));
        Assert.Equal(backup.Name, restarted.PendingRestore);
        Assert.True(File.Exists(Path.Combine(_dir, "backups", backup.Name)));
        Assert.Equal(RestoreResult.Failed, restarted.LastRestore!.State);
    }

    [Fact]
    public async Task ADatabaseStillHeldByTheOldCoreLeavesTheRestorePending_AndNothingSaysRestored()
    {
        MigratedDb();
        var svc = Service();
        var backup = svc.BackupNow();
        Exec("UPDATE app_meta SET value = 'two' WHERE key = 'marker';");
        svc.Restore(backup.Name);

        var restarted = Restarted((from, to) =>
        {
            if (from == Db) throw new IOException("The process cannot access the file because it is being used by another process.");
            File.Move(from, to);
        });
        Assert.Null(restarted.ApplyPendingRestore());

        Assert.Equal("two", Marker(Db));
        Assert.Equal(backup.Name, restarted.PendingRestore); // still staged: the next start tries again
        var last = restarted.LastRestore!;
        Assert.Equal(RestoreResult.Pending, last.State);
        Assert.Contains("being used by another process", last.Reason);

        using var _ = Actor.Use(Actor.Window);
        var status = (JsonObject)(await new BackupRpc(restarted).DispatchAsync("ops.restoreStatus", null, CancellationToken.None))!;
        Assert.Equal(backup.Name, (string?)status["pending"]);
        Assert.Equal("pending", (string?)status["last"]!["state"]);
        Assert.Contains("being used", (string?)status["last"]!["reason"]);
        Assert.DoesNotContain("restored", status.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        // the next start with the file free applies it and says so
        Assert.Equal(backup.Name, Service().ApplyPendingRestore());
        Assert.Equal("one", Marker(Db));
        Assert.Equal(RestoreResult.Restored, Service().LastRestore!.State);
    }

    [Fact]
    public async Task StagingARestoreAnswersStagedNotRestored()
    {
        MigratedDb();
        var svc = Service();
        var name = svc.BackupNow().Name;
        using var _ = Actor.Use(Actor.Window);
        var done = (JsonObject)(await new BackupRpc(svc).DispatchAsync("ops.restore", new JsonObject { ["backup"] = name, ["confirm"] = true }, CancellationToken.None))!;
        Assert.Equal(name, (string?)done["staged"]!["name"]);
        Assert.Null(done["restored"]);
        var status = (JsonObject)(await new BackupRpc(svc).DispatchAsync("ops.restoreStatus", null, CancellationToken.None))!;
        Assert.Equal(name, (string?)status["pending"]);
    }

    [Fact]
    public void ADamagedStagedRestoreIsDiscardedAndTheCurrentDatabaseKept()
    {
        MigratedDb();
        var svc = Service();
        var backup = svc.BackupNow();
        svc.Restore(backup.Name);
        File.WriteAllText(Path.Combine(_dir, "yawble.restore.db"), "this is not a database, just text padding " + new string('x', 5000));

        Assert.Null(Service().ApplyPendingRestore());
        Assert.Equal("one", Marker(Db));
        Assert.Null(svc.PendingRestore);
        Assert.DoesNotContain(svc.List(), b => b.Reason == "before-restore");
    }

    [Fact]
    public void ConnectionsComeFromTheServiceWithOnlySetSecretFieldKeys()
    {
        MigratedDb();
        var store = new Yawble.Core.Tests.Ops.MemoryCredentialStore();
        var conns = new Yawble.Core.Connections.ConnectionService(store, Cs);
        conns.Save(null, "Stripe", [new("user", "User", false, "bob"), new("token", "Token", true, null)],
            new Dictionary<string, string> { ["token"] = "sk_live_VERYSECRETVALUE1" });

        var info = Service(() => BackupConnections.FromService(conns)).BackupNow();
        var json = File.ReadAllText(Path.Combine(_dir, "backups", Path.ChangeExtension(info.Name, ".json")));
        Assert.DoesNotContain("VERYSECRET", json);
        Assert.DoesNotContain("sk_live", json);
        var c = JsonNode.Parse(json)!["connections"]!.AsArray().Single()!;
        Assert.Equal("Stripe", (string?)c["name"]);
        Assert.Equal("secret", (string?)c["kind"]);
        Assert.Equal(["token"], c["secretFields"]!.AsArray().Select(n => (string?)n).ToArray());

        var fromTable = Assert.Single(BackupConnections.FromDatabase(Cs));
        Assert.Equal("Stripe", fromTable.Name);
        Assert.Empty(fromTable.SecretFields);
    }

    [Theory]
    [InlineData("../yawble.db")]
    [InlineData("yawble.db")]
    [InlineData("nope.db")]
    public void RestoreRejectsNamesThatAreNotBackups(string name)
    {
        MigratedDb();
        Assert.ThrowsAny<Exception>(() => Service().Restore(name));
        Assert.Equal("one", Marker(Db));
    }

    private static async Task<RpcException> Refused(BackupRpc rpc, Actor actor, string method, JsonObject? p)
    {
        using var _ = Actor.Use(actor);
        return await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync(method, p, CancellationToken.None));
    }

    [Fact]
    public async Task RestoreIsThePersonsAloneAndNeedsConfirmation()
    {
        MigratedDb();
        var svc = Service();
        var name = svc.BackupNow().Name;
        var rpc = new BackupRpc(svc);
        var p = new JsonObject { ["backup"] = name, ["confirm"] = true };

        foreach (var actor in new[] { Actor.CloudViewer("x"), new Actor(Actor.Concierge, null), new Actor(Actor.Agent, null), new Actor(Actor.Trigger, null) })
            Assert.Equal("forbidden", (await Refused(rpc, actor, "ops.restore", p)).Code);
        Assert.Equal("forbidden", (await Refused(rpc, Actor.CloudViewer("x"), "ops.backupNow", null)).Code);
        Assert.Equal("bad_params", (await Refused(rpc, Actor.Window, "ops.restore", new JsonObject { ["backup"] = name })).Code);
        Assert.DoesNotContain(svc.List(), b => b.Reason == "before-restore");

        using var _ = Actor.Use(Actor.Window);
        var done = await rpc.DispatchAsync("ops.restore", p, CancellationToken.None);
        Assert.True((bool)done!["restarting"]!);
        Assert.Equal(name, svc.PendingRestore); // staged for the next start
    }

    [Fact]
    public async Task ListShapeIsNameSizeReasonTime()
    {
        MigratedDb();
        var svc = Service();
        svc.BackupNow();
        using var _ = Actor.Use(Actor.CloudViewer("x"));
        var list = (JsonArray)(await new BackupRpc(svc).DispatchAsync("ops.backups", null, CancellationToken.None))!;
        var item = Assert.Single(list)!.AsObject();
        Assert.Equal(["name", "size", "reason", "time"], item.Select(kv => kv.Key).ToArray());
    }

    [Theory]
    [InlineData("ops.backups", true)]
    [InlineData("ops.restoreStatus", false)]
    [InlineData("ops.backupNow", false)]
    [InlineData("ops.restore", false)]
    [InlineData("ops.setKeepDaily", false)]
    [InlineData("ops.keepDaily", false)]
    public void CloudLinkAllowsOnlyTheReadOnlyList(string method, bool allowed) =>
        Assert.Equal(allowed, RemoteAccess.Refusal(method, null, allowHostTerminals: false) is null);

    [Fact]
    public void RestoreIsRefusedForExternalUnlessApprovedByThePerson()
    {
        var external = Actor.ExternalClient("claude-code");
        Assert.NotNull(BackupRpc.RestoreRefusal(external));
        Assert.NotNull(BackupRpc.RestoreRefusal(Actor.CloudViewer("pat@example.test")));
        Assert.NotNull(BackupRpc.BackupNowRefusal(external));
        Assert.Null(BackupRpc.RestoreRefusal(external with { ApprovedBy = "pat" }));
        Assert.Null(BackupRpc.BackupNowRefusal(external with { ApprovedBy = "pat" }));
        Assert.Null(BackupRpc.RestoreRefusal(Actor.Window));
    }

    [Fact]
    public void ABackupNeverReadsASecretValue_TheSetFieldKeysComeFromAnExistenceCheck()
    {
        MigratedDb();
        var store = new IndexedNoReadsStore();
        var conns = new Yawble.Core.Connections.ConnectionService(store, Cs);
        conns.Save(null, "Stripe", [new("user", "User", false, "bob"), new("token", "Token", true, null), new("refresh", "Refresh", true, null)],
            new Dictionary<string, string> { ["token"] = "sk_live_VERYSECRETVALUE1", ["refresh"] = "rt_VERYSECRETVALUE2" });
        store.DeleteField("refresh"); // its entry is gone from the store: not set
        store.Armed = true; // from here on, any Get fails the test

        var info = Service(() => BackupConnections.FromService(conns)).BackupNow();
        var json = File.ReadAllText(Path.Combine(_dir, "backups", Path.ChangeExtension(info.Name, ".json")));
        Assert.Equal(0, store.ReadsWhileArmed);
        Assert.DoesNotContain("VERYSECRET", json);
        var c = JsonNode.Parse(json)!["connections"]!.AsArray().Single()!;
        Assert.Equal("Stripe", (string?)c["name"]);
        Assert.Equal(["token"], c["secretFields"]!.AsArray().Select(n => (string?)n).ToArray());
        Assert.Null(c["secretFieldsUnknown"]);
    }

    [Fact]
    public void AStoreThatCannotSayWhetherAKeyExistsGetsItsSecretFieldKeysListedAsUnknown()
    {
        MigratedDb();
        var store = new NoReadsStore();
        var conns = new Yawble.Core.Connections.ConnectionService(store, Cs);
        conns.Save(null, "Stripe", [new("token", "Token", true, null), new("refresh", "Refresh", true, null)],
            new Dictionary<string, string> { ["token"] = "sk_live_VERYSECRETVALUE1", ["refresh"] = "rt_VERYSECRETVALUE2" });
        store.DeleteField("refresh");
        store.Armed = true;

        var info = Service(() => BackupConnections.FromService(conns)).BackupNow();
        var json = File.ReadAllText(Path.Combine(_dir, "backups", Path.ChangeExtension(info.Name, ".json")));
        Assert.Equal(0, store.ReadsWhileArmed);
        var c = JsonNode.Parse(json)!["connections"]!.AsArray().Single()!;
        Assert.Empty(c["secretFields"]!.AsArray());
        Assert.Equal(["token", "refresh"], c["secretFieldsUnknown"]!.AsArray().Select(n => (string?)n).ToArray());
    }

    /// <summary>A memory store that counts (and refuses) every Get once armed. It cannot say whether a key exists without a Get.</summary>
    private class NoReadsStore : ICredentialStore
    {
        protected readonly Dictionary<string, string> Values = [];
        public bool Armed;
        public int ReadsWhileArmed;
        public string Kind => "memory";
        public string? Get(string key)
        {
            if (Armed) { ReadsWhileArmed++; Assert.Fail($"a backup read the value of {key}"); }
            return Values.GetValueOrDefault(key);
        }
        public void Set(string key, string value) => Values[key] = value;
        public bool Delete(string key) => Values.Remove(key);
        public void DeleteField(string field) => Values.Remove(Values.Keys.Single(k => k.EndsWith("/" + field, StringComparison.Ordinal)));
    }

    private sealed class IndexedNoReadsStore : NoReadsStore, Yawble.Core.Ops.ICredentialIndex
    {
        public bool Has(string key) => Values.ContainsKey(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) => Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string Kind => "memory";
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value) => _values[key] = value;
        public bool Delete(string key) => _values.Remove(key);
    }
}
