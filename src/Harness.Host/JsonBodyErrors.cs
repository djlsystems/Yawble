using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Metadata;

namespace Harness.Host;

/// <summary>
/// A REQUEST THE ROUTE CANNOT READ IS ANSWERED WITH A SENTENCE, in one place for every route.
///
/// <para>
/// A minimal-API route whose JSON body does not bind - a wrong type, malformed JSON - answered
/// 400 with an empty body, so a caller that sent `causation` as a number could not tell what was
/// wrong. Route handlers are made to throw on a bad request (<c>ThrowOnBadRequest</c>), and this
/// middleware turns the throw into 400 with <c>{ "error": "causation must be a string, as
/// \"4126\"" }</c>: the field by its JSON path, the type the route expects, and an example built
/// from what was sent when it can be.
/// </para>
///
/// <para>
/// The body is buffered for a JSON request, so the value that did not bind can be read back for
/// the example. Any other bad request (a route value that does not parse, a missing body) is
/// answered with the framework's own sentence under the same status.
/// </para>
/// </summary>
public static class JsonBodyErrors
{
    public static void Use(IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.HasJsonContentType()) context.Request.EnableBuffering();

        try
        {
            await next();
        }
        catch (BadHttpRequestException error) when (!context.Response.HasStarted)
        {
            var sentence = error.InnerException is JsonException json
                ? await SentenceAsync(context, json)
                : error.Message;

            context.Response.Clear();
            context.Response.StatusCode = error.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = sentence }, context.RequestAborted);
        }
    });

    private static async Task<string> SentenceAsync(HttpContext context, JsonException error)
    {
        var bodyType = context.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
        var options = context.RequestServices
            .GetService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?
            .Value.SerializerOptions ?? JsonSerializerOptions.Web;

        JsonElement? sent = null;

        try
        {
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
                using var document = await JsonDocument.ParseAsync(
                    context.Request.Body, cancellationToken: context.RequestAborted);
                sent = At(document.RootElement, error.Path)?.Clone();
            }
        }
        catch (JsonException)
        {
            // Not JSON at all: the sentence below says where it stopped being JSON.
            return Malformed(error);
        }

        if (sent is null && !IsConversion(error)) return Malformed(error);

        return Sentence(bodyType, error.Path, sent, options);
    }

    /// <summary>
    /// The sentence for a value at <paramref name="path"/> that did not bind to the route's body
    /// type: the field, the type expected, and an example. Public for the tests.
    /// </summary>
    public static string Sentence(Type? bodyType, string? path, JsonElement? sent, JsonSerializerOptions options)
    {
        var field = Field(path);
        var expected = bodyType is null ? null : TypeAt(bodyType, path, options);

        var subject = field is null ? "The request body" : field;

        return expected is null
            ? $"{subject} is not the type this route expects."
            : $"{subject} must be {Expected(expected, sent, options)}.";
    }

    private static string Malformed(JsonException error)
    {
        var where = error.LineNumber is { } line && error.BytePositionInLine is { } position
            ? $" at line {line + 1}, position {position + 1}"
            : "";

        return $"The request body is not valid JSON{where}.";
    }

    private static bool IsConversion(JsonException error) =>
        error.Message.StartsWith("The JSON value could not be converted", StringComparison.Ordinal);

    /// <summary>`$.items[0].name` as `items[0].name`; the root as null.</summary>
    private static string? Field(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$") return null;
        return path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');
    }

    /// <summary>One step of a JSON path: a property name, or an index into a list.</summary>
    private readonly record struct Step(string? Name, int Index);

    /// <summary>`$.items[0].name` as items, [0], name. Stops at anything it cannot read.</summary>
    private static List<Step> Steps(string? path)
    {
        var steps = new List<Step>();
        if (string.IsNullOrEmpty(path)) return steps;

        var i = path.StartsWith('$') ? 1 : 0;

        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
                steps.Add(new Step(path[(i + 1)..end], -1));
                i = end;
            }
            else if (path[i] == '[' && path.IndexOf(']', i) is var close and > 0)
            {
                var inner = path[(i + 1)..close];

                if (inner.StartsWith('\''))
                {
                    steps.Add(new Step(inner.Trim('\''), -1));
                }
                else if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    steps.Add(new Step(null, n));
                }
                else
                {
                    break;
                }

                i = close + 1;
            }
            else
            {
                break;
            }
        }

        return steps;
    }

    private static JsonElement? At(JsonElement root, string? path)
    {
        var current = root;

        foreach (var step in Steps(path))
        {
            if (step.Name is null)
            {
                if (current.ValueKind != JsonValueKind.Array || step.Index >= current.GetArrayLength()) return null;
                current = current[step.Index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object) return null;

            JsonElement? found = null;
            foreach (var property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, step.Name, StringComparison.OrdinalIgnoreCase))
                {
                    found = property.Value;
                    break;
                }
            }

            if (found is not { } next) return null;
            current = next;
        }

        return current;
    }

    private static Type? TypeAt(Type root, string? path, JsonSerializerOptions options)
    {
        var current = root;

        foreach (var step in Steps(path))
        {
            current = Nullable.GetUnderlyingType(current) ?? current;

            if (step.Name is null)
            {
                if (ElementType(current) is not { } element) return null;
                current = element;
                continue;
            }

            JsonTypeInfo info;
            try
            {
                info = options.GetTypeInfo(current);
            }
            catch (NotSupportedException)
            {
                return null;
            }

            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                current = info.ElementType ?? typeof(object);
                continue;
            }

            if (info.Kind != JsonTypeInfoKind.Object) return null;

            var property = info.Properties.FirstOrDefault(
                p => string.Equals(p.Name, step.Name, StringComparison.OrdinalIgnoreCase));
            if (property is null) return null;
            current = property.PropertyType;
        }

        return current;
    }

    private static Type? ElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();

        return type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?
            .GetGenericArguments()[0];
    }

    /// <summary>"a string, as \"4126\"" and its siblings.</summary>
    private static string Expected(Type type, JsonElement? sent, JsonSerializerOptions options)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string))
        {
            var example = sent is { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } value
                ? value.GetRawText()
                : "text";
            return $"a string, as {JsonSerializer.Serialize(example)}";
        }

        if (type == typeof(bool)) return "true or false";

        if (type.IsEnum)
        {
            return "one of " + string.Join(", ", Enum.GetNames(type).Select(n => JsonSerializer.Serialize(n)));
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
        {
            var example = sent is { ValueKind: JsonValueKind.String } value
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    ? n.ToString(CultureInfo.InvariantCulture)
                    : "42";
            return $"a whole number, as {example}";
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return "a number, as 1.5";
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
        {
            return "a date and time, as \"2026-10-01T09:30:00Z\"";
        }

        if (type == typeof(Guid)) return "a GUID string, as \"0f8fad5b-d9cb-469f-a165-70867728950e\"";

        if (type == typeof(JsonElement) || type == typeof(object)) return "a JSON value";

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            try
            {
                if (options.GetTypeInfo(type).Kind == JsonTypeInfoKind.Dictionary) return "an object";
            }
            catch (NotSupportedException)
            {
            }

            return ElementType(type) is { } element
                ? $"a list of {Plural(element, options)}"
                : "a list";
        }

        return "an object";
    }

    private static string Plural(Type element, JsonSerializerOptions options)
    {
        var single = Expected(element, null, options);

        // "a string, as \"text\"" -> "strings"
        var head = single.Split(',')[0];
        return head switch
        {
            "a string" => "strings",
            "a whole number" => "whole numbers",
            "a number" => "numbers",
            "true or false" => "true or false values",
            "an object" => "objects",
            _ => "values",
        };
    }
}
