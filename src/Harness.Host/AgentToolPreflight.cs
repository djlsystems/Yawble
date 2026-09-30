using System.Text.Json;

namespace Harness.Host;

/// <summary>What the pre-flight says about one preset. A string in the JSON, for the doctor and the web.</summary>
public static class ToolVerdicts
{
    /// <summary>Declared, listed, and nothing that offers a tool beyond harness and the allowed list.</summary>
    public const string Isolated = "isolated";

    /// <summary>Declared or not, the listing names something a member would be offered that it may not be.</summary>
    public const string ForeignFound = "foreignFound";

    /// <summary>A headless model preset with no declaration: shown as such, whatever its listing says.</summary>
    public const string NotVerified = "notVerified";

    /// <summary>The Concierge: what it has is information, never a warning.</summary>
    public const string Concierge = "concierge";

    /// <summary>A program that launches no model: nothing to check.</summary>
    public const string NotAModel = "notAModel";

    /// <summary>A declared preset whose CLI could not be listed: not installed, no listing, or it failed.
    /// Never read as isolated.</summary>
    public const string NotMeasured = "notMeasured";
}

/// <summary>One preset's pre-flight: its state from <see cref="AgentCatalog.Allowance"/> and what its
/// CLI's own listing says it would load, launched the way that preset launches.</summary>
/// <param name="Foreign">What would offer the model a tool outside harness and the allowed list.
/// For the Concierge always empty: its tools are in <paramref name="Loaded"/>, as information.</param>
/// <param name="Loaded">Everything the listing says would load, skills and hooks included.</param>
/// <param name="SwitchedOff">What the listing names that this launch switches off, with why.</param>
/// <param name="Gaps">The preset's recorded gaps: what no launch switch reaches.</param>
/// <param name="Ran">The listing commands, as run.</param>
/// <param name="Detail">Why nothing was measured, when nothing was.</param>
public sealed record PresetToolReport(
    string Preset,
    string Mode,
    string Command,
    string Verdict,
    IReadOnlyList<ListedItem> Foreign,
    IReadOnlyList<ListedItem> Loaded,
    IReadOnlyList<ListedItem> SwitchedOff,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<string> Ran,
    string? Detail = null);

