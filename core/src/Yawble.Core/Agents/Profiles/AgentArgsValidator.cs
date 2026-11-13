using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents.Profiles;

/// <summary>One argument that cannot be used, with the reason the person is shown.</summary>
public sealed record ArgProblem(string Argument, string Reason);

/// <summary>
/// Which arguments a custom agent may add or remove. The rules are AgentPresets' own (LockedFlags, ValidateCustomization): the
/// one place that knows the argv, so what is saved is what the launcher accepts. Arguments are a list of strings with no shell.
/// </summary>
public static class AgentArgsValidator
{
    /// <summary>Why <paramref name="argument"/> is locked for <paramref name="cli"/>, or null when it is free.</summary>
    public static string? LockedReason(AgentCli cli, string argument) =>
        AgentPresets.CustomizationProblems(cli, new ArgvCustomization([argument], [], [])).Select(p => p.Reason).FirstOrDefault();

    /// <summary>Every problem with the three lists; empty when they can be saved. Each problem names its argument. The
    /// lists are checked whole: a flag's value may be the next item, and the last item of a list matters.</summary>
    public static IReadOnlyList<ArgProblem> Validate(AgentCli cli, IReadOnlyList<string> before, IReadOnlyList<string> after, IReadOnlyList<string> removed) =>
        [.. AgentPresets.CustomizationProblems(cli, new ArgvCustomization(before, after, removed)).Select(p => new ArgProblem(p.Argument, p.Reason))];
}
