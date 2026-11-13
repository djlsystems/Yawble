using Yawble.Core.Agents;
using Yawble.Core.Ops;
using Xunit;

namespace Yawble.Core.Tests.Ops;

/// <summary>The doctor's checks, orphan credentials, quiet teams, log retention. Fakes only.</summary>
public class DoctorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private const long Gb = 1L << 30;
    private readonly Clock _clock = new(Start);
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-doctor-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSystem : IDoctorSystem
    {
        public string Quick = "ok";
        public List<string> Applied = ["0001", "0002"];
        public List<string> Shipped = ["0001", "0002"];
        public long Free = 50 * Gb;
        public Dictionary<string, long> Teams = new() { ["Yawble"] = 3 * Gb };
        public long Logs = 1024;
        public SandboxState Sandbox_ = new(null, "done", null, null);
        public LinkState Link_ = new("off", null);
        public string QuickCheck() => Quick;
        public IReadOnlyList<string> AppliedMigrations() => Applied;
        public IReadOnlyList<string> ShippedMigrations() => Shipped;
        public long FreeBytesOnDataDrive() => Free;
        public IReadOnlyDictionary<string, long> TeamFolderSizes() => Teams;
        public long LogBytes() => Logs;
        public SandboxState Sandbox() => Sandbox_;
        public LinkState Link() => Link_;
    }

    private sealed class FakeBackups(params BackupInfo[] list) : IBackupLister
    {
        public IReadOnlyList<BackupInfo> List() => list;
    }

    private sealed class FakeAgents(params DetectedCli[] list) : ICliDetector
    {
        public Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DetectedCli>>(list);
    }

    private sealed class FakeConnections(params ConnectionInfo[] list) : IConnectionSource
    {
        private readonly List<ConnectionInfo> _list = [.. list];
        public void Set(ConnectionInfo c) => _list.Add(c);
        public IReadOnlyList<ConnectionInfo> List() => _list;
    }

    private sealed class FakeTeams(params DoctorTeam[] list) : IDoctorTeams
    {
        public List<string> Archived = [];
        public Task<IReadOnlyList<DoctorTeam>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DoctorTeam>>(list);
        public Task ArchiveAsync(string team, CancellationToken ct) { Archived.Add(team); return Task.CompletedTask; }
    }

    private static DetectedCli Cli(string id, bool found, string? signIn) =>
        new(id, found, null, null, null, "other", [], [], [], "") { SignIn = signIn is null ? null : new CliSignIn(signIn, "", [], new Dictionary<string, string>()) };

    private static DoctorTeam Team(string name, int quietDays = 0, bool paused = false, int unattended = 0, params StuckMember[] stuck) =>
        new(name, paused, false, Start - TimeSpan.FromDays(quietDays), unattended, stuck);

    private DoctorService Doctor(FakeSystem? s = null, IBackupLister? b = null, ICliDetector? a = null, IConnectionSource? c = null,
        ICredentialIndex? k = null, IDoctorTeams? t = null) =>
        new(s ?? new FakeSystem(), b, a, c, k, t, _clock, git: new Yawble.Core.Teams.GitService(new Yawble.Core.Tests.Teams.FakeGitHost()));

    private static async Task<DoctorCheck> Check(DoctorService d, string id) =>
        (await d.RunAsync(Ct)).Single(c => c.Id == id);

    [Fact]
    public async Task All_checks_come_back_with_ids_and_a_healthy_machine_is_ok()
    {
        var ready = new FakeAgents(Cli("claude", true, SignInStatus.SignedIn));
        var checks = await Doctor(b: new FakeBackups(new BackupInfo("x", 1, "daily", Start - TimeSpan.FromHours(3))), a: ready, t: new FakeTeams(Team("Yawble"))).RunAsync(Ct);
        Assert.Equal(["database", "disk", "sandbox", "git", "agents", "cloudLink", "connections", "teams", "logs"], checks.Select(c => c.Id));
        Assert.All(checks, c => Assert.Equal("ok", c.Status));
    }

    [Fact]
    public async Task Database_ok_warn_fail()
    {
        var fresh = new FakeBackups(new BackupInfo("n", 1, "daily", Start - TimeSpan.FromHours(47)));
        Assert.Equal("ok", (await Check(Doctor(b: fresh), "database")).Status);

        var stale = new FakeBackups(new BackupInfo("n", 1, "daily", Start - TimeSpan.FromHours(49)));
        Assert.Equal("warn", (await Check(Doctor(b: stale), "database")).Status);
        Assert.Equal("warn", (await Check(Doctor(b: new FakeBackups()), "database")).Status);

        var pending = new FakeSystem { Shipped = ["0001", "0002", "0003"] };
        Assert.Equal("warn", (await Check(Doctor(pending, fresh), "database")).Status);

        var broken = await Check(Doctor(new FakeSystem { Quick = "row 4 missing from index" }, fresh), "database");
        Assert.Equal("fail", broken.Status);
        Assert.Contains("row 4 missing", broken.Detail);
    }

    [Fact]
    public async Task Disk_ok_warn_fail_and_lists_team_sizes()
    {
        var ok = await Check(Doctor(new FakeSystem { Free = 6 * Gb }), "disk");
        Assert.Equal("ok", ok.Status);
        Assert.Contains("Yawble", ok.Detail);
        Assert.Equal("warn", (await Check(Doctor(new FakeSystem { Free = 4 * Gb }), "disk")).Status);
        Assert.Equal("fail", (await Check(Doctor(new FakeSystem { Free = Gb / 2 }), "disk")).Status);
    }

    [Fact]
    public async Task Sandbox_ok_warn_fail()
    {
        Assert.Equal("ok", (await Check(Doctor(), "sandbox")).Status);
        Assert.Equal("warn", (await Check(Doctor(new FakeSystem { Sandbox_ = new(null, "failed", "escape", null) }), "sandbox")).Status);
        Assert.Equal("fail", (await Check(Doctor(new FakeSystem { Sandbox_ = new("no launcher", "none", null, "this core has no gateway to test") }), "sandbox")).Status);
        Assert.Equal("fail", (await Check(Doctor(new FakeSystem { Sandbox_ = new(null, "done", null, "no answer within 5 s") }), "sandbox")).Status);
    }

    [Fact]
    public async Task Agents_ok_warn_fail()
    {
        Assert.Equal("ok", (await Check(Doctor(a: new FakeAgents(Cli("claude", true, SignInStatus.SignedIn))), "agents")).Status);
        Assert.Equal("warn", (await Check(Doctor(a: new FakeAgents(Cli("claude", true, SignInStatus.SignedIn), Cli("grok", true, null))), "agents")).Status);
        Assert.Equal("fail", (await Check(Doctor(a: new FakeAgents(Cli("claude", false, null))), "agents")).Status);
    }

    [Fact]
    public async Task Cloud_link_ok_warn_fail()
    {
        Assert.Equal("ok", (await Check(Doctor(new FakeSystem { Link_ = new("connected", null) }), "cloudLink")).Status);
        Assert.Equal("ok", (await Check(Doctor(), "cloudLink")).Status);
        Assert.Equal("warn", (await Check(Doctor(new FakeSystem { Link_ = new("offline", null) }), "cloudLink")).Status);
        var failing = await Check(Doctor(new FakeSystem { Link_ = new("offline", "refused") }), "cloudLink");
        Assert.Equal("fail", failing.Status);
        Assert.Contains("refused", failing.Detail);
    }

    [Fact]
    public async Task Logs_ok_and_warn()
    {
        Assert.Equal("ok", (await Check(Doctor(), "logs")).Status);
        Assert.Equal("warn", (await Check(Doctor(new FakeSystem { Logs = Gb + 1 }), "logs")).Status);
    }

    [Fact]
    public async Task Teams_ok_stuck_unattended_paused_and_quiet()
    {
        Assert.Equal("ok", (await Check(Doctor(t: new FakeTeams(Team("A"), Team("Old", 90) with { Archived = true })), "teams")).Status);
        Assert.Equal("fail", (await Check(Doctor(t: new FakeTeams(Team("A", 0, false, 0, new StuckMember("Ines", TimeSpan.FromHours(5))))), "teams")).Status);
        Assert.Equal("warn", (await Check(Doctor(t: new FakeTeams(Team("A", 0, false, 2))), "teams")).Status);
        Assert.Equal("warn", (await Check(Doctor(t: new FakeTeams(Team("A", 0, true))), "teams")).Status);
    }

    [Fact]
    public async Task A_quiet_team_offers_archive_after_30_days_never_automatically()
    {
        var teams = new FakeTeams(Team("Edge", 29), Team("Dusty", 30));
        var d = Doctor(t: teams);
        var check = await Check(d, "teams");
        Assert.Equal("warn", check.Status);
        Assert.Contains("Dusty is quiet", check.Detail);
        Assert.DoesNotContain("Edge", check.Detail);
        var fix = Assert.Single(check.Fixes!);
        Assert.Equal("archive-team:Dusty", fix.Id);
        Assert.True(fix.Confirm);
        Assert.Empty(teams.Archived);

        var ask = await d.RunFixAsync(fix.Id, false, Ct);
        Assert.True(ask.NeedsConfirm);
        Assert.Empty(teams.Archived);
        var done = await d.RunFixAsync(fix.Id, true, Ct);
        Assert.True(done.Done);
        Assert.Equal(["Dusty"], teams.Archived);
    }

    private static ConnectionInfo Conn(string id, string name, string storeKey, string status = "works") => new(id, name, "secret", status, storeKey);

    private static (MemoryCredentialStore Store, MemoryLedger Ledger) StoreWith(params (string Key, bool Issued)[] entries)
    {
        var store = new MemoryCredentialStore();
        var ledger = new MemoryLedger();
        foreach (var (key, issued) in entries)
        {
            store.Set(key, "SECRET-" + key);
            if (issued) ledger.Record(key);
        }
        return (store, ledger);
    }

    private DoctorService Doctor(IConnectionSource c, MemoryCredentialStore k, MemoryLedger l) =>
        new(new FakeSystem(), null, null, c, k, null, _clock, null, l);

    [Fact]
    public async Task Connections_needing_reconnect_get_an_open_fix_and_never_show_secrets()
    {
        var (store, ledger) = StoreWith(("connection/gh1/token", true));
        var check = await Check(Doctor(new FakeConnections(Conn("7", "GitHub", "gh1", "needs_reconnect")), store, ledger), "connections");
        Assert.Equal("warn", check.Status);
        Assert.Equal("open", check.Fixes!.Single().Kind);
        Assert.Equal("open-connection:7", check.Fixes!.Single().Id);
        Assert.Contains("GitHub needs signing in again", check.Detail);
        Assert.DoesNotContain("SECRET", check.Detail);
        Assert.Equal("ok", (await Check(Doctor(new FakeConnections(Conn("7", "GitHub", "gh1")), store, ledger), "connections")).Status);
    }

    [Fact]
    public async Task Orphans_are_only_keys_this_data_folder_issued_and_nobody_owns()
    {
        var (store, ledger) = StoreWith(
            ("connection/gh1/token", true),            // issued, owned: keep
            ("connection/gone/token", true),           // issued here, no Connection: orphan
            ("connection/other-instance/token", false), // another data folder's Connection in the shared OS store: never touched
            ("claude.setup-token", true));              // not under connection/: never touched even if recorded
        var d = Doctor(new FakeConnections(Conn("1", "GitHub", "gh1")), store, ledger);

        var check = await Check(d, "connections");
        Assert.Equal("warn", check.Status);
        Assert.Equal(["connection/gone/token"], d.OrphanCredentialKeys());
        Assert.DoesNotContain("SECRET", check.Detail);
        var fix = check.Fixes!.Single(f => f.Id == DoctorService.RemoveOrphansFix);
        Assert.True(fix.Confirm);

        var ask = await d.RunFixAsync(fix.Id, false, Ct);
        Assert.True(ask.NeedsConfirm);
        Assert.DoesNotContain("SECRET", ask.Message);
        Assert.NotNull(store.Get("connection/gone/token"));

        var done = await d.RunFixAsync(fix.Id, true, Ct);
        Assert.True(done.Done);
        Assert.Null(store.Get("connection/gone/token"));
        Assert.NotNull(store.Get("connection/gh1/token"));
        Assert.NotNull(store.Get("connection/other-instance/token"));
        Assert.NotNull(store.Get("claude.setup-token"));
        Assert.DoesNotContain("connection/gone/token", ledger.Keys());
        Assert.Equal("ok", (await Check(d, "connections")).Status);
    }

    [Fact]
    public async Task Without_a_ledger_nothing_is_an_orphan()
    {
        var store = new MemoryCredentialStore();
        store.Set("connection/gone/token", "x");
        var d = new DoctorService(new FakeSystem(), null, null, new FakeConnections(), store, null, _clock);
        Assert.Empty(d.OrphanCredentialKeys());
        Assert.Equal("ok", (await Check(d, "connections")).Status);
        Assert.Equal("no unused credentials", (await d.RunFixAsync(DoctorService.RemoveOrphansFix, true, Ct)).Message);
        Assert.NotNull(store.Get("connection/gone/token"));
    }

    [Fact]
    public void A_store_that_cannot_list_is_asked_about_each_issued_key()
    {
        var store = new MemoryCredentialStore { CannotList = true };
        var ledger = new MemoryLedger();
        store.Set("connection/gone/token", "x");
        ledger.Record("connection/gone/token");
        ledger.Record("connection/never-written/token"); // recorded but the store write never happened
        store.Set("connection/foreign/token", "y");       // not recorded
        var d = Doctor(new FakeConnections(), store, ledger);
        Assert.Equal(["connection/gone/token"], d.OrphanCredentialKeys());
    }

    [Fact]
    public async Task The_fix_recomputes_and_refuses_to_remove_a_key_that_gained_an_owner()
    {
        var (store, ledger) = StoreWith(("connection/k1/token", true));
        var conns = new FakeConnections();
        var d = Doctor(conns, store, ledger);
        Assert.Single(d.OrphanCredentialKeys());
        conns.Set(Conn("1", "Late", "k1"));
        var done = await d.RunFixAsync(DoctorService.RemoveOrphansFix, true, Ct);
        Assert.Equal("no unused credentials", done.Message);
        Assert.NotNull(store.Get("connection/k1/token"));
    }
    [Fact]
    public async Task A_confirmed_run_with_a_list_that_differs_from_now_removes_nothing_and_shows_the_new_list()
    {
        var (store, ledger) = StoreWith(("connection/a/token", true), ("connection/b/token", true));
        var d = Doctor(new FakeConnections(), store, ledger);
        var ask = await d.RunFixAsync(DoctorService.RemoveOrphansFix, false, Ct);
        Assert.Equal(["connection/a/token", "connection/b/token"], ask.Targets);

        store.Set("connection/c/token", "SECRET");
        ledger.Record("connection/c/token");
        var moved = await d.RunFixAsync(DoctorService.RemoveOrphansFix, true, Ct, ask.Targets);
        Assert.False(moved.Done);
        Assert.True(moved.NeedsConfirm);
        Assert.True(moved.Changed);
        Assert.Equal(["connection/a/token", "connection/b/token", "connection/c/token"], moved.Targets);
        Assert.Contains("connection/c/token", moved.Message);
        Assert.NotNull(store.Get("connection/a/token"));
        Assert.NotNull(store.Get("connection/c/token"));

        var done = await d.RunFixAsync(DoctorService.RemoveOrphansFix, true, Ct, moved.Targets);
        Assert.True(done.Done);
        Assert.Empty(d.OrphanCredentialKeys());
    }

    [Fact]
    public async Task A_check_that_throws_fails_that_line_only()
    {
        var teams = new ThrowingTeams();
        var checks = await Doctor(t: teams).RunAsync(Ct);
        Assert.Equal("fail", checks.Single(c => c.Id == "teams").Status);
        Assert.Equal("ok", checks.Single(c => c.Id == "logs").Status);
    }

    private sealed class ThrowingTeams : IDoctorTeams
    {
        public Task<IReadOnlyList<DoctorTeam>> ListAsync(CancellationToken ct) => throw new InvalidOperationException("db locked");
        public Task ArchiveAsync(string team, CancellationToken ct) => Task.CompletedTask;
    }

    // ------------------------------------------------------------------ log retention

    private string Log(string rel, int ageDays)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, (Start - TimeSpan.FromDays(ageDays)).UtcDateTime);
        return path;
    }

    [Fact]
    public void Retention_deletes_logs_over_14_days_and_keeps_the_rest()
    {
        var old = Log("logs/core.log", 15);
        var oldRun = Log("transcripts/T/M/run-1.log", 30);
        var edge = Log("logs/edge.log", 13);
        var other = Log("logs/notes.txt", 100);
        var db = Path.Combine(_dir, "yawble.db");
        File.WriteAllText(db, "db");
        File.SetLastWriteTimeUtc(db, (Start - TimeSpan.FromDays(400)).UtcDateTime);

        var deleted = new LogRetention([Path.Combine(_dir, "logs"), Path.Combine(_dir, "transcripts"), Path.Combine(_dir, "missing")], _clock).Sweep();

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldRun));
        Assert.True(File.Exists(edge));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(db));
    }

    [Fact]
    public void Retention_leaves_a_log_that_is_open_for_writing()
    {
        if (!OperatingSystem.IsWindows()) return; // elsewhere an open file can be unlinked
        var path = Log("logs/live.log", 20);
        using var open = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        var deleted = new LogRetention([Path.Combine(_dir, "logs")], _clock).Sweep();
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(path));
    }
}
