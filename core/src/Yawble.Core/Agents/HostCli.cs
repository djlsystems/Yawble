namespace Yawble.Core.Agents;

/// <summary>Small helpers over <see cref="ICliDetector"/> for the host-side users of a CLI (the Grok keeper, the
/// Settings host panes): they run the person's own CLI, unsandboxed, as they always did.</summary>
public static class HostCli
{
    /// <summary>The detected CLI's real program, or null when it is not installed. Detects now (no cache).</summary>
    public static string? Path(ICliDetector clis, string cliId)
    {
        var all = Task.Run(() => clis.DetectAllAsync(CancellationToken.None)).GetAwaiter().GetResult();
        return all.FirstOrDefault(c => string.Equals(c.CliId, cliId, StringComparison.OrdinalIgnoreCase)) is { Found: true, ExecutablePath: { } p } ? p : null;
    }
}
