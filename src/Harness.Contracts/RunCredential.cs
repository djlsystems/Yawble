using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Contracts;

/// <summary>Where a preset's CLI signs in from: the shared agent home, or a credential issued in Admin > Agents.</summary>
public enum CredentialSource
{
    Home,
    Issued,
}

/// <summary>
/// THE CREDENTIAL ONE RUN STARTS WITH, decided once at run start and carried with the run's start
/// details, so the side that decides it is the only side that ever reads the store or the key ring;
/// whatever launches the CLI only applies it.
///
/// <para>
/// Under <see cref="CredentialSource.Home"/> it is <see cref="Home"/>: nothing is set, nothing is
/// removed, and HOME is inherited exactly as before. Under <see cref="CredentialSource.Issued"/> it
/// holds the issued variable, the variables the preset's declaration displaces (removed whoever set
/// them), the variables other presets declare for other commands (removed unless the preset's or
/// the team's env handed them in, as every provider key is scoped), and a HOME of the run's own -
/// or <see cref="Missing"/>, the sentence that stops the run before anything starts.
/// </para>
///
/// <para>
/// IT NEVER PRINTS OR SERIALISES ITS VALUE. <see cref="ToString"/> names the source and whether a
/// value is set, and the JSON converter refuses to write one at all, so the record cannot reach a
/// log line, a diagnostic or a run payload by accident.
/// </para>
/// </summary>
[JsonConverter(typeof(RunCredentialNeverSerialised))]
public sealed record RunCredential(
    CredentialSource Source,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Displace,
    IReadOnlyList<string> OtherProviders,
    bool PerRunHome,
    string? Missing)
{
    /// <summary>The shared home's run: nothing set, nothing removed, HOME inherited.</summary>
    public static RunCredential Home { get; } =
        new(CredentialSource.Home, new Dictionary<string, string>(), [], [], false, null);

    /// <summary>An issued run that cannot start, and why.</summary>
    public static RunCredential NotSet(string why) =>
        new(CredentialSource.Issued, new Dictionary<string, string>(), [], [], true, why);

    public override string ToString() =>
        $"RunCredential({Source}, set: {Environment.Count > 0}{(Missing is null ? "" : ", missing")})";
}

/// <summary>Refuses to write a <see cref="RunCredential"/>, or read one: it carries a secret.</summary>
public sealed class RunCredentialNeverSerialised : JsonConverter<RunCredential>
{
    public override RunCredential Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A run's credential is never read from JSON.");

    public override void Write(Utf8JsonWriter writer, RunCredential value, JsonSerializerOptions options) =>
        throw new NotSupportedException("A run's credential is never written as JSON: it carries a secret.");
}
