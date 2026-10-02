using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// AN ISSUED RUN, through the real <see cref="ProcessAgentRunner"/> and the real resolver over a real
/// store. The CLI is a fake named as a preset's command that writes what it was handed - its
/// environment, its HOME and what is in it - to a file OUTSIDE everything the run owns, and reads
/// <c>$HOME/.claude/.credentials.json</c> the way a CLI reads its login. The test's "shared home"
/// holds that file as a FIFO with no writer, so any open of it blocks and the run cannot finish.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class IssuedCredentialLaunchTests : IDisposable
{
    private const string Key = "fake-issued-key-0f3a9c";
    private const string HostKey = "fake-host-anthropic-key-77d1";
    private const string TeamKey = "fake-team-anthropic-key-2b8e";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-issued-").FullName;
    private readonly HashSet<string> _issued = new(StringComparer.OrdinalIgnoreCase);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Outside => Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;

    private string Seen => Path.Combine(Outside, "seen.txt");

    private string Workspace => Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;

    /// <summary>The home a home-source run inherits, holding a login file nobody may open.</summary>
    private string SharedHome => Path.Combine(_root, "shared-home");

    public void Dispose()
    {
        MemberTempCleanup.Remove(_root);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly IssuedCredential ClaudeLike = new(
        [new(IssuedCredential.ApiKey, "ANTHROPIC_API_KEY"), new(IssuedCredential.Token, "CLAUDE_CODE_OAUTH_TOKEN")],
        ["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN"],
        IssuedCredential.CredentialWins, "test", HomeVariables: ["CLAUDE_CONFIG_DIR"]);

    private static readonly IssuedCredential GrokLike = new(
        [new(IssuedCredential.ApiKey, "XAI_API_KEY")], ["XAI_API_KEY", "GROK_CODE_XAI_API_KEY"], IssuedCredential.LoginWins, "test",
        HomeVariables: ["GROK_HOME"]);

    /// <summary>A fake CLI called <paramref name="command"/>, the presets that launch it, and the
    /// runner and store a run of them goes through.</summary>
    private async Task<(ProcessAgentRunner Runner, AgentCredentialStore Store, IRunCredentials Credentials)> SetUpAsync(
        string command = "claude", IssuedCredential? declaration = null, int exitCode = 0)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script and the login a FIFO.");

        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var program = Path.Combine(bin, command);
        await TestExecutable.WriteAsync(program,
            "#!/bin/sh\n"
            + $"{{ echo \"HOME=$HOME\"; echo \"TMPDIR=$TMPDIR\"; echo '--- env'; env | sort; echo '--- home'; ls -A \"$HOME\"; "
            + "if [ -f \"$HOME/.grok/config.toml\" ]; then echo '--- grok'; cat \"$HOME/.grok/config.toml\"; fi; "
            + $"}} > '{Seen}'\n"
            // A CLI reads its login from its home. In the shared home that is a FIFO nobody writes.
            + "if [ -e \"$HOME/.claude/.credentials.json\" ]; then cat \"$HOME/.claude/.credentials.json\" > /dev/null; fi\n"
            + $"exit {exitCode}\n");

        Directory.CreateDirectory(Path.Combine(SharedHome, ".claude"));
        var login = Path.Combine(SharedHome, ".claude", ".credentials.json");
        if (!File.Exists(login)) Run("mkfifo", login);

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("fake-headless", AgentMode.Headless,
                new AgentLaunch(program, ["-p", "{userPrompt}"], LanguageModel: true),
                IssuedCredential: declaration ?? ClaudeLike),
            new AgentDefinition("fake-other", AgentMode.Headless,
                new AgentLaunch("grok", ["-p", "{userPrompt}"]),
                IssuedCredential: GrokLike),
        ]);

        var database = Path.Combine(_root, "messages.db");
        await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);
        var store = new AgentCredentialStore(database, new EphemeralDataProtectionProvider());
        var credentials = new RunCredentials(catalog, p => _issued.Contains(p) ? CredentialSource.Issued : CredentialSource.Home, store);

        return (new ProcessAgentRunner(catalog, new RunHeartbeat(), credentials: credentials), store, credentials);
    }

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
        // The FIFO blocks any open of the shared home's login: a run that reads it never ends.
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

    private IReadOnlyList<string> SeenHomeListing()
    {
        var lines = File.ReadAllLines(Seen);
        var start = Array.IndexOf(lines, "--- home") + 1;
        var end = Array.IndexOf(lines, "--- grok");
        return lines[start..(end < 0 ? lines.Length : end)];
    }

    [Fact]
    public async Task An_issued_run_gets_its_credential_and_a_home_of_its_own_and_opens_no_login_file_of_the_shared_home()
    {
        var (runner, store, _) = await SetUpAsync();
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        var result = await RunAsync(runner, Invocation());

        Assert.True(result.Succeeded, result.LaunchError);
        var seen = SeenEnvironment();

        Assert.Equal(Key, seen["ANTHROPIC_API_KEY"]);

        // A home of its own, inside this member's TMPDIR, holding nothing but what the CLI wrote.
        var home = seen["HOME"];
        Assert.StartsWith(seen["TMPDIR"] + Path.DirectorySeparatorChar + RunHome.Prefix, home);
        Assert.NotEqual(SharedHome, home);
        Assert.Empty(SeenHomeListing());

        // Nothing points it back at the shared home.
        Assert.DoesNotContain(seen, e => e.Value.StartsWith(SharedHome, StringComparison.Ordinal));
        Assert.False(seen.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal(Path.Combine(seen["TMPDIR"], RunHome.CacheFolder), seen["XDG_CACHE_HOME"]);
    }

    [Fact]
    public async Task The_issued_credential_is_set_after_the_presets_and_teams_env_and_every_displaced_variable_is_removed()
    {
        var (runner, store, _) = await SetUpAsync();
        await store.SetAsync("claude", ClaudeLike.Kinds[1], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        using (new EnvironmentScope([new("ANTHROPIC_API_KEY", HostKey)]))
        {
            var result = await RunAsync(runner, Invocation());
            Assert.True(result.Succeeded, result.LaunchError);
        }

        var seen = SeenEnvironment();

        // The token kind lands in its own variable; the key the team and the Host held, which the
        // CLI would prefer, and the preset's gateway token are all gone.
        Assert.Equal(Key, seen["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.False(seen.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(seen.ContainsKey("ANTHROPIC_AUTH_TOKEN"));

        // Everything else of the preset's and the team's is still there.
        Assert.Equal("preset", seen["PRESET_VAR"]);
        Assert.Equal("team", seen["TEAM_VAR"]);
    }

    [Fact]
    public async Task Every_other_providers_key_is_still_scoped_away_under_issued()
    {
        var (runner, store, _) = await SetUpAsync();
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        using (new EnvironmentScope(
        [
            new("XAI_API_KEY", "fake-xai-1"), new("GROK_CODE_XAI_API_KEY", "fake-xai-2"),
            new("OPENAI_API_KEY", "fake-openai-1"), new("GEMINI_API_KEY", "fake-gemini-1"),
        ]))
        {
            var result = await RunAsync(runner, Invocation(new Dictionary<string, string> { ["GROK_HOME"] = "/elsewhere" }));
            Assert.True(result.Succeeded, result.LaunchError);
        }

        var seen = SeenEnvironment();
        Assert.False(seen.ContainsKey("XAI_API_KEY"));
        // Declared by another command's preset only, so no older scoping knew it: issued runs drop it.
        Assert.False(seen.ContainsKey("GROK_CODE_XAI_API_KEY"));
        Assert.False(seen.ContainsKey("OPENAI_API_KEY"));
        Assert.False(seen.ContainsKey("GEMINI_API_KEY"));

        // Another command's home variable is another provider's, and the team handed it in, so it
        // stays as every handed-in key does; this preset's own home variable never does.
        Assert.Equal("/elsewhere", seen["GROK_HOME"]);
        Assert.Equal(Key, seen["ANTHROPIC_API_KEY"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task The_per_run_home_is_removed_when_the_run_ends_whether_it_succeeded_or_failed(int exitCode)
    {
        var (runner, store, _) = await SetUpAsync(exitCode: exitCode);
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        var result = await RunAsync(runner, Invocation());
        Assert.Equal(exitCode, result.ExitCode);

        var home = SeenEnvironment()["HOME"];
        Assert.False(Directory.Exists(home), $"{home} is still there after the run.");
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(home)!, RunHome.Prefix + "*"));
    }

    [Fact]
    public async Task A_home_left_by_a_stopped_host_is_removed_before_the_next_issued_run()
    {
        var (runner, store, _) = await SetUpAsync();
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        Assert.True((await RunAsync(runner, Invocation())).Succeeded);
        var temp = SeenEnvironment()["TMPDIR"];
        var stale = Directory.CreateDirectory(Path.Combine(temp, RunHome.Prefix + "abandoned000")).FullName;
        File.WriteAllText(Path.Combine(stale, "left"), "x");

        Assert.True((await RunAsync(runner, Invocation())).Succeeded);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task An_issued_run_with_no_credential_set_does_not_start()
    {
        var (runner, _, _) = await SetUpAsync();
        _issued.Add("fake-headless");

        var result = await RunAsync(runner, Invocation());

        Assert.False(result.Succeeded);
        Assert.Contains("is not set", result.LaunchError);
        Assert.Contains("never falls back to the shared home", result.LaunchError);
        Assert.False(File.Exists(Seen), "The CLI was started.");
    }

    [Fact]
    public async Task A_credential_resolved_at_run_start_is_the_one_the_run_uses()
    {
        var (runner, store, credentials) = await SetUpAsync();
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        // Resolved by the member runner and carried on the invocation; clearing the store after
        // that does not reach this run.
        var resolved = await credentials.ResolveAsync("fake-headless", null, Ct);
        await store.ClearAsync("claude", new CredentialActor("u1", "person@example.test"), Ct);

        var result = await RunAsync(runner, Invocation() with { Credential = resolved });

        Assert.True(result.Succeeded, result.LaunchError);
        Assert.Equal(Key, SeenEnvironment()["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public async Task Grok_harness_entry_is_written_to_the_runs_own_home()
    {
        var (runner, store, _) = await SetUpAsync("grok", GrokLike);
        await store.SetAsync("grok", GrokLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        var shared = Path.Combine(SharedHome, ".grok", "config.toml");
        var result = await RunAsync(runner, Invocation(new Dictionary<string, string>
        {
            ["HARNESS_URL"] = "http://127.0.0.1:5999",
            ["HARNESS_KEY"] = "fake-platform-key",
        }));

        Assert.True(result.Succeeded, result.LaunchError);
        var text = File.ReadAllText(Seen);
        Assert.Contains("[mcp_servers.harness]", text);
        Assert.Contains("http://127.0.0.1:5999/mcp", text);
        Assert.Equal([".grok"], SeenHomeListing());
        Assert.False(File.Exists(shared));
    }

    [Fact]
    public async Task A_home_preset_launches_with_the_inherited_home_and_unchanged_environment()
    {
        var (runner, store, _) = await SetUpAsync();
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);

        // No FIFO this time: a home run reads the shared home's login, and this one is a file.
        File.Delete(Path.Combine(SharedHome, ".claude", ".credentials.json"));
        File.WriteAllText(Path.Combine(SharedHome, ".claude", ".credentials.json"), "{}");

        var result = await RunAsync(runner, Invocation());
        Assert.True(result.Succeeded, result.LaunchError);

        var seen = SeenEnvironment();
        Assert.Equal(SharedHome, seen["HOME"]);
        Assert.Equal(TeamKey, seen["ANTHROPIC_API_KEY"]);
        Assert.Equal("from-the-preset", seen["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal(Path.Combine(SharedHome, ".claude"), seen["CLAUDE_CONFIG_DIR"]);
        Assert.NotEqual(Path.Combine(seen["TMPDIR"], RunHome.CacheFolder), seen.GetValueOrDefault("XDG_CACHE_HOME"));
        Assert.Empty(Directory.EnumerateDirectories(seen["TMPDIR"], RunHome.Prefix + "*"));
    }

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
