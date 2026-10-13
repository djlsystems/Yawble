using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ONE PLACE A RUN'S CREDENTIAL SCOPE IS DECIDED: every variable another command's preset
/// declares (<see cref="CredentialVariables"/>), so another provider's credential the Host holds
/// cannot ride along with a run. A member run decides it at run start and carries it with the run
/// (<see cref="AgentInvocation.Scope"/>); the launch check and the tool listing ask the same method.
/// Every run signs in through the shared agent home, so nothing is set and HOME is inherited.
/// </summary>
public static class CredentialScopes
{
    /// <summary>The command a preset launches: its launch's executable name.</summary>
    public static string CommandOf(AgentDefinition definition) => Path.GetFileName(definition.Launch.FileName);

    /// <summary>The scope of a run of <paramref name="agent"/>, or <see cref="CredentialScope.None"/> for a preset the catalog lacks.</summary>
    public static CredentialScope For(AgentCatalog catalog, string agent, AgentDefinition? definition = null)
    {
        definition ??= catalog.Definition(agent);
        return definition is null ? CredentialScope.None : For(catalog, definition);
    }

    /// <summary>Every variable another command's declaration names. Never one this preset's own command
    /// declares, and never the team's git token.</summary>
    public static CredentialScope For(AgentCatalog catalog, AgentDefinition definition) => For(catalog.Definitions, definition);

    /// <summary>The scope of <paramref name="definition"/> among <paramref name="definitions"/>.</summary>
    public static CredentialScope For(IEnumerable<AgentDefinition> definitions, AgentDefinition definition)
    {
        var own = CommandOf(definition);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var ours = new HashSet<string>(StringComparer.Ordinal) { AgentEnvironment.GitHubVariable };

        foreach (var other in definitions)
        {
            if (other.CredentialVariables is not { } declaration) continue;

            ISet<string> into = string.Equals(CommandOf(other), own, StringComparison.OrdinalIgnoreCase) ? ours : names;
            foreach (var name in declaration.Variables) into.Add(name);
        }

        names.ExceptWith(ours);
        return new CredentialScope([.. names]);
    }
}
