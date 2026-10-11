using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// COPILOT REACHES THE PLATFORM THROUGH `--additional-mcp-config @{mcpConfig}`. Without it a
/// copilot member never calls a tool and every run fails the credential-use check by design. The
/// flag is per launch; `~/.copilot/mcp-config.json` is shared by every team in the one agent home
/// and is not written. The file it names is McpLaunchConfig's `mcp.json`, which holds the variable
/// name, not the key, and is removed when the run ends.
/// </summary>
public sealed class CopilotMcpWiringTests : IDisposable
{
    private static readonly ContainerId Worker = new("alpha", "worker");

    private readonly string _workspace = Directory.CreateTempSubdirectory("harness-copilot-mcp-").FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_workspace);
        Directory.Delete(_workspace, recursive: true);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("copilot-headless")]
    public void Every_copilot_preset_passes_this_launchs_mcp_file(string name)
    {
        var arguments = Preset(name).Launch.Arguments.ToList();

        var at = arguments.IndexOf("--additional-mcp-config");
        Assert.True(at >= 0 && at + 1 < arguments.Count && arguments[at + 1] == "@{mcpConfig}",
            $"{name}: {string.Join(' ', arguments)}");
        Assert.Single(arguments, a => a == "--additional-mcp-config");
    }

    /// <summary>
    /// `auto` lets Copilot pick among the account's models; a named one (`gpt-5.4`) is refused on
    /// an account without it. `auto` in turn refuses `--reasoning-effort`: measured with 1.0.88, a
    /// headless run answered only "Model "auto" does not support reasoning effort configuration".
    /// </summary>
    [Theory]
    [InlineData("copilot")]
    [InlineData("copilot-headless")]
    public void Every_copilot_preset_runs_on_auto_and_asks_no_reasoning_effort(string name)
    {
        var arguments = Preset(name).Launch.Arguments.ToList();

        var at = arguments.IndexOf("--model");
        Assert.True(at >= 0 && at + 1 < arguments.Count && arguments[at + 1] == "auto",
            $"{name}: {string.Join(' ', arguments)}");
        Assert.DoesNotContain("--reasoning-effort", arguments);
    }

    [Fact]
    public void The_headless_prompt_is_still_the_last_argument()
    {
        // Anything after `-p <long prompt>` is dropped by the CLI; the MCP flag must sit before it.
        var arguments = Preset("copilot-headless").Launch.Arguments.ToList();

        Assert.Equal(["-p", "{userPrompt}"], arguments[^2..]);
        Assert.True(arguments.IndexOf("--additional-mcp-config") < arguments.Count - 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task The_file_the_flag_names_holds_no_key_lives_outside_the_workspace_and_is_gone_after_the_run(int exitCode)
    {
        // The built-in arguments, handed to a stand-in that reports what the flag resolved to.
        var arguments = Preset("copilot-headless").Launch.Arguments;
        const string script =
            """
            while [ $# -gt 0 ]; do
              if [ "$1" = "--additional-mcp-config" ]; then echo "flag=$2"; cat "${2#@}"; echo; fi
              shift
            done
            exit "$EXIT_WITH"
            """;

        var catalog = new AgentCatalog(
        [
            new AgentDefinition(
                "copilot-standin", AgentMode.Headless,
                new AgentLaunch("bash", ["-c", script, "copilot", .. arguments], LanguageModel: false),
                TimeoutSeconds: 30),
        ]);

        var environment = new Dictionary<string, string>
        {
            ["HARNESS_URL"] = "http://127.0.0.1:5391",
            ["HARNESS_KEY"] = "secret-platform-key",
            ["HARNESS_MEMBER"] = "DeveloperInes",
            ["EXIT_WITH"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var result = await new ProcessAgentRunner(catalog, new RunHeartbeat()).RunAsync(
            new AgentInvocation(Worker, "You are a probe.", "go", _workspace, environment, Agent: "copilot-standin"),
            TestContext.Current.CancellationToken);

        Assert.Equal(exitCode, result.ExitCode);

        var flag = result.Output.Split('\n').Single(line => line.StartsWith("flag=@", StringComparison.Ordinal));
        var path = flag["flag=@".Length..].Trim();

        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "harness-mcp", "DeveloperInes-"), path, StringComparison.Ordinal);
        Assert.False(path.StartsWith(_workspace, StringComparison.Ordinal), path);
        Assert.Contains("\"url\":\"http://127.0.0.1:5391/mcp\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"X-Api-Key\":\"${HARNESS_KEY}\"", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-platform-key", result.Output, StringComparison.Ordinal);

        Assert.False(File.Exists(path), path);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)), path);
    }

    private static AgentDefinition Preset(string name) =>
        AgentCatalogFile.BuiltIns().Single(a => a.Name == name);
}
