using System.Collections.Concurrent;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT A MEMBER RUN'S OWN CREDENTIAL IS, so what the run prints or reports can be redacted of it
/// before the Host stores or serves it. The set is the run's issued value plus every provider key
/// its environment carries, read from the environment the child is actually given - after the
/// credential is applied and other providers' keys are scoped out - so a key the child never had is
/// not in it, and a later change to that scoping cannot drift from what is redacted.
///
/// Which names are credentials is data: every provider variable the probe file names
/// (<see cref="AgentEnvironment.ProviderVariables"/>) and every variable a catalog declaration
/// issues into or displaces. Nothing here names a provider.
///
/// The registry keeps each member's last run's set in Host memory only, never written anywhere. It is
/// replaced by the member's next run and not cleared when a run ends, so a report from a process that
/// outlived its run is still redacted. A member that never ran here - a plugin, the Concierge, any
/// member after a Host restart - has an empty set.
/// </summary>
public sealed class RunSecrets(AgentCatalog catalog, IRunCredentials? credentials = null)
{
    private readonly ConcurrentDictionary<ContainerId, ValueRedactor> _byMember = new();

    /// <summary>Every variable that holds a credential: the probe file's provider variables and every
    /// variable a declaration issues into or displaces. Never a home folder variable.</summary>
    public static IReadOnlySet<string> CredentialNames(AgentCatalog catalog)
    {
        var names = new HashSet<string>(AgentEnvironment.ProviderVariables, StringComparer.Ordinal);
        foreach (var definition in catalog.Definitions)
        {
            if (definition.IssuedCredential is not { } declaration) continue;

            foreach (var kind in declaration.Kinds) names.Add(kind.Variable);
            foreach (var name in declaration.Displaces) names.Add(name);
        }

        return names;
    }

    /// <summary>The set of a run whose child is given <paramref name="environment"/>: the issued value
    /// and the value of every credential variable it carries.</summary>
    public static ValueRedactor Of(IDictionary<string, string?> environment, RunCredential credential, AgentCatalog catalog) =>
        ValueRedactor.OfEnvironment(environment, CredentialNames(catalog), credential.Environment.Values);

    /// <summary>Keeps <paramref name="redactor"/> as <paramref name="member"/>'s set until its next run.</summary>
    public void Remember(ContainerId member, ValueRedactor redactor) => _byMember[member] = redactor;

    /// <summary><paramref name="member"/>'s last run's set, or <see cref="ValueRedactor.Empty"/>.</summary>
    public ValueRedactor For(ContainerId member) => _byMember.GetValueOrDefault(member, ValueRedactor.Empty);

    /// <summary>
    /// The set a finished run's transcript is read with: its run's set, if this Host still holds it,
    /// together with the set a run of <paramref name="agent"/> would get now - the issued value the
    /// resolver gives, and every credential variable the member's own environment
    /// (<paramref name="containerEnvironment"/>) or the Host's carries. Larger than the run's own
    /// set, never smaller, while the value has not been replaced.
    /// </summary>
    public async Task<ValueRedactor> ForReadAsync(
        ContainerId member, string? agent, IReadOnlyDictionary<string, string>? containerEnvironment, CancellationToken ct)
    {
        var names = CredentialNames(catalog);
        var values = new List<string?>();

        if (credentials is not null && !string.IsNullOrEmpty(agent))
        {
            values.AddRange((await credentials.ResolveAsync(agent, null, ct)).Environment.Values);
        }

        foreach (var name in names)
        {
            values.Add(containerEnvironment?.GetValueOrDefault(name));
            values.Add(Environment.GetEnvironmentVariable(name));
        }

        return For(member).With(ValueRedactor.For(values.Where(v => !string.IsNullOrEmpty(v))));
    }
}
