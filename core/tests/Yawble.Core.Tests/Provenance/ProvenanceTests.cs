using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Triggers;
using Xunit;

namespace Yawble.Core.Tests.Provenance;

/// <summary>Who started each piece of work, how and when, and what the model requests used (0015_provenance.sql).</summary>
public class ProvenanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ messages: who, how, and the chain's origin

    [Fact]
    public async Task APersonsTell_CarriesTheCallersWayInAndAccount_AndTheWorkItCausesCarriesItAsOrigin()
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions());
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);

        long root;
        using (Actor.Use(Actor.CloudViewer("pat@example.test")))
            root = (await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct)).Seq;

        using var store = db.OpenStore();
        var told = store.ProvenanceOf(root)!;
        Assert.Equal(("cloud", "pat@example.test"), (told.Via, told.User));
        Assert.Equal(root, told.OriginSeq);
        Assert.Equal(("cloud", "pat@example.test"), (told.OriginVia, told.OriginUser));

        // Ada reports on it, and the app declares it done: neither is the person, both carry the person as origin.
        var progress = store.Append(new NewMessage(MessageTypes.Progress, "{\"status\":\"half\"}", "Alpha/Ada", root));
        var declared = store.Append(new NewMessage(MessageTypes.WorkflowCompleted, "{}", MessageTypes.HostSource, progress.Seq));
        var p = store.ProvenanceOf(progress.Seq)!;
        Assert.Equal(("agent", (string?)null), (p.Via, p.User));
        Assert.Equal((root, "cloud", "pat@example.test"), (p.OriginSeq, p.OriginVia, p.OriginUser));
        var d = store.ProvenanceOf(declared.Seq)!;
        Assert.Equal("host", d.Via);
        Assert.Equal((root, "pat@example.test"), (d.OriginSeq, d.OriginUser));
    }

    [Fact]
    public async Task WithNoCallerKnown_APersonsMessageIsTheWindowsAndThisComputersAccount()
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions());
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        var m = await engine.PersonTellAsync("Alpha", "Ada", "hello", ct: Ct);
        using var store = db.OpenStore();
        var p = store.ProvenanceOf(m.Seq)!;
        Assert.Equal(("app", Actor.LocalUser), (p.Via, p.User));
    }

    [Fact]
    public async Task ATriggersFire_CarriesWhoeverSavedIt()
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions());
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        TriggerRow t;
        using (Actor.Use(Actor.CloudViewer("lee@example.test")))
            t = await engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "nightly", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null),
                "Check the build.", true, true), Ct);
        await engine.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);

        using var store = db.OpenStore();
        var fired = store.Trigger(t.Id)!.LastSeq!.Value;
        var p = store.ProvenanceOf(fired)!;
        Assert.Equal(("trigger", "lee@example.test"), (p.Via, p.User));
        Assert.Equal((fired, "trigger", "lee@example.test"), (p.OriginSeq, p.OriginVia, p.OriginUser));
    }

    // ------------------------------------------------------------------ the audit log

    [Theory]
    [InlineData("board.list", true)]
    [InlineData("teams.agents", true)]
    [InlineData("terminal.input", true)]
    [InlineData("policy.previewLoosen", true)]
    [InlineData("git.status", true)]
    [InlineData("board.move", false)]
    [InlineData("credentials.setAnthropicApiKey", false)]
    [InlineData("teams.create", false)]
    [InlineData("concierge.send", false)]
    public void OnlyCallsThatChangeSomethingAreAudited(string method, bool readOnly) =>
        Assert.Equal(readOnly, AuditLog.IsReadOnly(method));

    [Fact]
    public void TheAuditKeepsWhatACallWasAbout_NeverItsSecretsOrText()
    {
        var p = new JsonObject { ["team"] = "web", ["member"] = "DeveloperPriya", ["key"] = "sk-ant-secret", ["text"] = "my message", ["token"] = "t0k" };
        Assert.Equal("team=web member=DeveloperPriya", AuditLog.Target(p));
        Assert.Null(AuditLog.Target(new JsonObject { ["key"] = "sk-ant-secret" }));
    }

    [Fact]
    public void ALongTargetIsCutAt80_EndingInARealEllipsis()
    {
        var target = AuditLog.Target(new JsonObject { ["name"] = new string('n', 81) });
        Assert.Equal("name=" + new string('n', 80) + "\u2026", target);
        Assert.EndsWith("\u2026", target, StringComparison.Ordinal);
        Assert.Equal("name=" + new string('n', 80), AuditLog.Target(new JsonObject { ["name"] = new string('n', 80) }));
    }

    [Fact]
    public void TheAuditRecordsWhoHowAndTheOutcome_AndSkipsReads()
    {
        using var db = new TempDb();
        var audit = new AuditLog(db.ConnectionString);
        audit.Record(Actor.CloudViewer("pat@example.test"), "board.move", new JsonObject { ["id"] = 4, ["lane"] = "done" }, "done");
        audit.Record(Actor.Window, "board.list", null, "done");
        audit.Record(Actor.Window, "teams.create", new JsonObject { ["name"] = "web" }, "refused: exists: a team named web exists");
        var rows = audit.Recent();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("app", "teams.create", "name=web"), (rows[0].Via, rows[0].Method, rows[0].Target));
        Assert.StartsWith("refused", rows[0].Outcome);
        Assert.Equal(("cloud", "pat@example.test", "id=4 lane=done", "done"), (rows[1].Via, rows[1].User, rows[1].Target, rows[1].Outcome));
    }

    [Fact]
    public void AuditRowsCarryTheClientName()
    {
        using var db = new TempDb();
        var audit = new AuditLog(db.ConnectionString);
        audit.Record(Actor.ExternalClient("claude-code"), "board.move", new JsonObject { ["id"] = 4 }, "done");
        audit.Record(Actor.Window, "teams.create", new JsonObject { ["name"] = "web" }, "done");
        var rows = audit.Recent();
        Assert.Equal((Actor.External, Actor.LocalUser, "claude-code"), (rows[1].Via, rows[1].User, rows[1].Client));
        Assert.Null(rows[0].Client);
    }

    [Fact]
    public void HostTaskWritesStayRefusedForExternal()
    {
        Assert.NotNull(Yawble.Core.Tasks.HostTaskRpc.WriteRefusal(Actor.ExternalClient("claude-code")));
        Assert.NotNull(Yawble.Core.Tasks.HostTaskRpc.WriteRefusal(Actor.ExternalClient("claude-code") with { ApprovedBy = "pat" }));
    }

    // ------------------------------------------------------------------ usage, as the providers report it

    [Fact]
    public void AnthropicStream_InputAndCacheFromTheStart_OutputFromTheLastDelta()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":1200,"cache_creation_input_tokens":300,"cache_read_input_tokens":5000,"output_tokens":1}}}

            event: content_block_delta
            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"hi"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var u = UsageMeter.Parse(sse);
        Assert.Equal(("claude-opus-5-5", 1200L, 42L, 5000L, 300L), (u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens));
        Assert.Contains("cache_creation_input_tokens", u.Usage);
    }

    [Fact]
    public void OpenAiResponsesStream_TotalsFromResponseCompleted()
    {
        const string sse = """
            event: response.created
            data: {"type":"response.created","response":{"id":"r1","model":"gpt-5.5-codex","usage":null}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"r1","model":"gpt-5.5-codex","usage":{"input_tokens":900,"input_tokens_details":{"cached_tokens":400},"output_tokens":120,"total_tokens":1020}}}

            """;
        var u = UsageMeter.Parse(sse);
        Assert.Equal(("gpt-5.5-codex", 900L, 120L, 400L, (long?)null), (u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens));
    }

    [Fact]
    public void ChatCompletionsJson_PromptAndCompletionTokens()
    {
        var u = UsageMeter.Parse("""{"id":"c1","model":"grok-5","choices":[],"usage":{"prompt_tokens":50,"completion_tokens":8,"prompt_tokens_details":{"cached_tokens":10}}}""");
        Assert.Equal(("grok-5", 50L, 8L, 10L), (u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens));
    }

    [Fact]
    public void ACompressedBody_IsReadToo_AndOneWithoutUsageGivesNoFigures()
    {
        var json = Encoding.UTF8.GetBytes("""{"model":"m","usage":{"input_tokens":5,"output_tokens":6}}""");
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(json);
        var meter = new UsageMeter("gzip");
        meter.Add(packed.ToArray());
        Assert.Equal((5L, 6L), (meter.Read().InputTokens, meter.Read().OutputTokens));

        var none = UsageMeter.Parse("""{"ok":true}""");
        Assert.Null(none.InputTokens);
        Assert.Null(none.Usage);
    }

    [Fact]
    public void ATooLargeResponse_IsRecordedAsNotRead()
    {
        var meter = new UsageMeter(null, cap: 10);
        meter.Add(new byte[20]);
        var u = meter.Read();
        Assert.Null(u.InputTokens);
        Assert.Contains("larger", u.Usage);
    }

    [Fact]
    public async Task TheGatewayFilesEachModelRequestsUsage_UnderTheRunsKey()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        await using var gateway = f.StartRun(21);
        using var http = ProxyClient.For(gateway);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Model}/v1/messages?beta=true")
        {
            Content = new StringContent("{\"model\":\"fake\",\"stream\":true}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        f.Upstream.ReleaseStream();
        using var response = await http.SendAsync(request, Ct);
        Assert.Contains("message_stop", await response.Content.ReadAsStringAsync(Ct));

        await Wait.UntilAsync(() => UsageRows(f.Db.ConnectionString).Count > 0, "the usage row");
        var row = Assert.Single(UsageRows(f.Db.ConnectionString));
        Assert.Equal(("run-21", 21L, "Alpha/m21", Vhosts.Model, "/v1/messages", 200L), (row.RunKey, row.RunNumber, row.Member, row.Vhost, row.Path, row.Status));
        Assert.Equal(("fake-model", 12L, 7L, 3L), (row.Model, row.In, row.Out, row.CacheRead));
    }

    private sealed record UsageRow(string? RunKey, long? RunNumber, string? Member, string Vhost, string Path, long Status, string? Model, long? In, long? Out, long? CacheRead);

    private static List<UsageRow> UsageRows(string cs)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT run_key, run_number, member, vhost, path, status, model, input_tokens, output_tokens, cache_read_tokens FROM usage_events ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var rows = new List<UsageRow>();
        static long? L(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            rows.Add(new UsageRow(S(r, 0), L(r, 1), S(r, 2), r.GetString(3), r.GetString(4), r.GetInt64(5), S(r, 6), L(r, 7), L(r, 8), L(r, 9)));
        return rows;
    }
}
