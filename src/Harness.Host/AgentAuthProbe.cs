using System.Text.Json;
using Harness.Contracts;
using Harness.Pty;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>One command's auth probe, from <c>auth-probes.json</c>. Keyed by executable, not by preset.</summary>
public sealed record AgentAuthProbeSpec(
    string? CredentialVariable, string[]? StatusArguments, string[]? CredentialFiles = null);

/// <summary>What <c>GET /api/agents/auth</c> says about one preset.</summary>
/// <param name="Referenced">Whether a team, a member or the Concierge uses this preset - see
/// <see cref="AgentReferences"/>. Only a referenced preset that is signed out is worth a warning:
/// a seeded <c>codex</c> nobody runs, never signed in, would otherwise light a red banner for every
/// person on every page. Set by the route, never by the probe, which caches per machine and knows nothing
/// of teams.</param>
/// <param name="Launch">Whether the preset's CLI starts through a member's launch (<see cref="AgentLaunchChecks"/>).
/// Set by the route, never by this probe: the probe runs the CLI directly and answers only "signed in".</param>
/// <param name="Source">Where the preset signs in from: <c>home</c> (the shared agent home) or
/// <c>issued</c> (the credential issued for its command in Admin > Agents), from
/// <c>agents.credentialSource</c>.</param>
public sealed record AgentAuthReport(
    string Agent,
    string Command,
    bool? Installed,
    bool? Authenticated,
    string Detail,
    bool Referenced = false,
    AgentLaunchReport? Launch = null,
    string Source = TenantSettings.HomeSource)
{
    /// <summary>The reports, each marked with whether <paramref name="referenced"/> names it.</summary>
    public static IReadOnlyList<AgentAuthReport> MarkReferenced(
        IEnumerable<AgentAuthReport> reports, IReadOnlySet<string> referenced) =>
        [.. reports.Select(report => report with { Referenced = referenced.Contains(report.Agent) })];
}

