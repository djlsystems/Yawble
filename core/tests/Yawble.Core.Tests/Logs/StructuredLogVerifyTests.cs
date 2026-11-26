using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Launch;
using Yawble.Core.Logs;
using Yawble.Core.Workflows.Actions;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Yawble.Core.Tests.Email;
using Yawble.Core.Tests.Workflows;
using Yawble.Core.Tests.Teams;
using IntegrationCaller = Yawble.Core.Integrations.WorkflowCaller;
using RunCaller = Yawble.Core.Workflows.Running.WorkflowCaller;
using RunStatus = Yawble.Core.Workflows.Running.WorkflowRunStatus;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// Structured logs end to end with fakes. A real Diagnostics file holding old 3-column lines and new 4-column lines,
/// read back through logs.query (the RPC) with the Level filter and the team+member filter on two teams that both have a
/// DeveloperInes; two start-up fetch-backs; a failing workflow run whose warning line goes into a real Diagnostics file and
/// is read back; the email login failure as the Tool calls log shows it.
/// </summary>
public sealed class StructuredLogVerifyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDb _db = new();
    private readonly Yawble.Core.Tests.Gateway.FakeClock _clock = new(Now);

    public StructuredLogVerifyTests() => _db.OpenStore().Dispose();

    public void Dispose() => _db.Dispose();

    private static List<JsonObject> Rows(JsonNode? page) => [.. page!["rows"]!.AsArray().Select(r => r!.AsObject())];

    private async Task<List<JsonObject>> RpcQuery(string? level = null, string? team = null, string? member = null)
    {
        var p = new JsonObject { ["source"] = "diagnostics", ["asOf"] = "9999", ["limit"] = 500 };
        if (level is not null) p["level"] = level;
        if (team is not null) p["team"] = team;
        if (member is not null) p["member"] = member;
        return Rows(await new LogRpc(new LogService(_db.Dir, _db.ConnectionString, _clock)).DispatchAsync("logs.query", p, Ct));
    }

    private static string T(JsonObject r) => r["text"]!.GetValue<string>();

    // ------------------------------------------------------------------ bullet 1: the real file, old and new lines

    [Fact]
    public async Task ARealFile_WithOldThreeColumnLines_AndNewFourColumnLines_ReadsBothWithTheirLevels()
    {
        // A day's file as the old core wrote it (3 columns, no fields), then the new core appends to the same file.
        var folder = Path.Combine(_db.Dir, FileLogs.DiagnosticsFolder);
        Directory.CreateDirectory(folder);
        var at = Now.AddMinutes(-5).ToString("O");
        var file = Path.Combine(folder, $"core-{Now:yyyy-MM-dd}.log");
        File.WriteAllText(file,
            $"{at}\tcore\tteam Alpha: push to origin failed: exit 128\n" +
            $"{at}\tcore\twarning: the shell went away\n" +
            $"{at}\tcore\tthe core started on port 5000\n");
        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        log.Write("debug", "bus", "run 9 admitted: Alpha/DeveloperInes", [("team", "Alpha"), ("member", "Alpha/DeveloperInes"), ("run", 9)]);
        log.Write("info", "teams", "merge failed? no, the clone is fine", [("team", "Alpha")]); // "failed" in an info line
        log.Write("warning", "teams", "Beta/DeveloperInes: the branch was left as it was", [("team", "Beta"), ("member", "Beta/DeveloperInes")]);

        var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToArray();
        Assert.Equal([3, 3, 3, 4, 4, 4], lines.Select(l => l.Split('\t').Length));
        Assert.Equal("level=debug area=bus team=Alpha member=Alpha/DeveloperInes run=9", lines[3].Split('\t')[3]);

        var all = (await RpcQuery("all")).ToDictionary(T, r => r["level"]!.GetValue<string>());
        Assert.Equal(6, all.Count);
        Assert.Equal("error", all["team Alpha: push to origin failed: exit 128"]); // the old line: the regex
        Assert.Equal("warning", all["warning: the shell went away"]);
        Assert.Equal("info", all["the core started on port 5000"]);
        Assert.Equal("debug", all["run 9 admitted: Alpha/DeveloperInes"]);
        Assert.Equal("info", all["merge failed? no, the clone is fine"]); // the call's level, not the regex
        Assert.Equal("warning", all["Beta/DeveloperInes: the branch was left as it was"]);

        // Default (absent or "default"): debug hidden. "debug": only it.
        Assert.DoesNotContain(await RpcQuery(), r => T(r).StartsWith("run 9 admitted", StringComparison.Ordinal));
        Assert.Equal(5, (await RpcQuery()).Count);
        Assert.Equal(5, (await RpcQuery("default")).Count);
        Assert.Equal("run 9 admitted: Alpha/DeveloperInes", T(Assert.Single(await RpcQuery("debug"))));
        Assert.Equal(["Beta/DeveloperInes: the branch was left as it was", "warning: the shell went away"],
            (await RpcQuery("warning")).Select(T).Order(StringComparer.Ordinal));
        Assert.Equal("level=warning area=teams team=Beta member=Beta/DeveloperInes",
            (await RpcQuery("warning")).Single(r => T(r).StartsWith("Beta", StringComparison.Ordinal))["fields"]!.GetValue<string>());
    }

    [Fact]
    public async Task TeamAndMember_OnTwoTeamsBothWithADeveloperInes_ReturnOnlyTheAskedTeamsLines()
    {
        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        log.Write("warning", "teams", "DeveloperInes: could not fetch", [("team", "Alpha"), ("member", "DeveloperInes")]);
        log.Write("info", "bus", "Alpha/DeveloperInes: run 3 ended", [("member", "Alpha/DeveloperInes"), ("run", 3)]);
        log.Write("warning", "teams", "DeveloperInes: could not fetch", [("team", "Beta"), ("member", "DeveloperInes")]);
        log.Write("info", "bus", "Beta/DeveloperInes: run 4 ended", [("member", "Beta/DeveloperInes"), ("run", 4)]);
        log.Write("debug", "bus", "Beta/DeveloperInes: run 4 admitted", [("member", "Beta/DeveloperInes"), ("run", 4)]);
        log.Write("info", "teams", "Beta's note about Alpha's DeveloperInes", [("team", "Beta")]); // names Alpha's Ines in the text only

        var alphaInes = await RpcQuery(member: "Alpha/DeveloperInes");
        Assert.Equal(["Alpha/DeveloperInes: run 3 ended", "DeveloperInes: could not fetch"], alphaInes.Select(T).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(alphaInes, r => r["fields"]!.GetValue<string>().Contains("Beta", StringComparison.Ordinal));

        var betaInes = await RpcQuery(member: "Beta/DeveloperInes");
        Assert.Equal(["Beta/DeveloperInes: run 4 ended", "DeveloperInes: could not fetch"], betaInes.Select(T).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(betaInes, r => r["fields"]!.GetValue<string>().Contains("Alpha", StringComparison.Ordinal));
        Assert.Equal(3, (await RpcQuery("all", member: "Beta/DeveloperInes")).Count); // debug only with the Level filter
        Assert.Equal("Beta/DeveloperInes: run 4 admitted", T(Assert.Single(await RpcQuery("debug", member: "Beta/DeveloperInes"))));

        Assert.Equal(2, (await RpcQuery(team: "Alpha")).Count);
        Assert.Equal(3, (await RpcQuery(team: "Beta")).Count);
        Assert.DoesNotContain(await RpcQuery(team: "Alpha"), r => T(r).StartsWith("Beta's note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTailRpc_HidesDebugUnlessAskedFor()
    {
        var events = new Yawble.Core.Ui.AppEvents();
        using var tail = new LogTail(events, interval: TimeSpan.FromHours(1));
        var rpc = new LogRpc(new LogService(_db.Dir, _db.ConnectionString, _clock), tail);
        var all = (await rpc.DispatchAsync("logs.tail", new JsonObject { ["level"] = "all" }, Ct))!;
        var debug = (await rpc.DispatchAsync("logs.tail", new JsonObject { ["level"] = "debug" }, Ct))!;
        await Assert.ThrowsAnyAsync<Exception>(() => rpc.DispatchAsync("logs.tail", new JsonObject { ["level"] = "loud" }, Ct));
        var log = new DiagnosticLog(_db.Dir, "core", _clock) { Tail = tail };
        var got = new System.Collections.Concurrent.ConcurrentQueue<JsonObject>();
        using var sub = events.Subscribe(m => { got.Enqueue(m); return Task.CompletedTask; });

        log.Write("debug", "lease", "lease: Alpha/Ada holds it", [("member", "Alpha/Ada")]);
        log.Write("info", "teams", "start-up fetch-back: fetched 2 clones, 0 behind");

        events.Publish("test.mark", "structured-log-verify");
        await Wait.UntilAsync(() => Task.FromResult(got.Any(m => m["event"]?.GetValue<string>() == "test.mark")), "the events to be delivered");
        var entries = got.Where(m => m["event"]?.GetValue<string>() == LogTail.EntryEvent).Select(m => m["data"]!).ToList();
        string Of(JsonNode t) => t.ToJsonString();
        var allId = Of(all["tail"]!);
        var debugId = Of(debug["tail"]!);
        Assert.Equal(["start-up fetch-back: fetched 2 clones, 0 behind"],
            entries.Where(e => Of(e["tail"]!) == allId).Select(e => e["entry"]!["text"]!.GetValue<string>()));
        Assert.Equal(2, entries.Count(e => Of(e["tail"]!) == debugId));
    }

    // ------------------------------------------------------------------ bullet 2: the start-up fetch-back, twice

    [Fact]
    public async Task TwoStartUps_EachWriteOneSummaryLine_AndTheSecondWritesNoPerCloneLineAboveDebug()
    {
        SandboxGit.RequireLocalTransport();
        var seen = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (seen) seen.Add(e); });
        using var f = new TeamsFixture();
        var (_, ada, ben) = f.Team();
        // STRUCTURED_LOG_VERIFY_APPROOT: a shorter folder for the app root where the temp folder is too deep for git ("Filename too long").
        var appRoot = Environment.GetEnvironmentVariable("STRUCTURED_LOG_VERIFY_APPROOT") is { Length: > 0 } shortRoot
            ? Directory.CreateDirectory(Path.Combine(shortRoot, Guid.NewGuid().ToString("N")[..8])).FullName
            : f.AppRoot;
        var emptyGlobal = Path.Combine(f.Db.Dir, "empty.gitconfig");
        File.WriteAllText(emptyGlobal, "");
        var first = new Yawble.Core.Teams.TeamRepos(appRoot, f.Db.ConnectionString) { Pusher = new Yawble.Core.Teams.PersonGitPush(globalConfig: emptyGlobal) };
        foreach (var m in new[] { ada, ben })
        {
            var checkout = await first.PrepareAsync(m, Ct);
            File.WriteAllText(Path.Combine(checkout.ClonePath, $"{m.Name}.txt"), m.Name);
            AgentGit.Ok(checkout, "add", ".");
            AgentGit.Ok(checkout, "commit", "-q", "-m", m.Name);
        }
        await first.FetchBackAllAsync([ada, ben], Ct);
        int mark;
        lock (seen)
        {
            mark = seen.Count;
            Assert.Equal("start-up fetch-back: fetched 2 clones, 0 behind", Assert.Single(seen, e => e.Level != LogLevels.Debug && e.Area == "teams").Text);
        }

        // The second start: a new TeamRepos (a new process) over the same folders, nothing changed.
        var again = new Yawble.Core.Teams.TeamRepos(appRoot, f.Db.ConnectionString) { Pusher = first.Pusher };
        await again.FetchBackAllAsync([ada, ben], Ct);

        List<DiagEntry> second;
        lock (seen) second = [.. seen.Skip(mark)];
        var summary = Assert.Single(second, e => e.Level != LogLevels.Debug);
        Assert.Equal("start-up fetch-back: fetched 2 clones, 0 behind", summary.Text);
        Assert.Equal((LogLevels.Info, "2", "0"), (summary.Level, summary.Field("fetched"), summary.Field("behind")));
        Assert.All(second.Where(e => e.Text.Contains(": fetched members/", StringComparison.Ordinal)), e => Assert.Equal(LogLevels.Debug, e.Level));
    }

    // ------------------------------------------------------------------ bullet 3: a failing workflow run, into a real file

    private sealed class Actions : IWorkflowActionsProvider, IWorkflowActions
    {
        public IWorkflowActionCatalog Catalog { get; } = FakeCatalog.MailAndSms();
        public string? SmsFailure { get; set; }
        public IWorkflowActions For(RunCaller caller) => this;

        public Task<JsonNode?> InvokeAsync(WorkflowActionCall call, CancellationToken ct)
        {
            if (call.Use == "sms" && SmsFailure is { } why) throw new WorkflowActionException(why);
            return Task.FromResult<JsonNode?>(call.Use == "mail"
                ? new JsonArray(WorkflowSamples.Email("m7", "Quarterly invoice 4471", "billing@vendor.example", "2026-11-24T08:00:00Z"))
                : new JsonObject { ["status"] = 201 });
        }
    }

    [Fact]
    public async Task AFailingWorkflowRun_WritesOneWarningLineWithEveryField_IntoTheFile_AndTheRunsRowSaysWhatFailed()
    {
        var actions = new Actions
        {
            SmsFailure = "twilio CreateMessage (POST /2010-04-01/Accounts/{AccountSid}/Messages.json): HTTP 400: auth tok_verify_9f8e7d6c5b4a3210 rejected",
        };
        WorkflowRunner? runner = null;
        await using var bus = new BusEngine(_db.OpenStore(), new WorkflowDispatchRunner(new ScriptedRunner(), () => runner), new BusOptions { IdleOffer = false });
        var teamId = await bus.CreateTeamAsync("Alpha", ct: Ct);
        var library = new WorkflowLibrary(_db.ConnectionString, () => actions.Catalog);
        var store = new WorkflowRunStore(_db.ConnectionString);
        runner = new WorkflowRunner(store, library, actions, SeamWorkflowEngine.Instance, Path.Combine(_db.Dir, "workflows"));
        bus.WorkflowMembers = runner;
        var saved = library.Save(WorkflowSamples.InboxSmsAlert);
        Assert.True(saved.Ok, saved.Check.ToJsonString());
        await bus.StartAsync(Ct);
        var bot = await bus.AddWorkflowMemberAsync(teamId, "Poller", Ct);
        store.SetWorkflowMember(bot.Id, saved.Id, null,
            new JsonObject { ["recipients"] = new JsonArray("+15551230001"), ["fromNumber"] = "+15559990000" },
            new JsonObject { ["mail"] = "mailbox", ["sms"] = "twilio" }, null);

        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        var owner = new object();
        SecretScrubber.Set(owner, 1, ["tok_verify_9f8e7d6c5b4a3210"]);
        WorkflowRunOutcome outcome;
        try
        {
            // What Diag does once attached: the entry is written to the Diagnostics log (here: a real file of this test's own).
            using (Diag.Capture(e => log.Write(e.Level, e.Area, e.Text, e.Fields)))
                outcome = await runner.RunMemberAsync(bot.Id, "trigger:12",
                    WorkflowPayloads.TriggerFired(12, "every", DateTimeOffset.Parse("2026-11-24T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), Ct);
        }
        finally { SecretScrubber.Forget(owner, 1); }
        await runner.StopAsync();

        Assert.Equal(RunStatus.Failed, outcome.Run.Status);
        var step = outcome.Run.FailedStepId!;
        var fileLines = Directory.GetFiles(Path.Combine(_db.Dir, FileLogs.DiagnosticsFolder)).SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToList();
        var line = Assert.Single(fileLines, l => l.Split('\t') is { Length: 4 } c && c[3].Contains("area=workflows", StringComparison.Ordinal) && c[3].Contains("level=warning", StringComparison.Ordinal));
        var cols = line.Split('\t');
        var pairs = DiagFields.Parse(cols[3]);
        string? F(string k) => DiagFields.Get(pairs, k);
        var runId = outcome.Run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(runId, F("workflowRun"));
        Assert.Equal("Alpha", F("team"));
        Assert.Equal("Alpha/Poller", F("member"));
        Assert.Equal("inbox-sms-alert@1", F("workflow"));
        Assert.Equal("trigger:12", F("trigger"));
        Assert.Equal(step, F("step"));
        Assert.Equal("integration", F("stepType"));
        Assert.Equal("twilio", F("integration"));
        Assert.False(string.IsNullOrEmpty(F("action")));
        Assert.Contains("HTTP 400", F("reason"));
        Assert.True(long.TryParse(F("ms"), out _), F("ms"));
        Assert.Equal("failed", F("status"));
        Assert.StartsWith($"workflow run {runId} failed: Alpha/Poller, inbox-sms-alert@1, started by trigger:12, at step {step} (", cols[2]);
        Assert.Contains("twilio CreateMessage (POST", cols[2]);
        // Scrubbed and no message content anywhere in the line: the token, the email's subject and sender, the phone numbers.
        foreach (var leak in new[] { "tok_verify_9f8e7d6c5b4a3210", "Quarterly invoice", "billing@vendor.example", "+15551230001", "+15559990000" })
            Assert.DoesNotContain(leak, line);

        // And through logs.query: a warning, in the member's lines.
        var rows = await RpcQuery("warning", member: "Alpha/Poller");
        Assert.Contains(rows, r => T(r).StartsWith($"workflow run {runId} failed", StringComparison.Ordinal));

        // The Runs row and the failed step.
        var stepRow = Assert.Single(store.Steps(outcome.Run.Id), s => s.StepId == step && s.Status == "failed");
        Assert.Contains("HTTP 400", (string?)stepRow.Output!["error"]);
        var runs = Rows(new LogService(_db.Dir, _db.ConnectionString).Query(new LogQuery("runs", Team: "Alpha")));
        var workflow = Assert.Single(runs, x => (string?)x["kind"] == "workflow");
        Assert.Equal(("inbox-sms-alert@1", "trigger:12", step, outcome.Run.Id),
            ((string?)workflow["workflow"], (string?)workflow["trigger"], (string?)workflow["failedStep"], (long?)workflow["workflowRun"]));
    }

    // ------------------------------------------------------------------ bullet 4: the email login failure in the Tool calls log

    [Fact]
    public async Task AFailedEmailLogin_ShowsInTheToolCallsLog_WithExitDurationHostAndTheError()
    {
        const string me = "pat@example.test", password = "test-password";
        await using var mail = new FakeMailServer(me, password);
        mail.Imap.WithMailbox();
        mail.Imap.Folder("INBOX").Put(new FakeMessage(FakeImapMailbox.Plain("Lee Client <lee@example.test>", me, "Private subject 31", "Private body."),
            new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero)));
        await using var engine = new BusEngine(_db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        var conns = new ConnectionService(new MemoryCredentialStore(), _db.ConnectionString);
        await using var svc = new IntegrationService(_db.ConnectionString, engine, clock: new Yawble.Core.Tests.Workflows.FakeClock(Now), connections: conns, mail: mail.Factory());
        engine.Integrations = svc;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var conn = (await svc.DispatchAsync("integrations.connectionSave", new JsonObject
        {
            ["kind"] = "email", ["address"] = me, ["imapHost"] = "127.0.0.1", ["imapPort"] = mail.Imap.Port,
            ["smtpHost"] = "127.0.0.1", ["smtpPort"] = mail.Smtp.Port, ["password"] = password,
        }, Ct))!;
        await svc.DispatchAsync("integrations.emailSave", new JsonObject { ["name"] = "Sales inbox", ["connectionId"] = conn["id"]!.GetValue<long>() }, Ct);
        await svc.DispatchAsync("integrations.emailEnable", new JsonObject { ["name"] = "Sales inbox", ["teamId"] = alpha }, Ct);

        mail.Imap.Password = "a-different-password";
        var e = await Assert.ThrowsAsync<MailException>(() => svc.UseEmailForWorkflowAsync<JsonNode?>(new IntegrationCaller("Alpha", "Reader", "77", false), "Sales inbox", "list",
            new JsonObject { ["folder"] = "INBOX", ["max"] = 5 }, async m => (await m.ListAsync("INBOX", null, false, 5, null, Ct)).ToJson(), Ct));

        var tools = Rows(new LogService(_db.Dir, _db.ConnectionString).Query(new LogQuery("tools", Team: "Alpha")));
        var row = Assert.Single(tools, r => ((string?)r["integration"])?.Contains("Sales inbox", StringComparison.Ordinal) == true);
        var text = row.ToJsonString();
        Assert.Equal("1", row["exit"]?.ToString());
        Assert.False(string.IsNullOrEmpty(row["duration"]?.ToString()), text);
        Assert.Contains("host 127.0.0.1", text);
        Assert.Contains("failed: ", text);
        Assert.Contains("refused the user name or password", e.Message);
        Assert.Equal("workflow", row["via"]?.ToString());
        foreach (var leak in new[] { password, "a-different-password", "Private subject 31", "Private body" })
            Assert.DoesNotContain(leak, text);
    }
}
