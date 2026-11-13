using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Connections;

/// <summary>Connections: rows in SQLite, secrets only in the (in-memory fake) credential store, masked views, the scrubber.</summary>
public class ConnectionServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<ConnectionField> TokenField => [new("token", "API key", true, null)];
    private static Dictionary<string, string> Token(string v) => new() { ["token"] = v };

    private static long Count(string cs, string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string StoreKey(string cs, long id)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT store_key FROM connections WHERE id = {id};";
        return (string)cmd.ExecuteScalar()!;
    }

    private static void Run(string cs, string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Mask_ShowsFirstAndLastFourOfLongValues()
    {
        Assert.Equal("gith••••••a9Qz", ConnectionService.Mask("github_pat_11ABCDEFG0123456a9Qz"));
        Assert.Equal("••••••12", ConnectionService.Mask("abcdefghij12"));
        Assert.Equal("••••••", ConnectionService.Mask("short"));
    }

    [Theory]
    [InlineData("abcdefg", "••••••")]
    [InlineData("abcdefgh", "••••••gh")]
    [InlineData("abcdefghijklmno", "••••••no")]
    [InlineData("abcdefghijklmnop", "abcd••••••mnop")]
    public void Mask_BoundaryLengths(string value, string expected) => Assert.Equal(expected, ConnectionService.Mask(value));

    [Fact]
    public void TwoServicesOverTwoDatabases_DoNotShareScrubberEntries()
    {
        using var db1 = new TempDb();
        using var db2 = new TempDb();
        var s1 = new ConnectionService(new MemoryCredentialStore(), db1.ConnectionString);
        var s2 = new ConnectionService(new MemoryCredentialStore(), db2.ConnectionString);
        var a = s1.Save(null, "A", TokenField, Token("owner-one-secret-aaa"));
        var b = s2.Save(null, "B", TokenField, Token("owner-two-secret-bbb"));
        try
        {
            Assert.Equal(a.Id, b.Id); // both are connection 1 in their own database
            s1.Remove(a.Id);
            Assert.Equal("owner-one-secret-aaa", SecretScrubber.Scrub("owner-one-secret-aaa"));
            Assert.Equal("[redacted]", SecretScrubber.Scrub("owner-two-secret-bbb"));
        }
        finally { s2.Remove(b.Id); }
    }

    [Fact]
    public void Scrubber_OnePassLongestFirst_TrimsAndCoversEscapedForms()
    {
        using var db = new TempDb();
        var svc = new ConnectionService(new MemoryCredentialStore(), db.ConnectionString);
        var v = svc.Save(null, "Forms", TokenField, Token("  pa$$word/with\"q+1  "));
        var w = svc.Save(null, "Overlap", [new("t", "t", true, null)], new Dictionary<string, string> { ["t"] = "pa$$word/with\"q+1-extended" });
        try
        {
            Assert.Equal("a [redacted] b", SecretScrubber.Scrub("a pa$$word/with\"q+1 b"));
            Assert.Equal("a [redacted] b", SecretScrubber.Scrub("a pa$$word/with\\\"q+1 b")); // JSON-escaped
            Assert.Equal("a [redacted] b", SecretScrubber.Scrub("a " + Uri.EscapeDataString("pa$$word/with\"q+1") + " b"));
            Assert.Equal("a [redacted] b", SecretScrubber.Scrub("a pa$$word/with\"q+1-extended b")); // longest first, no fragment
            Assert.Equal("[redacted][redacted]", SecretScrubber.Scrub("pa$$word/with\"q+1pa$$word/with\"q+1"));
        }
        finally { svc.Remove(v.Id); svc.Remove(w.Id); }
    }

    [Fact]
    public void Save_KeepsSecretsOutOfTheDatabase()
    {
        using var db = new TempDb();
        var store = new MemoryCredentialStore();
        var svc = new ConnectionService(store, db.ConnectionString);
        var view = svc.Save(null, "Stripe", TokenField, Token("sk_live_0123456789abcdef"));
        try
        {
            using var c = new SqliteConnection(db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT * FROM connections;";
            using var r = cmd.ExecuteReader();
            var rows = 0;
            while (r.Read())
            {
                rows++;
                for (var i = 0; i < r.FieldCount; i++)
                    Assert.DoesNotContain("0123456789", Convert.ToString(r.GetValue(i)) ?? "");
            }
            Assert.Equal(1, rows);
            Assert.Equal("sk_l••••••cdef", svc.List()[0].Fields[0].Shown);
            Assert.True(svc.List()[0].Fields[0].Set);
            var key = StoreKey(db.ConnectionString, view.Id);
            Assert.Matches("^[0-9a-f]{32}$", key);
            Assert.Equal("sk_live_0123456789abcdef", store.Get($"connection/{key}/token"));
            Assert.Equal("untested", view.Status);
        }
        finally { svc.Remove(view.Id); }
    }

    [Fact]
    public void Save_EmptySecretKeepsTheStoredValue()
    {
        using var db = new TempDb();
        var store = new MemoryCredentialStore();
        var svc = new ConnectionService(store, db.ConnectionString);
        var v = svc.Save(null, "Svc", TokenField, Token("keep-me-0123456789"));
        try
        {
            svc.MarkStatus(v.Id, "works", null);
            var again = svc.Save(v.Id, "Svc renamed", TokenField, new Dictionary<string, string>());
            Assert.Equal("keep-me-0123456789", svc.Secrets(v.Id)["token"]);
            Assert.Equal("works", again.Status); // no secret saved: the status stays
            svc.Save(v.Id, "Svc renamed", TokenField, Token(""));
            Assert.Equal("keep-me-0123456789", svc.Secrets(v.Id)["token"]);
            var changed = svc.Save(v.Id, "Svc renamed", TokenField, Token("brand-new-secret-99"));
            Assert.Equal("untested", changed.Status); // saving a secret resets it
            Assert.Equal("brand-new-secret-99", svc.Secrets(v.Id)["token"]);
        }
        finally { svc.Remove(v.Id); }
    }

    [Fact]
    public void Save_DuplicateNameThrows()
    {
        using var db = new TempDb();
        var svc = new ConnectionService(new MemoryCredentialStore(), db.ConnectionString);
        var a = svc.Save(null, "Stripe", TokenField, Token("dup-name-secret-1"));
        try
        {
            var ex = Assert.Throws<ArgumentException>(() => svc.Save(null, "stripe", TokenField, Token("dup-name-secret-2")));
            Assert.Equal("a connection named 'stripe' already exists", ex.Message);
        }
        finally { svc.Remove(a.Id); }
    }

    [Fact]
    public void Save_NewConnectionWithoutSecretThrows()
    {
        using var db = new TempDb();
        var store = new MemoryCredentialStore();
        var svc = new ConnectionService(store, db.ConnectionString);
        Assert.Throws<ArgumentException>(() => svc.Save(null, "Empty", TokenField, new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => svc.Save(null, "Empty", TokenField, Token("")));
        Assert.Empty(svc.List());
        Assert.Empty(store.Keys);
    }

    [Fact]
    public void Remove_DeletesStoreEntries()
    {
        using var db = new TempDb();
        var store = new MemoryCredentialStore();
        var svc = new ConnectionService(store, db.ConnectionString);
        var v = svc.Save(null, "Gone", TokenField, Token("remove-me-0123456789"));
        var key = StoreKey(db.ConnectionString, v.Id);
        Assert.Contains($"connection/{key}/token", store.Keys);
        Assert.True(svc.Remove(v.Id));
        Assert.Null(store.Get($"connection/{key}/token"));
        Assert.Empty(store.Keys);
        Assert.Null(svc.Get(v.Id));
        Assert.False(svc.Remove(v.Id));
    }

    /// <summary>C1: two data dirs on one computer share the OS credential store; each has a connection 1, and neither overwrites the other.</summary>
    [Fact]
    public void TwoDatabasesSharingOneStore_KeepTheirOwnSecrets()
    {
        using var db1 = new TempDb();
        using var db2 = new TempDb();
        var shared = new MemoryCredentialStore();
        var s1 = new ConnectionService(shared, db1.ConnectionString);
        var s2 = new ConnectionService(shared, db2.ConnectionString);
        var a = s1.Save(null, "Stripe", TokenField, Token("stripe-secret-one-111"));
        var b = s2.Save(null, "GitHub", TokenField, Token("github-secret-two-222"));
        try
        {
            Assert.Equal(1, a.Id);
            Assert.Equal(1, b.Id);
            Assert.Equal("stripe-secret-one-111", s1.Secrets(a.Id)["token"]);
            Assert.Equal("github-secret-two-222", s2.Secrets(b.Id)["token"]);
            Assert.Equal(2, shared.Keys.Count);
            // a restart of either reads its own back; removing one leaves the other's
            Assert.Equal("stripe-secret-one-111", new ConnectionService(shared, db1.ConnectionString).Secrets(1)["token"]);
            s2.Remove(b.Id);
            Assert.Equal("stripe-secret-one-111", s1.Secrets(a.Id)["token"]);
            Assert.Single(shared.Keys);
        }
        finally { s1.Remove(a.Id); s2.Remove(b.Id); }
    }

    private sealed class FailingStore(string failOn) : Yawble.Core.Credentials.ICredentialStore
    {
        public readonly MemoryCredentialStore Inner = new();
        public string FailOn = failOn;
        public string Kind => "failing";
        public string? Get(string key) => key.Contains(FailOn, StringComparison.Ordinal) ? throw new Yawble.Core.Credentials.CredentialStoreException("the store refused") : Inner.Get(key);
        public void Set(string key, string value) => Inner.Set(key, value);
        public bool Delete(string key) => Inner.Delete(key);
    }

    /// <summary>M1: a connection whose secret cannot be read, or whose fields are corrupt, is skipped and logged without its value; the rest load.</summary>
    [Fact]
    public void Construction_SkipsAConnectionThatFailsToLoad()
    {
        using var db = new TempDb();
        var store = new FailingStore("never-at-first");
        var first = new ConnectionService(store, db.ConnectionString);
        var good = first.Save(null, "Good", TokenField, Token("good-secret-value-1"));
        var bad = first.Save(null, "Bad", TokenField, Token("bad-secret-value-22"));
        var corrupt = first.Save(null, "Corrupt", TokenField, Token("corrupt-secret-333"));
        Run(db.ConnectionString, $"UPDATE connections SET fields = '{{not json' WHERE id = {corrupt.Id};");
        foreach (var v in new[] { good, bad, corrupt }) SecretScrubber.Forget(first, v.Id);
        store.FailOn = StoreKey(db.ConnectionString, bad.Id);
        var log = new List<string>();
        var svc = new ConnectionService(store, db.ConnectionString, log: log.Add);
        try
        {
            Assert.Equal(2, log.Count);
            Assert.All(log, l => Assert.DoesNotContain("secret-", l));
            Assert.Contains(log, l => l.Contains($"connection {bad.Id} ", StringComparison.Ordinal) && l.Contains("CredentialStoreException"));
            Assert.Contains(log, l => l.Contains($"connection {corrupt.Id} ", StringComparison.Ordinal));
            Assert.Equal("[redacted]", SecretScrubber.Scrub("good-secret-value-1")); // the good one still loaded
            Assert.Equal("bad-secret-value-22", SecretScrubber.Scrub("bad-secret-value-22"));
            Assert.Empty(svc.Secrets(bad.Id)); // never sent
            Assert.Equal(3, svc.List().Count); // the list still opens
            Assert.Equal("needs_reconnect", svc.Get(bad.Id)!.Status);
            Assert.Contains(svc.NeedingReconnect(), b => b.Id == corrupt.Id);
            // saving it again, with the key, makes it usable
            svc.Save(corrupt.Id, "Corrupt", TokenField, Token("corrupt-secret-new4"));
            Assert.Equal("corrupt-secret-new4", svc.Secrets(corrupt.Id)["token"]);
            Assert.DoesNotContain(svc.NeedingReconnect(), b => b.Id == corrupt.Id);
        }
        finally { store.FailOn = "never"; svc.Remove(good.Id); svc.Remove(corrupt.Id); svc.Remove(bad.Id); }
    }

    /// <summary>M2: the name and status of a connection (for apiList and Needs You) are read without touching the credential store.</summary>
    [Fact]
    public void Brief_AndNeedingReconnect_ReadNoSecret()
    {
        using var db = new TempDb();
        var store = new FailingStore("never-at-first");
        var svc = new ConnectionService(store, db.ConnectionString);
        var v = svc.Save(null, "Quiet", TokenField, Token("quiet-secret-value-5"));
        try
        {
            svc.MarkStatus(v.Id, "needs_reconnect", "the service answered 401");
            store.FailOn = "connection/"; // from now on any read of a secret throws
            Assert.Equal(new ConnectionBrief(v.Id, "Quiet", "needs_reconnect", "the service answered 401"), svc.Brief(v.Id));
            Assert.Equal("Quiet", Assert.Single(svc.NeedingReconnect()).Name);
            Assert.Null(svc.Brief(v.Id + 99));
        }
        finally { store.FailOn = "never"; svc.Remove(v.Id); }
    }

    [Fact]
    public void Scrubber_RemovesKnownValuesFromCliOutput()
    {
        using var db = new TempDb();
        var svc = new ConnectionService(new MemoryCredentialStore(), db.ConnectionString);
        var v = svc.Save(null, "Scrub", TokenField, Token("sk_live_0123456789abcdef"));
        try
        {
            Assert.Equal("x [redacted] y", CliRunner.Redact("x sk_live_0123456789abcdef y"));
        }
        finally { svc.Remove(v.Id); }
        Assert.Equal("x sk_live_0123456789abcdef y", CliRunner.Redact("x sk_live_0123456789abcdef y"));
    }

    [Fact]
    public void Scrubber_LoadsExistingConnectionsOnConstruction_AndIgnoresShortValues()
    {
        using var db = new TempDb();
        var store = new MemoryCredentialStore();
        var first = new ConnectionService(store, db.ConnectionString);
        var v = first.Save(null, "Boot", TokenField, Token("boot-secret-zq81x"));
        var short5 = first.Save(null, "Tiny", [new("t", "t", true, null)], new Dictionary<string, string> { ["t"] = "ab12c" });
        try
        {
            SecretScrubber.Forget(first, v.Id);
            Assert.Contains("boot-secret-zq81x", SecretScrubber.Scrub("boot-secret-zq81x"));
            _ = new ConnectionService(store, db.ConnectionString);
            Assert.Equal("[redacted]", SecretScrubber.Scrub("boot-secret-zq81x"));
            Assert.Equal("ab12c", SecretScrubber.Scrub("ab12c")); // under 6 characters: not scrubbed
        }
        finally { first.Remove(v.Id); first.Remove(short5.Id); }
    }

    [Fact]
    public void FindIdentical_MatchesOnAllSecretValues()
    {
        using var db = new TempDb();
        var svc = new ConnectionService(new MemoryCredentialStore(), db.ConnectionString);
        IReadOnlyList<ConnectionField> two = [new("id", "Key id", true, null), new("secret", "Secret", true, null), new("region", "Region", false, "eu")];
        var v = svc.Save(null, "Two", two, new Dictionary<string, string> { ["id"] = "ident-aaa-111111", ["secret"] = "ident-bbb-222222" });
        try
        {
            Assert.Equal(v.Id, svc.FindIdentical(new Dictionary<string, string> { ["id"] = "ident-aaa-111111", ["secret"] = "ident-bbb-222222" }));
            Assert.Null(svc.FindIdentical(new Dictionary<string, string> { ["id"] = "ident-aaa-111111", ["secret"] = "different-value-9" }));
            Assert.Null(svc.FindIdentical(new Dictionary<string, string> { ["id"] = "ident-aaa-111111" }));
            Assert.Null(svc.FindIdentical(new Dictionary<string, string>()));
            // the values decide, whatever the keys (a pasted "X-API-Key" matches a connection's "token")
            Assert.Equal(v.Id, svc.FindIdentical(new Dictionary<string, string> { ["p"] = "ident-bbb-222222", ["q"] = "ident-aaa-111111" }));
            Assert.Null(svc.FindIdentical(new Dictionary<string, string> { ["p"] = "ident-aaa-111111", ["q"] = "ident-aaa-111111" }));
            Assert.Equal("eu", v.Fields.Single(f => f.Key == "region").Shown); // non-secret shown in full
            // with the non-secret values given, they must match too (bob's password never reuses alice's sign-in)
            var both = new Dictionary<string, string> { ["id"] = "ident-aaa-111111", ["secret"] = "ident-bbb-222222" };
            Assert.Equal(v.Id, svc.FindIdentical(both, ["eu"]));
            Assert.Null(svc.FindIdentical(both, ["us"]));
            Assert.Null(svc.FindIdentical(both, []));
        }
        finally { svc.Remove(v.Id); }
    }

    [Fact]
    public void UsedBy_ListsApisUsingTheConnection_AndRemoveClearsTheLink()
    {
        using var db = new TempDb();
        var svc = new ConnectionService(new MemoryCredentialStore(), db.ConnectionString);
        var v = svc.Save(null, "Shared", TokenField, Token("shared-secret-7777"));
        Run(db.ConnectionString, $"INSERT INTO apis (name, base_url, connection_id, created_at, updated_at) VALUES ('Zeta', 'https://z.example', {v.Id}, 'x', 'x'), ('alpha', 'https://a.example', {v.Id}, 'x', 'x');");
        Assert.Equal(["alpha", "Zeta"], svc.Get(v.Id)!.UsedBy);
        svc.Remove(v.Id);
        Assert.Equal(0, Count(db.ConnectionString, "SELECT count(*) FROM apis WHERE connection_id IS NOT NULL"));
        Assert.Equal(2, Count(db.ConnectionString, "SELECT count(*) FROM apis"));
    }

    [Fact]
    public async Task ForgetTeam_RemovesApiTeamRows()
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await using var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var beta = await engine.CreateTeamAsync("Beta", ct: Ct);
        Run(db.ConnectionString, $"""
            INSERT INTO apis (id, name, base_url, created_at, updated_at) VALUES (1, 'petstore', 'https://p.example', 'x', 'x');
            INSERT INTO api_teams (api_id, team_id) VALUES (1, {alpha}), (1, {beta});
            INSERT INTO api_rules (id, api_id, team_id, pattern, decision, created_at) VALUES (1, 1, {alpha}, 'get*', 'allow', 'x'), (2, 1, NULL, 'del*', 'deny', 'x');
            INSERT INTO api_choices (api_id, team_id, path, decision, rule_id, chosen_at) VALUES (1, {alpha}, 'getPet', 'allow', 1, 'x'), (1, NULL, 'getPet', 'ask', NULL, 'x'), (1, {beta}, 'getPet', 'deny', NULL, 'x');
            """);

        integrations.ForgetTeam(alpha, "Alpha"); // the team row still exists: only the explicit deletes can remove these

        foreach (var table in new[] { "api_teams", "api_rules", "api_choices" })
            Assert.Equal(0, Count(db.ConnectionString, $"SELECT count(*) FROM {table} WHERE team_id = {alpha}"));
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM api_rules WHERE team_id IS NULL"));
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM api_choices WHERE team_id IS NULL"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM api_teams WHERE team_id = {beta}"));
        Assert.Equal(1, Count(db.ConnectionString, $"SELECT count(*) FROM api_choices WHERE team_id = {beta}"));
        Assert.Equal(1, Count(db.ConnectionString, "SELECT count(*) FROM apis"));
    }
}
