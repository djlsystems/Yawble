namespace Yawble.Core.Policy;

/// <summary>The agent CLI presets by id, as stored in members.cli and shown in the UI.</summary>
public static class AgentClis
{
    public const string Claude = "claude";
    public const string Grok = "grok";
    public const string Codex = "codex";
    public const string Copilot = "copilot";
    public const string Antigravity = "antigravity";

    /// <summary>Every preset, in the order the UI lists them.</summary>
    public static IReadOnlyList<string> Ids { get; } = [Claude, Grok, Codex, Copilot, Antigravity];

    public static string Id(this AgentCli cli) => cli switch
    {
        AgentCli.Claude => Claude,
        AgentCli.Grok => Grok,
        AgentCli.Codex => Codex,
        AgentCli.Copilot => Copilot,
        AgentCli.Antigravity => Antigravity,
        _ => throw new ArgumentOutOfRangeException(nameof(cli)),
    };

    /// <summary>The preset for a stored id. Throws for an unknown one (never silently another CLI).</summary>
    public static AgentCli Parse(string id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        Claude => AgentCli.Claude,
        Grok => AgentCli.Grok,
        Codex => AgentCli.Codex,
        Copilot => AgentCli.Copilot,
        Antigravity => AgentCli.Antigravity,
        _ => throw new ArgumentException($"'{id}' is not an agent CLI preset ({string.Join(", ", Ids)})"),
    };

    /// <summary>The name people know it by.</summary>
    public static string DisplayName(this AgentCli cli) => cli switch
    {
        AgentCli.Claude => "Claude Code",
        AgentCli.Grok => "Grok",
        AgentCli.Codex => "Codex",
        AgentCli.Copilot => "GitHub Copilot",
        AgentCli.Antigravity => "Antigravity",
        _ => cli.ToString(),
    };
}
