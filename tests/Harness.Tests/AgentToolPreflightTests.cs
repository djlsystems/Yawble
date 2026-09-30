using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE PRE-FLIGHT, per preset: isolated, foreign tools found (named), not verified, or - for the
/// Concierge - its tools as information. No test runs a CLI or reaches an account: the listings are
/// the CLIs' real output, recorded on the image (Fixtures/AgentTools, see its README), handed back
/// through <see cref="IListingRunner"/> the way each CLI answered for the launch shape it was given.
/// </summary>
public sealed class AgentToolPreflightTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentTools", name));

    /// <summary>
    /// The four CLIs as they answered on the image: the claude.ai connectors unless the launch sets
    /// ENABLE_CLAUDEAI_MCP_SERVERS=false; codex's apps, plugins and skills unless its `--disable`
    /// flags are passed; copilot's built-in GitHub server unless `--disable-builtin-mcps` is.
    /// </summary>
    private sealed class RecordedClis(
        IReadOnlySet<string>? missing = null, Func<string, IReadOnlyList<string>, string?>? overrideOutput = null)
        : IListingRunner
    {
        public List<(string Command, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)> Calls { get; } = [];

        public bool Installed(string command) => missing?.Contains(command) != true;

        public Task<ListingRun> RunAsync(
            string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
            CancellationToken ct)
        {
            lock (Calls) Calls.Add((command, arguments, environment));

            if (overrideOutput?.Invoke(command, arguments) is { } given)
                return Task.FromResult(given == "FAIL" ? new ListingRun(1, "") : new ListingRun(0, given));

            var line = string.Join(' ', arguments);
            var disabled = arguments.Contains("--disable");

            var file = command switch
            {
                "claude" when line == "mcp list" => environment.GetValueOrDefault("ENABLE_CLAUDEAI_MCP_SERVERS") == "false"
                    ? "claude-mcp-list.isolated.txt"
                    : "claude-mcp-list.base.txt",
                "claude" => "claude-plugin-list.json",
                "codex" when line.EndsWith("mcp list --json", StringComparison.Ordinal) => "codex-mcp-list.json",
                "codex" when line.EndsWith("features list", StringComparison.Ordinal) =>
                    disabled ? "codex-features.isolated.txt" : "codex-features.base.txt",
                "codex" => disabled ? "codex-prompt-input.isolated.json" : "codex-prompt-input.base.json",
                "grok" => "grok-inspect.json",
                "copilot" when line.EndsWith("mcp list --json", StringComparison.Ordinal) =>
                    arguments.Contains("--disable-builtin-mcps") ? "copilot-mcp-list.isolated.json" : "copilot-mcp-list.base.json",
                "copilot" when line.EndsWith("plugin list --json", StringComparison.Ordinal) => "copilot-plugin-list.json",
                "copilot" => "copilot-skill-list.json",
                _ => null,
            };

            return Task.FromResult(file is null ? new ListingRun(127, "") : new ListingRun(0, Fixture(file)));
        }
    }

    private static async Task<IReadOnlyList<PresetToolReport>> Reports(
        IReadOnlyList<AgentDefinition> definitions, IListingRunner runner) =>
        await AgentToolPreflight.ReportsAsync(definitions, runner, TestContext.Current.CancellationToken);

    private static PresetToolReport Named(IReadOnlyList<PresetToolReport> reports, string preset) =>
        Assert.Single(reports, r => r.Preset == preset);

    [Fact]
    public async Task Every_built_in_headless_preset_is_isolated_on_the_image_as_it_was_measured()
    {
        var reports = await Reports(AgentCatalogFile.BuiltIns(), new RecordedClis());

        Assert.All(["claude-headless", "codex-headless", "grok-headless", "copilot-headless"], name =>
        {
            var report = Named(reports, name);
            Assert.Equal(ToolVerdicts.Isolated, report.Verdict);
            Assert.Empty(report.Foreign);
            Assert.NotEmpty(report.Ran);
        });

        // What each launch switched off is named, not dropped: the reader sees the switch work.
        Assert.Contains(Named(reports, "copilot-headless").SwitchedOff, i => i.Name == "github-mcp-server");
        Assert.Contains(Named(reports, "codex-headless").SwitchedOff, i => i.Name == "ChatGPT apps (codex_apps)");

        // Its recorded gaps ride with it.
        Assert.NotEmpty(Named(reports, "grok-headless").Gaps);
        Assert.Contains(Named(reports, "grok-headless").Loaded, i => i.Kind == ListedKinds.Server && i.Name == "harness");
    }

    [Fact]
    public async Task The_concierge_lists_its_connectors_and_servers_as_information_and_is_never_foreign()
    {
        var reports = await Reports(AgentCatalogFile.BuiltIns(), new RecordedClis());

        var claude = Named(reports, "claude");
        Assert.Equal(ToolVerdicts.Concierge, claude.Verdict);
        Assert.Empty(claude.Foreign);
        Assert.Equal(
            ["claude.ai Claude Docs", "claude.ai Google Drive", "claude.ai Gmail", "claude.ai Google Calendar"],
            claude.Loaded.Where(i => i.Kind == ListedKinds.Connector).Select(i => i.Name));

        var copilot = Named(reports, "copilot");
        Assert.Equal(ToolVerdicts.Concierge, copilot.Verdict);
        Assert.Contains(copilot.Loaded, i => i.Name == "github-mcp-server");

        Assert.Contains(Named(reports, "codex").Loaded, i => i.Name == "ChatGPT apps (codex_apps)");
    }

    [Fact]
    public async Task A_member_is_listed_with_its_isolation_and_the_concierge_without_it()
    {
        var clis = new RecordedClis();
        await Reports(AgentCatalogFile.BuiltIns(), clis);

        var claudeLists = clis.Calls.Where(c => c.Command == "claude" && c.Arguments.SequenceEqual(["mcp", "list"])).ToList();
        Assert.Equal(2, claudeLists.Count);
        Assert.Single(claudeLists, c => c.Environment.GetValueOrDefault("ENABLE_CLAUDEAI_MCP_SERVERS") == "false");
        Assert.Single(claudeLists, c => !c.Environment.ContainsKey("ENABLE_CLAUDEAI_MCP_SERVERS"));

        Assert.Contains(clis.Calls, c => c.Command == "copilot" && c.Arguments[0] == "--disable-builtin-mcps");
        Assert.Contains(clis.Calls, c => c.Command == "codex" && c.Arguments.SequenceEqual(["--disable", "apps", "--disable", "plugins", "--disable", "remote_plugin", "--disable", "hooks", "--disable", "memories", "-c", "skills.include_instructions=false", "features", "list"]));

        // `exec`'s own switches are not handed to a subcommand that refuses them.
        Assert.DoesNotContain(clis.Calls, c => c.Arguments.Contains("--ignore-user-config"));

        // The environment every container gets, member and Concierge alike.
        Assert.All(clis.Calls, c => Assert.Equal("false", c.Environment["GROK_CLAUDE_MCPS_ENABLED"]));

        // Each launch shape once, however many presets share it.
        Assert.Equal(claudeLists.Count, claudeLists.Select(c => string.Join(',', c.Environment)).Distinct().Count());
    }

    [Fact]
    public async Task A_member_whose_launch_would_offer_a_connector_or_home_server_is_foreign_and_names_it()
    {
        // The claude member preset with its isolation environment gone: the CLI answers as it did
        // for a plain launch, with the account's connectors.
        var leaky = AgentCatalogFile.BuiltIns().Single(d => d.Name == "claude-headless") is var claude
            ? claude with { Isolation = claude.Isolation! with { Env = null, Arguments = [] } }
            : null!;

        var report = Named(await Reports([leaky], new RecordedClis()), "claude-headless");

        Assert.Equal(ToolVerdicts.ForeignFound, report.Verdict);
        Assert.Contains(report.Foreign, i => i.Name == "claude.ai Gmail" && i.Kind == ListedKinds.Connector);

        // A server a person added to copilot's home is foreign for a copilot member.
        var copilot = AgentCatalogFile.BuiltIns().Single(d => d.Name == "copilot-headless");
        var withHomeServer = new RecordedClis(overrideOutput: (command, arguments) =>
            command == "copilot" && arguments.Contains("mcp") ? Fixture("copilot-mcp-list.canary.json") : null);

        var copilotReport = Named(await Reports([copilot], withHomeServer), "copilot-headless");
        Assert.Equal(ToolVerdicts.ForeignFound, copilotReport.Verdict);
        Assert.Equal(["canary"], copilotReport.Foreign.Select(f => f.Name));
        Assert.Contains(copilotReport.SwitchedOff, i => i.Name == "github-mcp-server");
    }

    [Fact]
    public async Task A_server_the_preset_allows_is_not_foreign()
    {
        var copilot = AgentCatalogFile.BuiltIns().Single(d => d.Name == "copilot-headless");
        var allowing = copilot with { Isolation = copilot.Isolation! with { AllowedServers = ["canary"] } };
        var withHomeServer = new RecordedClis(overrideOutput: (command, arguments) =>
            command == "copilot" && arguments.Contains("mcp") ? Fixture("copilot-mcp-list.canary.json") : null);

        Assert.Equal(ToolVerdicts.Isolated, Named(await Reports([allowing], withHomeServer), "copilot-headless").Verdict);
    }

    [Fact]
    public async Task A_custom_preset_without_a_declaration_is_not_verified_and_still_names_what_it_would_get()
    {
        var mine = new AgentDefinition("my-claude", AgentMode.Headless, new AgentLaunch("claude", ["-p", "{userPrompt}"]));

        var report = Named(await Reports([mine], new RecordedClis()), "my-claude");

        Assert.Equal(ToolVerdicts.NotVerified, report.Verdict);
        Assert.Contains(report.Foreign, i => i.Name == "claude.ai Gmail");
    }

    [Fact]
    public async Task A_declared_preset_that_cannot_be_listed_is_not_measured_never_isolated()
    {
        var builtIns = AgentCatalogFile.BuiltIns();

        var missing = await Reports(builtIns, new RecordedClis(missing: new HashSet<string> { "grok" }));
        Assert.Equal(ToolVerdicts.NotMeasured, Named(missing, "grok-headless").Verdict);
        Assert.Contains("not on PATH", Named(missing, "grok-headless").Detail);

        // One listing command failing fails the whole listing: half a listing is not a clean one.
        var failing = await Reports(builtIns, new RecordedClis(overrideOutput: (command, arguments) =>
            command == "codex" && arguments.Contains("features") ? "FAIL" : null));
        Assert.Equal(ToolVerdicts.NotMeasured, Named(failing, "codex-headless").Verdict);
        Assert.Contains("features list", Named(failing, "codex-headless").Detail);

        var garbled = await Reports(builtIns, new RecordedClis(overrideOutput: (command, _) =>
            command == "grok" ? "not json" : null));
        Assert.Equal(ToolVerdicts.NotMeasured, Named(garbled, "grok-headless").Verdict);

        var unknownCli = new AgentDefinition(
            "other", AgentMode.Headless, new AgentLaunch("some-cli", ["{isolation}"]), Isolation: new AgentIsolation([]));
        var other = Named(await Reports([unknownCli], new RecordedClis()), "other");
        Assert.Equal(ToolVerdicts.NotMeasured, other.Verdict);
        Assert.Contains("no listing", other.Detail);
    }

    [Fact]
    public async Task A_program_that_is_not_a_model_is_not_listed_at_all()
    {
        var clis = new RecordedClis();
        var script = new AgentDefinition("script", AgentMode.Headless, new AgentLaunch("cat", [], LanguageModel: false));

        Assert.Equal(ToolVerdicts.NotAModel, Named(await Reports([script], clis), "script").Verdict);
        Assert.Empty(clis.Calls);
    }

    [Fact]
    public void The_listers_read_each_clis_real_output()
    {
        var connectors = AgentToolListers.ClaudeServers(Fixture("claude-mcp-list.base.txt"), []).ToList();
        Assert.Equal(4, connectors.Count);
        Assert.All(connectors, c => Assert.Equal(ListedKinds.Connector, c.Kind));

        Assert.Empty(AgentToolListers.ClaudeServers(Fixture("claude-mcp-list.isolated.txt"), []));

        var home = AgentToolListers.ClaudeServers(Fixture("claude-mcp-list.canary.txt"), ["--strict-mcp-config"]).ToList();
        Assert.Equal(["canary", "web-canary"], home.Select(s => s.Name));
        Assert.All(home, s => Assert.Equal("--strict-mcp-config", s.Off));

        Assert.Equal(
            ["canary"],
            AgentToolListers.CodexServers(Fixture("codex-mcp-list.canary.json"), []).Select(s => s.Name));
        Assert.All(
            AgentToolListers.CodexServers(Fixture("codex-mcp-list.canary.json"), ["--ignore-user-config"]),
            s => Assert.Equal("--ignore-user-config", s.Off));

        Assert.Contains(AgentToolListers.CodexFeatures(Fixture("codex-features.base.txt")), f => f.Name == "ChatGPT apps (codex_apps)" && f.Off is null);
        Assert.All(AgentToolListers.CodexFeatures(Fixture("codex-features.isolated.txt")), f => Assert.Equal("disabled", f.Off));

        Assert.Contains(AgentToolListers.CodexSkills(Fixture("codex-prompt-input.base.json")), s => s.Name == "imagegen");
        Assert.Empty(AgentToolListers.CodexSkills(Fixture("codex-prompt-input.isolated.json")));

        var grok = AgentToolListers.GrokInspect(Fixture("grok-inspect.json")).ToList();
        Assert.Equal(["harness"], grok.Where(i => i.Kind == ListedKinds.Server).Select(i => i.Name));
        Assert.Contains(grok, i => i.Kind == ListedKinds.Skill && i.Source!.Contains("/.claude/skills/", StringComparison.Ordinal));

        Assert.Null(Assert.Single(AgentToolListers.CopilotServers(Fixture("copilot-mcp-list.base.json"))).Off);
        Assert.Equal("disabled", Assert.Single(AgentToolListers.CopilotServers(Fixture("copilot-mcp-list.isolated.json"))).Off);
        Assert.Equal(2, AgentToolListers.NamedArray(Fixture("copilot-skill-list.json"), ListedKinds.Skill).Count());
    }

    [Fact]
    public async Task A_catalog_save_lists_the_clis_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("harness-tools-").FullName;
        try
        {
            var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
            var preflight = new AgentToolPreflight(catalog, new RecordedClis(), root);

            await preflight.StartAsync(ct);
            var first = await Eventually(() => preflight.Current, ct);
            Assert.DoesNotContain(first.Presets, p => p.Preset == "my-claude");

            catalog.Replace([
                .. AgentCatalogFile.BuiltIns(),
                new AgentDefinition("my-claude", AgentMode.Headless, new AgentLaunch("claude", ["-p"])),
            ]);

            var second = await Eventually(
                () => preflight.Current is { } now && now.Presets.Any(p => p.Preset == "my-claude") ? now : null, ct);
            Assert.Equal(ToolVerdicts.NotVerified, Named(second.Presets, "my-claude").Verdict);

            await preflight.StopAsync(ct);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_doctor_reports_the_hosts_last_pre_flight_and_null_before_there_is_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("harness-tools-").FullName;
        try
        {
            Assert.Null((await HostDoctor.ReportAsync(root, ct)).AgentTools);

            await new AgentToolPreflight(new AgentCatalog(AgentCatalogFile.BuiltIns()), new RecordedClis(), root).PassAsync(ct);

            var tools = (await HostDoctor.ReportAsync(root, ct)).AgentTools!;
            Assert.Equal(ToolVerdicts.Isolated, Named(tools.Presets, "claude-headless").Verdict);
            Assert.Equal(ToolVerdicts.Concierge, Named(tools.Presets, "claude").Verdict);

            var json = HostDoctor.ToJson(await HostDoctor.ReportAsync(root, ct));
            Assert.Contains("\"agentTools\":{", json);
            Assert.Contains("\"verdict\":\"isolated\"", json);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<T> Eventually<T>(Func<T?> read, CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (read() is { } value) return value;
            await Task.Delay(20, ct);
        }

        throw new TimeoutException("The pre-flight did not finish.");
    }
}
