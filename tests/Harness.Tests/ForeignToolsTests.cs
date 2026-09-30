using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Kanban;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE EXTRACTORS AND THE JUDGEMENT, against real transcripts. Each fixture under
/// <c>Fixtures/ForeignTools</c> or <c>Fixtures/LiveView</c> is a real run on this Host, redacted; see
/// that folder's README for where each came from.
/// </summary>
public sealed class ForeignToolsTests
{
    internal static string Fixture(string folder, string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, file));

    /// <summary>A transcript and the files its format keeps beside it, read as the check reads them.</summary>
    private static TranscriptToolUse Extract(string format, string folder, string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, file);
        var beside = TranscriptTools.Beside(format)
            .Where(name => File.Exists(Path.Combine(Path.GetDirectoryName(path)!, name)))
            .ToDictionary(name => name, name => File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, name)));

        return TranscriptTools.Extract(format, File.ReadAllText(path), beside);
    }

    private static IReadOnlyList<string> Names(IEnumerable<TranscriptTool> tools) =>
        [.. tools.Select(t => t.ToString()).Order(StringComparer.Ordinal)];

    /// <summary>The built-in presets, and the isolation model's answer for them: the real wiring.</summary>
    private static readonly AgentCatalog Catalog = new(AgentCatalogFile.BuiltIns());

    internal static ToolAllowance Allowance(string preset) => Catalog.Allowance(preset)!;

    /// <summary>The same preset with its declaration taken away: what the model says of an undeclared one.</summary>
    internal static ToolAllowance NotVerified(string preset) =>
        AgentIsolationPolicy.For(Catalog.Definition(preset)! with { Isolation = null })!;

    [Fact]
    public void The_built_in_presets_are_checked_by_the_isolation_models_states()
    {
        Assert.Equal(IsolationState.Isolated, Allowance("claude-headless").State);
        Assert.Equal(IsolationState.NotVerified, NotVerified("claude-headless").State);
        Assert.Null(NotVerified("claude-headless").AllowedTools);

        // The Concierge and a program are never checked, so never flagged.
        Assert.Equal(IsolationState.Concierge, Allowance("claude").State);
        Assert.False(Allowance("claude").Checked);
        Assert.Equal(IsolationState.NotAModel, Allowance("echo").State);
        Assert.False(Allowance("echo").Checked);
    }

    [Fact]
    public void Claude_names_the_whole_offer_and_every_call()
    {
        var use = Extract(LiveView.ClaudeJsonl, "ForeignTools", "claude-offers-connectors.jsonl");

        Assert.True(use.OfferedComplete);
        var offered = Names(use.Offered);
        Assert.Contains("claude_ai_Gmail/send_message", offered);
        Assert.Contains("claude_ai_Claude_Docs/create", offered);
        Assert.Contains("claude.ai Google Calendar", offered);
        Assert.Contains("claude.ai Google Drive", offered);
        Assert.Contains("harness/tell", offered);
        Assert.Contains("Bash", offered);

        Assert.Equal(["Bash", "ToolSearch", "harness/handback", "harness/skills_get"], Names(use.Called));
    }

    [Fact]
    public void A_member_offered_account_connectors_is_foreign_whether_or_not_its_preset_is_verified()
    {
        var use = Extract(LiveView.ClaudeJsonl, "ForeignTools", "claude-offers-connectors.jsonl");

        foreach (var allowance in new[] { Allowance("claude-headless"), NotVerified("claude-headless") })
        {
            var finding = ForeignToolsJudge.Judge(use, allowance);

            Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
            Assert.Empty(finding.Called);
            Assert.Contains(finding.Offered, t => t.ToString() == "claude_ai_Gmail/send_message");
            Assert.Contains(finding.Offered, t => t.ToString() == "claude.ai Google Calendar");
            Assert.DoesNotContain(finding.Offered, t => t.Server == "harness");
            Assert.StartsWith("DeveloperTobias was offered tools the platform did not give it, and called none: ",
                ForeignToolsJudge.Sentence("DeveloperTobias", finding, allowance), StringComparison.Ordinal);
        }

        // Not verified: only servers are judged, and the finding says so.
        var unverified = ForeignToolsJudge.Judge(use, NotVerified("claude-headless"));
        Assert.DoesNotContain(unverified.Offered, t => t.Server is null);
        Assert.Contains("is not verified", ForeignToolsJudge.Sentence("Dev", unverified, NotVerified("claude-headless")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_is_named_apart_from_an_offer()
    {
        var use = Extract(LiveView.ClaudeJsonl, "ForeignTools", "claude-calls-connectors.jsonl");
        var allowance = Allowance("claude-headless");
        var finding = ForeignToolsJudge.Judge(use, allowance);

        Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
        Assert.Equal(
            ["Artifact", "claude_ai_Claude_Docs/batch", "claude_ai_Claude_Docs/create", "claude_ai_Claude_Docs/guide", "claude_ai_Claude_Docs/update"],
            Names(finding.Called));
        Assert.DoesNotContain(finding.Offered, t => finding.Called.Contains(t));
        Assert.Contains(finding.Offered, t => t.ToString() == "claude_ai_Gmail/send_message");

        // Artifact is one of the Concierge's own tools, not in the headless preset's list: a local
        // tool outside the allowed list is foreign too.
        Assert.Contains(finding.Called, t => t.ToString() == "Artifact");
        Assert.StartsWith("Dev CALLED tools the platform did not give it: ", ForeignToolsJudge.Sentence("Dev", finding, allowance), StringComparison.Ordinal);
    }

    [Fact]
    public void An_isolated_claude_member_is_clean_under_its_preset_and_never_clean_when_not_verified()
    {
        var use = Extract(LiveView.ClaudeJsonl, "ForeignTools", "claude-isolated-member.jsonl");

        Assert.True(use.OfferedComplete);
        Assert.Equal(["Bash", "ToolSearch", "harness/progress"], Names(use.Called));
        Assert.Equal(ForeignToolsStatus.Clean, ForeignToolsJudge.Judge(use, Allowance("claude-headless")).Status);

        var unverified = ForeignToolsJudge.Judge(use, NotVerified("claude-headless"));
        Assert.Equal(ForeignToolsStatus.NotVerified, unverified.Status);
        Assert.Equal("notVerified", unverified.Word);
        Assert.Contains("not verified", ForeignToolsJudge.Sentence("Dev", unverified, NotVerified("claude-headless")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_that_only_drops_mcp_servers_still_offers_claude_tools_outside_the_preset()
    {
        // --strict-mcp-config alone: no foreign server, but the CLI's own tools that reach the
        // account and other sessions are offered, and the preset does not list them.
        var use = Extract(LiveView.ClaudeJsonl, "ForeignTools", "claude-isolated.jsonl");
        Assert.DoesNotContain(use.Offered, t => t.Server is { } server && server != "harness");

        var finding = ForeignToolsJudge.Judge(use, Allowance("claude-headless"));
        Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
        Assert.Contains(finding.Offered, t => t.ToString() == "RemoteTrigger");
        Assert.Contains(finding.Offered, t => t.ToString() == "SendMessage");
        Assert.All(finding.Offered, t => Assert.Null(t.Server));

        // Not verified, its own tools are not judged: nothing foreign, and still not clean.
        Assert.Equal(ForeignToolsStatus.NotVerified, ForeignToolsJudge.Judge(use, NotVerified("claude-headless")).Status);
    }

    [Fact]
    public void Copilot_names_its_whole_offer_from_its_usage_checkpoint()
    {
        var member = Extract(LiveView.CopilotEvents, "ForeignTools", "copilot-isolated-member.jsonl");
        Assert.True(member.OfferedComplete);
        Assert.Contains(member.Offered, t => t.ToString() == "harness/tell");
        Assert.Contains(member.Offered, t => t.ToString() == "bash");
        Assert.DoesNotContain(member.Offered, t => t.Server is { } server && server != "harness");
        Assert.Equal(ForeignToolsStatus.Clean, ForeignToolsJudge.Judge(member, Allowance("copilot-headless")).Status);

        // The built-in GitHub server: a server name with `-` in it, split by the server the run named.
        var github = Extract(LiveView.CopilotEvents, "LiveView", "copilot-events.jsonl");
        Assert.True(github.OfferedComplete);
        Assert.Contains(github.Offered, t => t.ToString() == "github-mcp-server/search_code");
        Assert.Equal(["bash", "harness/team_current", "view"], Names(github.Called));
        var finding = ForeignToolsJudge.Judge(github, Allowance("copilot-headless"));
        Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
        Assert.Contains(finding.Offered, t => t.ToString() == "github-mcp-server/search_code");
        Assert.DoesNotContain(finding.Offered, t => t.Server == "harness");
    }

    [Fact]
    public void Copilot_offers_different_own_tools_on_another_model_and_the_preset_list_judges_them()
    {
        // mai-code-1.1-flash is offered create, edit and grep where gpt-6-luna gets apply_patch; the
        // copilot preset lists the latter, so a run on that model is offered tools it does not list.
        var use = Extract(LiveView.CopilotEvents, "ForeignTools", "copilot-no-builtin-servers.jsonl");
        Assert.True(use.OfferedComplete);
        Assert.DoesNotContain(use.Offered, t => t.Server is not null);

        var finding = ForeignToolsJudge.Judge(use, Allowance("copilot-headless"));
        Assert.Equal(["create", "edit", "grep"], Names(finding.Offered));
        Assert.Equal(ForeignToolsStatus.NotVerified, ForeignToolsJudge.Judge(use, NotVerified("copilot-headless")).Status);
    }

    [Fact]
    public void A_copilot_run_stopped_before_its_checkpoint_is_not_measured()
    {
        var use = Extract(LiveView.CopilotEvents, "ForeignTools", "copilot-stopped-before-checkpoint.jsonl");

        Assert.False(use.OfferedComplete);
        Assert.Equal(["view"], Names(use.Called));
        var finding = ForeignToolsJudge.Judge(use, Allowance("copilot-headless"));
        Assert.Equal(ForeignToolsStatus.NotMeasured, finding.Status);
        Assert.Contains("no usage checkpoint", finding.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Grok_names_its_whole_offer_from_the_files_beside_its_transcript()
    {
        var member = Extract(LiveView.GrokUpdates, "ForeignTools", Path.Combine("grok-member", "updates.jsonl"));
        Assert.True(member.OfferedComplete);
        Assert.Contains(member.Offered, t => t.ToString() == "run_terminal_command");
        Assert.Contains(member.Offered, t => t.ToString() == "harness/progress");
        Assert.Contains(member.Called, t => t.ToString() == "web_search");
        Assert.Contains(member.Called, t => t.ToString() == "harness/handback");
        Assert.Equal(ForeignToolsStatus.Clean, ForeignToolsJudge.Judge(member, Allowance("grok-headless")).Status);

        var stub = Extract(LiveView.GrokUpdates, "ForeignTools", Path.Combine("grok-calls-stub-server", "updates.jsonl"));
        Assert.True(stub.OfferedComplete);
        Assert.Contains(stub.Offered, t => t.ToString() == "stubfs/echo");
        Assert.Equal(["search_tool", "stubfs/echo"], Names(stub.Called));
        var finding = ForeignToolsJudge.Judge(stub, Allowance("grok-headless"));
        Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
        Assert.Equal(["stubfs/echo"], Names(finding.Called));
        Assert.Empty(finding.Offered);
    }

    [Fact]
    public void A_grok_transcript_without_the_files_beside_it_is_not_measured()
    {
        var harness = Extract(LiveView.GrokUpdates, "LiveView", "grok-updates.jsonl");

        Assert.False(harness.OfferedComplete);
        Assert.Contains(harness.Called, t => t.ToString() == "harness/status");
        Assert.Equal(ForeignToolsStatus.NotMeasured, ForeignToolsJudge.Judge(harness, Allowance("grok-headless")).Status);
    }

    [Fact]
    public void Codex_names_every_call_and_no_offer()
    {
        var stub = Extract(LiveView.CodexRollout, "ForeignTools", "codex-calls-stub-server.jsonl");
        Assert.False(stub.OfferedComplete);
        Assert.Empty(stub.Offered);
        Assert.Equal(["exec", "stubfs/echo"], Names(stub.Called));
        Assert.Equal(["stubfs/echo"], Names(ForeignToolsJudge.Judge(stub, Allowance("codex-headless")).Called));

        var harness = Extract(LiveView.CodexRollout, "LiveView", "codex-rollout.jsonl");
        Assert.Equal(["exec", "harness/team_current"], Names(harness.Called));
        var finding = ForeignToolsJudge.Judge(harness, Allowance("codex-headless"));
        Assert.Equal(ForeignToolsStatus.NotMeasured, finding.Status);
        Assert.Contains("lists no offer", finding.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_format_nothing_reads_is_not_measured()
    {
        var use = TranscriptTools.Extract("some-new-cli", Fixture("ForeignTools", "claude-offers-connectors.jsonl"));

        Assert.Empty(use.Offered);
        Assert.Empty(use.Called);
        var finding = ForeignToolsJudge.Judge(use, Allowance("claude-headless"));
        Assert.Equal(ForeignToolsStatus.NotMeasured, finding.Status);
        Assert.Contains("some-new-cli", finding.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_mark_only_rises()
    {
        Assert.Equal(KanbanForeignTools.Called, KanbanForeignTools.Worse(KanbanForeignTools.Called, KanbanForeignTools.Offered));
        Assert.Equal(KanbanForeignTools.Called, KanbanForeignTools.Worse(KanbanForeignTools.Offered, KanbanForeignTools.Called));
        Assert.Equal(KanbanForeignTools.Offered, KanbanForeignTools.Worse(KanbanForeignTools.NotMeasured, KanbanForeignTools.Offered));
        Assert.Equal(KanbanForeignTools.NotMeasured, KanbanForeignTools.Worse(null, KanbanForeignTools.NotMeasured));
        Assert.Equal(KanbanForeignTools.NotMeasured, KanbanForeignTools.Worse(KanbanForeignTools.NotMeasured, null));
        Assert.Equal(KanbanForeignTools.NotMeasured, KanbanForeignTools.Worse(KanbanForeignTools.NotVerified, KanbanForeignTools.NotMeasured));
        Assert.Equal(KanbanForeignTools.NotVerified, KanbanForeignTools.Worse(null, KanbanForeignTools.NotVerified));
    }
}

/// <summary>
/// THE PER-RUN CHECK ON THE REAL HOST. A scripted member run hands back a real, redacted transcript,
/// as a real run's terminal row names its own; the platform reads it and writes the team log row, the
/// tenant row and the card mark - or, for a clean run, nothing.
/// </summary>
public sealed class ForeignToolsCheckTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-foreign-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, string, string, string?, string[], string[]> Runs => new()
    {
        // preset, format, folder, file, the card's mark (null: clean), called, offered (a sample)
        { "claude-headless", LiveView.ClaudeJsonl, "ForeignTools", "claude-offers-connectors.jsonl", KanbanForeignTools.Offered, [], ["claude_ai_Gmail/send_message", "claude.ai Google Drive"] },
        { "claude-headless", LiveView.ClaudeJsonl, "ForeignTools", "claude-calls-connectors.jsonl", KanbanForeignTools.Called, ["claude_ai_Claude_Docs/create"], ["claude_ai_Gmail/forward"] },
        { "claude-headless", LiveView.ClaudeJsonl, "ForeignTools", "claude-isolated-member.jsonl", null, [], [] },
        { "copilot-headless", LiveView.CopilotEvents, "LiveView", "copilot-events.jsonl", KanbanForeignTools.Offered, [], ["github-mcp-server/search_code"] },
        { "copilot-headless", LiveView.CopilotEvents, "ForeignTools", "copilot-isolated-member.jsonl", null, [], [] },
        { "copilot-headless", LiveView.CopilotEvents, "ForeignTools", "copilot-stopped-before-checkpoint.jsonl", KanbanForeignTools.NotMeasured, [], [] },
        { "grok-headless", LiveView.GrokUpdates, "ForeignTools", "grok-calls-stub-server/updates.jsonl", KanbanForeignTools.Called, ["stubfs/echo"], [] },
        { "grok-headless", LiveView.GrokUpdates, "ForeignTools", "grok-member/updates.jsonl", null, [], [] },
        { "grok-headless", LiveView.GrokUpdates, "LiveView", "grok-updates.jsonl", KanbanForeignTools.NotMeasured, [], [] },
        { "codex-headless", LiveView.CodexRollout, "ForeignTools", "codex-calls-stub-server.jsonl", KanbanForeignTools.Called, ["stubfs/echo"], [] },
        { "codex-headless", LiveView.CodexRollout, "LiveView", "codex-rollout.jsonl", KanbanForeignTools.NotMeasured, [], [] },
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_member_run_is_checked_and_what_is_not_clean_lands_on_the_log_the_tenant_log_and_the_card(
        string preset, string format, string folder, string file, string? mark, string[] called, string[] offered)
    {
        // THE REAL WIRING: no seam replaced; the member's built-in preset and the catalog's
        // isolation model decide what is foreign.
        await StartAsync(null, preset);
        var transcript = Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, file);
        var person = await PersonAsync();

        var told = await person.PostAsJsonAsync($"/api/teams/{_team}/containers/Dev/tell", new { instruction = "do the work" }, Ct);
        told.EnsureSuccessStatusCode();
        var terminal = await TerminalAsync(transcript, format);

        if (mark is null)
        {
            // Clean: give the watch time to pass the row, and see that it wrote nothing.
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.Empty(await DevRowsAsync());
            Assert.Empty(await TenantRowsAsync());
            Assert.Equal(JsonValueKind.Null, (await CardAsync(person, terminal)).GetProperty("foreignTools").ValueKind);
            return;
        }

        var row = await EventuallyAsync(async () => (await DevRowsAsync())
            .FirstOrDefault(r => JsonDocument.Parse(r.Payload).RootElement.GetProperty(PayloadFields.Run).GetInt64() == terminal.Seq));
        var payload = JsonDocument.Parse(row.Payload).RootElement;

        // The team log row: the member's, inside the run's workflow, naming the tools.
        Assert.Equal(terminal.Source, row.Source);
        Assert.Equal(terminal.CorrelationId, row.CorrelationId);
        Assert.Equal(terminal.Seq, payload.GetProperty(PayloadFields.Run).GetInt64());
        Assert.Equal(format, payload.GetProperty(PayloadFields.AgentTranscriptFormat).GetString());
        var calledNames = payload.GetProperty(PayloadFields.ForeignCalled).EnumerateArray().Select(e => e.GetString()).ToList();
        var offeredNames = payload.GetProperty(PayloadFields.ForeignOffered).EnumerateArray().Select(e => e.GetString()).ToList();
        foreach (var name in called) Assert.Contains(name, calledNames);
        foreach (var name in offered) Assert.Contains(name, offeredNames);
        Assert.Contains(payload.GetProperty(PayloadFields.Text).GetString()!, Harness.Containers.MessageText.Of(row), StringComparison.Ordinal);

        var tenant = await TenantRowsAsync();
        if (mark == KanbanForeignTools.NotMeasured)
        {
            Assert.Equal("notMeasured", payload.GetProperty(PayloadFields.ForeignToolsStatus).GetString());
            Assert.True(payload.GetProperty(PayloadFields.PresetVerified).GetBoolean());
            Assert.Empty(calledNames);
            Assert.Empty(offeredNames);
            Assert.Contains("not measured", payload.GetProperty(PayloadFields.Text).GetString(), StringComparison.Ordinal);

            // Not a finding about a person's accounts: the team log and the card say it, the tenant log does not.
            Assert.Empty(tenant);
        }
        else
        {
            Assert.Equal("foreign", payload.GetProperty(PayloadFields.ForeignToolsStatus).GetString());
            Assert.Equal(preset, payload.GetProperty(PayloadFields.Agent).GetString());

            // The tenant row: the same finding, about the member, written just after the log row.
            tenant = await EventuallyAsync(async () => await TenantRowsAsync() is { Count: > 0 } rows ? rows : null);

            // Dev may be woken again after its first run; every run is checked, each with its own row.
            var mine = tenant.Single(e => JsonDocument.Parse(e.Detail!).RootElement.GetProperty("run").GetString() == terminal.Seq.ToString());
            Assert.Equal(new ContainerId(_team, "Dev").ToString(), mine.Subject);
            var detail = JsonDocument.Parse(mine.Detail!).RootElement;
            foreach (var name in called) Assert.Contains(detail.GetProperty("called").EnumerateArray(), e => e.GetString() == name);
            foreach (var name in offered) Assert.Contains(detail.GetProperty("offered").EnumerateArray(), e => e.GetString() == name);
        }

        // The card: marked, a call above an offer, and the finding on its trail.
        var card = await CardAsync(person, terminal);
        Assert.Equal(mark, card.GetProperty("foreignTools").GetString());
        var detailJson = await person.GetStringAsync(
            $"/api/teams/{_team}/kanban/cards/{Uri.EscapeDataString(card.GetProperty("id").GetString()!)}", Ct);
        Assert.Contains(JsonDocument.Parse(detailJson).RootElement.GetProperty("trail").EnumerateArray(),
            t => t.GetProperty("type").GetString() == MessageTypes.AgentForeignTools);
    }

    [Fact]
    public async Task A_run_whose_preset_is_not_verified_says_so_and_is_never_reported_clean()
    {
        // The isolation model's own answer for the claude preset with its declaration taken away.
        await StartAsync(new PresetAllowedTools(ForeignToolsTests.NotVerified), "claude-headless");
        var transcript = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ForeignTools", "claude-isolated-member.jsonl");
        var person = await PersonAsync();

        (await person.PostAsJsonAsync($"/api/teams/{_team}/containers/Dev/tell", new { instruction = "do the work" }, Ct)).EnsureSuccessStatusCode();
        var terminal = await TerminalAsync(transcript, LiveView.ClaudeJsonl);

        var row = await EventuallyAsync(async () => (await DevRowsAsync()).FirstOrDefault());
        var payload = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal("notVerified", payload.GetProperty(PayloadFields.ForeignToolsStatus).GetString());
        Assert.False(payload.GetProperty(PayloadFields.PresetVerified).GetBoolean());
        Assert.Contains("not verified", payload.GetProperty(PayloadFields.Text).GetString(), StringComparison.Ordinal);
        Assert.Equal(KanbanForeignTools.NotVerified, (await CardAsync(person, terminal)).GetProperty("foreignTools").GetString());
        Assert.Empty(await TenantRowsAsync());
    }

    [Fact]
    public async Task A_member_whose_preset_runs_no_model_is_never_flagged()
    {
        // THE REAL WIRING: a member on `echo`, whose allowance is NotAModel, handing back a transcript
        // that called the person's connectors, is not checked.
        await StartAsync(null, "claude-headless", otherMember: "echo");
        var services = _factory.Services;
        var transcript = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ForeignTools", "claude-calls-connectors.jsonl");
        var row = await services.GetRequiredService<IMessageLog>().AppendAsync(new NewMessage(
            MessageTypes.Completed,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [PayloadFields.AgentTranscript] = transcript,
                [PayloadFields.AgentTranscriptFormat] = LiveView.ClaudeJsonl,
            }),
            new ContainerId(_team, "Con").ToString()), Ct);

        Assert.Equal(IsolationState.NotAModel, services.GetRequiredService<AgentCatalog>().Allowance("echo")!.State);
        Assert.Null(await services.GetRequiredService<ForeignToolsCheck>().CheckAsync(row, Ct));
        Assert.DoesNotContain(await RowsAsync(MessageTypes.AgentForeignTools), r => r.Source == row.Source);
    }

    [Fact]
    public async Task The_concierge_is_never_checked()
    {
        await StartAsync(null, "claude-headless");
        var services = _factory.Services;
        var log = services.GetRequiredService<IMessageLog>();

        // THE REAL WIRING. The Concierge presets' allowance is Concierge, and the Concierge is not a
        // team member: its own session, which called the person's connectors, on a terminal row whose
        // source is not a team member - the only shape a non-member's row could take - is not checked.
        Assert.Equal(IsolationState.Concierge, services.GetRequiredService<AgentCatalog>().Allowance("claude")!.State);
        var transcript = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ForeignTools", "claude-calls-connectors.jsonl");
        var row = await log.AppendAsync(new NewMessage(
            MessageTypes.Completed,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [PayloadFields.AgentTranscript] = transcript,
                [PayloadFields.AgentTranscriptFormat] = LiveView.ClaudeJsonl,
            }),
            new ContainerId(_team, "Concierge").ToString()), Ct);

        Assert.Null(await services.GetRequiredService<ForeignToolsCheck>().CheckAsync(row, Ct));

        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.DoesNotContain(await RowsAsync(MessageTypes.AgentForeignTools), r => r.Source == row.Source);
        Assert.DoesNotContain(
            (await services.GetRequiredService<ITenantLog>().ReadAsync(take: 500, ct: Ct)).Events,
            e => e.Action == TenantActions.AgentForeignTools && e.Subject == row.Source);
    }

    // --- host -------------------------------------------------------------------------------------

    private string? _transcript;
    private string? _format;

    /// <summary>
    /// A Host with a team whose member Dev launches as <paramref name="preset"/>, and, when
    /// <paramref name="otherMember"/> is given, a member Con launched as that preset. A null
    /// <paramref name="allowed"/> keeps Program.cs's own wiring.
    /// </summary>
    private async Task StartAsync(PresetAllowedTools? allowed, string preset, string? otherMember = null)
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);

        var agent = new Scripted(() => (_transcript, _format));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(agent);
                if (allowed is not null) services.AddSingleton(allowed);
            }));

        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        _team = (await registry.CreateAsync("Alpha", preset, memberAgent: preset, ct: Ct)).Id;
        await registry.AddContainerAsync(_team, "Dev", preset, "", [], permits: new HashSet<string>(Permits.All), ct: Ct);
        if (otherMember is not null) await registry.AddContainerAsync(_team, "Con", otherMember, "", [], ct: Ct);
        await services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);
    }

    private async Task<Message> TerminalAsync(string transcript, string format)
    {
        _transcript = transcript;
        _format = format;
        var source = new ContainerId(_team, "Dev").ToString();

        return await EventuallyAsync(async () =>
            (await RowsAsync(MessageTypes.Completed)).Concat(await RowsAsync(MessageTypes.Failed))
                .FirstOrDefault(r => r.Source == source && r.Payload.Contains(PayloadFields.AgentTranscript, StringComparison.Ordinal)));
    }

    private async Task<JsonElement> CardAsync(HttpClient person, Message terminal)
    {
        var board = JsonDocument.Parse(await person.GetStringAsync($"/api/teams/{_team}/kanban/board", Ct)).RootElement;
        return board.GetProperty("cards").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == $"{terminal.CorrelationId}_Dev");
    }

    private async Task<HttpClient> PersonAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Password }, Ct))
            .EnsureSuccessStatusCode();
        return client;
    }

    private async Task<IReadOnlyList<Message>> RowsAsync(string type) =>
        await _factory.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [type], 500, Ct);

    /// <summary>Dev's rows only: the team's Manager runs too, with no transcript of its own.</summary>
    private async Task<IReadOnlyList<Message>> DevRowsAsync() =>
        [.. (await RowsAsync(MessageTypes.AgentForeignTools)).Where(r => r.Source == new ContainerId(_team, "Dev").ToString())];

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync() =>
        [.. (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(take: 500, ct: Ct)).Events
            .Where(e => e.Action == TenantActions.AgentForeignTools && e.Subject == new ContainerId(_team, "Dev").ToString())];

    private async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } found) return found;
            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("Timed out waiting for the row.");
    }

    /// <summary>A member run that ends as a real one does, naming the transcript its CLI wrote.</summary>
    private sealed class Scripted(Func<(string? Path, string? Format)> transcript) : IAgentRunner
    {
        public async Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default)
        {
            // The test names the transcript after the tell; wait for it rather than race it.
            var deadline = DateTime.UtcNow + Patience;
            while (invocation.Container.Name == "Dev" && transcript().Path is null && DateTime.UtcNow < deadline) await Task.Delay(50, ct);

            if (invocation.Container.Name != "Dev") return new AgentResult(0, "ok");

            var (path, format) = transcript();
            return new AgentResult(0, "ok", AgentTranscript: path is null ? null : new AgentTranscript(path, format!));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
