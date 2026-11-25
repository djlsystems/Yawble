using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Yawble.Core.Tests.Email;

namespace Yawble.Core.Tests.Scripts.Actions;

/// <summary>
/// The Email integration's Tool-call row is written after the work: a refused sign-in leaves a row that says it failed
/// and why, with the host and the duration (before: a bare "allow" row written before IMAP opened); a success says how many messages came back,
/// never their content; a script member's routine lists are one row per member per hour, unless one failed. Against the fake IMAP server, with
/// a fixed clock (the hour is the clock's).
/// </summary>
public sealed class EmailCallRowTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Me = "pat@example.test";
    private const string Password = "test-password";

    private readonly TempDb _db = new();
    private readonly FakeMailServer _mail = new(Me, Password);
    private readonly Yawble.Core.Tests.Scripts.FakeClock _clock = new(new DateTimeOffset(2026, 11, 24, 10, 5, 0, TimeSpan.Zero));
    private BusEngine _engine = null!;
    private IntegrationService _svc = null!;

    public async ValueTask InitializeAsync()
    {
        _mail.Imap.WithMailbox();
        _mail.Imap.Folder("INBOX").Put(new FakeMessage(FakeImapMailbox.Plain("Lee Client <lee@example.test>", Me, "Secret quote", "Body text."),
            new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero)));
        _engine = new BusEngine(_db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        var conns = new ConnectionService(new MemoryCredentialStore(), _db.ConnectionString);
        _svc = new IntegrationService(_db.ConnectionString, _engine, clock: _clock, connections: conns, mail: _mail.Factory());
        _engine.Integrations = _svc;
        var alpha = await _engine.CreateTeamAsync("Alpha", ct: Ct);
        var conn = (await _svc.DispatchAsync("integrations.connectionSave", new JsonObject
        {
            ["kind"] = "email", ["address"] = Me, ["imapHost"] = "127.0.0.1", ["imapPort"] = _mail.Imap.Port,
            ["smtpHost"] = "127.0.0.1", ["smtpPort"] = _mail.Smtp.Port, ["password"] = Password,
        }, Ct))!;
        await _svc.DispatchAsync("integrations.emailSave", new JsonObject { ["name"] = "Sales inbox", ["connectionId"] = conn["id"]!.GetValue<long>() }, Ct);
        await _svc.DispatchAsync("integrations.emailEnable", new JsonObject { ["name"] = "Sales inbox", ["teamId"] = alpha }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _svc.DisposeAsync();
        await _engine.DisposeAsync();
        await _mail.DisposeAsync();
        _db.Dispose();
    }

    private sealed record Row(string Args, string Decision, long? Exit, long? Ms, string? Via);

    private List<Row> Rows()
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT args, decision, exit_code, duration_ms, via FROM integration_calls WHERE integration = 'email:Sales inbox' ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<Row>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    private Task<JsonNode?> List(string run) =>
        _svc.UseEmailForScriptAsync<JsonNode?>(new ScriptCaller("Alpha", "Reader", run, false), "Sales inbox", "list", new JsonObject { ["folder"] = "INBOX", ["max"] = 5 },
            async m => (await m.ListAsync("INBOX", null, false, 5, null, Ct)).ToJson(), Ct);

    [Fact]
    public async Task AFailedLogin_WritesTheRowAfterTheWork_WithTheMailErrorHostAndDuration()
    {
        _mail.Imap.Password = "another-test-password";
        var e = await Assert.ThrowsAsync<MailException>(() => List("41"));

        var row = Assert.Single(Rows());
        Assert.Equal(("allow", 1L, "script"), (row.Decision, row.Exit, row.Via));
        Assert.NotNull(row.Ms);
        var args = System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.Args)!;
        Assert.Equal("list", args[0]);
        Assert.Equal("script run 41", args[2]);
        Assert.Equal("host 127.0.0.1", args[3]);
        Assert.Equal("failed: " + e.Message, args[4]);
        Assert.Contains("refused the user name or password", args[4]);
        Assert.DoesNotContain(Password, row.Args);
        Assert.DoesNotContain("another-test-password", row.Args);
    }

    [Fact]
    public async Task ASuccess_SaysHowManyMessages_NeverTheirContent()
    {
        await _svc.UseEmailForScriptAsync<JsonNode?>(new ScriptCaller("Alpha", "Reader", "42", false), "Sales inbox", "search",
            new JsonObject { ["folder"] = "INBOX", ["query"] = "Secret quote" },
            async m => new JsonArray((await m.SearchAsync("INBOX", "Secret quote", Ct)).Select(x => (JsonNode)x.ToJson()).ToArray()), Ct);

        var row = Assert.Single(Rows());
        Assert.Equal(0L, row.Exit);
        var args = System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.Args)!;
        Assert.Equal(["search", """{"folder":"INBOX","query":"(12 chars)"}""", "script run 42", "host 127.0.0.1", "1 message"], args);
        Assert.DoesNotContain("Secret quote", row.Args);
        Assert.DoesNotContain("Body text", row.Args);
    }

    [Fact]
    public async Task RoutineLists_AreOneRowPerMemberPerHour_UnlessOneFailed()
    {
        await List("1");
        _clock.Now = _clock.Now.AddMinutes(10);
        await List("2");
        _clock.Now = _clock.Now.AddMinutes(10);
        await List("3");

        var folded = Assert.Single(Rows());
        var args = System.Text.Json.JsonSerializer.Deserialize<List<string>>(folded.Args)!;
        Assert.Equal(["list", """{"folder":"INBOX","max":5}""", "script runs 1 to 3", "host 127.0.0.1", "3 messages", "3 calls this hour"], args);
        Assert.Equal(0L, folded.Exit);

        // a failure is its own row, and is never folded into
        _mail.Imap.Password = "another-test-password";
        await Assert.ThrowsAsync<MailException>(() => List("4"));
        _mail.Imap.Password = Password;
        await List("5");
        Assert.Equal(2, Rows().Count);
        Assert.Contains("4 calls this hour", Rows()[0].Args);
        Assert.Equal(1L, Rows()[1].Exit);

        // the next hour starts a new row
        _clock.Now = new DateTimeOffset(2026, 11, 24, 11, 0, 1, TimeSpan.Zero);
        await List("6");
        var rows = Rows();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["list", """{"folder":"INBOX","max":5}""", "script run 6", "host 127.0.0.1", "1 message"], System.Text.Json.JsonSerializer.Deserialize<List<string>>(rows[2].Args)!);
    }
}
