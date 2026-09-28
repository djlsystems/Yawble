using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// CLAUDE'S SEEDED ARGUMENTS PASS `--mcp-config {mcpConfig}`. It is a launch token in the catalog,
/// not a branch on `claude` in code, so removing it from the seed leaves Claude with no tools.
/// </summary>
public sealed class SeededCatalogTests
{
    [Fact]
    public void Every_seeded_claude_preset_passes_the_mcp_config_token()
    {
        var claude = AgentCatalogFile.BuiltIns().Where(a => a.Launch.FileName == "claude").ToList();
        Assert.NotEmpty(claude);

        Assert.All(claude, preset =>
        {
            var arguments = preset.Launch.Arguments.ToList();
            var at = arguments.IndexOf("--mcp-config");
            Assert.True(at >= 0 && at + 1 < arguments.Count && arguments[at + 1] == "{mcpConfig}",
                $"{preset.Name}: {string.Join(' ', arguments)}");
        });
    }

    /// <summary>
    /// THE LAUNCH TOKENS THAT HAND A CLI THE HARNESS MCP SERVER. `{mcpConfig}` is Claude-shaped
    /// JSON, `{mcpConfigToml}` Codex-shaped TOML, and `{mcpUrl}` the bare URL a `-c` flag carries.
    /// </summary>
    private static readonly string[] McpTokens = ["{mcpConfig}", "{mcpConfigToml}", "{mcpUrl}"];

    /// <summary>
    /// VISIBLE BUILT-INS THAT REACH THE PLATFORM WITHOUT A TOKEN, each with the mechanism that
    /// wires it. A new preset is not on this list, so it fails until it carries a token or is
    /// added here beside a named, tested mechanism.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> WiredWithoutAToken =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // grok reads ~/.grok/config.toml, not a flag: McpLaunchConfig.EnsureGrokConfig writes the
            // harness entry on every launch (McpLaunchConfigTests, The_grok_entry_follows_...).
            ["grok"] = "~/.grok/config.toml via McpLaunchConfig.EnsureGrokConfig",
            ["grok-headless"] = "~/.grok/config.toml via McpLaunchConfig.EnsureGrokConfig",
        };

    /// <summary>The presets among these that name no MCP token and no mechanism.</summary>
    private static IReadOnlyList<string> Unwired(IEnumerable<AgentDefinition> presets) =>
        presets
            .Where(p => !p.Hidden && p.Launch.LanguageModel)
            .Where(p => !p.Launch.Arguments.Any(a => McpTokens.Any(t => a.Contains(t, StringComparison.Ordinal))))
            .Where(p => !WiredWithoutAToken.ContainsKey(p.Name))
            .Select(p => p.Name)
            .ToList();

    [Fact]
    public void Every_visible_built_in_preset_reaches_the_platform_by_a_token_or_a_named_mechanism()
    {
        Assert.Empty(Unwired(AgentCatalogFile.BuiltIns()));

        // A stale allowlist entry would exempt whatever preset takes that name next.
        var names = AgentCatalogFile.BuiltIns().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(WiredWithoutAToken.Keys, name => Assert.Contains(name, names));
    }

    [Fact]
    public void A_visible_preset_with_no_mcp_wiring_is_reported()
    {
        var unwired = new AgentDefinition(
            "newcli-headless", AgentMode.Headless, new AgentLaunch("newcli", ["-p", "{userPrompt}"]));
        var hidden = unwired with { Name = "newcli-hidden", Hidden = true };

        Assert.Equal(["newcli-headless"], Unwired([.. AgentCatalogFile.BuiltIns(), unwired, hidden]));
    }

    [Fact]
    public void Antigravity_is_neither_a_built_in_preset_nor_an_auth_probe()
    {
        Assert.DoesNotContain(AgentCatalogFile.BuiltIns(), p =>
            p.Launch.FileName == "agy" || p.Name.Contains("antigravity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("agy", AgentAuthProbe.LoadSpecs().Keys);
    }
}

/// <summary>
/// A PRESET'S IDLE CLOCK RESETS ON `progress`, NOT ON STDOUT. A spinner prints forever; a run that
/// only prints is stopped at its timeout, and a run whose member keeps calling progress is not.
/// </summary>
public sealed class IdleClockTests : IDisposable
{
    private static readonly ContainerId Worker = new("alpha", "worker");

    private readonly string _workspace = Directory.CreateTempSubdirectory("harness-idle-").FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_workspace);
        Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task Output_alone_does_not_hold_the_clock_open()
    {
        var heartbeat = new RunHeartbeat();
        var clock = Stopwatch.StartNew();

        var result = await RunAsync(heartbeat, "for i in $(seq 60); do echo spinner; sleep 0.1; done; echo finished");

        // Stopped by the one-second clock although it printed every tenth of a second.
        Assert.Equal(FailureClasses.Timeout, result.FailureClass);
        Assert.DoesNotContain("finished", result.Output, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Progress_resets_the_clock()
    {
        var heartbeat = new RunHeartbeat();
        using var stop = new CancellationTokenSource();
        var touching = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                heartbeat.Touch(Worker);
                try { await Task.Delay(200, stop.Token); }
                catch (OperationCanceledException) { }
            }
        }, TestContext.Current.CancellationToken);

        var result = await RunAsync(heartbeat, "sleep 3; echo finished");
        await stop.CancelAsync();
        await touching;

        Assert.True(result.Succeeded, result.LaunchError ?? result.Output);
        Assert.Contains("finished", result.Output, StringComparison.Ordinal);
    }

    private Task<AgentResult> RunAsync(RunHeartbeat heartbeat, string script)
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition(
                "probe", AgentMode.Headless,
                new AgentLaunch("bash", ["-c", script], LanguageModel: false),
                TimeoutSeconds: 1),
        ]);

        return new ProcessAgentRunner(catalog, heartbeat).RunAsync(new AgentInvocation(
            Worker, "You are a probe.", "go", _workspace, new Dictionary<string, string>(), Agent: "probe"),
            TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// AUTH IS PROBED UP FRONT, AND `authenticated: null` MEANS NOT MEASURED. A preset with no way to
/// ask - no probe for it, or not installed - reports null, never false.
/// </summary>
public sealed class AgentAuthProbeTests
{
    [Fact]
    public async Task A_preset_the_probe_cannot_ask_is_not_measured()
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("installed", AgentMode.Headless, new AgentLaunch("bash", [])),
            new AgentDefinition("missing", AgentMode.Headless, new AgentLaunch("harness-no-such-command-anywhere", [])),
        ]);

        var reports = await new AgentAuthProbe(catalog).ReportsAsync(TestContext.Current.CancellationToken);

        var installed = Assert.Single(reports, r => r.Agent == "installed");
        Assert.True(installed.Installed);
        Assert.Null(installed.Authenticated);

        var missing = Assert.Single(reports, r => r.Agent == "missing");
        Assert.False(missing.Installed);
        Assert.Null(missing.Authenticated);
    }
}

