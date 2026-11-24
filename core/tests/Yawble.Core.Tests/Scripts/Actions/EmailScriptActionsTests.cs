using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Scripts.Actions;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Yawble.Core.Tests.Email;
using RunCaller = Yawble.Core.Scripts.Running.ScriptCaller;
using ScriptRunStatus = Yawble.Core.Scripts.Engine.ScriptRunStatus;

namespace Yawble.Core.Tests.Scripts.Actions;

/// <summary>
/// Card 590 (SC-3): the built-in Email integration's actions from a script, through the real script engine, the email script actions and the
/// real IntegrationService, against the fake IMAP server (no real server, no OS credential store, placeholder credentials only).
/// </summary>
public sealed class EmailScriptActionsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Me = "pat@example.test";
    private const string Password = "test-password";
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private const string Requires = """[ { "name": "mail", "kind": "email", "actions": ["list", "get", "search", "thread", "markRead", "move"] } ]""";

    private const string Variables = """
        [ { "name": "msgs", "type": "list<EmailMessage>" }, { "name": "one", "type": "EmailMessage" },
          { "name": "lastSeen", "type": "datetime", "scope": "saved" } ]
        """;

    /// <summary>List the unread since lastSeen, get the newest, mark it read.</summary>
    private const string ReadSteps = """
        [ { "id": "a", "type": "integration", "use": "mail", "action": "list",
            "args": { "folder": "INBOX", "since": "{vars.lastSeen}", "unreadOnly": true, "max": 10 }, "into": "msgs", "next": "b" },
          { "id": "b", "type": "integration", "use": "mail", "action": "get", "args": { "id": "{vars.msgs[1].id}" }, "into": "one", "next": "c" },
          { "id": "c", "type": "integration", "use": "mail", "action": "markRead", "args": { "id": "{vars.one.id}" }, "next": "d" },
          { "id": "d", "type": "set", "var": "lastSeen", "value": "{vars.one.receivedAt}", "next": "e" },
          { "id": "e", "type": "end", "outputs": { "first": "{vars.msgs[0].subject}", "subject": "{vars.one.subject}", "body": "{vars.one.body}",
              "unread": "{steps.c.unread}", "from": "{vars.one.from.address}" } } ]
        """;

    private readonly TempDb _db = new();
    private readonly TempFolder _temp = new();
    private readonly FakeMailServer _mail = new(Me, Password);
    private readonly List<string> _connLog = [];
    private BusEngine _engine = null!;
    private IntegrationService _svc = null!;
    private ScriptActionKinds _kinds = null!;
    private long _alpha, _beta, _connectionId;
    private FakeMessage _old = null!, _a = null!, _b = null!;

    public async ValueTask InitializeAsync()
    {
        _mail.Imap.WithMailbox();
        _mail.Imap.Folder("INBOX").UidValidity = 31;
        _old = Put(0, "Old news");
        _a = Put(10, "Quote request");
        _b = Put(20, "Follow up");

        _engine = new BusEngine(_db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        var conns = new ConnectionService(new MemoryCredentialStore(), _db.ConnectionString, log: line => { lock (_connLog) _connLog.Add(line); });
        _svc = new IntegrationService(_db.ConnectionString, _engine, connections: conns, mail: _mail.Factory());
        _engine.Integrations = _svc;
        _alpha = await _engine.CreateTeamAsync("Alpha", ct: Ct);
        _beta = await _engine.CreateTeamAsync("Beta", ct: Ct);

        var conn = (await Rpc("integrations.connectionSave", new JsonObject
        {
            ["kind"] = "email", ["address"] = Me, ["imapHost"] = "127.0.0.1", ["imapPort"] = _mail.Imap.Port,
            ["smtpHost"] = "127.0.0.1", ["smtpPort"] = _mail.Smtp.Port, ["password"] = Password,
        }))!;
        _connectionId = conn["id"]!.GetValue<long>();
        await Rpc("integrations.emailSave", new JsonObject { ["name"] = "Sales inbox", ["connectionId"] = _connectionId });
        await Rpc("integrations.emailEnable", new JsonObject { ["name"] = "Sales inbox", ["teamId"] = _alpha });

        _kinds = new ScriptActionKinds(new Dictionary<string, IScriptActionsProvider>
        {
            [EmailActions.Kind] = new EmailScriptActionsProvider(new IntegrationScriptEmail(_svc)),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _svc.DisposeAsync();
        await _engine.DisposeAsync();
        await _mail.DisposeAsync();
        _temp.Dispose();
        _db.Dispose();
    }

    private FakeMessage Put(int minutes, string subject)
    {
        var m = new FakeMessage(FakeImapMailbox.Plain("Lee Client <lee@example.test>", Me, subject, $"Body of {subject}."), T0.AddMinutes(minutes));
        _mail.Imap.Folder("INBOX").Put(m);
        return m;
    }

    private async Task<JsonNode?> Rpc(string method, JsonObject p) => await _svc.DispatchAsync(method, p, Ct);

    private Task<ScriptRunResult> Run(ScriptDocument doc, bool dryRun = false, string team = "Alpha", long? teamId = null, string? binding = "Sales inbox",
        JsonObject? saved = null)
    {
        var bindings = binding is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["mail"] = binding };
        var caller = new RunCaller(teamId ?? _alpha, team, 7, "Reader", 42, bindings, dryRun);
        var actions = _kinds.For(caller);
        return ScriptEngine.RunAsync(new ScriptRunRequest
        {
            Document = doc,
            Actions = actions,
            Catalog = ScriptActionKinds.CatalogFor(actions, _kinds),
            Schemas = BuiltInSchemas.Lookup,
            DataFolder = _temp.Sub("data"),
            DryRun = dryRun,
            SavedVariables = saved ?? new JsonObject { ["lastSeen"] = "2026-11-20T09:00:00Z" },
            Input = JsonNode.Parse("""{ "triggerId": "t1", "kind": "once", "firedAt": "2026-11-23T17:59:00Z" }"""),
        }, new RecordingJournal(), Ct);
    }

    private static ScriptDocument Doc(string steps, string variables = Variables) => ScriptSamples.Script(steps, variables: variables, requires: Requires);

    private List<(string Integration, string Member, string Args, string? Via)> Calls()
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT integration, member, args, via FROM integration_calls WHERE integration LIKE 'email:%' ORDER BY id;";
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, string, string, string?)>();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3)));
        return list;
    }

    // ------------------------------------------------------------------ the catalog and the check

    [Fact]
    public void TheCatalogNamesTheActionsAndTheirSignatures()
    {
        var catalog = _kinds.Catalog;
        foreach (var a in new[] { "list", "get", "search", "thread", "markRead", "move" }) Assert.True(catalog.HasAction("email", a), a);
        Assert.False(catalog.HasAction("email", "delete"));
        Assert.False(catalog.HasAction("email", "send")); // phase 2
        Assert.False(catalog.IsChanging("email", "list"));
        Assert.False(catalog.IsChanging("email", "thread"));
        Assert.True(catalog.IsChanging("email", "markRead"));
        Assert.True(catalog.IsChanging("email", "move"));
        var sigs = Assert.IsAssignableFrom<IScriptActionSignatures>(catalog);
        Assert.Equal("list<EmailMessage>", sigs.Signature("email", "list")!.Result);
        Assert.Equal("EmailMessage", sigs.Signature("email", "get")!.Result);
        Assert.Equal(["folder"], sigs.Signature("email", "list")!.Args["required"]!.AsArray().Select(n => (string?)n));
        Assert.Null(sigs.Signature("api", "list"));
    }

    [Fact]
    public void TheCheckAcceptsAGoodScript()
    {
        var check = ScriptChecker.Check(Doc(ReadSteps), BuiltInSchemas.Lookup, _kinds.Catalog);
        Assert.True(check.Ok, string.Join("; ", check.Issues.Select(i => i.Message)));
    }

    [Theory]
    [InlineData("""{ "folder": "INBOX", "unreadonly": true }""", ScriptCheckCodes.UnknownArgument, "list has no argument 'unreadonly'")]
    [InlineData("""{ "unreadOnly": true }""", ScriptCheckCodes.MissingField, "list needs the argument 'folder'")]
    [InlineData("""{ "folder": "INBOX", "unreadOnly": "yes" }""", ScriptCheckCodes.TypeMismatch, "'unreadOnly' is bool")]
    [InlineData("""{ "folder": "INBOX", "max": 2.5 }""", ScriptCheckCodes.TypeMismatch, "'max'")]
    [InlineData("""{ "folder": "INBOX", "since": "last tuesday" }""", ScriptCheckCodes.TypeMismatch, "'since'")]
    [InlineData("""{ "folder": "INBOX", "since": "{vars.msgs}" }""", ScriptCheckCodes.TypeMismatch, "'since' is datetime")]
    public void TheCheckRejectsBadArguments(string args, string code, string message)
    {
        var steps = $$"""
            [ { "id": "a", "type": "integration", "use": "mail", "action": "list", "args": {{args}}, "into": "msgs", "next": null } ]
            """;
        var check = ScriptChecker.Check(Doc(steps), BuiltInSchemas.Lookup, _kinds.Catalog);
        Assert.Contains(check.Issues, i => i.Code == code && i.StepId == "a" && i.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void TheCheckTypesTheResult()
    {
        // list gives list<EmailMessage>: into an EmailMessage variable is a mismatch, and a field it does not have is unknown
        var into = """[ { "id": "a", "type": "integration", "use": "mail", "action": "list", "args": { "folder": "INBOX" }, "into": "one", "next": null } ]""";
        Assert.Contains(ScriptChecker.Check(Doc(into), BuiltInSchemas.Lookup, _kinds.Catalog).Issues, i => i.Code == ScriptCheckCodes.TypeMismatch);
        var field = """
            [ { "id": "a", "type": "integration", "use": "mail", "action": "get", "args": { "id": "1:1" }, "next": "b" },
              { "id": "b", "type": "log", "text": "{steps.a.subjekt}", "next": null } ]
            """;
        Assert.Contains(ScriptChecker.Check(Doc(field), BuiltInSchemas.Lookup, _kinds.Catalog).Issues,
            i => i.Code == ScriptCheckCodes.UnknownReference && i.Message.Contains("subjekt", StringComparison.Ordinal));
        var unknown = ScriptSamples.Script(into, variables: Variables, requires: Requires.Replace("\"move\"", "\"delete\""));
        Assert.Contains(ScriptChecker.Check(unknown, BuiltInSchemas.Lookup, _kinds.Catalog).Issues,
            i => i.Code == ScriptCheckCodes.UnknownAction && i.Message.Contains("'delete'", StringComparison.Ordinal));
    }

    [Fact]
    public void ArgumentsAreCheckedAgainWhenTheStepRuns()
    {
        var list = EmailActions.Find("list")!;
        EmailScriptActions.CheckArgs("mail", list, new JsonObject { ["folder"] = "INBOX", ["since"] = null, ["max"] = 3 });
        Assert.Contains("no argument 'foldr'", Assert.Throws<ScriptActionException>(() =>
            EmailScriptActions.CheckArgs("mail", list, new JsonObject { ["foldr"] = "INBOX" })).Message);
        Assert.Contains("'folder' is required", Assert.Throws<ScriptActionException>(() =>
            EmailScriptActions.CheckArgs("mail", list, new JsonObject())).Message);
        Assert.Contains("expected integer", Assert.Throws<ScriptActionException>(() =>
            EmailScriptActions.CheckArgs("mail", list, new JsonObject { ["folder"] = "INBOX", ["max"] = "ten" })).Message);
        Assert.Contains("'folder' is empty", Assert.Throws<ScriptActionException>(() =>
            EmailScriptActions.CheckArgs("mail", list, new JsonObject { ["folder"] = " " })).Message);
    }

    // ------------------------------------------------------------------ runs

    [Fact]
    public async Task AScriptListsGetsAndMarksReadThroughTheRealIntegrationService()
    {
        var r = await Run(Doc(ReadSteps));

        Assert.True(r.Status == ScriptRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");
        // since lastSeen (09:00, the old message's time) is exclusive: the old one is not listed again
        Assert.Equal("Quote request", (string?)r.Outputs!["first"]);
        Assert.Equal("Follow up", (string?)r.Outputs["subject"]);
        Assert.Equal("Body of Follow up.", (string?)r.Outputs["body"]);
        Assert.Equal("lee@example.test", (string?)r.Outputs["from"]);
        Assert.Equal("false", r.Outputs["unread"]!.ToString());
        Assert.True(_b.Seen);
        Assert.False(_a.Seen);
        Assert.False(_old.Seen);
        Assert.Equal("2026-11-20T09:20:00Z", (string?)r.SavedVariables!["lastSeen"]);

        var calls = Calls();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.Equal(("email:Sales inbox", "Alpha/Reader", "script"), (c.Integration, c.Member, c.Via)));
        Assert.Contains("\"list\"", calls[0].Args);
        Assert.Contains("script run 42", calls[0].Args);
    }

    [Fact]
    public async Task ADryRunReadsButDoesNotMarkRead()
    {
        var r = await Run(Doc(ReadSteps.Replace("\"unread\": \"{steps.c.unread}\", ", "")), dryRun: true);
        Assert.True(r.Status == ScriptRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");
        Assert.False(_b.Seen);
        Assert.Contains(r.Log, l => l.Contains("would markRead on mail", StringComparison.Ordinal));
        Assert.DoesNotContain(_mail.Imap.Lines(), l => l.Contains("STORE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchThreadAndMoveRunFromAScript()
    {
        const string steps = """
            [ { "id": "a", "type": "integration", "use": "mail", "action": "search", "args": { "folder": "INBOX", "query": "quote" }, "into": "msgs", "next": "b" },
              { "id": "b", "type": "integration", "use": "mail", "action": "thread", "args": { "threadId": "{vars.msgs[0].threadId}" }, "next": "c" },
              { "id": "c", "type": "integration", "use": "mail", "action": "move", "args": { "id": "{vars.msgs[0].id}", "folder": "Archive" }, "into": "one", "next": "d" },
              { "id": "d", "type": "end", "outputs": { "found": "{vars.msgs[0].subject}", "thread": "{steps.b[0].subject}", "folder": "{vars.one.folder}" } } ]
            """;
        var r = await Run(Doc(steps));
        Assert.True(r.Status == ScriptRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");
        Assert.Equal("Quote request", (string?)r.Outputs!["found"]);
        Assert.Equal("Quote request", (string?)r.Outputs["thread"]);
        Assert.Equal("Archive", (string?)r.Outputs["folder"]);
        Assert.Contains(_mail.Imap.Folder("Archive").Entries(), e => ReferenceEquals(e.Message, _a));
    }

    [Fact]
    public async Task TheIntegrationOffForTheTeamIsRefused()
    {
        var r = await Run(Doc(ReadSteps), team: "Beta", teamId: _beta);
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("refused: Sales inbox is not turned on for Beta", r.Reason);
        Assert.Empty(Calls());
        Assert.Equal(0, _mail.Imap.Connections);

        // offered to every team, Beta may read it
        await Rpc("integrations.emailEnable", new JsonObject { ["name"] = "Sales inbox", ["all"] = true });
        Assert.Equal(ScriptRunStatus.Succeeded, (await Run(Doc(ReadSteps), team: "Beta", teamId: _beta)).Status);
    }

    [Fact]
    public async Task AnUnboundOrUnknownIntegrationIsRefused()
    {
        var unbound = await Run(Doc(ReadSteps), binding: null);
        Assert.Equal(ScriptRunStatus.Failed, unbound.Status);
        Assert.Contains("'mail' is not bound", unbound.Reason);
        var unknown = await Run(Doc(ReadSteps), binding: "Nope");
        Assert.Contains("refused: no Email integration named 'Nope'", unknown.Reason);
    }

    [Fact]
    public async Task AMailFailureFailsTheStepWithAReason()
    {
        var r = await Run(Doc("""[ { "id": "a", "type": "integration", "use": "mail", "action": "list", "args": { "folder": "Nowhere" }, "into": "msgs", "next": null } ]"""));
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Contains("mail list: the mailbox has no folder named 'Nowhere'", r.Reason);
    }

    [Fact]
    public async Task ThePasswordIsNeverInTheDatabaseOrTheLogs()
    {
        var ok = await Run(Doc(ReadSteps));
        Assert.Equal(ScriptRunStatus.Succeeded, ok.Status);
        // a refused sign-in too
        _mail.Imap.Password = "another-test-password";
        var refused = await Run(Doc(ReadSteps));
        Assert.Equal(ScriptRunStatus.Failed, refused.Status);
        Assert.Contains("refused the user name or password", refused.Reason);

        var texts = new List<string>();
        using (var c = new SqliteConnection(_db.ConnectionString))
        {
            c.Open();
            var tables = new List<string>();
            using (var t = c.CreateCommand())
            {
                t.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                using var rd = t.ExecuteReader();
                while (rd.Read()) tables.Add(rd.GetString(0));
            }
            foreach (var table in tables)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{table}\";";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                    for (var i = 0; i < rd.FieldCount; i++)
                        if (!rd.IsDBNull(i) && rd.GetValue(i) is string s) texts.Add(s);
            }
        }
        Assert.NotEmpty(texts);
        Assert.DoesNotContain(texts, s => s.Contains(Password, StringComparison.Ordinal));
        lock (_connLog) Assert.DoesNotContain(_connLog, s => s.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(ok.Log.Concat(refused.Log).Append(refused.Reason ?? ""), s => s.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(Password, (await Rpc("integrations.emailList", new JsonObject()))!.ToJsonString());
    }

    // ------------------------------------------------------------------ setup RPCs

    [Fact]
    public async Task TheIntegrationIsSetUpByRpc()
    {
        var list = (JsonArray)(await Rpc("integrations.emailList", new JsonObject()))!;
        var e = Assert.Single(list)!;
        Assert.Equal("Sales inbox", (string?)e["name"]);
        Assert.Equal("email", (string?)e["kind"]);
        Assert.Equal(_connectionId, (long?)e["connectionId"]);
        Assert.Equal([_alpha], e["teamIds"]!.AsArray().Select(n => (long)n!));
        Assert.Equal(["Sales inbox"], _svc.EmailIntegrationsOfferedTo("alpha"));
        Assert.Empty(_svc.EmailIntegrationsOfferedTo("Beta"));

        // the Connection says what uses it
        var conns = (JsonArray)(await Rpc("integrations.connections", new JsonObject()))!;
        Assert.Contains("Email: Sales inbox", conns.Single(c => (long?)c!["id"] == _connectionId)!["usedBy"]!.AsArray().Select(n => (string?)n));

        // only an email account, a unique name
        var secret = (await Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "Some key", ["fields"] = new JsonArray(new JsonObject { ["key"] = "token", ["label"] = "Token", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["token"] = "test-token" },
        }))!["id"]!.GetValue<long>();
        await Assert.ThrowsAnyAsync<Exception>(() => Rpc("integrations.emailSave", new JsonObject { ["name"] = "Other", ["connectionId"] = secret }));
        await Assert.ThrowsAnyAsync<Exception>(() => Rpc("integrations.emailSave", new JsonObject { ["name"] = "sales INBOX", ["connectionId"] = _connectionId }));

        await Rpc("integrations.emailEnable", new JsonObject { ["name"] = "Sales inbox", ["teamId"] = _alpha, ["on"] = false });
        Assert.Empty(_svc.EmailIntegrationsOfferedTo("Alpha"));
        await Rpc("integrations.emailRemove", new JsonObject { ["name"] = "Sales inbox" });
        Assert.Empty((JsonArray)(await Rpc("integrations.emailList", new JsonObject()))!);
        Assert.Contains("no Email integration named 'Sales inbox'", (await Run(Doc(ReadSteps))).Reason);
    }

    [Fact]
    public async Task ARemovedConnectionLeavesTheIntegrationRefusing()
    {
        await Rpc("integrations.connectionRemove", new JsonObject { ["id"] = _connectionId, ["confirm"] = true });
        var r = await Run(Doc(ReadSteps));
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Contains("refused: Sales inbox has no email account", r.Reason);
    }

    [Fact]
    public async Task DeletingATeamForgetsItsOffer()
    {
        _svc.ForgetTeam(_alpha, "Alpha");
        Assert.Empty(_svc.EmailIntegrations().Single().TeamIds);
        await Task.CompletedTask;
    }
}
