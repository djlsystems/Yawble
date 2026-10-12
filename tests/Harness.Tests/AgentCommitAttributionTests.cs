using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A MEMBER'S COMMITS CARRY NO AI ATTRIBUTION UNLESS A PERSON TURNED IT ON. While the setting
/// <c>agents.commitAttribution</c> is off, a member's launch carries its preset's attribution-off -
/// arguments with the isolation arguments, variables after the preset's and the team's env - and
/// with it on the launch is the preset's exactly. The Concierge never carries it.
///
/// Stub programs only, at absolute paths in this test's own folder: no real agent starts
/// (<c>AgentCliIsolation</c>).
/// </summary>
public sealed class AgentCommitAttributionTests : IDisposable
{
    private const string ClaudeSwitch = """{"attribution":{"commit":"","pr":""}}""";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-agent-attribution-").FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(Path.Combine(_root, "work"));
        Directory.Delete(_root, recursive: true);
    }

    private static AgentCatalog BuiltIns(bool? attribution) =>
        attribution is { } on
            ? new([.. AgentCatalogFile.BuiltIns()], commitAttribution: () => on)
            : new([.. AgentCatalogFile.BuiltIns()]);

    /// <summary>The headless presets that run a language model: every one declares attribution.</summary>
    public static TheoryData<string> HeadlessModelPresets()
    {
        var data = new TheoryData<string>();
        foreach (var definition in AgentCatalogFile.BuiltIns()
                     .Where(d => d.Mode == AgentMode.Headless && d.Launch is { LanguageModel: true }))
        {
            data.Add(definition.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HeadlessModelPresets))]
    public void Every_built_in_headless_model_preset_declares_its_attribution_with_what_was_measured(string preset)
    {
        var attribution = AgentCatalogFile.BuiltIns().Single(d => d.Name == preset).Attribution;

        Assert.NotNull(attribution);
        Assert.NotEmpty(attribution!.Notes ?? []);
        Assert.All(attribution.Notes!, note => Assert.Matches(@"\d+\.\d+\.\d+", note));
    }

    /// <summary>
    /// What each was found to need: claude's flag settings empty its commit and pull-request text;
    /// codex, grok and copilot are pinned as declaring no switch (codex adds its trailer only for an
    /// account that enables it, grok adds none, and no launch switch reaches copilot's).
    /// </summary>
    [Fact]
    public void Claude_declares_its_flag_settings_and_codex_grok_and_copilot_declare_no_switch()
    {
        var builtIns = AgentCatalogFile.BuiltIns().ToDictionary(d => d.Name);

        Assert.Equal(["--settings", ClaudeSwitch], builtIns["claude-headless"].Attribution!.Arguments);
        Assert.Null(builtIns["claude-headless"].Attribution!.Env);

        foreach (var preset in new[] { "codex-headless", "grok-headless", "copilot-headless" })
        {
            Assert.False(builtIns[preset].Attribution!.Switches, preset);
        }

        Assert.Contains(builtIns["copilot-headless"].Attribution!.Notes!, n => n.Contains("no launch switch reaches it", StringComparison.Ordinal));
        Assert.Contains(builtIns["grok-headless"].Attribution!.Notes!, n => n.Contains("nothing is declared", StringComparison.Ordinal));
        Assert.Contains(builtIns["codex-headless"].Attribution!.Notes!, n => n.Contains("nothing is declared", StringComparison.Ordinal));
    }

    /// <summary>
    /// With the setting off, Claude's member launch carries the switch right after its isolation
    /// arguments, before `-p` and the launch's own arguments.
    /// </summary>
    [Fact]
    public void With_the_setting_off_a_claude_members_launch_carries_the_switch_after_its_isolation()
    {
        var launch = BuiltIns(attribution: false).For("claude-headless")!;
        var isolation = AgentCatalogFile.BuiltIns().Single(d => d.Name == "claude-headless").Isolation!.Arguments;

        Assert.Equal([.. isolation, "--settings", ClaudeSwitch], launch.Arguments.Take(isolation.Count + 2));
        Assert.Equal("-p", launch.Arguments[isolation.Count + 2]);
        Assert.Single(launch.Arguments, a => a == "--settings");
    }

    /// <summary>
    /// With the setting on, every member launch is byte for byte the launch with no setting handed
    /// in - the preset's own, isolation and update-off included - and none carries the switch.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeadlessModelPresets))]
    public void With_the_setting_on_a_members_launch_is_the_presets_exactly(string preset)
    {
        var on = BuiltIns(attribution: true).For(preset)!;
        var unset = BuiltIns(attribution: null).For(preset)!;

        Assert.Equal(unset.Arguments, on.Arguments);
        Assert.Equal(unset.IsolationEnvironment, on.IsolationEnvironment);
        Assert.Equal(unset.UpdateEnvironment, on.UpdateEnvironment);
        Assert.DoesNotContain(ClaudeSwitch, on.Arguments);
    }

    /// <summary>A preset that declares no switch launches the same whichever way the setting is.</summary>
    [Theory]
    [InlineData("codex-headless")]
    [InlineData("grok-headless")]
    [InlineData("copilot-headless")]
    public void A_preset_that_declares_no_switch_launches_the_same_either_way(string preset)
    {
        var off = BuiltIns(attribution: false).For(preset)!;
        var on = BuiltIns(attribution: true).For(preset)!;

        Assert.Equal(on.Arguments, off.Arguments);
        Assert.Equal(on.IsolationEnvironment, off.IsolationEnvironment);
    }

    /// <summary>The setting is read at every launch: a change reaches the next launch with no restart.</summary>
    [Fact]
    public void The_setting_is_read_at_every_launch()
    {
        var on = false;
        var catalog = new AgentCatalog([.. AgentCatalogFile.BuiltIns()], commitAttribution: () => on);

        Assert.Contains(ClaudeSwitch, catalog.For("claude-headless")!.Arguments);
        on = true;
        Assert.DoesNotContain(ClaudeSwitch, catalog.For("claude-headless")!.Arguments);
        on = false;
        Assert.Contains(ClaudeSwitch, catalog.For("claude-headless")!.Arguments);
    }

    /// <summary>
    /// With no `{isolation}` token the switch goes first and the isolation arguments before it; with a
    /// token and no isolation the token is still dropped and the switch stays in its place.
    /// </summary>
    [Fact]
    public void The_switch_follows_the_isolation_arguments_with_or_without_a_token()
    {
        var attribution = new AgentAttribution(["--quiet-attribution"]);
        var isolation = new AgentIsolation(["--isolated"]);
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("no-token", AgentMode.Headless, new AgentLaunch("probe", ["run", "{userPrompt}"]),
                Isolation: isolation, Attribution: attribution),
            new AgentDefinition("token", AgentMode.Headless, new AgentLaunch("probe", ["run", AgentIsolation.Token, "{userPrompt}"]),
                Isolation: isolation, Attribution: attribution),
            new AgentDefinition("token-no-isolation", AgentMode.Headless, new AgentLaunch("probe", ["run", AgentIsolation.Token, "{userPrompt}"]),
                Attribution: attribution),
        ], commitAttribution: () => false);

        Assert.Equal(["--isolated", "--quiet-attribution", "run", "{userPrompt}"], catalog.For("no-token")!.Arguments);
        Assert.Equal(["run", "--isolated", "--quiet-attribution", "{userPrompt}"], catalog.For("token")!.Arguments);
        Assert.Equal(["run", "--quiet-attribution", "{userPrompt}"], catalog.For("token-no-isolation")!.Arguments);
    }

    /// <summary>
    /// A declared variable is set after the preset's env and the team's, with the isolation
    /// variables, so neither can switch attribution back on; with the setting on, the team's value
    /// stands.
    /// </summary>
    [Theory]
    [InlineData(false, "PROBE_ATTRIBUTION=off")]
    [InlineData(true, "PROBE_ATTRIBUTION=team")]
    public async Task A_members_process_gets_the_attribution_off_variable_last(bool attribution, string expected)
    {
        var program = Path.Combine(_root, "stub-env");
        await TestExecutable.WriteAsync(program, "#!/bin/sh\ncat >/dev/null\nenv\n");

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch(program, [], LanguageModel: false),
                Env: new Dictionary<string, string> { ["PROBE_ATTRIBUTION"] = "preset" },
                Isolation: new AgentIsolation([], Env: new Dictionary<string, string> { ["PROBE_ISOLATED"] = "1" }),
                Attribution: new AgentAttribution(Env: new Dictionary<string, string>
                {
                    ["PROBE_ATTRIBUTION"] = "off",
                    ["HARNESS_PROBE"] = "ignored",
                })),
        ], commitAttribution: () => attribution);

        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var result = await new ProcessAgentRunner(catalog, new RunHeartbeat()).RunAsync(
            new AgentInvocation(
                new ContainerId("alpha", "worker"), "You are a probe.", "hello", work,
                new Dictionary<string, string> { ["PROBE_ATTRIBUTION"] = "team" }, Agent: "probe"),
            TestContext.Current.CancellationToken);

        var lines = result.Output.Split('\n');
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expected, lines);
        Assert.Contains("PROBE_ISOLATED=1", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("HARNESS_PROBE=", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE PLATFORM'S OWN COMMITS CARRY NO TRAILER: nothing the Host or the worker builds - a merge
    /// commit's message, an initial commit, a hook it writes - holds a co-author or "generated with"
    /// line. The built-in presets' declarations, which quote what each CLI adds, are the one place
    /// such text appears.
    /// </summary>
    [Fact]
    public void The_platforms_own_code_writes_no_co_author_or_generated_with_line()
    {
        var src = Path.Combine(SolutionSamples.RepoRoot(), "src");
        var trailer = new System.Text.RegularExpressions.Regex(
            @"co-authored-by:|generated with \[|claude-session:", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && Path.GetFileName(f) != "AgentCatalogFile.cs")
            .Where(f => trailer.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        Assert.Empty(found);
        Assert.Matches(trailer, File.ReadAllText(Path.Combine(src, "Harness.Host", "AgentCatalogFile.cs")));
    }

    /// <summary>The Concierge is the person's own session: its terminal never carries the switch, whatever the setting.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_Concierges_terminal_never_carries_the_switch(bool attribution)
    {
        var catalog = BuiltIns(attribution);
        var factory = new ConciergeLaunchFactory(
            new TeamPaths(_root), "http://localhost:5000", new MintingPrincipals(), catalog);

        foreach (var preset in AgentCatalogFile.BuiltIns().Where(d => d.Mode == AgentMode.Interactive && d.Launch is { LanguageModel: true }))
        {
            var spec = await factory.ForAsync(
                team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
                preset.Name, teamEnv: new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken);

            Assert.DoesNotContain(ClaudeSwitch, spec.Argv ?? []);
            Assert.Null(catalog.Interactive(preset.Name)!.IsolationEnvironment);
            Assert.DoesNotContain(ClaudeSwitch, catalog.Interactive(preset.Name)!.Arguments);
        }
    }
}