/// <summary>
/// CODEX REACHES THE PLATFORM BY TWO `-c` FLAGS, NOT BY `~/.codex/config.toml`, which every team
/// shares. The spelling is the whole of it: codex-cli 0.156.0 silently ignores a misspelt key, so a
/// typo here is a preset that launches, bills and never calls a tool.
/// </summary>
public sealed class CodexMcpWiringTests
{
    private static readonly string[] Wiring =
    [
        "-c", "mcp_servers.harness.url=\"{mcpUrl}\"",
        "-c", "mcp_servers.harness.env_http_headers={\"X-Api-Key\"=\"HARNESS_KEY\"}",
    ];

    [Fact]
    public void Both_codex_presets_carry_the_exact_mcp_flags()
    {
        var presets = AgentCatalogFile.BuiltIns().ToDictionary(a => a.Name);

        Assert.Equal(
            ["--dangerously-bypass-approvals-and-sandbox", .. Wiring],
            presets["codex"].Launch.Arguments);
        Assert.Equal(
            ["exec", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check", .. Wiring],
            presets["codex-headless"].Launch.Arguments);
    }

    [Fact]
    public void Every_seeded_codex_preset_is_wired()
    {
        var codex = AgentCatalogFile.BuiltIns().Where(a => a.Launch.FileName == "codex").ToList();
        Assert.NotEmpty(codex);

        Assert.All(codex, preset => Assert.Equal(
            Wiring, preset.Launch.Arguments.Skip(preset.Launch.Arguments.Count - Wiring.Length)));
    }

    [Fact]
    public void The_url_token_becomes_this_hosts_mcp_endpoint_and_no_key_is_on_the_command_line()
    {
        var config = McpLaunchConfig.TryWrite("http://127.0.0.1:8080/", "secret-platform-key", "DeveloperTomas")!;
        try
        {
            var argv = AgentCatalogFile.BuiltIns().Single(a => a.Name == "codex-headless").Launch.Arguments
                .Select(argument => McpLaunchConfig.Apply(argument, config))
                .ToList();

            Assert.Contains("mcp_servers.harness.url=\"http://127.0.0.1:8080/mcp\"", argv);
            Assert.DoesNotContain(argv, argument => argument.Contains("secret-platform-key", StringComparison.Ordinal));
            Assert.DoesNotContain(argv, argument => argument.Contains("{mcp", StringComparison.Ordinal));
        }
        finally
        {
            config.Delete();
        }
    }
}
