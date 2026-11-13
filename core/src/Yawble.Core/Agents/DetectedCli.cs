namespace Yawble.Core.Agents;

/// <summary>
/// One agent CLI as found on the host, right now. The user installs and updates their CLIs themselves; the app
/// runs the CLI from where it is installed, granting only <see cref="ReadOnlyFolders"/> read-only, and keeps
/// <see cref="ConfigFolders"/> blocked (HOME/cwd/writable stay the app's own team/member folders).
/// Contract shared with the launcher work: members may be added, never renamed or removed.
/// </summary>
public sealed record DetectedCli(
    string CliId,                           // "claude" | "grok"
    bool Found,
    string? LauncherPath,                   // what PATH (or a known location) gave us
    string? ExecutablePath,                 // real program after resolving shims/symlinks/.cmd wrappers
    string? Version,                        // what the CLI itself reports (--version), null if it failed
    string InstallKind,                     // "native" | "npm" | "homebrew" | "winget" | "other"
    IReadOnlyList<string> ReadOnlyFolders,  // EXACT folders the program needs to run. Nothing else from home.
    IReadOnlyList<string> ConfigFolders,    // the CLI's config/credential folders - reported so the launcher keeps them BLOCKED
    IReadOnlyList<string> Problems,         // plain-language things wrong
    string InstallHelp)                     // plain-language install instructions + official URL, shown when not found
{
    /// <summary>
    /// The CLI's own sign-in on the host: the exact files the sandbox is granted and whether the person is
    /// signed in, from presence only. Null for a CLI with no sign-in catalog entry.
    /// </summary>
    public CliSignIn? SignIn { get; init; }
}

/// <summary>Finds every supported agent CLI on the host. Real time: no cache.</summary>
public interface ICliDetector
{
    Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct);

    /// <summary>One CLI only, fresh (a launch or a command-line description needs just its own). Null when it is not supported.</summary>
    async Task<DetectedCli?> DetectOneAsync(string cliId, CancellationToken ct) =>
        (await DetectAllAsync(ct)).FirstOrDefault(c => string.Equals(c.CliId, cliId, StringComparison.OrdinalIgnoreCase));
}
