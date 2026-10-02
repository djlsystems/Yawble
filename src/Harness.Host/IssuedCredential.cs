using System.ComponentModel;

namespace Harness.Host;

/// <summary>
/// How a preset's CLI takes a credential issued in Admin > Agents instead of the login in the
/// shared agent home: the variables it reads one from, every variable it would otherwise read
/// for authentication, and which one wins when a home login also exists.
///
/// DATA IN THE CATALOG, MEASURED. Nothing in code names a provider or a variable: a run's
/// credential is placed by reading this declaration, and the built-in ones record, beside each
/// preset, the CLI version and the launches that measured them.
///
/// ONE VALUE PER COMMAND. The value is stored once for the command a preset launches, so every
/// preset that runs the same CLI uses the same credential; which presets use it is each preset's
/// own source (<see cref="TenantSettings.AgentCredentialSourceName"/>). Presets that launch the
/// same command therefore declare the same <see cref="Kinds"/>.
/// </summary>
public sealed record IssuedCredential(
    [property: Description(
        "What a person may set for this CLI, each `kind` (`apiKey` or `token`) with the variable "
        + "the CLI reads it from. At least one.")]
    IReadOnlyList<IssuedCredentialKind> Kinds,
    [property: Description(
        "Every variable this CLI reads for authentication. An issued run has each of them removed "
        + "before its credential is set, so a key the Host or a team holds cannot outrank the issued one.")]
    IReadOnlyList<string> Displaces,
    [property: Description(
        "Which credential this CLI uses when a home login exists as well, as measured: `credential` "
        + "(the issued one), `login` (the home's) or `unmeasured`. Only the Concierge keeps its home, "
        + "so this is what the Agents screen says about it.")]
    string LoginPrecedence,
    [property: Description("The CLI version this declaration was measured on.")]
    string MeasuredWith,
    [property: Description(
        "Variables that point this CLI at a home folder of its own (its config directory). An issued "
        + "run has them removed, so the run's own HOME is the only one it reads and no variable points "
        + "it back at the shared home's login. The Concierge keeps them. Null for none.")]
    IReadOnlyList<string>? HomeVariables = null)
{
    public const string ApiKey = "apiKey";
    public const string Token = "token";

    public const string CredentialWins = "credential";
    public const string LoginWins = "login";
    public const string Unmeasured = "unmeasured";

    /// <summary>The variable <paramref name="kind"/> is placed in, or null when it is not declared.</summary>
    public string? VariableFor(string kind) =>
        Kinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.Ordinal))?.Variable;

    /// <summary>Whether two declarations offer the same kinds in the same variables.</summary>
    public bool SameKinds(IssuedCredential other) =>
        Kinds.Select(k => (k.Kind, k.Variable)).Order()
            .SequenceEqual(other.Kinds.Select(k => (k.Kind, k.Variable)).Order());

    /// <summary>
    /// Why <paramref name="declaration"/> cannot be used, or null. A variable the launch itself
    /// sets - <c>HARNESS_*</c>, <c>HOME</c>, <c>PATH</c>, <c>TMPDIR</c> - is refused: a credential
    /// placed there would replace the platform's own value.
    /// </summary>
    public static string? Refusal(string preset, IssuedCredential declaration)
    {
        if (declaration.Kinds is not { Count: > 0 })
        {
            return $"'{preset}' declares an issued credential with no kinds.";
        }

        foreach (var kind in declaration.Kinds)
        {
            if (kind is null || kind.Kind is not (ApiKey or Token))
            {
                return $"'{preset}': an issued credential's kind is `{ApiKey}` or `{Token}`.";
            }

            if (VariableRefusal(kind.Variable) is { } variable) return $"'{preset}': {variable}";
        }

        if (declaration.Kinds.GroupBy(k => k.Kind).Any(g => g.Count() > 1))
        {
            return $"'{preset}' declares one issued-credential kind twice.";
        }

        foreach (var displaced in (declaration.Displaces ?? []).Concat(declaration.HomeVariables ?? []))
        {
            if (VariableRefusal(displaced) is { } variable) return $"'{preset}': {variable}";
        }

        if (declaration.LoginPrecedence is not (CredentialWins or LoginWins or Unmeasured))
        {
            return $"'{preset}': loginPrecedence is `{CredentialWins}`, `{LoginWins}` or `{Unmeasured}`.";
        }

        return null;
    }

    private static readonly string[] Reserved = ["HOME", "PATH", MemberTemp.Variable, "USER", "LOGNAME", "SHELL"];

    private static string? VariableRefusal(string? variable)
    {
        if (string.IsNullOrWhiteSpace(variable)
            || !variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            || char.IsAsciiDigit(variable[0]))
        {
            return $"'{variable}' is not an environment variable name.";
        }

        if (variable.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase)
            || Reserved.Contains(variable, StringComparer.OrdinalIgnoreCase))
        {
            return $"{variable} is set by the launch itself and cannot carry an issued credential.";
        }

        return null;
    }
}

/// <summary>One kind of credential a CLI accepts, and the variable it reads it from.</summary>
public sealed record IssuedCredentialKind(
    [property: Description("`apiKey` or `token`.")]
    string Kind,
    [property: Description("The environment variable the CLI reads this kind from.")]
    string Variable,
    [property: Description(
        "Beginnings of a value the CLI refuses to start with, measured. A value beginning with one "
        + "is refused when it is set, rather than stored to fail every run. Null for none.")]
    IReadOnlyList<string>? RefusedPrefixes = null);
