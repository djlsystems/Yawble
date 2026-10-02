using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ONE PLACE A RUN'S CREDENTIAL IS DECIDED. A member run resolves it at run start and carries
/// it with the run (<see cref="AgentInvocation.Credential"/>); the launch check, the sign-in probe
/// and the Concierge ask the same method. Nothing else reads the store's values.
/// </summary>
public interface IRunCredentials
{
    /// <param name="agent">The preset the run launches.</param>
    /// <param name="definition">Its definition, when the caller already holds it; null looks it up.</param>
    Task<RunCredential> ResolveAsync(string agent, AgentDefinition? definition, CancellationToken ct);
}

/// <summary>
/// <see cref="IRunCredentials"/> over the preset's source (<c>agents.credentialSource</c>), its
/// <see cref="IssuedCredential"/> declaration and the credential stored for its command.
///
/// <para>
/// HOME COSTS NOTHING AND CHANGES NOTHING: a preset whose source is home resolves to
/// <see cref="RunCredential.Home"/> without touching the store, so its launch is the one it always was.
/// </para>
///
/// <para>
/// ISSUED FAILS CLOSED. No declaration, nothing stored for the command, a value that no longer
/// decrypts, or a stored kind this preset does not declare each resolve to
/// <see cref="RunCredential.Missing"/>, and a member run with it does not start; it never falls
/// back to the shared home.
/// </para>
/// </summary>
public sealed class RunCredentials(AgentCatalog catalog, Func<string, CredentialSource> sourceOf, AgentCredentialStore store)
    : IRunCredentials
{
    public RunCredentials(AgentCatalog catalog, TenantSettings settings, AgentCredentialStore store)
        : this(catalog, settings.CredentialSourceOf, store)
    {
    }

    /// <summary>The command a preset's credential is stored under: its launch's executable name.</summary>
    public static string CommandOf(AgentDefinition definition) => Path.GetFileName(definition.Launch.FileName);

    public async Task<RunCredential> ResolveAsync(string agent, AgentDefinition? definition, CancellationToken ct)
    {
        if (sourceOf(agent) != CredentialSource.Issued) return RunCredential.Home;

        definition ??= catalog.Definition(agent);
        if (definition is null) return RunCredential.Home;

        if (definition.IssuedCredential is not { } declaration)
        {
            return RunCredential.NotSet(
                $"'{definition.Name}' is set to sign in with an issued credential, but it declares none, so this "
                + "run did not start. Switch it back to the shared home in Admin > Agents.");
        }

        var command = CommandOf(definition);

        if (await store.RevealAsync(command, ct) is not { } issued)
        {
            return RunCredential.NotSet(
                $"The credential issued for `{command}` is not set, so this run did not start: '{definition.Name}' "
                + "signs in with an issued credential and never falls back to the shared home. Set it in "
                + "Admin > Agents, or switch the preset back to the shared home.");
        }

        if (declaration.VariableFor(issued.Kind) is not { } variable)
        {
            return RunCredential.NotSet(
                $"The credential issued for `{command}` is of a kind '{definition.Name}' does not declare, so this "
                + "run did not start. Set it again in Admin > Agents.");
        }

        return new RunCredential(
            CredentialSource.Issued,
            new Dictionary<string, string>(StringComparer.Ordinal) { [variable] = issued.Value },
            Own(declaration, variable),
            OtherProviders(definition, variable),
            PerRunHome: true,
            Missing: null);
    }

    /// <summary>What an issued run has removed whoever set it: every variable its own declaration
    /// displaces or points at a home. Never the issued variable itself.</summary>
    private static IReadOnlyList<string> Own(IssuedCredential declaration, string variable)
    {
        var names = new SortedSet<string>(declaration.Displaces, StringComparer.Ordinal);
        foreach (var name in declaration.HomeVariables ?? []) names.Add(name);

        names.Remove(variable);
        return [.. names];
    }

    /// <summary>Every variable another command's declaration names, so another provider's key the
    /// Host holds cannot ride along with an issued run.</summary>
    private IReadOnlyList<string> OtherProviders(AgentDefinition definition, string variable)
    {
        var own = CommandOf(definition);
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var other in catalog.Definitions)
        {
            if (other.IssuedCredential is not { } theirs
                || string.Equals(CommandOf(other), own, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var name in theirs.Displaces) names.Add(name);
            foreach (var kind in theirs.Kinds) names.Add(kind.Variable);
        }

        names.Remove(variable);
        return [.. names];
    }
}
