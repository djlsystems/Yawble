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

    private static IReadOnlyList<string> Names(IEnumerable<TranscriptTool> tools) =>
        [.. tools.Select(t => t.ToString()).Order(StringComparer.Ordinal)];

    /// <summary>Claude's own tools in the isolated probe: what a preset's allowed list would name.</summary>
    internal static readonly IReadOnlyCollection<string> ClaudeLocal =
    [
        "Agent", "Bash", "Edit", "ListAgents", "Read", "ReportFindings", "ScheduleWakeup", "Skill", "ToolSearch",
        "Workflow", "Write", "CronCreate", "CronDelete", "CronList", "DesignSync", "EnterWorktree", "ExitWorktree",
        "Monitor", "NotebookEdit", "PushNotification", "RemoteTrigger", "SendMessage", "TaskCreate", "TaskGet",
        "TaskList", "TaskStop", "TaskUpdate", "WebFetch", "WebSearch",
    ];

    [Fact]
    public void Claude_names_the_whole_offer_and_every_call()
    {
        var use = TranscriptTools.Extract(LiveView.ClaudeJsonl, Fixture("ForeignTools", "claude-offers-connectors.jsonl"));

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
    public void A_member_offered_account_connectors_is_foreign_whatever_its_preset_allows()
    {
        var use = TranscriptTools.Extract(LiveView.ClaudeJsonl, Fixture("ForeignTools", "claude-offers-connectors.jsonl"));

        foreach (var allowed in new[] { null, ClaudeLocal })
        {
            var finding = ForeignToolsJudge.Judge(use, allowed);

            Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
            Assert.Empty(finding.Called);
            Assert.Contains(finding.Offered, t => t.ToString() == "claude_ai_Gmail/send_message");
            Assert.Contains(finding.Offered, t => t.ToString() == "claude.ai Google Calendar");
            Assert.DoesNotContain(finding.Offered, t => t.Server == "harness");
            Assert.StartsWith("DeveloperTobias was offered tools the platform did not give it, and called none: ",
                ForeignToolsJudge.Sentence("DeveloperTobias", finding), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_call_is_named_apart_from_an_offer()
    {
        var use = TranscriptTools.Extract(LiveView.ClaudeJsonl, Fixture("ForeignTools", "claude-calls-connectors.jsonl"));
        var finding = ForeignToolsJudge.Judge(use, ClaudeLocal);

        Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
        Assert.Equal(
            ["Artifact", "claude_ai_Claude_Docs/batch", "claude_ai_Claude_Docs/create", "claude_ai_Claude_Docs/guide", "claude_ai_Claude_Docs/update"],
            Names(finding.Called));
        Assert.DoesNotContain(finding.Offered, t => finding.Called.Contains(t));
        Assert.Contains(finding.Offered, t => t.ToString() == "claude_ai_Gmail/send_message");

        // Artifact is one of the Concierge's own tools, not in a member preset's list: a local tool
        // outside the allowed list is foreign too.
        Assert.Contains(finding.Called, t => t.ToString() == "Artifact");
        Assert.StartsWith("Dev CALLED tools the platform did not give it: ", ForeignToolsJudge.Sentence("Dev", finding), StringComparison.Ordinal);
    }

    [Fact]
    public void An_isolated_claude_run_is_clean_only_when_its_preset_declares_an_allowed_list()
    {
        var use = TranscriptTools.Extract(LiveView.ClaudeJsonl, Fixture("ForeignTools", "claude-isolated.jsonl"));

        Assert.True(use.OfferedComplete);
        Assert.DoesNotContain(use.Offered, t => t.Server is { } server && server != "harness");
        Assert.Equal(ForeignToolsStatus.Clean, ForeignToolsJudge.Judge(use, ClaudeLocal).Status);

        // Not verified: nothing foreign, and still never clean.
        var unverified = ForeignToolsJudge.Judge(use, null);
        Assert.Equal(ForeignToolsStatus.NotMeasured, unverified.Status);
        Assert.Contains("not verified", unverified.Why, StringComparison.Ordinal);

        // A declared list that leaves out a tool the CLI offered makes that tool foreign.
        var narrower = ForeignToolsJudge.Judge(use, [.. ClaudeLocal.Where(n => n != "WebSearch")]);
        Assert.Equal(ForeignToolsStatus.Foreign, narrower.Status);
        Assert.Equal(["WebSearch"], Names(narrower.Offered));
    }

    [Fact]
    public void Copilot_names_the_servers_whose_instructions_it_carries_and_every_call()
    {
        var github = TranscriptTools.Extract(LiveView.CopilotEvents, Fixture("LiveView", "copilot-events.jsonl"));
        Assert.False(github.OfferedComplete);
        Assert.Equal(["github-mcp-server"], Names(github.Offered));
        Assert.Equal(["bash", "harness/team_current", "view"], Names(github.Called));

        var finding = ForeignToolsJudge.Judge(github, null);
        Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
        Assert.Equal(["github-mcp-server"], Names(finding.Offered));

        var none = TranscriptTools.Extract(LiveView.CopilotEvents, Fixture("ForeignTools", "copilot-no-builtin-servers.jsonl"));
        Assert.Empty(none.Offered);
        Assert.Equal(["view"], Names(none.Called));
        Assert.Equal(ForeignToolsStatus.NotMeasured, ForeignToolsJudge.Judge(none, ["view", "bash"]).Status);
    }

    [Fact]
    public void Grok_names_every_call_and_what_its_tool_search_found()
    {
        var stub = TranscriptTools.Extract(LiveView.GrokUpdates, Fixture("ForeignTools", "grok-calls-stub-server.jsonl"));
        Assert.False(stub.OfferedComplete);
        Assert.Equal(["stubfs/echo"], Names(stub.Offered));
        Assert.Equal(["search_tool", "stubfs/echo"], Names(stub.Called));

        var finding = ForeignToolsJudge.Judge(stub, null);
        Assert.Equal(ForeignToolsStatus.Foreign, finding.Status);
        Assert.Equal(["stubfs/echo"], Names(finding.Called));
        Assert.Empty(finding.Offered);

        var harness = TranscriptTools.Extract(LiveView.GrokUpdates, Fixture("LiveView", "grok-updates.jsonl"));
        Assert.Contains(harness.Called, t => t.ToString() == "harness/status");
        Assert.Equal(ForeignToolsStatus.NotMeasured, ForeignToolsJudge.Judge(harness, null).Status);
    }

    [Fact]
    public void Codex_names_every_call_and_no_offer()
    {
        var stub = TranscriptTools.Extract(LiveView.CodexRollout, Fixture("ForeignTools", "codex-calls-stub-server.jsonl"));
        Assert.False(stub.OfferedComplete);
        Assert.Empty(stub.Offered);
        Assert.Equal(["exec", "stubfs/echo"], Names(stub.Called));
        Assert.Equal(["stubfs/echo"], Names(ForeignToolsJudge.Judge(stub, null).Called));

        var harness = TranscriptTools.Extract(LiveView.CodexRollout, Fixture("LiveView", "codex-rollout.jsonl"));
        Assert.Equal(["exec", "harness/team_current"], Names(harness.Called));
        var finding = ForeignToolsJudge.Judge(harness, ["exec"]);
        Assert.Equal(ForeignToolsStatus.NotMeasured, finding.Status);
        Assert.Contains("lists no offer", finding.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_format_nothing_reads_is_not_measured()
    {
        var use = TranscriptTools.Extract("some-new-cli", Fixture("ForeignTools", "claude-offers-connectors.jsonl"));

        Assert.Empty(use.Offered);
        Assert.Empty(use.Called);
        var finding = ForeignToolsJudge.Judge(use, ClaudeLocal);
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

    public static TheoryData<string, string, string, string?, string[], string[]> Runs => new()
    {
        // format, folder, file, the card's mark (null: clean), called, offered (a sample)
        { LiveView.ClaudeJsonl, "ForeignTools", "claude-offers-connectors.jsonl", KanbanForeignTools.Offered, [], ["claude_ai_Gmail/send_message", "claude.ai Google Drive"] },
        { LiveView.ClaudeJsonl, "ForeignTools", "claude-calls-connectors.jsonl", KanbanForeignTools.Called, ["claude_ai_Claude_Docs/create"], ["claude_ai_Gmail/forward"] },
        { LiveView.ClaudeJsonl, "ForeignTools", "claude-isolated.jsonl", null, [], [] },
        { LiveView.CopilotEvents, "LiveView", "copilot-events.jsonl", KanbanForeignTools.Offered, [], ["github-mcp-server"] },
        { LiveView.CopilotEvents, "ForeignTools", "copilot-no-builtin-servers.jsonl", KanbanForeignTools.NotMeasured, [], [] },
        { LiveView.GrokUpdates, "ForeignTools", "grok-calls-stub-server.jsonl", KanbanForeignTools.Called, ["stubfs/echo"], [] },
        { LiveView.GrokUpdates, "LiveView", "grok-updates.jsonl", KanbanForeignTools.NotMeasured, [], [] },
        { LiveView.CodexRollout, "ForeignTools", "codex-calls-stub-server.jsonl", KanbanForeignTools.Called, ["stubfs/echo"], [] },
        { LiveView.CodexRollout, "LiveView", "codex-rollout.jsonl", KanbanForeignTools.NotMeasured, [], [] },
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_member_run_is_checked_and_what_is_not_clean_lands_on_the_log_the_tenant_log_and_the_card(
        string format, string folder, string file, string? mark, string[] called, string[] offered)
    {
        // The preset declares Claude's own tools, Copilot's, Grok's and Codex's that the fixtures call:
        // a stand-in for the isolation declaration, through the one seam the check reads.
        await StartAsync(new PresetAllowedTools(_ =>
            [.. ForeignToolsTests.ClaudeLocal, "view", "bash", "search_tool", "use_tool", "exec", "read_file", "run_terminal_command", "todo_write"]));
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
            Assert.Empty(calledNames);
            Assert.Empty(offeredNames);
            Assert.Contains("not measured", payload.GetProperty(PayloadFields.Text).GetString(), StringComparison.Ordinal);

            // Not a finding about a person's accounts: the team log and the card say it, the tenant log does not.
            Assert.Empty(tenant);
        }
        else
        {
            Assert.Equal("foreign", payload.GetProperty(PayloadFields.ForeignToolsStatus).GetString());

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
    public async Task A_run_whose_preset_is_not_verified_is_never_reported_clean()
    {
        await StartAsync(new PresetAllowedTools(_ => null));
        var transcript = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ForeignTools", "claude-isolated.jsonl");
        var person = await PersonAsync();

        (await person.PostAsJsonAsync($"/api/teams/{_team}/containers/Dev/tell", new { instruction = "do the work" }, Ct)).EnsureSuccessStatusCode();
        var terminal = await TerminalAsync(transcript, LiveView.ClaudeJsonl);

        var row = await EventuallyAsync(async () => (await DevRowsAsync()).FirstOrDefault());
        Assert.Equal("notMeasured", JsonDocument.Parse(row.Payload).RootElement.GetProperty(PayloadFields.ForeignToolsStatus).GetString());
        Assert.Equal(KanbanForeignTools.NotMeasured, (await CardAsync(person, terminal)).GetProperty("foreignTools").GetString());
    }

    [Fact]
    public async Task The_concierge_is_never_checked()
    {
        await StartAsync(new PresetAllowedTools(_ => ForeignToolsTests.ClaudeLocal));
        var services = _factory.Services;
        var log = services.GetRequiredService<IMessageLog>();

        // The Concierge's own session, which called the person's connectors, on a terminal row whose
        // source is not a team member - the only shape a non-member's row could take.
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

    private async Task StartAsync(PresetAllowedTools allowed)
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
                services.AddSingleton(allowed);
            }));

        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var preset = services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless && d.Launch.LanguageModel).Name;

        _team = (await registry.CreateAsync("Alpha", preset, memberAgent: preset, ct: Ct)).Id;
        await registry.AddContainerAsync(_team, "Dev", preset, "", [], permits: new HashSet<string>(Permits.All), ct: Ct);
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
