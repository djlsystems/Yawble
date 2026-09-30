using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A MEMBER GETS ONLY THE TOOLS THE PLATFORM GIVES IT (B001K). Every built-in headless preset
/// declares how its launch is isolated and which of its CLI's own tools it may use; the Concierge's
/// presets declare nothing and launch exactly as before; a headless preset with no declaration is
/// not verified. What each built-in declaration switches off was measured with real launches -
/// these pin the shape, and that the launch carries it.
/// </summary>
public sealed class AgentIsolationTests
{
    private static readonly string[] HeadlessBuiltIns =
        ["claude-headless", "codex-headless", "grok-headless", "copilot-headless"];

    private static AgentCatalog BuiltIns() => new(AgentCatalogFile.BuiltIns());

    [Fact]
    public void Every_built_in_headless_language_model_preset_declares_its_isolation()
    {
        var headless = AgentCatalogFile.BuiltIns()
            .Where(d => d.Mode == AgentMode.Headless && d.Launch.LanguageModel)
            .Select(d => d.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(HeadlessBuiltIns.Order(StringComparer.Ordinal), headless);

        var catalog = BuiltIns();
        Assert.All(HeadlessBuiltIns, name =>
        {
            var allowance = catalog.Allowance(name)!;
            Assert.Equal(IsolationState.Isolated, allowance.State);
            Assert.True(allowance.Checked);
            Assert.NotEmpty(allowance.AllowedTools!);
            Assert.Equal([AgentIsolation.PlatformServer], allowance.AllowedServers);
        });
    }

    [Fact]
    public void The_concierge_presets_declare_nothing_and_launch_exactly_as_their_launch_says()
    {
        var catalog = BuiltIns();
        var interactive = AgentCatalogFile.BuiltIns().Where(d => d.Mode == AgentMode.Interactive).ToList();
        Assert.NotEmpty(interactive);

        Assert.All(interactive, preset =>
        {
            Assert.Null(preset.Isolation);
            Assert.DoesNotContain(AgentIsolation.Token, preset.Launch.Arguments);

            var command = catalog.Interactive(preset.Name)!;
            Assert.Equal(preset.Launch.Arguments, command.Arguments);
            Assert.Null(command.IsolationEnvironment);

            var allowance = catalog.Allowance(preset.Name)!;
            Assert.Equal(IsolationState.Concierge, allowance.State);
            Assert.False(allowance.Checked);
        });
    }

    [Fact]
    public void A_custom_headless_preset_without_a_declaration_is_not_verified_and_only_harness_is_an_allowed_server()
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("mine", AgentMode.Headless, new AgentLaunch("some-cli", ["-p", "{userPrompt}"])),
            new AgentDefinition("script", AgentMode.Headless, new AgentLaunch("cat", [], LanguageModel: false)),
        ]);

        var allowance = catalog.Allowance("mine")!;
        Assert.Equal(IsolationState.NotVerified, allowance.State);
        Assert.True(allowance.Checked);
        Assert.Null(allowance.AllowedTools);
        Assert.True(allowance.Allows("harness", "progress"));
        Assert.False(allowance.Allows("claude_ai_Gmail", "send_message"));
        Assert.True(allowance.Allows(null, "Bash"));

        Assert.Equal(IsolationState.NotAModel, catalog.Allowance("script")!.State);
        Assert.Null(catalog.Allowance("nobody"));
    }

    [Fact]
    public void An_isolated_preset_allows_harness_and_its_declared_local_tools_and_nothing_else()
    {
        var allowance = BuiltIns().Allowance("claude-headless")!;

        Assert.True(allowance.Allows("harness", "tell"));
        Assert.True(allowance.Allows(null, "Bash"));
        Assert.True(allowance.Allows(null, "Task"));
        Assert.False(allowance.Allows("claude_ai_Gmail", "send_message"));
        Assert.False(allowance.Allows(null, "RemoteTrigger"));
        Assert.False(allowance.Allows(null, "SendMessage"));
    }

    [Fact]
    public void Claudes_member_launch_switches_off_connectors_the_home_and_auto_memory()
    {
        var command = BuiltIns().For("claude-headless")!;

        Assert.DoesNotContain(AgentIsolation.Token, command.Arguments);
        Assert.Contains("--strict-mcp-config", command.Arguments);
        AssertFollowedBy(command.Arguments, "--setting-sources", "project,local");
        AssertFollowedBy(command.Arguments, "--mcp-config", "{mcpConfig}");

        var tools = command.Arguments[command.Arguments.ToList().IndexOf("--tools") + 1].Split(',');
        Assert.DoesNotContain("RemoteTrigger", tools);
        Assert.DoesNotContain("SendMessage", tools);
        Assert.Contains("Bash", tools);

        Assert.Equal("false", command.IsolationEnvironment!["ENABLE_CLAUDEAI_MCP_SERVERS"]);
        Assert.Equal("1", command.IsolationEnvironment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"]);
    }

    [Fact]
    public void Codexs_member_launch_turns_off_apps_and_plugins_after_exec()
    {
        var arguments = BuiltIns().For("codex-headless")!.Arguments;

        Assert.Equal("exec", arguments[0]);
        AssertFollowedBy(arguments, "--disable", "apps");
        AssertFollowedBy(arguments, "-c", "skills.include_instructions=false");
        Assert.Contains("--ignore-user-config", arguments);
        Assert.True(arguments.ToList().IndexOf("--ignore-user-config") > 0);
    }

    [Fact]
    public void Copilots_member_launch_has_no_github_server_and_its_prompt_stays_last()
    {
        var arguments = BuiltIns().For("copilot-headless")!.Arguments;

        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Equal("{userPrompt}", arguments[^1]);
        Assert.Equal("-p", arguments[^2]);
        Assert.Empty(BuiltIns().Allowance("copilot-headless")!.AllowedServers.Except(["harness"]));
    }

    [Fact]
    public void Grok_members_also_lose_the_codex_surfaces_and_memory()
    {
        var command = BuiltIns().For("grok-headless")!;

        Assert.DoesNotContain(AgentIsolation.Token, command.Arguments);
        Assert.Equal("false", command.IsolationEnvironment!["GROK_CODEX_MCPS_ENABLED"]);
        Assert.Equal("0", command.IsolationEnvironment["GROK_MEMORY"]);
    }

    [Fact]
    public void Isolation_arguments_go_at_the_token_or_first_and_a_token_without_a_declaration_is_dropped()
    {
        var isolation = new AgentIsolation(["--a", "--b"]);
        var command = new AgentCommand("cli", ["exec", AgentIsolation.Token, "--x"]);

        Assert.Equal(["exec", "--a", "--b", "--x"], AgentIsolationPolicy.Apply(command, isolation).Arguments);
        Assert.Equal(["--a", "--b", "exec", "--x"],
            AgentIsolationPolicy.Apply(command with { Arguments = ["exec", "--x"] }, isolation).Arguments);
        Assert.Equal(["exec", "--x"], AgentIsolationPolicy.Apply(command, null).Arguments);
    }

    [Fact]
    public void A_harness_variable_in_an_isolation_declaration_is_ignored()
    {
        var isolation = new AgentIsolation([], Env: new Dictionary<string, string>
        {
            ["HARNESS_KEY"] = "stolen",
            ["harness_url"] = "elsewhere",
            ["KEEP"] = "1",
        });

        var environment = AgentIsolationPolicy.Apply(new AgentCommand("cli", []), isolation).IsolationEnvironment!;

        Assert.Equal(["KEEP"], environment.Keys);
    }

    [Fact]
    public void A_catalog_file_round_trips_a_custom_presets_declaration()
    {
        var root = Directory.CreateTempSubdirectory("harness-isolation-").FullName;
        try
        {
            var preset = new AgentDefinition(
                "mine", AgentMode.Headless, new AgentLaunch("some-cli", [AgentIsolation.Token, "-p"]),
                Isolation: new AgentIsolation(
                    ["--no-home"], Env: new Dictionary<string, string> { ["X"] = "1" },
                    AllowedTools: ["shell"], AllowedServers: ["github"], Gaps: ["none known"]));

            AgentCatalogFile.Save(root, [preset]);
            var read = Assert.Single(AgentCatalogFile.LoadCustom(root, TextWriter.Null));

            Assert.Equal(["--no-home"], read.Isolation!.Arguments);
            Assert.Equal("1", read.Isolation.Env!["X"]);
            Assert.Equal(["shell"], read.Isolation.AllowedTools!);
            Assert.Equal(["harness", "github"], AgentIsolationPolicy.For(read)!.AllowedServers);
            Assert.Equal(["none known"], read.Isolation.Gaps!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertFollowedBy(IReadOnlyList<string> arguments, string flag, string value)
    {
        var list = arguments.ToList();
        var at = list.IndexOf(flag);
        while (at >= 0 && (at + 1 >= list.Count || list[at + 1] != value)) at = list.IndexOf(flag, at + 1);
        Assert.True(at >= 0, $"{flag} {value} not in: {string.Join(' ', arguments)}");
    }
}

/// <summary>The runner sets a member's isolation variables last and puts its arguments at the token.</summary>
public sealed class AgentIsolationLaunchTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("harness-isolation-run-").FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_workspace);
        Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task A_members_isolation_variables_win_over_the_presets_and_the_teams_and_its_arguments_reach_the_cli()
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition(
                "probe",
                AgentMode.Headless,
                new AgentLaunch("bash", ["-c", "echo \"$ISO_PROBE $0 $1\"", AgentIsolation.Token], LanguageModel: false),
                Isolation: new AgentIsolation(
                    ["first", "second"],
                    Env: new Dictionary<string, string> { ["ISO_PROBE"] = "isolated" })),
        ]);

        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat());

        var result = await runner.RunAsync(new AgentInvocation(
            new ContainerId("alpha", "worker"),
            "You are a probe.",
            "hello",
            _workspace,
            new Dictionary<string, string> { ["ISO_PROBE"] = "from-the-team" },
            Agent: "probe"), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("isolated first second", result.Output.Trim());
    }
}