/// <summary>
/// Reports whether an agent CLI is installed and authenticated, before a run spends tokens to
/// discover that it never called the platform.
/// </summary>
/// <remarks>
/// <c>Authenticated == null</c> means not measured. A probe that cannot see must not convict.
/// An environment variable named by the probe file is sufficient, and so is a saved login: a file
/// the CLI writes when a person signs in, named relative to HOME. A status command is only run
/// when neither is present, and a non-zero exit means unauthenticated. A timeout stays null.
/// </remarks>
/// <para>
/// ASKED OF A WORKER: the CLIs, the agent's home and the environment a run inherits are the worker's,
/// so every command on the shared home is asked of the connected worker with the most measured
/// headroom, in one request (<see cref="ProbeSignIn"/>). No worker, or none that answers: every one of
/// them is NOT MEASURED - <c>installed</c> and <c>authenticated</c> null, and the detail says why -
/// never signed out. In a Host that runs its runs itself the worker is its own, over the transport.
/// Each probe is recorded in <c>agent-auth.json</c> (<see cref="AgentAuthRecord"/>), which the doctor
/// reads: it is another process, and asks nothing.
/// </para>
/// <para>
/// A PRESET THAT SIGNS IN WITH AN ISSUED CREDENTIAL is answered from the run credential resolver
/// alone: signed in when the credential its command was issued is set and decrypts, not signed in
/// otherwise. The shared home is not looked at - no credential file, no status command, no Host
/// variable - because an issued run never reads it. Whether the value is ACCEPTED is not known
/// until a run: no CLI here has a status command that checks a key.
/// </para>
public sealed class AgentAuthProbe(
    AgentCatalog catalog,
    AgentLaunchUser? runAs = null,
    IRunCredentials? credentials = null,
    WorkerAsks? asks = null,
    string? dataRoot = null,
    ILogger<AgentAuthProbe>? log = null,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private readonly object _gate = new();
    private readonly Lazy<WorkerAsks> _asks = new(() => asks ?? WorkerAsks.InProcess(runAs));
    private (DateTimeOffset At, IReadOnlyList<AgentAuthReport> Reports)? _cache;

    /// <summary>What a command reads when no worker is connected to ask it.</summary>
    public const string NoWorkerText = "Not measured: no worker is connected to ask this CLI.";

    /// <summary>What a command reads when the worker asked did not answer.</summary>
    public static string NoAnswerText(WorkerId worker) => $"Not measured: worker {worker} did not answer.";

    /// <summary>How long one probe of <paramref name="commands"/> commands may take on a worker: each its status command's bound, and a margin.</summary>
    public static TimeSpan Bound(int commands) => Timeout * Math.Max(1, commands) + TimeSpan.FromSeconds(5);

    /// <summary>Drops the cached answer, so the next read probes again: after an issued credential
    /// or a preset's source changes, which the next read must show.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    public async Task<IReadOnlyList<AgentAuthReport>> ReportsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cache is { } cached && _clock.GetUtcNow() - cached.At < TimeSpan.FromSeconds(30))
            {
                return cached.Reports;
            }
        }

        var specs = LoadSpecs();
        var home = new List<(AgentDefinition Definition, string Command)>();
        var reports = new List<(AgentDefinition Definition, AgentAuthReport? Report)>();

        foreach (var definition in catalog.Definitions)
        {
            var command = definition.Launch.FileName;

            var credential = credentials is null
                ? RunCredential.Home
                : await credentials.ResolveAsync(definition.Name, definition, ct);

            if (credential.Source == CredentialSource.Issued)
            {
                var installed = PathSearch.Find(command) is not null;
                var detail = credential.Missing is null
                    ? $"Signs in with the credential issued for `{RunCredentials.CommandOf(definition)}`, in "
                      + $"{string.Join(", ", credential.Environment.Keys)}. Whether it is accepted shows at the first run."
                    : $"Signs in with an issued credential, and the one for `{RunCredentials.CommandOf(definition)}` is not set.";

                reports.Add((definition, new AgentAuthReport(
                    definition.Name, command, installed, credential.Missing is null, detail,
                    Source: TenantSettings.IssuedSource)));
                continue;
            }

            home.Add((definition, command));
            reports.Add((definition, null));
        }

        // Every command on the shared home, once each, in one request to one worker.
        var commands = home.Select(h => h.Command).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (answers, worker, why) = await ProbeAsync(commands, specs, ct);

        var answered = reports.Select(entry => entry.Report ?? Answer(entry.Definition, answers, why)).ToList();

        lock (_gate)
        {
            _cache = (_clock.GetUtcNow(), answered);
        }

        if (dataRoot is not null && commands.Count > 0)
        {
            new AgentAuthRecord(
                _clock.GetUtcNow(),
                worker?.Value,
                [.. commands.Select(command => answers.GetValueOrDefault(command) is { } found
                    ? new CommandSignIn(command, found.Installed, found.Authenticated, found.Detail)
                    : new CommandSignIn(command, null, null, why ?? NoWorkerText))]).Write(dataRoot, log);
        }

        return answered;
    }

    private static AgentAuthReport Answer(
        AgentDefinition definition, IReadOnlyDictionary<string, SignInProbeResult> answers, string? why)
    {
        var command = definition.Launch.FileName;
        return answers.GetValueOrDefault(command) is { } found
            ? new AgentAuthReport(definition.Name, command, found.Installed, found.Authenticated, found.Detail)
            : new AgentAuthReport(definition.Name, command, null, null, why ?? NoWorkerText);
    }

    /// <summary>Each command's answer from one worker, who answered, and why there is none when there is not.</summary>
    private async Task<(IReadOnlyDictionary<string, SignInProbeResult> Answers, WorkerId? Worker, string? Why)> ProbeAsync(
        IReadOnlyList<string> commands, IReadOnlyDictionary<string, AgentAuthProbeSpec> specs, CancellationToken ct)
    {
        var none = new Dictionary<string, SignInProbeResult>(StringComparer.OrdinalIgnoreCase);
        if (commands.Count == 0) return (none, null, null);

        var asked = await _asks.Value.AskAsync<SignInProbed>(
            new ProbeSignIn(WorkerAsks.NewRequest(), [.. commands.Select(command => Spec(command, specs))]), Bound(commands.Count), ct);

        if (asked.Answer is not { } probed)
        {
            return (none, asked.Worker, asked.Worker is { } worker ? NoAnswerText(worker) : NoWorkerText);
        }

        foreach (var result in probed.Results) none[result.Command] = result;
        return (none, asked.Worker, null);
    }

    /// <summary>One command's probe as a worker is asked it: the probe file's entry and the command's update-off.</summary>
    public static SignInProbeSpec Spec(string command, IReadOnlyDictionary<string, AgentAuthProbeSpec> specs)
    {
        specs.TryGetValue(command, out var spec);
        var updateOff = AgentUpdates.ForCommand(command);
        return new SignInProbeSpec(
            command, spec?.CredentialVariable, spec?.CredentialFiles, spec?.StatusArguments,
            updateOff?.Environment, updateOff?.Arguments);
    }

    /// <summary>
    /// One COMMAND's answer, by name, from a worker of this process's own (<see cref="WorkerAsks.InProcess"/>):
    /// the one-process composition of the probe a worker runs, for a caller with no catalog and no
    /// connected worker.
    /// </summary>
    public static async Task<(bool Installed, bool? Authenticated, string Detail)> ProbeCommandAsync(
        string command, IReadOnlyDictionary<string, AgentAuthProbeSpec> specs, CancellationToken ct,
        AgentLaunchUser? runAs = null)
    {
        var asked = await WorkerAsks.InProcess(runAs).AskAsync<SignInProbed>(
            new ProbeSignIn(WorkerAsks.NewRequest(), [Spec(command, specs)]), Bound(1), ct);

        return asked.Answer?.Results.SingleOrDefault() is { } result
            ? (result.Installed, result.Authenticated, result.Detail)
            : (false, null, asked.Why ?? NoWorkerText);
    }

    /// <summary>The probe file, keyed by command. Empty when the file is missing.</summary>
    public static IReadOnlyDictionary<string, AgentAuthProbeSpec> LoadSpecs()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "auth-probes.json");
        if (!File.Exists(path)) return new Dictionary<string, AgentAuthProbeSpec>();

        var specs = JsonSerializer.Deserialize<Dictionary<string, AgentAuthProbeSpec>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return specs ?? new Dictionary<string, AgentAuthProbeSpec>();
    }
}

/// <summary>One command's last sign-in probe, as <c>agent-auth.json</c> records it. Null: not measured.</summary>
public sealed record CommandSignIn(string Command, bool? Installed, bool? Authenticated, string Detail);

/// <summary>
/// THE HOST'S LAST SIGN-IN PROBE, recorded for <c>--doctor</c>, which is another process and asks no
/// worker: when it was measured, on which worker (null when none answered), and each command's answer.
/// </summary>
public sealed record AgentAuthRecord(DateTimeOffset At, string? Worker, IReadOnlyList<CommandSignIn> Commands)
{
    public const string FileName = "agent-auth.json";

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
            log?.LogWarning("Sign-in probe: could not record it in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>The last probe, or null when there is none or it cannot be read.</summary>
    public static AgentAuthRecord? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentAuthRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>One command's answer, or null when this probe did not ask it.</summary>
    public CommandSignIn? For(string command) =>
        Commands.FirstOrDefault(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase));
}