/// <summary>
/// THE HOST'S LAST PRE-FLIGHT, in <c>&lt;dataRoot&gt;/agent-tools.json</c>. `--doctor` is another
/// process that must not load the catalog (loading it writes on a fresh volume) nor run five CLIs
/// as whoever started it, so it reads what the Host measured, the way it reads
/// <see cref="AgentLaunchRecord"/>.
/// </summary>
public sealed record AgentToolsRecord(DateTimeOffset At, IReadOnlyList<PresetToolReport> Presets)
{
    public const string FileName = "agent-tools.json";

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
            log?.LogWarning("Agent tools: could not record the pre-flight in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>The last pre-flight, or null when there is none or it cannot be read.</summary>
    public static AgentToolsRecord? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentToolsRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// THE PRE-FLIGHT: at start and whenever the catalog changes, what each preset's CLI would load
/// when run as the agent user - a member's preset WITH its isolation applied, the Concierge's
/// without it - held against <see cref="AgentCatalog.Allowance"/>. The operator CLI's doctor and Admin →
/// Agents read the result.
///
/// NEVER ON THE START PATH: the first pass begins once the Host is serving, and a CLI that hangs
/// costs its own listing's timeout, not a start. NEVER WRITES THE HOME: every listing is a read
/// (<see cref="AgentToolListers"/>), and the Host's own per-launch configuration (the grok config
/// the member launch rewrites) is not touched here.
/// </summary>
public sealed class AgentToolPreflight(
    AgentCatalog catalog, IListingRunner runner, string dataRoot, ILogger<AgentToolPreflight>? log = null)
    : BackgroundService
{
    private readonly SemaphoreSlim _again = new(0);
    private volatile AgentToolsRecord? _current;

    /// <summary>The last finished pass, or null before the first one ends.</summary>
    public AgentToolsRecord? Current => _current;

    /// <summary>True while a pass is running.</summary>
    public bool Running { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        catalog.Changed += Again;
        try
        {
            // Yield first, so the Host's start never waits on a CLI.
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                // Changes that arrived while a pass ran are one more pass, not one each.
                while (_again.CurrentCount > 0) await _again.WaitAsync(stoppingToken);

                await PassAsync(stoppingToken);
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

    /// <summary>One pass over the catalog. Public for the tests, which hand it a runner of recorded output.</summary>
    public async Task<AgentToolsRecord> PassAsync(CancellationToken ct)
    {
        Running = true;
        try
        {
            var record = new AgentToolsRecord(DateTimeOffset.UtcNow, await ReportsAsync(catalog.Definitions, runner, ct));
            _current = record;
            record.Write(dataRoot, log);

            foreach (var preset in record.Presets)
            {
                log?.LogInformation(
                    "Agent tools: {Preset} {Verdict}{Foreign}", preset.Preset, preset.Verdict,
                    preset.Foreign.Count > 0 ? " - " + string.Join(", ", preset.Foreign.Select(f => f.Name)) : "");
            }

            return record;
        }
        finally
        {
            Running = false;
        }
    }

    /// <summary>Every preset's report. A launch shape shared by two presets is listed once.</summary>
    public static async Task<IReadOnlyList<PresetToolReport>> ReportsAsync(
        IReadOnlyList<AgentDefinition> definitions, IListingRunner runner, CancellationToken ct)
    {
        var listed = new Dictionary<string, CliListing>(StringComparer.Ordinal);
        var reports = new List<PresetToolReport>(definitions.Count);

        foreach (var definition in definitions)
        {
            if (definition.Launch is not { } launch) continue;

            var allowance = AgentIsolationPolicy.For(definition)!;
            CliListing? listing = null;

            if (launch.LanguageModel && allowance.State != IsolationState.NotAModel)
            {
                var (arguments, environment) = LaunchShape(definition);
                var key = string.Join('\u0001', [launch.FileName, .. arguments, .. environment.Select(e => e.Key + "=" + e.Value)]);

                if (!listed.TryGetValue(key, out listing))
                {
                    listing = await AgentToolListers.ListAsync(launch.FileName, arguments, environment, runner, ct);
                    listed[key] = listing;
                }
            }

            reports.Add(Assess(definition, allowance, listing));
        }

        return reports;
    }

    /// <summary>
    /// The arguments and environment a listing runs with: what the launch itself sets on top of the
    /// Host's environment. Every container gets <see cref="AgentEnvironment.Isolated"/> and its
    /// preset's own env (never a HARNESS_ key); a MEMBER's launch adds its isolation, set last, as
    /// the runner sets it. The Concierge gets no isolation. Both get the preset's update-off, last.
    /// </summary>
    public static (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment) LaunchShape(
        AgentDefinition definition)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in AgentEnvironment.Isolated) environment[key] = value;

        foreach (var (key, value) in definition.Env ?? new Dictionary<string, string>())
        {
            if (!key.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase)) environment[key] = value;
        }

        IReadOnlyList<string> arguments = [];

        if (definition.Mode == AgentMode.Headless && definition.Isolation is { } isolation)
        {
            foreach (var (key, value) in isolation.Env ?? new Dictionary<string, string>())
            {
                if (!key.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase)) environment[key] = value;
            }

            arguments = isolation.Arguments;
        }

        // The CLI's own updater off, on the listing as on every launch the Host makes: a listing is
        // a run of the shared install too. Only the environment - codex's key is a launch argument
        // its listing subcommands do not act on.
        foreach (var (key, value) in definition.Updates?.Environment ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        return (arguments, environment);
    }

    /// <summary>
    /// ONE PRESET'S VERDICT, from its allowance and its listing. Pure, so every state is a test.
    ///
    /// Foreign means something that puts a TOOL in front of the model - a server, an account
    /// connector or app, a plugin - that is on and that the allowance does not allow. Skills and
    /// hooks are listed and never foreign: they are instructions and commands, not tools offered.
    /// </summary>
    public static PresetToolReport Assess(AgentDefinition definition, ToolAllowance allowance, CliListing? listing)
    {
        var mode = definition.Mode == AgentMode.Headless ? "headless" : "interactive";
        var command = definition.Launch?.FileName ?? "";
        var items = listing is { Listed: true } ? listing.Items : [];
        var loaded = items.Where(i => i.Off is null).ToList();
        var switchedOff = items.Where(i => i.Off is not null).ToList();

        IReadOnlyList<ListedItem> foreign = allowance.Checked
            ? [.. loaded.Where(i => ListedKinds.OffersTools(i.Kind) && !allowance.Allows(i.Name, i.Name))]
            : [];

        // A program with no model (an interactive `shell` as much as a headless `cat`) has no tools to list.
        var verdict = definition.Launch is { LanguageModel: false } ? ToolVerdicts.NotAModel : allowance.State switch
        {
            IsolationState.NotAModel => ToolVerdicts.NotAModel,
            IsolationState.Concierge => ToolVerdicts.Concierge,
            IsolationState.NotVerified => ToolVerdicts.NotVerified,
            _ when listing is not { Listed: true } => ToolVerdicts.NotMeasured,
            _ when foreign.Count > 0 => ToolVerdicts.ForeignFound,
            _ => ToolVerdicts.Isolated,
        };

        return new PresetToolReport(
            definition.Name, mode, command, verdict, foreign, loaded, switchedOff, allowance.Gaps,
            listing?.Ran ?? [],
            listing is { Listed: false } ? listing.Detail : null);
    }
}
