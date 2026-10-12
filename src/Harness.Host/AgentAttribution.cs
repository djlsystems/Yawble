using System.ComponentModel;

namespace Harness.Host;

/// <summary>
/// How a headless preset's CLI is kept from signing a member's commits and pull requests as its
/// own: the co-author trailer and the "generated with" line some CLIs tell the model to add.
///
/// APPLIED ONLY WHEN THE TENANT SETTING <c>agents.commitAttribution</c> IS OFF, which it is unless a
/// person turns it on, and only to a member's launch: the Concierge is the person's own session and
/// launches exactly as its preset says. Everything here is a LAUNCH decision - arguments placed with
/// the isolation arguments, variables set after the preset's and the team's env - and nothing in it
/// edits a file in the shared home or a member's checkout.
///
/// WHAT EACH BUILT-IN DECLARES WAS MEASURED against the CLI installed in the image, never read from
/// documentation alone; <see cref="Notes"/> says how, and says so when the CLI adds nothing or when
/// no launch switch reaches what it adds.
/// </summary>
public sealed record AgentAttribution(
    [property: Description(
        "Arguments that switch the CLI's own commit and pull-request attribution off. They go with "
        + "the isolation arguments - where the launch holds the `{isolation}` token, after the "
        + "isolation arguments, or first when there is none - on a member's launch only, and only "
        + "while the setting `agents.commitAttribution` is off.")]
    IReadOnlyList<string>? Arguments = null,
    [property: Description(
        "Variables that do the same, set for a member's launch AFTER every other source - the "
        + "preset's `env` and the team's env - so neither can switch attribution back on. A key "
        + "beginning HARNESS_ is ignored.")]
    IReadOnlyDictionary<string, string>? Env = null,
    [property: Description(
        "What was measured, in sentences: the CLI version, what it adds to a commit or pull request, "
        + "and how the switch was checked - or that the CLI adds nothing, or that no launch switch "
        + "reaches what it adds.")]
    IReadOnlyList<string>? Notes = null)
{
    /// <summary>Whether this declaration changes a launch at all.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Switches => Arguments is { Count: > 0 } || Environment is not null;

    /// <summary><see cref="Env"/> without any HARNESS_ key, or null when there is none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string>? Environment =>
        Env?.Where(e => !e.Key.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) is { Count: > 0 } env
            ? env
            : null;

    /// <summary>
    /// A member's launch arguments with the attribution-off folded in BEFORE isolation is applied:
    /// the arguments follow the <see cref="AgentIsolation.Token"/>, so the isolation arguments that
    /// replace the token come first and these after them; with no token they go first, and the
    /// isolation arguments are then put before them. Null, or a declaration with no arguments,
    /// leaves the arguments as they are.
    /// </summary>
    public static AgentCommand ApplyArguments(AgentCommand command, AgentAttribution? attribution)
    {
        if (attribution?.Arguments is not { Count: > 0 } inserted) return command;

        var arguments = new List<string>(command.Arguments.Count + inserted.Count);
        var placed = false;

        foreach (var argument in command.Arguments)
        {
            arguments.Add(argument);
            if (argument == AgentIsolation.Token && !placed)
            {
                arguments.AddRange(inserted);
                placed = true;
            }
        }

        if (!placed) arguments.InsertRange(0, inserted);

        return command with { Arguments = arguments };
    }

    /// <summary>
    /// A member's command, isolation already applied, with the attribution-off variables added to
    /// its isolation environment - the one the spawn site sets after the preset's and the team's
    /// env - each over an isolation variable of the same name.
    /// </summary>
    public static AgentCommand ApplyEnvironment(AgentCommand command, AgentAttribution? attribution)
    {
        if (attribution?.Environment is not { } variables) return command;

        var environment = new Dictionary<string, string>(
            command.IsolationEnvironment ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (key, value) in variables) environment[key] = value;

        return command with { IsolationEnvironment = environment };
    }
}
