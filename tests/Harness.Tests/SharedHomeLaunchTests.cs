using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// A RUN ON THE SHARED HOME, through the real <see cref="ProcessAgentRunner"/>. The CLI is a fake
/// named as a preset's command that writes what it was handed - its environment, its HOME and what is
/// in it - to a file OUTSIDE everything the run owns, and reads <c>$HOME/.claude/.credentials.json</c>
/// the way a CLI reads its login.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SharedHomeLaunchTests : IDisposable
{
    private const string HostKey = "fake-host-anthropic-key-77d1";
    private const string TeamKey = "fake-team-anthropic-key-2b8e";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-shared-home-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Outside => Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;

    private string Seen => Path.Combine(Outside, "seen.txt");

    private string Workspace => Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;

    /// <summary>The home a run inherits.</summary>
    private string SharedHome => Path.Combine(_root, "shared-home");

    public void Dispose()
    {
        MemberTempCleanup.Remove(_root);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly CredentialVariables ClaudeLike =
        new(["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN"], "test");

    private static readonly CredentialVariables GrokLike = new(["XAI_API_KEY", "GROK_CODE_XAI_API_KEY"], "test");

    /// <summary>A fake CLI called <paramref name="command"/>, the presets that launch it, and the
    /// runner a run of them goes through.</summary>
    private async Task<ProcessAgentRunner> SetUpAsync(
        string command = "claude", int exitCode = 0, AgentLiveView? liveView = null, bool hold = false)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var program = Path.Combine(bin, command);
        await TestExecutable.WriteAsync(program,
            "#!/bin/sh\n"
            + $"{{ echo \"HOME=$HOME\"; echo \"TMPDIR=$TMPDIR\"; echo '--- env'; env | sort; echo '--- home'; ls -A \"$HOME\"; "
            + "if [ -f \"$HOME/.grok/config.toml\" ]; then echo '--- grok'; cat \"$HOME/.grok/config.toml\"; fi; "
            + $"}} > '{Seen}'\n"
            // A CLI reads its login from its home.
            + "if [ -e \"$HOME/.claude/.credentials.json\" ]; then cat \"$HOME/.claude/.credentials.json\" > /dev/null; fi\n"
            // A transcript in its home, where a session id names it; a plugin listing that lists none.
            + "if [ \"$3\" = --session-id ]; then mkdir -p \"$HOME/t\"; echo '{\"type\":\"user\"}' > \"$HOME/t/$4.jsonl\"; fi\n"
            + "if [ \"$1\" = plugin ]; then echo '[]'; fi\n"
            // A run still going when it is stopped.
            + (hold ? "sleep 30\n" : "")
            + $"exit {exitCode}\n");

        Directory.CreateDirectory(Path.Combine(SharedHome, ".claude"));
        File.WriteAllText(Path.Combine(SharedHome, ".claude", ".credentials.json"), "{}");

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("fake-headless", AgentMode.Headless,
                new AgentLaunch(program, liveView is null ? ["-p", "{userPrompt}"] : ["-p", "{userPrompt}", "--session-id", "{sessionId}"],
                    LanguageModel: true),
                CredentialVariables: ClaudeLike,
                LiveView: liveView),
            new AgentDefinition("fake-other", AgentMode.Headless,
                new AgentLaunch("grok", ["-p", "{userPrompt}"]),
                CredentialVariables: GrokLike),
        ]);

        _catalog = catalog;
        return new ProcessAgentRunner(catalog, new RunHeartbeat());
    }

    private AgentCatalog? _catalog;

    private AgentInvocation Invocation(IReadOnlyDictionary<string, string>? extra = null)
    {
        var environment = new Dictionary<string, string>
        {
            // What the container's environment holds: the preset's env, then the team's.
            ["PRESET_VAR"] = "preset",
            ["ANTHROPIC_AUTH_TOKEN"] = "from-the-preset",
            ["HOME"] = SharedHome,
            ["TEAM_VAR"] = "team",
            ["ANTHROPIC_API_KEY"] = TeamKey,
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(SharedHome, ".claude"),
        };
        foreach (var (name, value) in extra ?? new Dictionary<string, string>()) environment[name] = value;

        return new AgentInvocation(
            new ContainerId("Team", "Dev"), "You are a test.", "work", Workspace, environment, Agent: "fake-headless");
    }

    private async Task<AgentResult> RunAsync(ProcessAgentRunner runner, AgentInvocation invocation)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(60));
        return await runner.RunAsync(invocation, bounded.Token);
    }

    private Dictionary<string, string> SeenEnvironment()
    {
        var lines = File.ReadAllLines(Seen);
        var start = Array.IndexOf(lines, "--- env") + 1;
        var end = Array.IndexOf(lines, "--- home");
        return lines[start..end]
            .Where(l => l.Contains('='))
            .Select(l => l.Split('=', 2))
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);
    }


    [Fact]
    public async Task A_run_launches_with_the_inherited_home_and_unchanged_environment()
    {
        var runner = await SetUpAsync();

        var result = await RunAsync(runner, Invocation());
        Assert.True(result.Succeeded, result.LaunchError);

        var seen = SeenEnvironment();
        Assert.Equal(SharedHome, seen["HOME"]);
        Assert.Equal(TeamKey, seen["ANTHROPIC_API_KEY"]);
        Assert.Equal("from-the-preset", seen["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal(Path.Combine(SharedHome, ".claude"), seen["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public async Task A_runs_transcript_stays_where_the_cli_wrote_it()
    {
        var runner = await SetUpAsync(liveView: new AgentLiveView("~/t/{sessionId}.jsonl", LiveView.ClaudeJsonl));

        var result = await RunAsync(runner, Invocation());
        Assert.True(result.Succeeded, result.LaunchError);

        var transcript = Assert.IsType<AgentTranscript>(result.AgentTranscript);
        Assert.Equal(Path.Combine(SharedHome, "t"), Path.GetDirectoryName(transcript.Path));
        Assert.True(File.Exists(transcript.Path));
    }

    [Fact]
    public async Task The_tool_listing_of_a_preset_runs_on_the_hosts_home()
    {
        await SetUpAsync();

        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain-home")).FullName;

        using (new EnvironmentScope([new("HOME", plain), new("ANTHROPIC_API_KEY", HostKey)]))
        {
            await ListedAsync(new CliListingRunner(null));
        }

        var seen = SeenEnvironment();
        Assert.Equal(plain, seen["HOME"]);
        Assert.Equal(HostKey, seen["ANTHROPIC_API_KEY"]);
    }

    private async Task<IReadOnlyList<PresetToolReport>> ListedAsync(IListingRunner runner)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(60));
        // The fake CLI's preset only: the other names a real CLI on PATH, which no test runs.
        return await AgentToolPreflight.ReportsAsync(
            [.. _catalog!.Definitions.Where(d => d.Name == "fake-headless")], runner, bounded.Token);
    }

}
