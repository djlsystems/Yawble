namespace Yawble.Core.Bus;

/// <summary>
/// Starts the core's one bus engine over the app database. The runner is chosen by YAWBLE_RUNNER:
/// unset → the core's sandbox launcher, or <see cref="UnavailableRunner"/> when it has none; "fake-hang" or
/// "fake-complete" → <see cref="FakeAgentRunner"/>, which exists only so tests can drive and kill the real core process.
/// </summary>
public static class BusHost
{
    public const string RunnerVariable = "YAWBLE_RUNNER";

    /// <summary>
    /// The app data root for a --data-dir: the shell passes &lt;userData&gt;/data, and team repositories and member
    /// clones live beside it under &lt;userData&gt;/teams (Policy.RunLayout). Any other folder name means no app root
    /// (a test's temp folder), and runs get no team repository.
    /// </summary>
    public static string? AppRootFor(string dataDir)
    {
        var full = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(full) == "data" ? Path.GetDirectoryName(full) : null;
    }

    public static Task<BusEngine> StartAsync(string connectionString, Func<string, string?> env, Action<string>? log = null) =>
        StartAsync(connectionString, env, log, null, null);

    public static Task<BusEngine> StartAsync(string connectionString, Func<string, string?> env, Action<string>? log, IAgentRunner? launcher) =>
        StartAsync(connectionString, env, log, launcher, null, null);

    /// <summary>
    /// <paramref name="runner"/> replaces the YAWBLE_RUNNER choice when given (the core's fake-pty test
    /// runner), and <paramref name="maxConcurrentAgents"/> overrides the run limit (default: app.maxConcurrentAgents of the global policy).
    /// </summary>
    public static Task<BusEngine> StartAsync(
        string connectionString, Func<string, string?> env, Action<string>? log, IAgentRunner? runner, Func<int>? maxConcurrentAgents) =>
        StartAsync(connectionString, env, log, launcher: null, runner, maxConcurrentAgents);

    /// <summary>
    /// The real launcher is the default runner; <paramref name="runner"/> (fake-pty) and the run limit override.
    /// With <paramref name="appDataRoot"/>, the launcher (or an YAWBLE_RUNNER fake) is wrapped in
    /// <see cref="Teams.TeamRepoRunner"/>, so every run works in the member's own clone of the team repository and
    /// its branch is fetched back afterwards. An explicit <paramref name="runner"/> (the UI's fake-pty) is not
    /// wrapped: it starts no agent and touches no repository.
    /// </summary>
    public static async Task<BusEngine> StartAsync(
        string connectionString, Func<string, string?> env, Action<string>? log, IAgentRunner? launcher, IAgentRunner? runner, Func<int>? maxConcurrentAgents,
        string? appDataRoot = null, Skills.SkillCatalog? skills = null,
        Func<CancellationToken, Task<IReadOnlyList<AgentChoice>>>? availableAgents = null)
    {
        var chosen = runner ?? env(RunnerVariable) switch
        {
            "fake-hang" => new FakeAgentRunner(FakeAgentRunner.Mode.Hang),
            "fake-complete" => new FakeAgentRunner(FakeAgentRunner.Mode.Complete),
            _ => launcher ?? new UnavailableRunner(),
        };
        // One TeamRepos per core: its per-team lock serialises runs' fetch-back and push with the Git dialog's actions.
        var repos = appDataRoot is null ? null : new Teams.TeamRepos(appDataRoot, connectionString, log: log);
        if (runner is null && repos is not null && chosen is not UnavailableRunner)
        {
            // Every run works in the member's own clone; its branch is fetched into the team repo afterwards.
            using (var store = new BusStore(connectionString)) await repos.FetchBackAllAsync(store.AllMembers());
            chosen = new Teams.TeamRepoRunner(chosen, repos, log);
        }
        // ONE global run limit for every team, member run, Manager run and terminal session. Unless the
        // caller gives one, it is app.maxConcurrentAgents of the saved global policy, read at every admission.
        var engine = new BusEngine(new BusStore(connectionString), chosen, new BusOptions
        {
            Log = log,
            MaxConcurrentAgents = maxConcurrentAgents ?? Scheduling.PolicyRunLimit.From(connectionString, log),
            HeavyWorkAtOnce = Scheduling.PolicyRunLimit.HeavyFrom(connectionString),
            Skills = skills,
            AvailableAgents = availableAgents,
            Repos = repos,
        });
        await engine.StartAsync();
        return engine;
    }
}

/// <summary>The runner of a core with no launcher: every run fails at once with a reason the card can show.</summary>
public sealed class UnavailableRunner(string? reason = null) : IAgentRunner
{
    public Task<RunResult> RunAsync(RunContext run, CancellationToken ct) =>
        Task.FromResult(RunResult.Fail(reason ?? "no agent launcher in this build"));
}

/// <summary>Test-only runner (no sandbox, no CLI). Hang: runs until the core stops. Complete: a member hands back, then the run ends.</summary>
public sealed class FakeAgentRunner(FakeAgentRunner.Mode mode) : IAgentRunner
{
    public enum Mode { Hang, Complete }

    public async Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
    {
        if (mode == Mode.Hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        if (run.Member.Role == MemberRole.Member) await run.HandbackAsync($"fake run {run.RunId} done", ct);
        return RunResult.Ok;
    }
}
