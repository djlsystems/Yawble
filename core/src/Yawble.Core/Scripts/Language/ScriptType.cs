using System.Text.Json.Nodes;

namespace Yawble.Core.Scripts.Language;

public enum ScriptTypeKind
{
    /// <summary>Not known when the script is checked (an integration's result, an API body): anything goes.</summary>
    Any,
    Null,
    Int,
    Float,
    String,
    Bool,
    Date,
    DateTime,
    Duration,
    Enum,
    Object,
    List,
}

/// <summary>
/// A script type: a primitive (<c>int float string bool date datetime duration</c>), <c>enum(a, b)</c>, an object (a
/// schema by name, or an anonymous object a schema's field describes), or <c>list&lt;T&gt;</c>.
/// </summary>
public sealed class ScriptType
{
    public ScriptTypeKind Kind { get; }

    /// <summary>The schema's name for a named object or enum; null for an anonymous one.</summary>
    public string? Name { get; }

    /// <summary>The item type of a list.</summary>
    public ScriptType? Element { get; }

    /// <summary>The values of an enum.</summary>
    public IReadOnlyList<string> EnumValues { get; }

    /// <summary>The JSON Schema of an object (its properties), when known.</summary>
    public JsonObject? Schema { get; }

    private ScriptType(ScriptTypeKind kind, string? name = null, ScriptType? element = null, IReadOnlyList<string>? values = null, JsonObject? schema = null)
    {
        Kind = kind;
        Name = name;
        Element = element;
        EnumValues = values ?? [];
        Schema = schema;
    }

    public static readonly ScriptType Any = new(ScriptTypeKind.Any);
    public static readonly ScriptType Null = new(ScriptTypeKind.Null);
    public static readonly ScriptType Int = new(ScriptTypeKind.Int);
    public static readonly ScriptType Float = new(ScriptTypeKind.Float);
    public static readonly ScriptType String = new(ScriptTypeKind.String);
    public static readonly ScriptType Bool = new(ScriptTypeKind.Bool);
    public static readonly ScriptType Date = new(ScriptTypeKind.Date);
    public static readonly ScriptType DateTime = new(ScriptTypeKind.DateTime);
    public static readonly ScriptType Duration = new(ScriptTypeKind.Duration);

    public static ScriptType ListOf(ScriptType element) => new(ScriptTypeKind.List, element: element);
    public static ScriptType EnumOf(IReadOnlyList<string> values, string? name = null) => new(ScriptTypeKind.Enum, name, values: values);
    public static ScriptType ObjectOf(string? name, JsonObject? schema) => new(ScriptTypeKind.Object, name, schema: schema);

    public bool IsNumber => Kind is ScriptTypeKind.Int or ScriptTypeKind.Float;

    /// <summary>
    /// Parses a type as a script writes it: <c>int</c>, <c>list&lt;EmailMessage&gt;</c>, <c>enum(order, proof)</c>, or a
    /// schema's name. A name is looked up with <paramref name="findSchema"/> (built-in schemas first); an unknown name, or a
    /// malformed type, gives null and the reason in <paramref name="error"/>.
    /// </summary>
    public static ScriptType? Parse(string? text, Func<string, ScriptSchema?> findSchema, out string? error)
    {
        error = null;
        var t = (text ?? "").Trim();
        if (t.Length == 0) { error = "no type"; return null; }
        switch (t)
        {
            case "int": return Int;
            case "float": return Float;
            case "string": return String;
            case "bool": return Bool;
            case "date": return Date;
            case "datetime": return DateTime;
            case "duration": return Duration;
            case "any": return Any;
        }
        if (t.StartsWith("list<", StringComparison.Ordinal) && t.EndsWith('>'))
        {
            var inner = Parse(t[5..^1], findSchema, out error);
            return inner is null ? null : ListOf(inner);
        }
        if (t.StartsWith("enum(", StringComparison.Ordinal) && t.EndsWith(')'))
        {
            var values = t[5..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 0) { error = $"enum '{t}' has no values"; return null; }
            return EnumOf(values);
        }
        if (!IsName(t)) { error = $"'{t}' is not a type"; return null; }
        var schema = BuiltInSchemas.Find(t) ?? findSchema(t);
        if (schema is null) { error = $"no schema named '{t}'"; return null; }
        return FromSchema(schema);
    }

