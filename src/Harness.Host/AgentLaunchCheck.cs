namespace Harness.Host;

/// <summary>
/// What the launch check says about one preset, as <c>GET /api/agents/auth</c> carries it in
/// <c>launch</c>. <see cref="Result"/> is <see cref="Ok"/>, <see cref="Failed"/> or
/// <see cref="NotChecked"/>; <see cref="ExitCode"/> and <see cref="StderrTail"/> (redacted, see
/// <see cref="AgentCrash.StderrTail"/>) are set when a process ran; <see cref="Detail"/> says
/// what was run, or why nothing was.
/// </summary>
public sealed record AgentLaunchReport(string Result, int? ExitCode, string? StderrTail, string? Detail)
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string NotChecked = "not checked";

    public static AgentLaunchReport Unchecked(string detail) => new(NotChecked, null, null, detail);
}

/// <summary>
/// THE LAUNCH CHECK: whether each preset's CLI STARTS THE WAY A MEMBER RUN STARTS IT. A sign-in
/// probe runs the CLI directly, so a CLI that cannot start under a member's launch - the agent user,
/// the run's memory limit, the preset's isolation and update-off - still reads "signed in". This
/// runs the preset's declared free invocation (<see cref="AgentDefinition.LaunchCheck"/>) through
/// <see cref="ProcessAgentRunner.CheckLaunchAsync"/>, which builds the child with the member launch's
/// own steps; there is no second launcher here. It never sends a prompt and never spends tokens: a
/// preset with no free invocation reads <see cref="AgentLaunchReport.NotChecked"/>, never ok.
/// </summary>
public sealed class AgentLaunchChecks(AgentCatalog catalog, ProcessAgentRunner runner)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, IReadOnlyDictionary<string, AgentLaunchReport> Reports)? _cache;

    /// <summary>One report per preset, by name, case-insensitively; cached for 30 seconds like the sign-in probe.</summary>
    public async Task<IReadOnlyDictionary<string, AgentLaunchReport>> ReportsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } cached && DateTimeOffset.UtcNow - cached.At < Fresh) return cached.Reports;

            var reports = new Dictionary<string, AgentLaunchReport>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in catalog.Definitions)
            {
                reports[definition.Name] = await runner.CheckLaunchAsync(definition.Name, ct);
            }

            _cache = (DateTimeOffset.UtcNow, reports);
            return reports;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The sign-in reports, each carrying its preset's launch check.</summary>
    public static IReadOnlyList<AgentAuthReport> Attach(
        IEnumerable<AgentAuthReport> reports, IReadOnlyDictionary<string, AgentLaunchReport> launches) =>
        [.. reports.Select(report => report with { Launch = launches.GetValueOrDefault(report.Agent) })];
}
