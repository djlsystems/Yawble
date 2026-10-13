namespace Harness.Contracts;

/// <summary>
/// THE CREDENTIAL VARIABLES ONE RUN IS SCOPED AWAY FROM, decided once in control from the catalog and
/// carried with the run's start details, so the side that launches the CLI never reads the catalog:
/// every variable another command's preset declares as its own (<c>credentialVariables</c>), removed
/// from the child unless the preset's or the team's env handed it in by name. Names only, never a
/// value, so it travels as any other field.
/// </summary>
public sealed record CredentialScope(IReadOnlyList<string> OtherCommands)
{
    /// <summary>A run of a preset nothing is known of: nothing removed.</summary>
    public static CredentialScope None { get; } = new([]);

    /// <summary>
    /// Removes from a child's <paramref name="environment"/> every other command's variable that the
    /// preset's and the team's env (<paramref name="handedIn"/>) did not hand in. Here so the worker
    /// that launches a run and control that lists its tools apply one rule.
    /// </summary>
    public void ApplyTo(IDictionary<string, string?> environment, IReadOnlyDictionary<string, string> handedIn)
    {
        foreach (var name in OtherCommands)
        {
            if (!handedIn.ContainsKey(name)) environment.Remove(name);
        }
    }
}
