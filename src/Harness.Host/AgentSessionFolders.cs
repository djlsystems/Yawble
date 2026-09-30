namespace Harness.Host;

/// <summary>
/// The folders the agent CLIs keep in the shared agent home per working directory - Claude's
/// <c>~/.claude/projects/&lt;workspace, dashed&gt;</c> and <c>~/.cache/claude-cli-nodejs/&lt;workspace, dashed&gt;</c>,
/// grok's <c>~/.grok/sessions/&lt;workspace, encoded&gt;</c> - as each built-in preset names them in
/// <see cref="AgentDefinition.SessionFolders"/>. A team deletion removes the ones keyed to the
/// deleted team's own workspaces and nothing else in the home.
/// </summary>
public static class AgentSessionFolders
{
    private const string Dashed = "{workspaceDashed}";
    private const string Encoded = "{workspaceEncoded}";

    /// <summary>Every built-in preset's templates, once each. A custom preset's are never read:
    /// a hand-edited catalog must not be able to name folders a deletion removes.</summary>
    public static IReadOnlyList<string> BuiltIn() =>
        [.. AgentCatalogFile.BuiltIns().SelectMany(d => d.SessionFolders ?? []).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The folder <paramref name="template"/> names for <paramref name="workspace"/> under
    /// <paramref name="home"/>, or null when the template is not one this acts on: it must start
    /// with <c>~/</c>, have no empty, <c>.</c> or <c>..</c> segment, and end in exactly one segment
    /// that is <c>{workspaceDashed}</c> or <c>{workspaceEncoded}</c>, with no placeholder before it.
    /// </summary>
    public static string? Resolve(string template, string home, string workspace)
    {
        if (string.IsNullOrEmpty(home) || string.IsNullOrEmpty(workspace)
            || !template.StartsWith("~/", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = template[2..].Split('/');
        if (segments.Length < 2 || segments.Any(s => s is "" or "." or "..")) return null;
        if (segments[..^1].Any(s => s.Contains('{') || s.Contains('}'))) return null;

        var key = segments[^1] switch
        {
            Dashed => LiveView.Dashed(workspace),
            Encoded => LiveView.Encoded(workspace),
            _ => null,
        };

        if (key is null or "" or "." or ".." || key.Contains('/')) return null;

        return Path.Combine([home, .. segments[..^1], key]);
    }
}
