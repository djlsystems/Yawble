using System.ComponentModel;

namespace Harness.Host;

/// <summary>
/// The environment variables a preset's CLI reads for authentication, measured on the CLI. A member
/// run keeps its own command's and loses every other command's (<see cref="CredentialScopes"/>), and
/// what a run prints is redacted of the value of each one its environment carries
/// (<see cref="RunSecrets"/>).
///
/// DATA IN THE CATALOG, MEASURED. Nothing in code names a provider or a variable: the built-in
/// declarations record, beside each preset, the CLI version that measured them.
/// </summary>
public sealed record CredentialVariables(
    [property: Description(
        "Every variable this CLI reads for authentication. A member run of another command's preset "
        + "has each of them removed unless its preset's or its team's env hands it in by name.")]
    IReadOnlyList<string> Variables,
    [property: Description("The CLI version this declaration was measured on.")]
    string MeasuredWith)
{
    /// <summary>
    /// Why <paramref name="declaration"/> cannot be used, or null. A variable the launch itself sets -
    /// <c>HARNESS_*</c>, <c>HOME</c>, <c>PATH</c>, <c>TMPDIR</c> - is refused:
    /// a value placed in the run by the platform must never be scoped away from it.
    /// </summary>
    public static string? Refusal(string preset, CredentialVariables declaration)
    {
        if (declaration.Variables is not { Count: > 0 })
        {
            return $"'{preset}' declares credential variables with none in them.";
        }

        foreach (var variable in declaration.Variables)
        {
            if (VariableRefusal(variable) is { } refused) return $"'{preset}': {refused}";
        }

        return null;
    }

    private static readonly string[] Reserved =
        ["HOME", "PATH", MemberTemp.Variable, "USER", "LOGNAME", "SHELL"];

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
            return $"{variable} is set by the launch itself and cannot be declared as a credential variable.";
        }

        return null;
    }
}
