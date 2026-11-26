using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;

namespace Yawble.Core.Workflows.Language;

/// <summary>A value that cannot be used the way an expression or a step uses it; fails the step with its message.</summary>
public sealed class WorkflowValueException(string message) : Exception(message);

/// <summary>
/// Values while a workflow runs. In JSON (variables, step results, checkpoints) a value is a JsonNode: a datetime is an ISO
/// 8601 string in UTC, a date <c>yyyy-MM-dd</c>, a duration ISO 8601 (<c>PT26H</c>). Inside an expression a value is one of
/// null, long, double, string, bool, DateTimeOffset, DateOnly, TimeSpan, JsonArray or JsonObject.
/// </summary>
public static partial class WorkflowValues
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}")]
    private static partial Regex InstantShape();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateShape();

    public static bool TryParseInstant(string s, out DateTimeOffset value)
    {
        value = default;
        return InstantShape().IsMatch(s)
               && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    public static bool TryParseDate(string s, out DateOnly value)
    {
        value = default;
        return DateShape().IsMatch(s) && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    public static bool TryParseDuration(string s, out TimeSpan value)
    {
        value = default;
        if (s.StartsWith('P') || s.StartsWith("-P", StringComparison.Ordinal))
        {
            try { value = XmlConvert.ToTimeSpan(s); return true; }
            catch (FormatException) { return false; }
        }
        return s.Contains(':') && TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A JSON value as an expression value (a JSON object or array is used as it is: clone before storing it).</summary>
    public static object? FromNode(JsonNode? node)
    {
        if (node is not JsonValue v) return node;
        switch (v.GetValueKind())
        {
            case JsonValueKind.String: return v.GetValue<string>();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<int>(out var i)) return (long)i;
                if (v.TryGetValue<double>(out var d)) return Math.Floor(d) == d && Math.Abs(d) < 9e15 ? (long)d : d;
                return double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);
            case JsonValueKind.Null: return null;
            default: return v.ToJsonString();
        }
    }

    /// <summary>An expression value as JSON (a fresh node, safe to store anywhere).</summary>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create((long)i),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : throw new WorkflowValueException("a number is not finite"),
        DateTimeOffset t => JsonValue.Create(FormatInstant(t)),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeSpan d => JsonValue.Create(XmlConvert.ToString(d)),
        _ => JsonValue.Create(value.ToString()),
    };

    public static string FormatInstant(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>A value as text in a template: null is empty, numbers and times are invariant, lists and objects JSON.</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTimeOffset t => FormatInstant(t),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan d => FormatDuration(d),
        JsonNode n => n.ToJsonString(),
        _ => value.ToString() ?? "",
    };

    /// <summary>A duration for a person: <c>26h</c>, <c>1h 5m</c>, <c>12m</c>, <c>40s</c>.</summary>
    public static string FormatDuration(TimeSpan d)
    {
        var sign = d < TimeSpan.Zero ? "-" : "";
        d = d.Duration();
        var hours = (long)d.TotalHours;
        if (hours > 0) return sign + (d.Minutes > 0 ? $"{hours}h {d.Minutes}m" : $"{hours}h");
        if (d.Minutes > 0) return $"{sign}{d.Minutes}m";
        return $"{sign}{d.Seconds}s";
    }

    public static bool IsNumber(object? v) => v is long or double;

    public static double ToDouble(object? v) => v switch
    {
        long l => l,
        double d => d,
        _ => throw new WorkflowValueException($"{Describe(v)} is not a number"),
    };

    public static DateTimeOffset ToInstant(object? v) => v switch
    {
        DateTimeOffset t => t,
        DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        string s when TryParseInstant(s, out var t) => t,
        string s when TryParseDate(s, out var d) => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        _ => throw new WorkflowValueException($"{Describe(v)} is not a datetime"),
    };

    public static TimeSpan ToDuration(object? v) => v switch
    {
        TimeSpan d => d,
        string s when TryParseDuration(s, out var d) => d,
        long or double => TimeSpan.FromHours(ToDouble(v)),
        _ => throw new WorkflowValueException($"{Describe(v)} is not a duration"),
    };

    public static JsonArray ToList(object? v) => v switch
    {
        null => [],
        JsonArray a => a,
        _ => throw new WorkflowValueException($"{Describe(v)} is not a list"),
    };

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        long l => l != 0,
        double d => d != 0,
        JsonArray a => a.Count > 0,
        _ => true,
    };

    public static bool IsEmpty(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        _ => false,
    };

    public static string Describe(object? v) => v switch
    {
        null => "null",
        string s => $"'{(s.Length > 60 ? s[..60] + "..." : s)}'",
        JsonArray => "a list",
        JsonObject => "an object",
        _ => Format(v),
    };

    /// <summary>Equality as conditions use it: numbers by value, times as instants, lists and objects deeply.</summary>
    public static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (TryCompare(a, b, out var c)) return c == 0;
        if (a is JsonNode na && b is JsonNode nb) return JsonNode.DeepEquals(na, nb);
        return a.Equals(b);
    }

    /// <summary>Orders two values of the same kind (numbers, text, datetimes, dates, durations); false when they do not compare.</summary>
    public static bool TryCompare(object? a, object? b, out int result)
    {
        result = 0;
        if (a is null || b is null) return false;
        if (IsNumber(a) && IsNumber(b))
        {
            result = a is long la && b is long lb ? la.CompareTo(lb) : ToDouble(a).CompareTo(ToDouble(b));
            return true;
        }
        if (a is bool ba && b is bool bb) { result = ba.CompareTo(bb); return true; }
        if (a is DateTimeOffset || b is DateTimeOffset || (a is string sa && b is string sb && TryParseInstant(sa, out _) && TryParseInstant(sb, out _)))
        {
            try { result = ToInstant(a).CompareTo(ToInstant(b)); return true; }
            catch (WorkflowValueException) { return false; }
        }
        if (a is DateOnly || b is DateOnly)
        {
            if (AsDate(a) is { } da && AsDate(b) is { } db) { result = da.CompareTo(db); return true; }
            return false;
        }
        if (a is TimeSpan || b is TimeSpan)
        {
            try { result = ToDuration(a).CompareTo(ToDuration(b)); return true; }
            catch (WorkflowValueException) { return false; }
        }
        if (a is string s1 && b is string s2) { result = string.CompareOrdinal(s1, s2); return true; }
        return false;
    }

    private static DateOnly? AsDate(object? v) => v switch
    {
        DateOnly d => d,
        string s when TryParseDate(s, out var d) => d,
        _ => null,
    };

    /// <summary>
    /// Converts a value to a declared type, as a JSON node ready to store: a number to int or float, text to a datetime, date
    /// or duration, an object checked against its schema, a list item by item. Null is allowed for every type.
    /// </summary>
    public static JsonNode? Convert(object? value, WorkflowType type, Func<string, WorkflowSchema?> findSchema)
    {
        if (value is null) return null;
        switch (type.Kind)
        {
            case WorkflowTypeKind.Any or WorkflowTypeKind.Null:
                return ToNode(value);
            case WorkflowTypeKind.Int:
                if (value is long l) return JsonValue.Create(l);
                if (value is double d && Math.Floor(d) == d && Math.Abs(d) < 9e18) return JsonValue.Create((long)d);
                if (value is string si && long.TryParse(si, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi)) return JsonValue.Create(pi);
                break;
            case WorkflowTypeKind.Float:
                if (IsNumber(value)) return JsonValue.Create(ToDouble(value));
                if (value is string sf && double.TryParse(sf, NumberStyles.Float, CultureInfo.InvariantCulture, out var pf)) return JsonValue.Create(pf);
                break;
            case WorkflowTypeKind.String:
                if (value is not (JsonArray or JsonObject)) return JsonValue.Create(Format(value));
                break;
            case WorkflowTypeKind.Bool:
                if (value is bool b) return JsonValue.Create(b);
                if (value is "true" or "false") return JsonValue.Create((string)value == "true");
                break;
            case WorkflowTypeKind.DateTime:
                if (value is DateTimeOffset or DateOnly or string)
                {
                    try { return JsonValue.Create(FormatInstant(ToInstant(value))); }
                    catch (WorkflowValueException) { }
                }
                break;
            case WorkflowTypeKind.Date:
                if (value is DateOnly dd) return ToNode(dd);
                if (value is DateTimeOffset dt) return ToNode(DateOnly.FromDateTime(dt.UtcDateTime));
                if (value is string sd)
                {
                    if (TryParseDate(sd, out var pd)) return ToNode(pd);
                    if (TryParseInstant(sd, out var pt)) return ToNode(DateOnly.FromDateTime(pt.UtcDateTime));
                }
                break;
            case WorkflowTypeKind.Duration:
                if (value is TimeSpan or string or long or double)
                {
                    try { return ToNode(ToDuration(value)); }
                    catch (WorkflowValueException) { }
                }
                break;
            case WorkflowTypeKind.Enum:
                if (value is string se && type.EnumValues.Contains(se)) return JsonValue.Create(se);
                break;
            case WorkflowTypeKind.Object:
                if (value is JsonObject o)
                {
                    if (type.Schema is { } schema)
                    {
                        var errors = JsonSchemaValidator.Validate(schema, o, findSchema);
                        if (errors.Count > 0) throw new WorkflowValueException($"the value does not match {type}: {string.Join("; ", errors.Take(3))}");
                    }
                    return o.DeepClone();
                }
                break;
            case WorkflowTypeKind.List:
                if (value is JsonArray a)
                {
                    var list = new JsonArray();
                    for (var i = 0; i < a.Count; i++)
                    {
                        try { list.Add(Convert(FromNode(a[i]), type.Element!, findSchema)); }
                        catch (WorkflowValueException e) { throw new WorkflowValueException($"item {i}: {e.Message}"); }
                    }
                    return list;
                }
                break;
        }
        throw new WorkflowValueException($"{Describe(value)} is not a {type}");
    }
}
