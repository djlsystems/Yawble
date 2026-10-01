using System.Text.Json;

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
///
/// RECORDED FOR THE DOCTOR: every pass is written as <see cref="AgentLaunchChecksRecord"/>, once the
/// Host is serving, after every catalog save and whenever <c>GET /api/agents/auth</c> asks again, so
/// <c>--doctor</c> - another process, often another user - reads the Host's own check and never runs one.
/// </summary>
public sealed class AgentLaunchChecks(
    AgentCatalog catalog, ProcessAgentRunner runner, string? dataRoot = null, ILogger<AgentLaunchChecks>? log = null)
    : BackgroundService
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _again = new(0);
    private (DateTimeOffset At, IReadOnlyDictionary<string, AgentLaunchReport> Reports)? _cache;

    /// <summary>
    /// One report per preset, by name, case-insensitively; cached for 30 seconds like the sign-in probe.
    /// <paramref name="fresh"/> runs the check again whatever the cache holds.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AgentLaunchReport>> ReportsAsync(CancellationToken ct, bool fresh = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!fresh && _cache is { } cached && DateTimeOffset.UtcNow - cached.At < Fresh) return cached.Reports;

            var reports = new Dictionary<string, AgentLaunchReport>(StringComparer.OrdinalIgnoreCase);
            var presets = new List<PresetLaunchCheck>();
            foreach (var definition in catalog.Definitions)
            {
                var report = await runner.CheckLaunchAsync(definition.Name, ct);
                reports[definition.Name] = report;
                presets.Add(new PresetLaunchCheck(definition.Name, catalog.For(definition.Name)?.FileName ?? definition.Launch?.FileName, report));
            }

            var at = DateTimeOffset.UtcNow;
            _cache = (at, reports);
            if (dataRoot is not null) new AgentLaunchChecksRecord(at, presets).Write(dataRoot, log);
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

    /// <summary>A pass once the Host is serving - never on the start path - and one after every catalog save.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        catalog.Changed += Again;
        try
        {
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                while (_again.CurrentCount > 0) await _again.WaitAsync(stoppingToken);

                await ReportsAsync(stoppingToken, fresh: true);
                await _again.WaitAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            catalog.Changed -= Again;
        }
    }

    private void Again() => _again.Release();
}

/// <summary>One preset's launch check as the Host recorded it, with the command it starts.</summary>
public sealed record PresetLaunchCheck(string Preset, string? Command, AgentLaunchReport Launch);

/// <summary>
/// THE HOST'S LAST LAUNCH CHECK, in <c>&lt;dataRoot&gt;/agent-launch-checks.json</c>. <c>--doctor</c> must
/// not load the catalog nor start CLIs as whoever ran it, so it reads what the Host measured, the way it
/// reads <see cref="AgentToolsRecord"/>, and states it per command (<see cref="ForCommand"/>).
/// </summary>
public sealed record AgentLaunchChecksRecord(DateTimeOffset At, IReadOnlyList<PresetLaunchCheck> Presets)
{
    public const string FileName = "agent-launch-checks.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Never throws: a Host that cannot record it still serves the in-memory copy.</summary>
    public void Write(string dataRoot, ILogger? log = null)
    {
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("Launch check: could not record it in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>The last check, or null when there is none or it cannot be read.</summary>
    public static AgentLaunchChecksRecord? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentLaunchChecksRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The launch of one COMMAND, which is how the doctor lists agents: the presets that start it, read
    /// worst first. One that failed is the command's answer (its own exit code and stderr tail, the preset
    /// named); otherwise one that started is ok; otherwise not checked. A command no preset starts is not
    /// checked, never ok. The values are the presets' own reports, as <c>GET /api/agents/auth</c> carries them.
    /// </summary>
    public AgentLaunchReport ForCommand(string command)
    {
        var mine = Presets.Where(p => p.Command is { } c && string.Equals(Path.GetFileName(c), command, StringComparison.Ordinal)).ToArray();
        if (mine.Length == 0) return AgentLaunchReport.Unchecked($"No preset starts `{command}`, so nothing was started.");

        var chosen = mine.FirstOrDefault(p => p.Launch.Result == AgentLaunchReport.Failed)
                     ?? mine.FirstOrDefault(p => p.Launch.Result == AgentLaunchReport.Ok)
                     ?? mine[0];

        return chosen.Launch with { Detail = $"{chosen.Preset}: {chosen.Launch.Detail}" };
    }
}
