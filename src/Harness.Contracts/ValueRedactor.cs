using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A SET OF SECRET VALUES, AND THE ONE WAY THEY ARE REPLACED in text the Host stores or serves.
/// Plugin runs and agent runs share it. Each value is replaced with the fixed marker
/// <see cref="DiagnosticRedaction.Placeholder"/>, never removed, so a reader sees that something
/// was there. Three rules make the replacement a better net:
/// <list type="bullet">
/// <item>each value is matched in ANY CASE, so an upper-cased copy is caught;</item>
/// <item>each value's JSON-ESCAPED forms are matched too, because a raw line keeps a value the
/// way its writer serialised it (the default encoder escapes <c>+</c>, <c>&lt;</c>, quotes; other
/// encoders - PHP, some Java ones - also write <c>/</c> as <c>\/</c>);</item>
/// <item>the LONGEST form is replaced first, so a value that is a prefix of another cannot
/// leave the other's tail behind.</item>
/// </list>
/// A value shorter than <see cref="MinimumLength"/> is NOT redacted: replacing it would destroy the
/// text around it. A plugin refuses such a secret; an agent run's key is not that short.
/// Only values in the set are touched - no shape rule runs here - so text holding none of them
/// comes back as the same instance. A writer that transforms a value otherwise - reversed, base64,
/// split - defeats this.
///
/// A WHOLE-TOKEN value (<see cref="For(IEnumerable{string?}, IEnumerable{string?})"/>) is replaced only
/// where no letter or digit is next to it (an escape such as <c>\u0022</c> or <c>%3D</c> just before it
/// counts as none): a short session value such as <c>k9x4z</c> is caught in <c>x=k9x4z;</c> and in
/// <c>\u0022k9x4z\u0022</c>, but left in a longer word. Whole-token values never cross a connection
/// (<see cref="Forms"/> lists only the match-anywhere ones); a plugin run's set stays in the Host.
///
/// In the contracts because a run's set travels to the worker with its start, which applies it; like
/// the run's credential, it is never written as JSON.
/// </summary>
[JsonConverter(typeof(ValueRedactorNeverSerialised))]
public sealed class ValueRedactor
{
    /// <summary>The shortest value that is redacted.</summary>
    public const int MinimumLength = 4;

    /// <summary>Redacts nothing.</summary>
    public static readonly ValueRedactor Empty = new([]);

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private ValueRedactor(IReadOnlyList<string> forms, IReadOnlyList<string>? tokenForms = null)
    {
        Forms = forms;
        TokenForms = tokenForms ?? [];
        _applied = [.. forms.Select(f => (f, false)).Concat(TokenForms.Select(f => (f, true))).OrderByDescending(f => f.Item1.Length).ThenBy(f => f.Item2)];
    }

    private readonly IReadOnlyList<(string Form, bool WholeToken)> _applied;

    /// <summary>
    /// Where a whole token may start: after no letter or digit, or right after an escape that ends in
    /// one - <c>\u0022</c> as JSON writes a quote, <c>\n</c>, <c>%3D</c> - so an escaped copy is caught.
    /// </summary>
    private const string TokenStart = @"(?:(?<![\p{L}\p{N}])|(?<=\\u[0-9A-Fa-f]{4})|(?<=\\[nrtbf])|(?<=%[0-9A-Fa-f]{2}))";

    /// <summary>Every spelling that <see cref="Apply"/> replaces anywhere, longest first.</summary>
    public IReadOnlyList<string> Forms { get; }

    /// <summary>Every spelling that <see cref="Apply"/> replaces only as a whole token, longest first.</summary>
    public IReadOnlyList<string> TokenForms { get; }

    /// <summary>A redactor for <paramref name="values"/>; values shorter than <see cref="MinimumLength"/> are left out.</summary>
    public static ValueRedactor For(IEnumerable<string?> values)
    {
        return Ordered(Spellings(values));
    }

