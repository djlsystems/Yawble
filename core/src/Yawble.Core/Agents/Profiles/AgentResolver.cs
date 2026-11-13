using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents.Profiles;

/// <summary>An agent name (a member's cli) resolved: the CLI it runs, and, for a custom agent, its argv changes and default model.</summary>
/// <param name="Name">The name as stored in members.cli (the built-in's id, or the custom agent's name).</param>
public sealed record ResolvedAgent(string Name, AgentCli Cli, bool Custom, string DefaultModel, ArgvCustomization? Argv)
{
    /// <summary>The name a run is recorded under: null for a built-in (recorded by its CLI id).</summary>
    public string? RecordedAgent => Custom ? Name : null;
}

/// <summary>Where a member's cli (a built-in id or a custom agent's name) becomes the CLI, argv changes and default model. One lookup for every site.</summary>
public interface IAgentResolver
{
    /// <summary>Null when no built-in or custom agent has that name.</summary>
    ResolvedAgent? Find(string name);

    /// <summary>Every name a member may have as its cli, built-ins first.</summary>
    IReadOnlyList<string> Names();
}

public static class AgentResolvers
{
    /// <summary>The name resolved through <paramref name="resolver"/>, or as a built-in id when there is none. Throws ArgumentException for an unknown name.</summary>
    public static ResolvedAgent Resolve(this IAgentResolver? resolver, string name)
    {
        name = (name ?? "").Trim();
        if (resolver?.Find(name) is { } found) return found;
        if (resolver is null) { var cli = AgentClis.Parse(name); return new ResolvedAgent(cli.Id(), cli, false, "", null); }
        throw new ArgumentException($"'{name}' is not an agent ({string.Join(", ", resolver.Names())})");
    }

    /// <summary>The stored form of a name: a built-in's lower-case id, or the custom agent's name as saved. Null when unknown.</summary>
    public static string? Canonical(this IAgentResolver? resolver, string? name)
    {
        var id = (name ?? "").Trim();
        return resolver is null ? (AgentClis.Ids.Contains(id.ToLowerInvariant()) ? id.ToLowerInvariant() : null) : resolver.Find(id)?.Name;
    }
}
