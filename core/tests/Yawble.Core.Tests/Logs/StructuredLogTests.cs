using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Teams;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// Structured Diagnostics lines (a level from the call, key=value fields in a fourth
/// column), debug hidden by default and shown with the Level filter, the team and member filters on the fields, and the
/// routine lines at debug or summed up once. Fakes only: temp folders, a temp database, a fake grok, scripted runs.
/// </summary>
public sealed class StructuredLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDb _db = new();
    private readonly FakeClock _clock = new(Now);

    public StructuredLogTests() => _db.OpenStore().Dispose(); // the schema

    public void Dispose() => _db.Dispose();

    private LogService Logs() => new(_db.Dir, _db.ConnectionString, _clock);

    private static List<JsonObject> Rows(JsonObject page) => [.. page["rows"]!.AsArray().Select(r => r!.AsObject())];

    private List<JsonObject> Query(string? level = null, string? team = null, string? member = null) =>
        Rows(Logs().Query(new LogQuery("diagnostics", Team: team, Member: member, AsOf: "9999", Level: level)));

    private static string[] Lines(string dir) =>
        [.. Directory.GetFiles(Path.Combine(dir, FileLogs.DiagnosticsFolder)).SelectMany(File.ReadAllLines).Where(l => l.Length > 0)];

    // ---------------------------------------------------------------- the fields column

    [Fact]
    public void TheFieldsColumn_RoundTrips_SpacesQuotesAndEqualsIncluded()
    {
        var column = DiagFields.Format("warning", "teams",
        [
            ("team", "Yawble Engineering Team"), ("member", "Alpha/Ada"), ("run", 42L), ("ms", TimeSpan.FromMilliseconds(1500)),
            ("status", "a \"quoted\" = value"), ("path", @"C:\x\y"), ("ok", true), ("gone", null), ("empty", ""),
        ]);
        var pairs = DiagFields.Parse(column);
        Assert.Equal(
            [("level", "warning"), ("area", "teams"), ("team", "Yawble Engineering Team"), ("member", "Alpha/Ada"), ("run", "42"), ("ms", "1500"),
             ("status", "a \"quoted\" = value"), ("path", @"C:\x\y"), ("ok", "true"), ("empty", "")],
            pairs);
        Assert.DoesNotContain('\t', column);
        Assert.Equal(("Alpha", "Ada"), DiagFields.TeamMember(pairs)); // a qualified member names its team too
    }

    [Fact]
    public void AStructuredLine_HasFourColumns_AndReadsBackWithItsFieldsAndLevel()
    {
        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        log.Write("warning", "teams", "Alpha/Ada: could not fetch\tthe branch", [("team", "Alpha"), ("member", "Alpha/Ada"), ("run", 7)]);
        log.Write("the core started"); // untagged, as the stderr tee writes

        var lines = Lines(_db.Dir);
        Assert.Equal(4, lines[0].Split('\t').Length);
        Assert.Equal(3, lines[1].Split('\t').Length); // the old format, unchanged
        var rows = Query();
        var fetched = Assert.Single(rows, r => r["text"]!.GetValue<string>().StartsWith("Alpha/Ada", StringComparison.Ordinal));
        Assert.Equal("Alpha/Ada: could not fetch the branch", fetched["text"]!.GetValue<string>());
        Assert.Equal("level=warning area=teams team=Alpha member=Alpha/Ada run=7", fetched["fields"]!.GetValue<string>());
        Assert.Equal("warning", fetched["level"]!.GetValue<string>());
        Assert.Contains(Logs().Sources().Select(s => s!.AsObject()), s => s["id"]!.GetValue<string>() == "diagnostics" && s["levels"]!.GetValue<bool>());
    }

    [Fact]
    public void TheLevel_IsTheCallers_AndTheRegexOnlyReadsUntaggedLines()
    {
        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        log.Write("info", "teams", "merge failed? no: the clone is fine"); // says "failed", is info
        log.Write("warning", "teams", "the branch was left as it was"); // says nothing, is a warning
        log.Write("push to origin failed: exit 128"); // untagged: the regex reads it

        var levels = Query("all").ToDictionary(r => r["text"]!.GetValue<string>(), r => r["level"]!.GetValue<string>());
        Assert.Equal("info", levels["merge failed? no: the clone is fine"]);
        Assert.Equal("warning", levels["the branch was left as it was"]);
        Assert.Equal("error", levels["push to origin failed: exit 128"]);
    }

    // ---------------------------------------------------------------- debug and the Level filter

    [Fact]
    public async Task Debug_IsHiddenByDefault_AndShownWithTheLevelFilter_InQueryAndTail()
    {
        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        log.Write("debug", "bus", "run 5 admitted");
        log.Write("info", "teams", "start-up fetch-back: fetched 2 clones, 0 behind");
        log.Write("error", "doctor", "doctor: the periodic check failed");

        Assert.Equal(2, Query().Count);
        Assert.Equal(2, Query("default").Count);
        Assert.Equal(3, Query("all").Count);
        Assert.Equal("run 5 admitted", Assert.Single(Query("debug"))["text"]!.GetValue<string>());
        Assert.Equal("doctor: the periodic check failed", Assert.Single(Query("error"))["text"]!.GetValue<string>());

        var rpc = new LogRpc(Logs());
        var page = (JsonObject)(await rpc.DispatchAsync("logs.query", new JsonObject { ["source"] = "diagnostics", ["level"] = "debug", ["asOf"] = "9999" }, Ct))!;
        Assert.Single(Rows(page));
        await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => rpc.DispatchAsync("logs.query", new JsonObject { ["source"] = "diagnostics", ["level"] = "loud" }, Ct));

        LogTailEntry Entry(string level) => new(Now.ToString("O"), "diagnostics", level, null, null, "core", "x");
        Assert.False(new LogTailFilter(All: true).Matches(Entry(LogLevels.Debug)));
        Assert.True(new LogTailFilter(All: true).Matches(Entry(LogLevels.Info)));
        Assert.True(new LogTailFilter(All: true, Debug: true).Matches(Entry(LogLevels.Debug)));
        Assert.False(new LogTailFilter().Matches(Entry(LogLevels.Debug)));
    }

    // ---------------------------------------------------------------- team and member on the fields

    [Fact]
    public void TheTeamAndMemberFilters_UseTheFields_NotTheText()
    {
        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        log.Write("info", "teams", "Alpha/DeveloperInes: fetched", [("team", "Alpha"), ("member", "Alpha/DeveloperInes")]);
        log.Write("info", "teams", "Beta/DeveloperInes: fetched", [("team", "Beta"), ("member", "DeveloperInes")]);
        log.Write("info", "teams", "a note that names DeveloperInes and Alpha in passing", [("team", "Gamma")]);
        log.Write("Alpha/DeveloperInes: an untagged line"); // the old lines: by their text, as before

        Assert.Equal(["Alpha/DeveloperInes: an untagged line", "Alpha/DeveloperInes: fetched"],
            Query(member: "Alpha/DeveloperInes").Select(r => r["text"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal("Beta/DeveloperInes: fetched", Assert.Single(Query(member: "Beta/DeveloperInes"))["text"]!.GetValue<string>());
        Assert.Equal("a note that names DeveloperInes and Alpha in passing", Assert.Single(Query(team: "Gamma"))["text"]!.GetValue<string>());
        Assert.Equal(2, Query(team: "Alpha").Count);
    }

    [Fact]
    public async Task TheTail_TakesTheTeamAndMemberFromTheFields()
    {
        var events = new Yawble.Core.Ui.AppEvents();
        using var tail = new LogTail(events, interval: TimeSpan.FromHours(1));
        var log = new DiagnosticLog(_db.Dir, "core", _clock) { Tail = tail };
        var got = new System.Collections.Concurrent.ConcurrentQueue<JsonObject>();
        using var sub = events.Subscribe(m => { got.Enqueue(m); return Task.CompletedTask; });
        tail.Start(new LogTailFilter(All: true, Member: "Beta/DeveloperInes"), null);

        log.Write("warning", "teams", "Alpha/DeveloperInes: fetch failed", [("member", "Alpha/DeveloperInes")]);
        log.Write("warning", "teams", "Beta/DeveloperInes: fetch failed", [("member", "Beta/DeveloperInes")]);
        log.Write("debug", "teams", "Beta/DeveloperInes: fetched", [("member", "Beta/DeveloperInes")]); // debug: not in an "all" tail

        // Every event published before now, once delivered (the mark comes after them).
        events.Publish("test.mark", "structured-log");
        await Wait.UntilAsync(() => Task.FromResult(got.Any(m => m["event"]?.GetValue<string>() == "test.mark")), "the events to be delivered");
        var entry = Assert.Single(got, m => m["event"]?.GetValue<string>() == LogTail.EntryEvent)["data"]!["entry"]!;
        Assert.Equal("Beta/DeveloperInes: fetch failed", entry["text"]!.GetValue<string>());
        Assert.Equal("Beta", entry["team"]!.GetValue<string>());
        Assert.Equal("warning", entry["level"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- Diag

    [Fact]
    public void Diag_WritesOnceToTheLog_NotAgainThroughTheTee_AndEchoesToStderr()
    {
        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        var raw = new StringWriter();
        using var tee = log.Tee(raw);
        var text = "logged once " + Guid.NewGuid().ToString("N");
        Diag.Attach(log, raw);
        try
        {
            Diag.Warning("teams", text, ("team", "Alpha"), ("attempt", 2));
        }
        finally
        {
            Diag.Attach(null, null);
        }

        var mine = Lines(_db.Dir).Where(l => l.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.Equal(["level=warning area=teams team=Alpha attempt=2"], mine.Select(l => l.Split('\t')[3]));
        Assert.Contains($"{text} team=Alpha attempt=2", raw.ToString());
    }

    [Fact]
    public void Diag_Capture_TakesTheEntriesOfItsOwnFlow()
    {
        var seen = new List<DiagEntry>();
        using (Diag.Capture(e => seen.Add(e)))
        {
            Diag.Debug("bus", "run 1 admitted", ("run", 1L));
            Diag.Write("WARN", "link", "ended");
        }
        Diag.Info("bus", "after the capture"); // not taken
        Assert.Equal([("debug", "bus", "run 1 admitted"), ("warning", "link", "ended")], seen.Select(e => (e.Level, e.Area, e.Text)));
        Assert.Equal("1", seen[0].Field("run"));
    }

    // ---------------------------------------------------------------- the noisy lines

    private static List<DiagEntry> Captured(List<DiagEntry> seen, string contains)
    {
        lock (seen) return [.. seen.Where(e => e.Text.Contains(contains, StringComparison.Ordinal))];
    }

    [Fact]
    public async Task TheStartUpFetchBack_WritesOneSummaryLine_AndEachCloneAtDebug()
    {
        var seen = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (seen) seen.Add(e); });
        using var f = new TeamsFixture();
        var (_, ada, ben) = f.Team();
        foreach (var m in new[] { ada, ben })
        {
            var checkout = await f.Repos.PrepareAsync(m, Ct);
            File.WriteAllText(Path.Combine(checkout.ClonePath, $"{m.Name}.txt"), m.Name);
            AgentGit.Ok(checkout, "add", ".");
            AgentGit.Ok(checkout, "commit", "-q", "-m", m.Name);
        }

        await f.Repos.FetchBackAllAsync([ada, ben], Ct);

        var summary = Assert.Single(Captured(seen, "start-up fetch-back"));
        Assert.Equal(LogLevels.Info, summary.Level);
        Assert.Equal("start-up fetch-back: fetched 2 clones, 0 behind", summary.Text);
        var fetched = Captured(seen, ": fetched members/");
        Assert.Equal(2, fetched.Count);
        Assert.All(fetched, e => Assert.Equal(LogLevels.Debug, e.Level));
        Assert.Contains(fetched, e => e.Field("member") == ada.Qualified && e.Field("team") == ada.Team);

        // A later fetch of a clone whose state did not change writes no line above debug.
        await f.Repos.FetchBackAsync(ada, Ct);
        Assert.Single(Captured(seen, "start-up fetch-back"));
        Assert.DoesNotContain(seen.ToArray(), e => e.Area == "teams" && e.Level != LogLevels.Debug && e.Text.Contains("level with", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheGrokKeepersRuns_AreDebug()
    {
        var seen = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (seen) seen.Add(e); });
        var home = GrokHome.InDataDir(Path.Combine(_db.Dir, "data")).EnsureCreated();
        FakeSecrets.WriteGrokAuth(home, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddMinutes(2)); // inside RefreshAhead
        await using var keeper = new GrokKeeper(home, new GrokKeeperOptions { GrokBinary = "fake-grok", RunGrok = _ => Task.FromResult("exited 0") });

        await keeper.RefreshAsync();

        var lines = Captured(seen, "grok keeper:");
        Assert.NotEmpty(lines);
        Assert.All(lines, e => Assert.Equal(LogLevels.Debug, e.Level));
    }

    [Fact]
    public async Task RunAdmitted_AndTheLeaseHeldAndReleased_AreDebug_TheLimitIsNot()
    {
        var seen = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (seen) seen.Add(e); });
        var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<RunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner();
        runner.On("Alpha/Ada", async (run, ct) =>
        {
            started.TrySetResult(run);
            await end.Task.WaitAsync(ct);
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(new BusStore(_db.ConnectionString, _clock), runner, new BusOptions
        {
            SafetyPoll = TimeSpan.FromMilliseconds(100),
            IdleOffer = false,
        });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "work", ct: Ct);
        var ada = await started.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        await Lease(ada, "acquire", "full test suite");
        await Lease(ada, "release");
        end.TrySetResult();

        var admitted = Assert.Single(Captured(seen, "admitted: Alpha/Ada"));
        Assert.Equal(LogLevels.Debug, admitted.Level);
        Assert.Equal(ada.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture), admitted.Field("run"));
        Assert.Equal(LogLevels.Debug, Assert.Single(Captured(seen, "lease: Alpha/Ada holds it")).Level);
        Assert.Equal(LogLevels.Debug, Assert.Single(Captured(seen, "lease: Alpha/Ada released it")).Level);
    }

    private static async Task Lease(RunContext run, string action, string? what = null)
    {
        var args = what is null ? new JsonObject { ["action"] = action } : new JsonObject { ["action"] = action, ["what"] = what };
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "lease", ["arguments"] = args },
        };
        await new McpBusServer(run).HandleAsync(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
    }

    [Fact]
    public void TheDailySweep_WritesOneDebugLine()
    {
        var seen = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (seen) seen.Add(e); });
        Directory.CreateDirectory(Path.Combine(_db.Dir, FileLogs.DiagnosticsFolder));
        var old = (Now - TimeSpan.FromDays(40)).ToString("O");
        File.WriteAllText(Path.Combine(_db.Dir, FileLogs.DiagnosticsFolder, "core-2026-10-15.log"), $"{old}\tcore\tone\n{old}\tcore\ttwo\n");
        File.WriteAllText(Path.Combine(_db.Dir, "cloud-link-audit.log"), $"{(Now - TimeSpan.FromDays(400)).ToString("O")}\tviewer@example.com\tteams.pause\tdone\n");

        Logs().Sweep();

        var line = Assert.Single(Captured(seen, "Logs: pruned"));
        Assert.Equal(LogLevels.Debug, line.Level);
        Assert.Contains("2 diagnostics entries", line.Text);
        Assert.Contains("1 cloudlink entry", line.Text);
        Assert.Equal("2", line.Field("diagnostics"));
    }

    [Fact]
    public void RoutineNetworkRefusals_AreCountedOnOneRowPerMemberAndHour_AcrossRuns()
    {

        var log = new GatewayLog(_db.ConnectionString, _clock);
        GatewayEvent Analytics(long run) => new(run, "alpha/Wren", _clock.GetUtcNow(), GatewayEventKinds.PathDenied, true, "chatgpt.yawble.internal",
            "POST", "/backend-api/codex/analytics-events/events?x=1", 403, "path not on the allowlist");
        GatewayEvent Mcp(long run) => new(run, "alpha/Wren", _clock.GetUtcNow(), GatewayEventKinds.Forward, false, "chatgpt.yawble.internal",
            "GET", "/backend-api/ps/mcp", 401);

        for (var run = 1; run <= 6; run++)
        {
            log.Write(Analytics(run));
            log.Write(Mcp(run));
            _clock.Advance(TimeSpan.FromMinutes(9));
        }
        log.Write(new GatewayEvent(6, "alpha/Wren", _clock.GetUtcNow(), GatewayEventKinds.PathDenied, true, "chatgpt.yawble.internal", "POST", "/v1/files", 403));

        var rows = log.Read();
        Assert.Equal(6, Assert.Single(rows, e => e.Target!.Contains("analytics-events", StringComparison.Ordinal)).Count);
        Assert.Equal(6, Assert.Single(rows, e => e.Target == "/backend-api/ps/mcp").Count);
        Assert.Equal(1, Assert.Single(rows, e => e.Target == "/v1/files").Count); // any other refusal: its own row, as before

        _clock.Advance(GatewayLog.RoutineWindow); // a new hour: a new row
        log.Write(Analytics(7));
        Assert.Equal(2, log.Read().Count(e => e.Target!.Contains("analytics-events", StringComparison.Ordinal)));
    }
}