    /// <summary>The type a schema describes: an object, or a named enum when the schema is a string enum.</summary>
    public static ScriptType FromSchema(ScriptSchema schema)
    {
        var s = schema.Schema;
        if (s["enum"] is JsonArray values && (string?)s["type"] is null or "string")
            return EnumOf(values.Select(v => v?.ToString() ?? "").ToList(), schema.Name);
        return ObjectOf(schema.Name, s);
    }

    /// <summary>
    /// The type a JSON Schema node describes (a schema's field). A <c>$ref</c> names another schema and is looked up with
    /// <paramref name="findSchema"/>; an unknown one, or a shape the subset does not describe, is <see cref="Any"/>.
    /// </summary>
    public static ScriptType FromJsonSchema(JsonNode? node, Func<string, ScriptSchema?> findSchema)
    {
        if (node is not JsonObject o) return Any;
        if ((string?)o["$ref"] is { } reference)
        {
            var target = BuiltInSchemas.Find(reference) ?? findSchema(reference);
            return target is null ? Any : FromSchema(target);
        }
        var type = JsonSchemaValidator.PrimaryType(o);
        if (o["enum"] is JsonArray values && type is null or "string")
            return EnumOf(values.Select(v => v?.ToString() ?? "").ToList());
        return type switch
        {
            "integer" => Int,
            "number" => Float,
            "boolean" => Bool,
            "string" => (string?)o["format"] switch
            {
                "date-time" => DateTime,
                "date" => Date,
                "duration" => Duration,
                _ => String,
            },
            "array" => ListOf(FromJsonSchema(o["items"], findSchema)),
            "object" => ObjectOf(null, o),
            "null" => Null,
            _ => Any,
        };
    }

    /// <summary>The type of a field of this object type, when this is an object whose schema lists the field.</summary>
    public bool TryGetField(string field, Func<string, ScriptSchema?> findSchema, out ScriptType type)
    {
        type = Any;
        if (Kind != ScriptTypeKind.Object || Schema?["properties"] is not JsonObject props) return false;
        if (!props.TryGetPropertyValue(field, out var f)) return false;
        type = FromJsonSchema(f, findSchema);
        return true;
    }

    /// <summary>Whether a value of <paramref name="source"/> can go where <paramref name="target"/> is declared.</summary>
    public static bool IsAssignable(ScriptType target, ScriptType source)
    {
        if (target.Kind == ScriptTypeKind.Any || source.Kind is ScriptTypeKind.Any or ScriptTypeKind.Null) return true;
        switch (target.Kind)
        {
            case ScriptTypeKind.Float: return source.IsNumber;
            // A scalar formats as text; a list or object does not.
            case ScriptTypeKind.String: return source.Kind is not (ScriptTypeKind.List or ScriptTypeKind.Object);
            // Text converts when the run reaches it (a literal is checked up front, see ScriptChecker).
            case ScriptTypeKind.Date or ScriptTypeKind.DateTime or ScriptTypeKind.Duration:
                return source.Kind == target.Kind || source.Kind == ScriptTypeKind.String
                       || (target.Kind == ScriptTypeKind.DateTime && source.Kind == ScriptTypeKind.Date);
            case ScriptTypeKind.Enum:
                return source.Kind == ScriptTypeKind.String
                       || (source.Kind == ScriptTypeKind.Enum && source.EnumValues.All(target.EnumValues.Contains));
            case ScriptTypeKind.Object:
                return source.Kind == ScriptTypeKind.Object && (target.Name is null || source.Name is null || target.Name == source.Name);
            case ScriptTypeKind.List:
                return source.Kind == ScriptTypeKind.List && IsAssignable(target.Element!, source.Element!);
            default:
                return source.Kind == target.Kind;
        }
    }

    public override string ToString() => Kind switch
    {
        ScriptTypeKind.List => $"list<{Element}>",
        ScriptTypeKind.Enum => Name ?? $"enum({string.Join(", ", EnumValues)})",
        ScriptTypeKind.Object => Name ?? "object",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    internal static bool IsName(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');
}
