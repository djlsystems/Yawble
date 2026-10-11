using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Host.Solutions;

/// <summary>
/// A package's <c>panel.status</c>: one line of TEXT with placeholders the platform fills, shown on
/// the solution's launcher tile and at the top of its panel. See <c>docs/solutions.md</c>.
///
/// <para>
/// ONLY FOUR PLACEHOLDERS EXIST, and the template is text, never code: <c>{data.&lt;collection&gt;.count}</c>,
/// <c>{data.&lt;collection&gt;.count &lt;field&gt;=&lt;value&gt;}</c>, <c>{lastRun.at}</c> and
/// <c>{lastRun.outcome}</c>. There is no expression, no call, no markup: a brace that is not one of
/// them is refused by the check, and the filled line is a plain string the UI renders as text. A
/// field value that looks like HTML is compared as the characters it is.
/// </para>
/// </summary>
public sealed partial class SolutionStatusTemplate
{
    /// <summary>The longest template the check accepts, and the longest line the filler answers.</summary>
    public const int MaximumLength = 200;

    public const string LastRunAt = "lastRun.at";
    public const string LastRunOutcome = "lastRun.outcome";

    /// <summary>What <c>{lastRun.at}</c> and <c>{lastRun.outcome}</c> say before any run.</summary>
    public const string NeverRan = "never";
    public const string NoOutcome = "none";

    private readonly IReadOnlyList<Part> _parts;

    private SolutionStatusTemplate(IReadOnlyList<Part> parts) => _parts = parts;

    /// <summary>The site collections the template counts, so the filler reads only those.</summary>
    public IReadOnlyList<string> Collections => [.. _parts.OfType<Count>().Select(c => c.Collection).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether any placeholder reads the primary site's data.</summary>
    public bool ReadsData => _parts.OfType<Count>().Any();

    /// <summary>
    /// Reads a template. Answers it, or null with the reason - a sentence naming the first
    /// placeholder this Host does not fill.
    /// </summary>
    public static (SolutionStatusTemplate? Template, string? Refusal) Parse(string template)
    {
        if (template.Length > MaximumLength)
        {
            return (null, $"is longer than {MaximumLength} characters.");
        }

        var parts = new List<Part>();
        var text = new StringBuilder();
        var at = 0;

        while (at < template.Length)
        {
            var c = template[at];

            if (c == '}')
            {
                return (null, "has a `}` with no `{` before it. Placeholders are {data.<collection>.count}, {data.<collection>.count <field>=<value>}, {lastRun.at} and {lastRun.outcome}.");
            }

            if (c != '{')
            {
                text.Append(c);
                at++;
                continue;
            }

            var close = template.IndexOf('}', at + 1);
            if (close < 0)
            {
                return (null, "has a `{` that is never closed.");
            }

            var inside = template[(at + 1)..close];

            if (Placeholder(inside) is not { } part)
            {
                return (null, $"has `{{{inside}}}`, which is not a placeholder this Host fills. Placeholders are {{data.<collection>.count}}, {{data.<collection>.count <field>=<value>}}, {{lastRun.at}} and {{lastRun.outcome}}.");
            }

            if (text.Length > 0) parts.Add(new Text(text.ToString()));
            text.Clear();
            parts.Add(part);
            at = close + 1;
        }

        if (text.Length > 0) parts.Add(new Text(text.ToString()));

        return (new SolutionStatusTemplate(parts), null);
    }

    private static Part? Placeholder(string inside)
    {
        if (inside == LastRunAt) return new LastRun(Outcome: false);
        if (inside == LastRunOutcome) return new LastRun(Outcome: true);

        var match = CountPattern().Match(inside);
        if (!match.Success || !SiteRules.IsSlug(match.Groups["collection"].Value)) return null;

        return new Count(
            match.Groups["collection"].Value,
            match.Groups["field"].Success ? match.Groups["field"].Value : null,
            match.Groups["value"].Success ? match.Groups["value"].Value : null);
    }

    /// <summary>
    /// Fills the template. <paramref name="data"/> answers a collection's documents, each as its JSON
    /// text, or null when there is no primary site or no such collection: a count of nothing is 0.
    /// The answer is plain text, clipped to <see cref="MaximumLength"/>.
    /// </summary>
    public string Fill(Func<string, IReadOnlyList<string>?> data, SolutionLastRun? lastRun)
    {
        var line = new StringBuilder();

        foreach (var part in _parts)
        {
            line.Append(part switch
            {
                Text t => t.Value,
                LastRun { Outcome: false } => lastRun is null ? NeverRan : Stamp(lastRun.At),
                LastRun => lastRun?.Outcome ?? NoOutcome,
                Count count => CountOf(data(count.Collection), count).ToString(CultureInfo.InvariantCulture),
                _ => "",
            });
        }

        return Clip(line.ToString());
    }

    /// <summary>The line without a template: the last run and the state.</summary>
    public static string Default(SolutionLastRun? lastRun, string state) =>
        Clip(lastRun is null
            ? $"No runs yet · {state}"
            : $"Last run {Stamp(lastRun.At)} ({lastRun.Outcome}) · {state}");

    /// <summary>A time as the status line writes it: ISO-8601 UTC to the second.</summary>
    public static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Clip(string line) => line.Length <= MaximumLength ? line : line[..(MaximumLength - 1)] + "…";

    private static int CountOf(IReadOnlyList<string>? documents, Count count)
    {
        if (documents is null) return 0;
        if (count.Field is null) return documents.Count;

        return documents.Count(json => FieldEquals(json, count.Field, count.Value!));
    }

    /// <summary>Whether the document's top-level <paramref name="field"/> reads as
    /// <paramref name="value"/>: a string by its characters, a number, true, false or null as JSON
    /// writes it. A document that is not a JSON object matches nothing.</summary>
    private static bool FieldEquals(string json, string field, string value)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty(field, out var element))
            {
                return false;
            }

            var text = element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => element.GetString(),
                System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null => element.GetRawText(),
                _ => null,
            };

            return string.Equals(text, value, StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private abstract record Part;

    private sealed record Text(string Value) : Part;

    private sealed record LastRun(bool Outcome) : Part;

    private sealed record Count(string Collection, string? Field, string? Value) : Part;

    [GeneratedRegex(@"^data\.(?<collection>[^.\s{}]+)\.count(?: (?<field>[A-Za-z0-9_-]+)=(?<value>[^\s{}]+))?$")]
    private static partial Regex CountPattern();
}

/// <summary>The latest finished run of any of a solution's members: when it ended and how.</summary>
public sealed record SolutionLastRun(DateTimeOffset At, string Outcome);
