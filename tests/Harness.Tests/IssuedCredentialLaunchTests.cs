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
        string command = "claude", IssuedCredential? declaration = null, int exitCode = 0, AgentLiveView? liveView = null,
        bool hold = false)
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
            // A transcript in its home, where a session id names it; a plugin listing that lists none.
            + "if [ \"$3\" = --session-id ]; then mkdir -p \"$HOME/t\"; echo '{\"type\":\"user\"}' > \"$HOME/t/$4.jsonl\"; fi\n"
            + "if [ \"$1\" = plugin ]; then echo '[]'; fi\n"
            // A run still going when it is stopped.
            + (hold ? "sleep 30\n" : "")
            + $"exit {exitCode}\n");

        Directory.CreateDirectory(Path.Combine(SharedHome, ".claude"));
        var login = Path.Combine(SharedHome, ".claude", ".credentials.json");
        if (!File.Exists(login)) Run("mkfifo", login);

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("fake-headless", AgentMode.Headless,
                new AgentLaunch(program, liveView is null ? ["-p", "{userPrompt}"] : ["-p", "{userPrompt}", "--session-id", "{sessionId}"],
                    LanguageModel: true),
                IssuedCredential: declaration ?? ClaudeLike,
                LiveView: liveView),
            new AgentDefinition("fake-other", AgentMode.Headless,
                new AgentLaunch("grok", ["-p", "{userPrompt}"]),
                IssuedCredential: GrokLike),
        ]);

        var database = Path.Combine(_root, "messages.db");
        await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);
        var store = new AgentCredentialStore(database, new EphemeralDataProtectionProvider());
        var credentials = new RunCredentials(catalog, p => _issued.Contains(p) ? CredentialSource.Issued : CredentialSource.Home, store);

        _catalog = catalog;
        return (new ProcessAgentRunner(catalog, new RunHeartbeat(), credentials: credentials), store, credentials);
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
    public async Task An_issued_run_cancelled_mid_launch_leaves_no_home()
    {
        var (runner, store, _) = await SetUpAsync(hold: true);
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync(Invocation(), stop.Token);

        // Stopped once the CLI is running in its home.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(Seen) || !File.ReadAllText(Seen).Contains("--- home", StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, "The CLI never started.");
            await Task.Delay(50, Ct);
        }

        var home = SeenEnvironment()["HOME"];
        Assert.True(Directory.Exists(home));

        await stop.CancelAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run may end by throwing.
        }

        Assert.False(Directory.Exists(home), $"{home} is still there after the run was cancelled.");
        Assert.Empty(Directory.EnumerateDirectories(SeenEnvironment()["TMPDIR"], RunHome.Prefix + "*"));
    }

    [Fact]
    public async Task A_home_whose_making_is_cancelled_or_fails_after_it_was_begun_is_not_left()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes.");
        var parent = Directory.CreateDirectory(Path.Combine(_root, "member-temp")).FullName;

        // Cancelled once the folder is made.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunHome.CreateAsync(parent, null, null, cancelled.Token));
        Assert.Empty(Directory.EnumerateDirectories(parent, RunHome.Prefix + "*"));

        // Failed after the folder is made: the grok entry to copy in is not there.
        Assert.Null(await RunHome.CreateAsync(parent, null, Path.Combine(_root, "no-such-config.toml"), Ct));
        Assert.Empty(Directory.EnumerateDirectories(parent, RunHome.Prefix + "*"));
    }

    [Fact]
    public async Task Shared_homes_left_behind_are_swept_at_start_and_before_the_next_but_never_a_live_or_recent_one()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes.");
        var shared = Directory.CreateDirectory(Path.Combine(_root, "shared")).FullName;

        var old = Directory.CreateDirectory(Path.Combine(shared, RunHome.Prefix + "old000000000")).FullName;
        File.WriteAllText(Path.Combine(old, "left"), "x");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - RunHome.SharedLeftoverAge - TimeSpan.FromMinutes(1));
        var recent = Directory.CreateDirectory(Path.Combine(shared, RunHome.Prefix + "recent000000")).FullName;

        // Before a new one is made: the old leftover goes, the recent one stays.
        var live = await RunHome.CreateAsync(shared, null, null, Ct, memberFolder: false);
        Assert.NotNull(live);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));

        // At Host start, as Program calls it, later on: what nobody owns goes, the live one stays.
        Directory.SetLastWriteTimeUtc(live, DateTime.UtcNow - RunHome.SharedLeftoverAge - TimeSpan.FromMinutes(1));
        await RunHome.SweepSharedAsync(shared, null, Ct, now: DateTime.UtcNow + RunHome.SharedLeftoverAge + TimeSpan.FromMinutes(1));
        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(recent));

        await RunHome.RemoveAsync(live, null);
        Assert.False(Directory.Exists(live));
    }

    [Fact]
    public async Task Kept_transcripts_older_than_the_bound_are_pruned_when_another_is_kept()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes.");
        var temp = Directory.CreateDirectory(Path.Combine(_root, "member-temp")).FullName;
        var kept = Directory.CreateDirectory(Path.Combine(temp, RunHome.TranscriptsFolder)).FullName;

        var old = Path.Combine(kept, "home-aaaaaaaaaaaa-old.jsonl");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - RunHome.TranscriptsKept - TimeSpan.FromDays(1));
        var recent = Path.Combine(kept, "home-bbbbbbbbbbbb-recent.jsonl");
        File.WriteAllText(recent, "{}");
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow - TimeSpan.FromDays(1));

        var home = await RunHome.CreateAsync(temp, null, null, Ct);
        Assert.NotNull(home);
        var written = Path.Combine(Directory.CreateDirectory(Path.Combine(home, "t")).FullName, "s.jsonl");
        File.WriteAllText(written, "{\"type\":\"user\"}");

        var moved = await RunHome.KeepTranscriptAsync(new AgentTranscript(written, LiveView.ClaudeJsonl), home, temp, null);
        await RunHome.RemoveAsync(home, null);

        Assert.True(File.Exists(moved!.Path));
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(old));
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

    [Fact]
    public async Task An_issued_runs_transcript_is_still_readable_after_the_run_ends_and_its_home_is_removed()
    {
        var (runner, store, _) = await SetUpAsync(liveView: new AgentLiveView("~/t/{sessionId}.jsonl", LiveView.ClaudeJsonl));
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        var result = await RunAsync(runner, Invocation());
        Assert.True(result.Succeeded, result.LaunchError);

        var seen = SeenEnvironment();
        var home = seen["HOME"];
        Assert.False(Directory.Exists(home), $"{home} is still there after the run.");

        // The terminal row names a copy beside the run homes, outside the removed one, and the
        // finished-run view reads it as it reads any other.
        var transcript = Assert.IsType<AgentTranscript>(result.AgentTranscript);
        Assert.Equal(LiveView.ClaudeJsonl, transcript.Format);
        Assert.Equal(Path.Combine(seen["TMPDIR"], RunHome.TranscriptsFolder), Path.GetDirectoryName(transcript.Path));
        Assert.False(transcript.Path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        var read = await AgentFiles.ReadAllAsync(transcript.Path, null, Ct);
        Assert.Equal("{\"type\":\"user\"}", read.Text?.Trim());
    }

    [Fact]
    public async Task A_home_runs_transcript_stays_where_the_cli_wrote_it()
    {
        var (runner, _, _) = await SetUpAsync(liveView: new AgentLiveView("~/t/{sessionId}.jsonl", LiveView.ClaudeJsonl));
        File.Delete(Path.Combine(SharedHome, ".claude", ".credentials.json"));
        File.WriteAllText(Path.Combine(SharedHome, ".claude", ".credentials.json"), "{}");

        var result = await RunAsync(runner, Invocation());
        Assert.True(result.Succeeded, result.LaunchError);

        var transcript = Assert.IsType<AgentTranscript>(result.AgentTranscript);
        Assert.Equal(Path.Combine(SharedHome, "t"), Path.GetDirectoryName(transcript.Path));
        Assert.True(File.Exists(transcript.Path));
    }

    [Fact]
    public async Task The_tool_listing_of_an_issued_preset_runs_with_its_credential_in_a_home_of_its_own_removed_after()
    {
        var (_, store, credentials) = await SetUpAsync();
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);
        _issued.Add("fake-headless");

        var homes = Directory.CreateDirectory(Path.Combine(_root, "homes")).FullName;

        // The Host's own environment: the shared home, a key of its own, and another provider's.
        using (new EnvironmentScope(
               [
                   new("HOME", SharedHome), new("ANTHROPIC_API_KEY", HostKey), new("ANTHROPIC_AUTH_TOKEN", "fake-host-token"),
                   new("CLAUDE_CONFIG_DIR", Path.Combine(SharedHome, ".claude")), new("XAI_API_KEY", "fake-xai-1"),
               ]))
        {
            var reports = await ListedAsync(new CliListingRunner(null, homes), credentials);
            var report = Assert.Single(reports, r => r.Preset == "fake-headless");
            Assert.True(report.Verdict != ToolVerdicts.NotMeasured, report.Detail);
        }

        var seen = SeenEnvironment();
        Assert.Equal(Key, seen["ANTHROPIC_API_KEY"]);
        Assert.False(seen.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        Assert.False(seen.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.False(seen.ContainsKey("XAI_API_KEY"));

        Assert.StartsWith(homes + Path.DirectorySeparatorChar + RunHome.Prefix, seen["HOME"]);
        Assert.Empty(SeenHomeListing());
        Assert.Empty(Directory.EnumerateDirectories(homes, RunHome.Prefix + "*"));

        // One cache beside the listing homes, kept across listings, not one in each fresh home.
        Assert.Equal(Path.Combine(homes, RunHome.CacheFolder), seen["XDG_CACHE_HOME"]);
        Assert.True(Directory.Exists(seen["XDG_CACHE_HOME"]));
    }

    [Fact]
    public async Task The_tool_listing_of_a_home_preset_runs_on_the_hosts_home_as_before()
    {
        var (_, store, credentials) = await SetUpAsync();
        await store.SetAsync("claude", ClaudeLike.Kinds[0], Key, new CredentialActor("u1", "person@example.test"), Ct);

        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain-home")).FullName;
        var homes = Directory.CreateDirectory(Path.Combine(_root, "homes")).FullName;

        using (new EnvironmentScope([new("HOME", plain), new("ANTHROPIC_API_KEY", HostKey)]))
        {
            await ListedAsync(new CliListingRunner(null, homes), credentials);
        }

        var seen = SeenEnvironment();
        Assert.Equal(plain, seen["HOME"]);
        Assert.Equal(HostKey, seen["ANTHROPIC_API_KEY"]);
        Assert.Empty(Directory.EnumerateDirectories(homes));
    }

    private async Task<IReadOnlyList<PresetToolReport>> ListedAsync(IListingRunner runner, IRunCredentials credentials)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(60));
        // The fake CLI's preset only: the other names a real CLI on PATH, which no test runs.
        return await AgentToolPreflight.ReportsAsync(
            [.. _catalog!.Definitions.Where(d => d.Name == "fake-headless")], runner, bounded.Token, credentials);
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
