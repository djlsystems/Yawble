using System.Text.RegularExpressions;

namespace Harness.Contracts;

/// <summary>
/// Substitution inside a system prompt or a trigger's instruction: `{team}`, `{teamId}`, `{member}`,
/// `{members}`, `{shared}`, `{env:NAME}` and `{event.&lt;field&gt;}`.
///
/// ONE PASS, deliberately. A replacement is never re-scanned, so a team someone named
/// `{env:HARNESS_KEY}` stays that string rather than becoming a credential - the same reason the
/// launch arguments substitute `{systemFile}` into an already-tokenized element rather than
/// re-splitting a joined command line. A trigger's event payload can carry a field whose VALUE is
/// literally `{env:HARNESS_KEY}` - the identical hazard wearing a different source - which is why
/// `{event.&lt;field&gt;}` goes through this same one-pass regex rather than a second scanner beside
/// it: <see cref="Regex.Replace(string,MatchEvaluator)"/> never revisits text it has already written
/// into the result.
///
/// An unknown token is LEFT AS WRITTEN rather than blanked. A prompt that silently loses a phrase is
/// harder to diagnose than one showing its own typo, and braces appear in ordinary prose.
///
/// LIVES IN CONTRACTS, NOT HOST. A trigger's `{event.*}` tokens are resolved on the delivery pump
/// (<c>ContainerHost.ResolveDeliveryAsync</c>, in <c>Harness.Containers</c>), which references only
/// the contract and never the Host - see that project's own note: "A container depends on the
/// CONTRACT for the log, never on the implementation." Moving here rather than forking the regex into
/// a second copy beside it is what lets the pump reuse the exact one-pass, leave-unknown-verbatim
/// behaviour instead of reimplementing it.
/// </summary>
public static partial class PromptTokens
{
    // Dot added to the name class so `event.output` is one token rather than two literal braces -
    // every other caller's names (team, teamId, member, members, shared, and every env var) never
    // contain a dot, so this is additive.
    [GeneratedRegex(@"\{(env:)?([A-Za-z_][A-Za-z0-9_.]*)\}")]
    private static partial Regex Token();

    private const string EventPrefix = "event.";

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

    public static string Resolve(
        string? template,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, string>? eventFields = null)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        return Token().Replace(template, match =>
        {
            var name = match.Groups[2].Value;

            if (match.Groups[1].Success)
            {
                return environment.TryGetValue(name, out var value) ? value : match.Value;
            }

            if (name.StartsWith(EventPrefix, StringComparison.Ordinal))
            {
                var field = name[EventPrefix.Length..];
                return (eventFields ?? Empty).TryGetValue(field, out var known)
                    ? known
                    : match.Value;
            }

            return values.TryGetValue(name, out var found) ? found : match.Value;
        });
    }

    /// <summary>
    /// `{event.&lt;field&gt;}` only, for a trigger's instruction. That text names no team, member or
    /// environment - it is never composed through the system-prompt channel's `values`/`environment`
    /// dictionaries - so this is the convenience the pump calls, still going through the identical
    /// one-pass <see cref="Resolve"/> rather than a parallel implementation.
    /// </summary>
    public static string ResolveEventTokens(
        string? template, IReadOnlyDictionary<string, string> eventFields) =>
        Resolve(template, Empty, Empty, eventFields);
}
