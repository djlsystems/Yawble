using System.Text;
using System.Text.Json.Nodes;
using MailKit.Security;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Link;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Email;

/// <summary>The email Connection kind (B133): save, Gmail preset, Test over IMAP and SMTP against <see cref="FakeMailServer"/>, secrets kept out of the DB.</summary>
public class EmailConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;
        public ConnectionService Conns = null!;
        public FakeMailServer Mail = new();
        public List<string> Log = [];

        public static Rig Make()
        {
            var r = new Rig();
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
            r.Conns = new ConnectionService(new MemoryCredentialStore(), r.Db.ConnectionString, log: line => { lock (r.Log) r.Log.Add(line); });
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, connections: r.Conns, mail: r.Mail.Factory());
            r.Engine.Integrations = r.Svc;
            return r;
        }

        public async Task<JsonNode?> Rpc(string method, JsonObject? p = null) => await Svc.DispatchAsync(method, p, Ct);

        /// <summary>An email Connection pointing at the fake pair.</summary>
        public async Task<JsonObject> Account(string? password = FakeMailServer.Password, int? imapPort = null, int? smtpPort = null, string? user = null)
        {
            var p = new JsonObject
            {
                ["kind"] = "email", ["address"] = FakeMailServer.User,
                ["imapHost"] = "127.0.0.1", ["imapPort"] = imapPort ?? Mail.Imap.Port,
                ["smtpHost"] = "127.0.0.1", ["smtpPort"] = smtpPort ?? Mail.Smtp.Port,
            };
            if (password is not null) p["password"] = password;
            if (user is not null) p["user"] = user;
            return (JsonObject)(await Rpc("integrations.connectionSave", p))!;
        }

        public async ValueTask DisposeAsync() { await Svc.DisposeAsync(); await Engine.DisposeAsync(); await Mail.DisposeAsync(); Db.Dispose(); }
    }

    private static string? Field(JsonNode c, string key) =>
        ((JsonArray)c["fields"]!).OfType<JsonObject>().FirstOrDefault(f => f["key"]!.GetValue<string>() == key)?["shown"]?.GetValue<string>();

    // ------------------------------------------------------------------ the seam against the fake server

    [Fact]
    public async Task ImapSessionSignsInAndListsFoldersInboxFirst()
    {
        await using var mail = new FakeMailServer();
        await using var imap = await mail.Factory().OpenImapAsync(new EmailAccount(mail.Settings(), FakeMailServer.Password), Ct);
        Assert.True(imap.Client.IsAuthenticated);
        Assert.False(imap.IsGmail);
        var folders = await imap.ListFoldersAsync(Ct);
        Assert.Equal("INBOX", folders[0]);
        Assert.Contains("Sent", folders);
        Assert.Contains("Projects/2026", folders);
    }

    [Fact]
    public async Task ImapSessionSaysGmailWhenTheServerOffersItsExtensions()
    {
        await using var mail = new FakeMailServer();
        mail.Imap.Capabilities.Add("X-GM-EXT-1");
        await using var imap = await mail.Factory().OpenImapAsync(new EmailAccount(mail.Settings(), FakeMailServer.Password), Ct);
        Assert.True(imap.IsGmail);
    }

    [Fact]
    public async Task SmtpSessionAuthenticatesAndSendsNothing()
    {
        await using var mail = new FakeMailServer();
        await using (var smtp = await mail.Factory().OpenSmtpAsync(new EmailAccount(mail.Settings(), FakeMailServer.Password), Ct))
            Assert.True(smtp.Client.IsAuthenticated);
        Assert.Empty(mail.Smtp.Messages);
        Assert.DoesNotContain(mail.Smtp.Lines(), l => l.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase) || l.StartsWith("DATA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BadPasswordIsASignInFailureOnBoth()
    {
        await using var mail = new FakeMailServer();
        var account = new EmailAccount(mail.Settings(), "wrong-password");
        var imap = await Assert.ThrowsAsync<MailException>(() => mail.Factory().OpenImapAsync(account, Ct));
        Assert.Equal(MailFailure.SignIn, imap.Failure);
        Assert.Contains("refused the user name or password", imap.Message);
        var smtp = await Assert.ThrowsAsync<MailException>(() => mail.Factory().OpenSmtpAsync(account, Ct));
        Assert.Equal(MailFailure.SignIn, smtp.Failure);
        Assert.DoesNotContain("wrong-password", imap.Message + smtp.Message);
    }

    [Fact]
    public async Task RefusedPortIsAConnectFailure()
    {
        await using var mail = new FakeMailServer();
        var port = mail.RefusedPort();
        var account = new EmailAccount(new EmailSettings("pat@example.test", "127.0.0.1", port, "127.0.0.1", port, "pat@example.test"), "x");
        var e = await Assert.ThrowsAsync<MailException>(() => mail.Factory().OpenImapAsync(account, Ct));
        Assert.Equal(MailFailure.Connect, e.Failure);
        Assert.Contains($"127.0.0.1:{port} (connection refused)", e.Message);
    }

    /// <summary>The same sign-in over real sockets: MailKit dials a TcpListener on loopback. Opt-in (YAWBLE_REAL_LOOPBACK=1); agent sandboxes refuse loopback connects.</summary>
    [Fact]
    public async Task OverRealLoopbackImapAndSmtpSignIn()
    {
        Gateway.GatewayFixture.SkipUnlessRealLoopback("the fake mail server over real loopback ports");
        await using var mail = new FakeMailServer(realLoopback: true);
        Assert.Null(mail.Connector);
        var account = new EmailAccount(mail.Settings(), FakeMailServer.Password);
        await using (var imap = await mail.Factory().OpenImapAsync(account, Ct))
            Assert.Equal("INBOX", (await imap.ListFoldersAsync(Ct))[0]);
        await using (var smtp = await mail.Factory().OpenSmtpAsync(account, Ct))
            Assert.True(smtp.Client.IsAuthenticated);
        var refused = new EmailAccount(mail.Settings() with { ImapPort = mail.RefusedPort() }, FakeMailServer.Password);
        Assert.Equal(MailFailure.Connect, (await Assert.ThrowsAsync<MailException>(() => mail.Factory().OpenImapAsync(refused, Ct))).Failure);
    }

    [Fact]
    public async Task SmtpWithoutAuthIsRefusedNotSkipped()
    {
        await using var mail = new FakeMailServer();
        mail.Smtp.OffersAuth = false;
        var e = await Assert.ThrowsAsync<MailException>(() => mail.Factory().OpenSmtpAsync(new EmailAccount(mail.Settings(), FakeMailServer.Password), Ct));
        Assert.Contains("does not offer a sign-in", e.Message);
    }

    [Theory]
    [InlineData("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect)]
    [InlineData("smtp.gmail.com", 465, SecureSocketOptions.SslOnConnect)]
    [InlineData("mail.example.com", 143, SecureSocketOptions.StartTls)]
    [InlineData("mail.example.com", 587, SecureSocketOptions.StartTls)]
    [InlineData("127.0.0.1", 1143, SecureSocketOptions.StartTlsWhenAvailable)]
    [InlineData("localhost", 1025, SecureSocketOptions.StartTlsWhenAvailable)]
    [InlineData("::1", 1025, SecureSocketOptions.StartTlsWhenAvailable)]
    public void TlsByPortAndOnlyThisComputerMayGoPlain(string host, int port, SecureSocketOptions expected) =>
        Assert.Equal(expected, MailKitClientFactory.SecurityFor(host, port));

    [Fact]
    public void AccountToStringLeavesThePasswordOut()
    {
        var a = new EmailAccount(new EmailSettings("a@b.test", "imap.b.test", 993, "smtp.b.test", 465, "a@b.test"), "s3cret-value");
        Assert.DoesNotContain("s3cret-value", a.ToString());
    }

    // ------------------------------------------------------------------ fields, preset, validation

    [Fact]
    public async Task SavedAccountHasItsFieldsAndUserDefaultsToTheAddress()
    {
        await using var r = Rig.Make();
        var c = await r.Account();
        Assert.Equal("email", c["kind"]!.GetValue<string>());
        Assert.Equal(FakeMailServer.User, c["name"]!.GetValue<string>());
        Assert.Equal(FakeMailServer.User, Field(c, "address"));
        Assert.Equal(FakeMailServer.User, Field(c, "user"));
        Assert.Equal("127.0.0.1", Field(c, "imapHost"));
        Assert.Equal(r.Mail.Imap.Port.ToString(), Field(c, "imapPort"));
        var pw = ((JsonArray)c["fields"]!).OfType<JsonObject>().Single(f => f["key"]!.GetValue<string>() == "password");
        Assert.True(pw["secret"]!.GetValue<bool>());
        Assert.True(pw["set"]!.GetValue<bool>());
        Assert.DoesNotContain(FakeMailServer.Password, c.ToJsonString());
        Assert.Equal(FakeMailServer.Password, r.Conns.Secrets(c["id"]!.GetValue<long>())["password"]);
        Assert.Equal("untested", c["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnotherUserNameIsKept()
    {
        await using var r = Rig.Make();
        var c = await r.Account(user: "pat.login");
        Assert.Equal("pat.login", Field(c, "user"));
    }

    [Fact]
    public void GmailPresetValues()
    {
        var g = EmailConnection.Gmail;
        Assert.Equal(("imap.gmail.com", 993, "smtp.gmail.com", 465), (g.ImapHost, g.ImapPort, g.SmtpHost, g.SmtpPort));
        Assert.Equal("Use a Google app password; it needs 2-Step Verification", g.Note);
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailKitClientFactory.SecurityFor(g.ImapHost, g.ImapPort));
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailKitClientFactory.SecurityFor(g.SmtpHost, g.SmtpPort));
        Assert.Same(g, EmailConnection.Preset("Gmail"));
    }

    [Fact]
    public async Task GmailPresetFillsTheServersOnSave()
    {
        await using var r = Rig.Make();
        var c = (await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["kind"] = "email", ["preset"] = "gmail", ["address"] = "pat@gmail.com", ["password"] = "abcd efgh ijkl mnop",
        }))!;
        Assert.Equal("imap.gmail.com", Field(c, "imapHost"));
        Assert.Equal("993", Field(c, "imapPort"));
        Assert.Equal("smtp.gmail.com", Field(c, "smtpHost"));
        Assert.Equal("465", Field(c, "smtpPort"));
        Assert.Equal("pat@gmail.com", Field(c, "user"));
    }

    [Theory]
    [InlineData("not-an-address", "imap.x.test", 993, "address")]
    [InlineData("a@x.test", "https://imap.x.test", 993, "host name")]
    [InlineData("a@x.test", "imap.x.test", 0, "port")]
    [InlineData("a@x.test", "imap.x.test", 70000, "port")]
    public async Task BadFieldsAreRefused(string address, string imapHost, int imapPort, string says)
    {
        await using var r = Rig.Make();
        var e = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["kind"] = "email", ["address"] = address, ["imapHost"] = imapHost, ["imapPort"] = imapPort,
            ["smtpHost"] = "smtp.x.test", ["smtpPort"] = 465, ["password"] = "p",
        }));
        Assert.Contains(says, e.Message);
        Assert.Empty(r.Conns.List());
    }

    [Fact]
    public async Task NewAccountNeedsItsPassword()
    {
        await using var r = Rig.Make();
        await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Account(password: null));
    }

    [Fact]
    public async Task EditKeepsThePasswordAndAMovedServerIsUntestedAgain()
    {
        await using var r = Rig.Make();
        var c = await r.Account();
        var id = c["id"]!.GetValue<long>();
        Assert.Equal("works", (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id }))!["status"]!.GetValue<string>());

        // an edit with no kind is still an email edit; the address moves, the defaulted user follows it
        var edited = (await r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["address"] = "pat2@example.test" }))!;
        Assert.Equal("email", edited["kind"]!.GetValue<string>());
        Assert.Equal("pat2@example.test", Field(edited, "user"));
        Assert.Equal("127.0.0.1", Field(edited, "imapHost"));
        Assert.Equal("untested", edited["status"]!.GetValue<string>());
        Assert.Equal(FakeMailServer.Password, r.Conns.Secrets(id)["password"]);

        // a rename only keeps the status
        await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id });
        var renamed = (await r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["name"] = "Pat's mail" }))!;
        Assert.Equal("Pat's mail", renamed["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnApiKeyConnectionCannotBecomeAnEmailAccount()
    {
        await using var r = Rig.Make();
        var key = r.Conns.Save(null, "k", [new ConnectionField("token", "Token", true, null)], new Dictionary<string, string> { ["token"] = "t" });
        var e = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["kind"] = "email", ["id"] = key.Id, ["address"] = "a@x.test", ["imapHost"] = "i.x.test", ["imapPort"] = 993,
            ["smtpHost"] = "s.x.test", ["smtpPort"] = 465, ["password"] = "p",
        }));
        Assert.Contains("not an email account", e.Message);
    }

    [Fact]
    public async Task AnApiCannotSignInWithAnEmailAccount()
    {
        await using var r = Rig.Make();
        var c = await r.Account();
        var e = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Rpc("integrations.apiSave", new JsonObject
        {
            ["name"] = "svc", ["baseUrl"] = "https://api.example.test/", ["connectionId"] = c["id"]!.GetValue<long>(),
        }));
        Assert.Contains("email account", e.Message);
    }

    // ------------------------------------------------------------------ Test

    [Fact]
    public async Task TestSignsInToBothAndWorks()
    {
        await using var r = Rig.Make();
        var id = (await r.Account())["id"]!.GetValue<long>();
        var t = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id }))!;
        Assert.Equal("works", t["status"]!.GetValue<string>());
        Assert.Contains("(4 folders)", t["reason"]!.GetValue<string>());
        Assert.Equal("works", r.Conns.Get(id)!.Status);
        Assert.Contains(r.Mail.Imap.Lines(), l => l.Contains(" LIST ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Mail.Smtp.Lines(), l => l.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(r.Mail.Smtp.Messages); // nothing sent
    }

    [Fact]
    public async Task TestWithABadPasswordNeedsReconnect()
    {
        await using var r = Rig.Make();
        var id = (await r.Account(password: "wrong-pass-123"))["id"]!.GetValue<long>();
        var t = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id }))!;
        Assert.Equal("needs_reconnect", t["status"]!.GetValue<string>());
        Assert.Contains("refused the user name or password", t["reason"]!.GetValue<string>());
        Assert.Equal("needs_reconnect", r.Conns.Get(id)!.Status);
        Assert.Single(r.Svc.ConnectionsNeedingReconnect());
    }

    [Fact]
    public async Task TestWithARefusedPortNeedsReconnect()
    {
        await using var r = Rig.Make();
        var id = (await r.Account(smtpPort: r.Mail.RefusedPort()))["id"]!.GetValue<long>();
        var t = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id }))!;
        Assert.Equal("needs_reconnect", t["status"]!.GetValue<string>());
        Assert.Contains("could not reach the SMTP server", t["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task SeamOpensASessionForASavedAccount()
    {
        await using var r = Rig.Make();
        var id = (await r.Account())["id"]!.GetValue<long>();
        await using var imap = await r.Svc.OpenEmailImapAsync(id, Ct);
        Assert.NotEmpty(await imap.ListFoldersAsync(Ct));
        await using var smtp = await r.Svc.OpenEmailSmtpAsync(id, Ct);
        Assert.True(smtp.Client.IsAuthenticated);
    }

    // ------------------------------------------------------------------ the secret stays out

    [Fact]
    public async Task ThePasswordIsNeverInTheDatabaseOrTheLogs()
    {
        await using var r = Rig.Make();
        var good = (await r.Account())["id"]!.GetValue<long>();
        await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = good });
        // a failing one too: its reason is written to the row
        r.Mail.Imap.Password = "rotated";
        await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = good });
        var list = (await r.Rpc("integrations.connections"))!.ToJsonString();

        Assert.DoesNotContain(FakeMailServer.Password, list);
        Assert.DoesNotContain(FakeMailServer.Password, string.Join("\n", r.Log));
        Assert.DoesNotContain(FakeMailServer.Password, DumpAllTables(r.Db.ConnectionString));
        foreach (var file in Directory.GetFiles(r.Db.Dir, "yawble.db*"))
        {
            byte[] bytes;
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[s.Length]; s.ReadExactly(bytes); }
            Assert.DoesNotContain(FakeMailServer.Password, Encoding.UTF8.GetString(bytes));
        }
        // and anything that reaches a log, an agent or a call record is scrubbed of it
        Assert.DoesNotContain(FakeMailServer.Password, SecretScrubber.Scrub($"password was {FakeMailServer.Password}"));
    }

    private static string DumpAllTables(string cs)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) tables.Add(rd.GetString(0));
        }
        var sb = new StringBuilder();
        foreach (var t in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{t}\";";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                for (var i = 0; i < rd.FieldCount; i++) sb.Append(rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))).Append('\t');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Cloud Link

    [Theory]
    [InlineData("integrations.connections")]
    [InlineData("integrations.connectionSave")]
    [InlineData("integrations.connectionTest")]
    [InlineData("integrations.connectionRemove")]
    public void CloudLinkNeverSeesOrChangesAnEmailAccount(string method)
    {
        var refusal = RemoteAccess.Refusal(method, new JsonObject { ["kind"] = "email", ["id"] = 1 }, allowHostTerminals: true);
        Assert.Equal("connections can only be seen or changed on the computer itself", refusal);
    }

    [Fact]
    public async Task TheIndexHasOnlyNameKindAndStatus()
    {
        await using var r = Rig.Make();
        var id = (await r.Account())["id"]!.GetValue<long>();
        var entry = Assert.Single(r.Conns.Index());
        Assert.Equal((id, FakeMailServer.User, "email", "untested"), (entry.Id, entry.Name, entry.Kind, entry.Status));
        // the record carries nothing of the mailbox: no address beyond the name, no servers, no password
        var props = typeof(ConnectionIndexEntry).GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["Id", "Kind", "Name", "Status", "StoreKey"], props);
    }

    // ------------------------------------------------------------------ the 0057 rebuild

    [Fact]
    public void RebuildingTheConnectionsTableKeepsIdsAndReferences()
    {
        using var db = new TempDb();
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        void Exec(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        object? Scalar(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
        Exec("PRAGMA foreign_keys=ON;");
        Exec("INSERT INTO connections (id, name, store_key, kind, fields, created_at, updated_at) VALUES (7, 'k', 'sk7', 'secret', '[]', 't', 't'), (9, 'gone', 'sk9', 'secret', '[]', 't', 't');");
        Exec("DELETE FROM connections WHERE id = 9;");
        Exec("INSERT INTO apis (name, base_url, connection_id, created_at, updated_at) VALUES ('svc', 'https://x.test/', 7, 't', 't');");
        Exec("INSERT INTO mcp_servers (name, command, connection_id, created_at, updated_at) VALUES ('m', 'm', 7, 't', 't');");

        using (var tx = c.BeginTransaction())
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            using var s = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Yawble.Core.Migrations.0057_email_connections.sql")!;
            cmd.CommandText = new StreamReader(s).ReadToEnd();
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        Assert.Equal(7L, Scalar("SELECT connection_id FROM apis WHERE name = 'svc';"));
        Assert.Equal(7L, Scalar("SELECT connection_id FROM mcp_servers WHERE name = 'm';"));
        Assert.Equal("sk7", Scalar("SELECT store_key FROM connections WHERE id = 7;"));
        Exec("INSERT INTO connections (name, store_key, kind, fields, created_at, updated_at) VALUES ('mail', 'skm', 'email', '[]', 't', 't');");
        Assert.Equal(10L, Scalar("SELECT id FROM connections WHERE name = 'mail';")); // 9 was used once: never again
        Assert.Equal(0L, Scalar("SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public void AGoogleAppPasswordPastedWithSpacesSignsInWithoutThem_AndOtherPasswordsAreUsedAsGiven()
    {
        var gmail = EmailConnection.Settings("a@example.com", "imap.gmail.com", 993, "smtp.gmail.com", 465, null);
        var other = EmailConnection.Settings("a@example.com", "mail.example.com", 993, "mail.example.com", 465, null);
        Assert.Equal("abcdefghijklmnop", EmailConnection.SignInPassword(gmail, "abcd efgh ijkl mnop"));
        Assert.Equal("abcdefghijklmnop", EmailConnection.SignInPassword(gmail, " abcd efgh ijkl mnop "));
        Assert.Equal("abcdefghijklmnop", EmailConnection.SignInPassword(gmail, "abcdefghijklmnop"));
        Assert.Equal("my pass word", EmailConnection.SignInPassword(gmail, "my pass word"));
        Assert.Equal("abcd efgh ijkl mnop", EmailConnection.SignInPassword(other, "abcd efgh ijkl mnop"));
    }
}
