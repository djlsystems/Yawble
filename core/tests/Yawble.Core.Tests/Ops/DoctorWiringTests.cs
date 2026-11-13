using Yawble.Core.Connections;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Ops;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Ops;

/// <summary>Card 44: the doctor over the real ConnectionService, the issued-keys ledger, the file store index and the gateway self-test. Fakes only.</summary>
public class DoctorWiringTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-doctor-wiring-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static IReadOnlyList<ConnectionField> Token => [new("token", "API key", true, null)];

    [Fact]
    public void Index_lists_id_name_kind_status_and_store_key_and_reads_no_secret()
    {
        using var db = new TempDb();
        var store = new ReadCountingStore();
        var svc = new ConnectionService(store, db.ConnectionString);
        var view = svc.Save(null, "GitHub", Token, new Dictionary<string, string> { ["token"] = "ghp_secret_value_123456" });
        svc.MarkStatus(view.Id, "needs_reconnect", "expired");
        var before = store.Gets;

        var index = svc.Index();

        var one = Assert.Single(index);
        Assert.Equal(view.Id, one.Id);
        Assert.Equal("GitHub", one.Name);
        Assert.Equal("secret", one.Kind);
        Assert.Equal("needs_reconnect", one.Status);
        Assert.Equal(32, one.StoreKey.Length);
        Assert.Equal(before, store.Gets); // the credential store was not read
        Assert.DoesNotContain("ghp_secret", System.Text.Json.JsonSerializer.Serialize(index));
    }

    [Fact]
    public async Task The_doctor_sees_a_reconnect_through_the_adapter_and_offers_to_open_Connections()
    {
        using var db = new TempDb();
        var svc = new ConnectionService(new MemoryCredentialStore(), db.ConnectionString);
        var view = svc.Save(null, "GitHub", Token, new Dictionary<string, string> { ["token"] = "ghp_secret_value_123456" });
        svc.MarkStatus(view.Id, "needs_reconnect", null);
        var d = new DoctorService(new StubSystem(), null, null, new ConnectionSourceAdapter(svc), null, null);
        var check = (await d.RunAsync(Ct)).Single(c => c.Id == "connections");
        Assert.Equal("warn", check.Status);
        Assert.Equal($"open-connection:{view.Id}", check.Fixes!.Single().Id);
    }

    [Fact]
    public async Task Real_service_with_ledger_save_remove_and_a_foreign_key_in_the_shared_store()
    {
        using var db = new TempDb();
        var store = new MemoryCredentialStore();
        var ledger = new FileIssuedKeyLedger(_dir);
        var svc = new ConnectionService(store, db.ConnectionString, ledger: ledger);
        var view = svc.Save(null, "GitHub", Token, new Dictionary<string, string> { ["token"] = "value-aaaaaaaaaa" });
        var key = Assert.Single(ledger.Keys());
        Assert.StartsWith("connection/", key);
        Assert.NotNull(store.Get(key));
        store.Set("connection/another-data-folder/token", "theirs"); // same OS store, another instance; never in this ledger

        var d = new DoctorService(new StubSystem(), null, null, new ConnectionSourceAdapter(svc), store, null, ledger: ledger);
        Assert.Empty(d.OrphanCredentialKeys());

        // The row goes away behind the service's back (as a restore of an older database would do): its key is now this folder's orphan.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"DELETE FROM connections WHERE id = {view.Id};";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal([key], d.OrphanCredentialKeys());
        var done = await d.RunFixAsync(DoctorService.RemoveOrphansFix, true, Ct);
        Assert.True(done.Done);
        Assert.Null(store.Get(key));
        Assert.Equal("theirs", store.Get("connection/another-data-folder/token"));
        Assert.Empty(ledger.Keys());
    }

    [Fact]
    public void A_normal_remove_and_a_dropped_secret_field_leave_the_ledger_empty()
    {
        using var db = new TempDb();
        var ledger = new FileIssuedKeyLedger(_dir);
        var svc = new ConnectionService(new MemoryCredentialStore(), db.ConnectionString, ledger: ledger);
        var two = new ConnectionField[] { new("a", "A", true, null), new("b", "B", true, null) };
        var view = svc.Save(null, "Two", two, new Dictionary<string, string> { ["a"] = "aaaaaaaa1", ["b"] = "bbbbbbbb2" });
        Assert.Equal(2, ledger.Keys().Count);
        svc.Save(view.Id, "Two", [two[0]], new Dictionary<string, string>());
        Assert.Single(ledger.Keys());
        Assert.True(svc.Remove(view.Id));
        Assert.Empty(ledger.Keys());
    }

    [Fact]
    public void The_ledger_survives_a_restart_and_a_corrupt_file_is_set_aside_not_fatal()
    {
        var a = new FileIssuedKeyLedger(_dir);
        a.Record("connection/x/token");
        a.Record("connection/x/token");
        Assert.Equal(["connection/x/token"], new FileIssuedKeyLedger(_dir).Keys());
        File.WriteAllText(Path.Combine(_dir, FileIssuedKeyLedger.FileName), "{not json");
        var b = new FileIssuedKeyLedger(_dir);
        Assert.Empty(b.Keys());
        b.Record("connection/y/token");
        Assert.Equal(["connection/y/token"], b.Keys());
        Assert.True(File.Exists(Path.Combine(_dir, FileIssuedKeyLedger.FileName + ".unreadable")));
    }

    [Fact]
    public void The_file_credential_store_lists_keys_by_prefix_and_never_values()
    {
        var store = new FileCredentialStore(Path.Combine(_dir, "creds"));
        store.Set("connection/a/token", "v1");
        store.Set("connection/b/token", "v2");
        store.Set("claude.setup-token", "v3");
        Assert.Equal(["connection/a/token", "connection/b/token"], store.KeysWithPrefix("connection/"));
        Assert.True(store.Has("claude.setup-token"));
        Assert.False(store.Has("nope"));
    }

    /// <summary>
    /// The real gateway (its whole request pipeline) on the in-memory listener: the self-test's request reaches it and
    /// gets its own refusal back, and nothing is left behind. No port is bound.
    /// </summary>
    [Fact]
    public async Task The_gateway_self_test_gets_a_real_answer_from_the_gateway_and_leaves_nothing_behind()
    {
        await using var f = await GatewayFixture.StartAsync();
        Assert.Null(await GatewaySelfTest.CheckAsync(f.Host, TimeSpan.FromSeconds(10), Ct));
        Assert.Empty(f.Log.Read());                                                    // quiet: no gateway log row
        Assert.Equal(TokenCheck.Wrong, f.Tokens.Check(GatewaySelfTest.SelfTestRunId, "x")); // token forgotten
    }

    /// <summary>
    /// Opt-in real-host test: it binds real loopback ports (a Kestrel upstream and the gateway's listener), so it runs only with
    /// YAWBLE_REAL_LOOPBACK=1. The doctor's side of the check is covered with a fake probe below.
    /// </summary>
    [Fact]
    public async Task The_gateway_self_test_gets_a_real_answer_from_a_real_gateway_and_leaves_nothing_behind()
    {
        GatewayFixture.SkipUnlessRealLoopback("the self-test over a real loopback socket");
        await using var f = await GatewayFixture.StartAsync(realLoopback: true);
        Assert.Null(await GatewaySelfTest.CheckAsync(f.Host, TimeSpan.FromSeconds(10), Ct));
        Assert.Empty(f.Log.Read());                                                    // quiet: no gateway log row
        Assert.Equal(TokenCheck.Wrong, f.Tokens.Check(GatewaySelfTest.SelfTestRunId, "x")); // token forgotten
    }

    [Fact]
    public async Task A_gateway_that_does_not_answer_is_reported_by_the_host_system()
    {
        var host = new DoctorHostSystem(_dir, () => [], null, null, () => [], [], _ => Task.FromResult<string?>("no answer within 5 s"));
        Assert.Equal("no answer within 5 s", host.Sandbox().GatewayProblem);
        var none = new DoctorHostSystem(_dir, () => [], null, null, () => [], []);
        Assert.NotNull(none.Sandbox().GatewayProblem);
        var broken = new DoctorHostSystem(_dir, () => [], "no launcher", null, () => [], []);
        Assert.Null(broken.Sandbox().GatewayProblem); // the launcher problem is what the check reports then
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_gateway_that_answers_the_probe_is_reported_as_answering()
    {
        var asked = 0;
        var host = new DoctorHostSystem(_dir, () => [], null, null, () => [], [], _ => { asked++; return Task.FromResult<string?>(null); });
        var d = new DoctorService(host, null, null, null, null, null);
        var sandbox = (await d.RunAsync(Ct)).Single(c => c.Id == "sandbox");
        Assert.Contains("gateway answered a test request", sandbox.Detail);
        Assert.DoesNotContain("did not answer", sandbox.Detail);
        Assert.Equal(1, asked);
    }

    private sealed class ReadCountingStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _v = [];
        public int Gets;
        public string Kind => "memory";
        public string? Get(string key) { Gets++; return _v.GetValueOrDefault(key); }
        public void Set(string key, string value) => _v[key] = value;
        public bool Delete(string key) => _v.Remove(key);
    }

    private sealed class StubSystem : IDoctorSystem
    {
        public string QuickCheck() => "ok";
        public IReadOnlyList<string> AppliedMigrations() => [];
        public IReadOnlyList<string> ShippedMigrations() => [];
        public long FreeBytesOnDataDrive() => 100L << 30;
        public IReadOnlyDictionary<string, long> TeamFolderSizes() => new Dictionary<string, long>();
        public long LogBytes() => 0;
        public SandboxState Sandbox() => new(null, "done", null, null);
        public LinkState Link() => new("off", null);
    }
}
