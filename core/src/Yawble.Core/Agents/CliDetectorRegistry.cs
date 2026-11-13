namespace Yawble.Core.Agents;

/// <summary>
/// Every supported CLI, detected in parallel on a fresh view of the host each call (no cache: a CLI installed or
/// updated while the app is open shows on the next agents.detect). A new CLI is one <see cref="AgentCliDetector"/>
/// subclass added to <see cref="Default"/>.
/// </summary>
public sealed class CliDetectorRegistry(IReadOnlyList<AgentCliDetector> detectors, Func<DetectionHost> host) : ICliDetector
{
    public static IReadOnlyList<AgentCliDetector> Default => [new ClaudeDetector(), new GrokDetector(), new CodexDetector(), new CopilotDetector(), new AntigravityDetector()];

    public static CliDetectorRegistry ForThisHost() => new(Default, DetectionHost.ForThisHost);

    public IReadOnlyList<AgentCliDetector> Detectors => detectors;

    public async Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct)
    {
        var h = host();
        return await Task.WhenAll(detectors.Select(d => Task.Run(() => d.DetectAsync(h, ct), ct)));
    }

    /// <summary>Only that CLI's detector runs (its --version probe alone), on a fresh view of the host.</summary>
    public async Task<DetectedCli?> DetectOneAsync(string cliId, CancellationToken ct)
    {
        var d = detectors.FirstOrDefault(x => string.Equals(x.CliId, cliId, StringComparison.OrdinalIgnoreCase));
        return d is null ? null : await Task.Run(() => d.DetectAsync(host(), ct), ct);
    }
}