    /// <summary>
    /// A redactor for <paramref name="values"/>, replaced anywhere, and <paramref name="wholeTokens"/>,
    /// replaced only where no letter or digit is next to them; values shorter than <see cref="MinimumLength"/> are left out.
    /// </summary>
    public static ValueRedactor For(IEnumerable<string?> values, IEnumerable<string?> wholeTokens)
    {
        var forms = Ordered(Spellings(values));
        var tokens = Spellings(wholeTokens).Distinct(StringComparer.OrdinalIgnoreCase).Except(forms.Forms, StringComparer.OrdinalIgnoreCase);
        return forms.With(new ValueRedactor([], OrderedList(tokens)));
    }

    private static IEnumerable<string> Spellings(IEnumerable<string?> values) => values
        .OfType<string>()
        .Where(v => v.Length >= MinimumLength)
        .SelectMany(v => new[] { v, JsonBody(v, null), JsonBody(v, Relaxed), JsonBody(v, Relaxed).Replace("/", "\\/", StringComparison.Ordinal) });

    /// <summary>
    /// The set of a run whose child is given <paramref name="environment"/>: the value of every
    /// variable in <paramref name="names"/> that environment carries. Read where the child's
    /// environment is, so a key the child never had is not in it.
    /// </summary>
    public static ValueRedactor OfEnvironment(IDictionary<string, string?> environment, IEnumerable<string> names) =>
        For(names
            .Select(name => environment.TryGetValue(name, out var value) ? value : null)
            .Where(value => !string.IsNullOrEmpty(value)));

    /// <summary>A set of exactly these spellings, as <see cref="Forms"/> listed them: how a set crosses a connection, sealed.</summary>
    internal static ValueRedactor FromForms(IEnumerable<string> forms) => Ordered(forms);

    /// <summary>This set together with <paramref name="other"/>'s.</summary>
    public ValueRedactor With(ValueRedactor other)
    {
        if (other.Forms.Count == 0 && other.TokenForms.Count == 0) return this;
        if (Forms.Count == 0 && TokenForms.Count == 0) return other;
        var forms = OrderedList(Forms.Concat(other.Forms));
        return new ValueRedactor(forms, OrderedList(TokenForms.Concat(other.TokenForms).Except(forms, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary><paramref name="text"/> with each form replaced; the same instance when nothing matched.</summary>
    public string Apply(string text)
    {
        foreach (var (form, wholeToken) in _applied)
        {
            if (!text.Contains(form, StringComparison.OrdinalIgnoreCase)) continue;
            text = wholeToken
                ? Regex.Replace(
                    text,
                    $@"{TokenStart}{Regex.Escape(form)}(?![\p{{L}}\p{{N}}])",
                    DiagnosticRedaction.Placeholder.Replace("$", "$$", StringComparison.Ordinal),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                : text.Replace(form, DiagnosticRedaction.Placeholder, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary><see cref="Apply"/>, passing null through.</summary>
    public string? ApplyOrNull(string? text) => text is null ? null : Apply(text);

    private static ValueRedactor Ordered(IEnumerable<string> forms)
    {
        var ordered = OrderedList(forms);
        return ordered.Count == 0 ? Empty : new ValueRedactor(ordered);
    }

    private static List<string> OrderedList(IEnumerable<string> forms) => forms
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(f => f.Length)
        .ThenBy(f => f, StringComparer.Ordinal)
        .ToList();

    /// <summary>A value as it appears between the quotes of a JSON string.</summary>
    private static string JsonBody(string value, JsonSerializerOptions? options) =>
        JsonSerializer.Serialize(value, options)[1..^1];
}

/// <summary>Refuses to write a <see cref="ValueRedactor"/>, or read one: it holds secret values.</summary>
public sealed class ValueRedactorNeverSerialised : JsonConverter<ValueRedactor>
{
    public override ValueRedactor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A redaction set is never read from JSON.");

    public override void Write(Utf8JsonWriter writer, ValueRedactor value, JsonSerializerOptions options) =>
        throw new NotSupportedException("A redaction set is never written as JSON: it holds secret values.");
}
