using System.Diagnostics;
using System.Text.Json;

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
public sealed record AgentAuthReport(
    string Agent,
    string Command,
    bool Installed,
    bool? Authenticated,
    string Detail,
    bool Referenced = false)
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
public sealed class AgentAuthProbe(AgentCatalog catalog, AgentLaunchUser? runAs = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private readonly object _gate = new();
    private (DateTimeOffset At, IReadOnlyList<AgentAuthReport> Reports)? _cache;

    public async Task<IReadOnlyList<AgentAuthReport>> ReportsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cache is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(30))
            {
                return cached.Reports;
            }
        }

        var specs = LoadSpecs();
        var seen = new Dictionary<string, (bool Installed, bool? Authenticated, string Detail)>(
            StringComparer.OrdinalIgnoreCase);
        var reports = new List<AgentAuthReport>();

        foreach (var definition in catalog.Definitions)
        {
            var command = definition.Launch.FileName;
            if (!seen.TryGetValue(command, out var answer))
            {
                answer = await ProbeCommandAsync(command, specs, ct, runAs);
                seen[command] = answer;
            }

            reports.Add(new AgentAuthReport(
                definition.Name, command, answer.Installed, answer.Authenticated, answer.Detail));
        }

        lock (_gate)
        {
            _cache = (DateTimeOffset.UtcNow, reports);
        }

        return reports;
    }

    /// <summary>
    /// One COMMAND's answer, by name. Public for `--doctor`, which reports per command from the
    /// probe file rather than per preset from the catalog: it runs before the catalog is loaded,
    /// and loading the catalog seeds `agents.json` on a fresh volume, which a diagnostic must not.
    /// </summary>
    public static async Task<(bool Installed, bool? Authenticated, string Detail)> ProbeCommandAsync(
        string command, IReadOnlyDictionary<string, AgentAuthProbeSpec> specs, CancellationToken ct,
        AgentLaunchUser? runAs = null)
    {
        var resolved = PathSearch.Find(command);
        if (resolved is null)
        {
            return (false, null, $"'{command}' is not on PATH.");
        }

        if (specs.TryGetValue(command, out var spec)
            && spec.CredentialVariable is { Length: > 0 } variable
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            return (true, true, $"Authenticated via {variable}.");
        }

        if (spec?.CredentialFiles is { Length: > 0 } files
            && Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
        {
            var saved = files.FirstOrDefault(file => File.Exists(Path.Combine(home, file)));
            if (saved is not null)
            {
                return (true, true, $"Authenticated via a saved login ({saved}).");
            }
        }

        if (spec?.StatusArguments is not { Length: > 0 } args)
        {
            return (true, null, "Installed. No auth probe is configured for this command, and no credential variable is set.");
        }

        // The CLI was found on PATH, whose first folders the agent owns. Where an
        // `agent` user exists but this process cannot start anything as it (a doctor run as
        // `harness` from a shell), running that file here would run agent-written code as this
        // user. A root-owned system program is still asked.
        if (runAs is { Switches: false, AgentUserUnreachable: true } && !SystemCommand.IsTrusted(resolved))
        {
            return (true, null,
                $"Not probed: this process cannot start '{command}' as '{runAs.Name}' ({runAs.Reason}), and it does not run an agent-installed program as itself.");
        }

        var prefix = runAs?.Prefix ?? [];

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    // Asked as the user the agents run as, whose login it is, and with no
                    // capability: `setpriv` (from a system directory) first whenever there is a prefix.
                    FileName = prefix.Count > 0 ? prefix[0] : resolved,
                    WorkingDirectory = Path.GetTempPath(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            if (prefix.Count > 0)
            {
                foreach (var part in prefix.Skip(1)) process.StartInfo.ArgumentList.Add(part);
                process.StartInfo.ArgumentList.Add(resolved);
            }

            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);

            if (!process.Start()) return (true, null, "The status command could not be started.");

            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(Timeout, ct);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (true, null, "The status command did not finish in time.");
            }

            return process.ExitCode == 0
                ? (true, true, "The status command exited 0.")
                : (true, false, $"The status command exited {process.ExitCode}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (true, null, "The status command could not be run.");
        }
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
