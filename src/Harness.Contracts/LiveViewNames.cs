namespace Harness.Contracts;

/// <summary>
/// The live view's words and rules that both sides need: the transcript formats, how a found
/// transcript's workspace is read, and how a workspace path is spelled in a transcript's name.
/// The catalog validates against these; a worker uses them to find and name a run's transcript.
/// </summary>
public static class LiveViewNames
{
    public const string ClaudeJsonl = "claude-jsonl";

    public const string GrokUpdates = "grok-updates";

    public const string CopilotEvents = "copilot-events";

    public const string CodexRollout = "codex-rollout";

    /// <summary>Every format something renders, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Formats { get; } = [ClaudeJsonl, GrokUpdates, CopilotEvents, CodexRollout];

    public const string CwdFromFolderName = "folder-name";

    public const string CwdFromWorkspaceYaml = "workspace-yaml";

    public const string CwdFromFirstLine = "first-line-cwd";

    /// <summary>Every way a found transcript's workspace is read, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> CwdRules { get; } = [CwdFromFolderName, CwdFromWorkspaceYaml, CwdFromFirstLine];

    /// <summary>The display line's bound, in characters, the ellipsis included.</summary>
    public const int MaxLine = 240;

    /// <summary>How long after launch a transcript that has to be found is looked for.</summary>
    public static readonly TimeSpan FindFor = TimeSpan.FromSeconds(30);

    /// <summary>A working directory with every <c>/</c> and every <c>.</c> as <c>-</c>.</summary>
    public static string Dashed(string workingDirectory) =>
        workingDirectory.Replace('/', '-').Replace('.', '-');

    /// <summary>A working directory percent-encoded as one path segment, <c>/</c> as <c>%2F</c>.</summary>
    public static string Encoded(string workingDirectory) => Uri.EscapeDataString(workingDirectory);

    /// <summary>Whether a catalog path starts at the agent's home and never climbs out of it.</summary>
    public static bool UnderHome(string path) =>
        !string.IsNullOrEmpty(path) && path.StartsWith("~/", StringComparison.Ordinal) && !path.Split('/').Contains("..");
}
