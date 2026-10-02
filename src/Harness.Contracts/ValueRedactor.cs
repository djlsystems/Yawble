using System.Text.Json;
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

    private ValueRedactor(IReadOnlyList<string> forms) => Forms = forms;

    /// <summary>Every spelling that <see cref="Apply"/> replaces, longest first.</summary>
    public IReadOnlyList<string> Forms { get; }

    /// <summary>A redactor for <paramref name="values"/>; values shorter than <see cref="MinimumLength"/> are left out.</summary>
    public static ValueRedactor For(IEnumerable<string?> values)
    {
        var forms = values
            .OfType<string>()
            .Where(v => v.Length >= MinimumLength)
            .SelectMany(v => new[] { v, JsonBody(v, null), JsonBody(v, Relaxed), JsonBody(v, Relaxed).Replace("/", "\\/", StringComparison.Ordinal) });
        return Ordered(forms);
    }

    /// <summary>This set together with <paramref name="other"/>'s.</summary>
    public ValueRedactor With(ValueRedactor other) =>
        other.Forms.Count == 0 ? this : Forms.Count == 0 ? other : Ordered(Forms.Concat(other.Forms));

    /// <summary><paramref name="text"/> with each form replaced; the same instance when nothing matched.</summary>
    public string Apply(string text)
    {
        foreach (var form in Forms)
        {
            if (text.Contains(form, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Replace(form, DiagnosticRedaction.Placeholder, StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }

    /// <summary><see cref="Apply"/>, passing null through.</summary>
    public string? ApplyOrNull(string? text) => text is null ? null : Apply(text);

    private static ValueRedactor Ordered(IEnumerable<string> forms)
    {
        var ordered = forms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => f.Length)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        return ordered.Count == 0 ? Empty : new ValueRedactor(ordered);
    }

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
