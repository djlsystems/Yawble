using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Scripts.Language;

/// <summary>What a path's root name stands for while a script runs: <c>input</c>, <c>settings</c>, <c>vars</c>,
/// <c>steps</c> and the names of the loops the step is in.</summary>
public interface IScriptScope
{
    bool TryGetRoot(string name, out JsonNode? value);
}

/// <summary>A scope over fixed roots (tests, and the engine's own).</summary>
public sealed class ScriptScope(IReadOnlyDictionary<string, JsonNode?> roots) : IScriptScope
{
    public bool TryGetRoot(string name, out JsonNode? value) => roots.TryGetValue(name, out value);
}

/// <summary>Evaluates expressions, templates and values (JSON with templates in its strings) in a scope.</summary>
public sealed class ScriptEvaluator(IScriptScope scope, TimeProvider? clock = null)
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>A JSON value with every string read as a template: a template that is one expression keeps its type.</summary>
    public JsonNode? EvaluateValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => ScriptValues.ToNode(EvaluateTemplate(v.GetValue<string>())),
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, EvaluateValue(p.Value)))),
        JsonArray a => new JsonArray(a.Select(EvaluateValue).ToArray()),
        _ => node.DeepClone(),
    };

    public object? EvaluateTemplate(string text) => Evaluate(ScriptTemplate.Parse(text));

    public object? Evaluate(ScriptTemplate template)
    {
        if (template.IsSingleExpression) return Evaluate((ScriptExpr)template.Parts[0]);
        var sb = new StringBuilder();
        foreach (var part in template.Parts)
            sb.Append(part is ScriptExpr e ? ScriptValues.Format(Evaluate(e)) : (string)part);
        return sb.ToString();
    }

    public object? Evaluate(ScriptExpr expr) => expr switch
    {
        LiteralExpr l => l.Value,
        PathExpr p => Resolve(p),
        NegateExpr n => Evaluate(n.Operand) switch
        {
            long l => (object)checked(-l),
            double d => -d,
            TimeSpan t => -t,
            var v => throw new ScriptValueException($"cannot negate {ScriptValues.Describe(v)}"),
        },
        BinaryExpr b => Binary(b.Op, Evaluate(b.Left), Evaluate(b.Right)),
        CallExpr c => Call(c),
        _ => throw new ScriptValueException("unknown expression"),
    };

    private object? Resolve(PathExpr path)
    {
        if (!scope.TryGetRoot(path.Root, out var node)) throw new ScriptValueException($"'{path.Root}' is not defined");
        foreach (var segment in path.Segments)
        {
            node = (node, segment) switch
            {
                (null, _) => null,
                (JsonObject o, string field) => o[field],
                (JsonArray a, int index) => index < a.Count ? a[index] : null,
                (JsonArray, string field) => throw new ScriptValueException($"'{path}': a list has no field '{field}'"),
                (_, var s) => throw new ScriptValueException($"'{path}': {ScriptValues.Describe(ScriptValues.FromNode(node))} has no {(s is int ? "items" : $"field '{s}'")}"),
            };
        }
        return ScriptValues.FromNode(node);
    }

    internal static object? Binary(string op, object? a, object? b)
    {
        switch (op)
        {
            case "==": return ScriptValues.AreEqual(a, b);
            case "!=": return !ScriptValues.AreEqual(a, b);
            case "<" or "<=" or ">" or ">=":
                if (!ScriptValues.TryCompare(a, b, out var c))
                    throw new ScriptValueException($"cannot compare {ScriptValues.Describe(a)} with {ScriptValues.Describe(b)}");
                return op switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 };
        }

        if (a is long la && b is long lb)
        {
            // Boxed per branch: a switch with a double branch would make every result a double.
            return op switch
            {
                "+" => (object)checked(la + lb),
                "-" => checked(la - lb),
                "*" => checked(la * lb),
                _ => lb == 0 ? throw new ScriptValueException("division by zero") : la % lb == 0 ? la / lb : (object)((double)la / lb),
            };
        }
        if (ScriptValues.IsNumber(a) && ScriptValues.IsNumber(b))
        {
            double x = ScriptValues.ToDouble(a), y = ScriptValues.ToDouble(b);
            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                _ => y == 0 ? throw new ScriptValueException("division by zero") : x / y,
            };
        }

        // Times: datetime +/- duration, datetime - datetime, duration +/- duration, duration * or / a number.
        if (TimeArithmetic(op, a, b, out var timeResult)) return timeResult;

        if (op == "+" && (a is string || b is string)) return ScriptValues.Format(a) + ScriptValues.Format(b);
        throw new ScriptValueException($"cannot apply '{op}' to {ScriptValues.Describe(a)} and {ScriptValues.Describe(b)}");
    }

    private static bool TimeArithmetic(string op, object? a, object? b, out object? result)
    {
        result = null;
        bool IsInstant(object? v) => v is DateTimeOffset || (v is string s && ScriptValues.TryParseInstant(s, out _));
        bool IsDate(object? v) => v is DateOnly || (v is string s && ScriptValues.TryParseDate(s, out _));
        bool IsDuration(object? v) => v is TimeSpan || (v is string s && s.StartsWith('P') && ScriptValues.TryParseDuration(s, out _));

        if ((op == "+" || op == "-") && (IsInstant(a) || a is DateOnly) && IsDuration(b))
        {
            var t = ScriptValues.ToInstant(a);
            var d = ScriptValues.ToDuration(b);
            result = op == "+" ? t + d : t - d;
            return true;
        }
        if (op == "+" && IsDuration(a) && (IsInstant(b) || b is DateOnly)) { result = ScriptValues.ToInstant(b) + ScriptValues.ToDuration(a); return true; }
        if (op == "-" && (IsInstant(a) || IsDate(a)) && (IsInstant(b) || IsDate(b)))
        {
            result = ScriptValues.ToInstant(a) - ScriptValues.ToInstant(b);
            return true;
        }
        if ((op == "+" || op == "-") && ((a is TimeSpan && IsDuration(b)) || (IsDuration(a) && b is TimeSpan)))
        {
            var x = ScriptValues.ToDuration(a);
            var y = ScriptValues.ToDuration(b);
            result = op == "+" ? x + y : x - y;
            return true;
        }
        if ((op == "*" || op == "/") && a is TimeSpan da && ScriptValues.IsNumber(b))
        {
            var n = ScriptValues.ToDouble(b);
            if (op == "/" && n == 0) throw new ScriptValueException("division by zero");
            result = op == "*" ? da * n : da / n;
            return true;
        }
        return false;
    }

    private object? Call(CallExpr call)
    {
        var args = call.Args.Select(Evaluate).ToArray();
        var now = _clock.GetUtcNow();
        switch (call.Function)
        {
            case "now": return now;
            case "today": return DateOnly.FromDateTime(now.UtcDateTime);
            case "count":
                return args[0] switch
                {
                    null => 0L,
                    JsonArray a => (long)a.Count,
                    string s => (long)s.Length,
                    JsonObject o => (long)o.Count,
                    var v => throw new ScriptValueException($"count() of {ScriptValues.Describe(v)}: not a list"),
                };
            case "first":
            {
                var list = ScriptValues.ToList(args[0]);
                return list.Count == 0 ? null : ScriptValues.FromNode(list[0]);
            }
            case "last":
            {
                var list = ScriptValues.ToList(args[0]);
                return list.Count == 0 ? null : ScriptValues.FromNode(list[^1]);
            }
            case "max" or "min":
            {
                var list = ScriptValues.ToList(args[0]);
                var field = args.Length > 1 ? args[1] as string ?? throw new ScriptValueException($"{call.Function}(): the field is not text") : null;
                object? best = null;
                foreach (var item in list)
                {
                    var v = field is null ? ScriptValues.FromNode(item) : ScriptValues.FromNode((item as JsonObject)?[field]);
                    if (v is null) continue;
                    if (best is null) { best = v; continue; }
                    if (!ScriptValues.TryCompare(v, best, out var c))
                        throw new ScriptValueException($"{call.Function}(): cannot compare {ScriptValues.Describe(v)} with {ScriptValues.Describe(best)}");
                    if (call.Function == "max" ? c > 0 : c < 0) best = v;
                }
                return best;
            }
            case "addHours":
                return ScriptValues.ToInstant(args[0]).AddHours(ScriptValues.ToDouble(args[1]));
            case "addDays":
            {
                var n = ScriptValues.ToDouble(args[1]);
                if (args[0] is DateOnly d || (args[0] is string s && ScriptValues.TryParseDate(s, out d)))
                {
                    if (Math.Floor(n) != n) throw new ScriptValueException("addDays() on a date takes a whole number of days");
                    return d.AddDays((int)n);
                }
                return ScriptValues.ToInstant(args[0]).AddDays(n);
            }
            case "hoursSince":
                return args[0] is null ? null : (now - ScriptValues.ToInstant(args[0])).TotalHours;
            case "formatDuration":
                return args[0] is null ? "" : ScriptValues.FormatDuration(ScriptValues.ToDuration(args[0]));
            case "lower": return ScriptValues.Format(args[0]).ToLowerInvariant();
            case "upper": return ScriptValues.Format(args[0]).ToUpperInvariant();
            case "contains":
                return args[0] switch
                {
                    null => false,
                    JsonArray a => a.Any(i => ScriptValues.AreEqual(ScriptValues.FromNode(i), args[1])),
                    JsonObject o => args[1] is string key && o.ContainsKey(key),
                    var v => ScriptValues.Format(v).Contains(ScriptValues.Format(args[1]), StringComparison.Ordinal),
                };
            case "startsWith":
                return args[0] is not null && ScriptValues.Format(args[0]).StartsWith(ScriptValues.Format(args[1]), StringComparison.Ordinal);
            case "matches":
                return args[0] is not null && Matches(ScriptValues.Format(args[0]), ScriptValues.Format(args[1]));
            case "join":
                return string.Join(ScriptValues.Format(args[1]), ScriptValues.ToList(args[0]).Select(i => ScriptValues.Format(ScriptValues.FromNode(i))));
            case "coalesce":
                return args.FirstOrDefault(a => a is not null);
        }
        throw new ScriptValueException($"'{call.Function}' is not a function");
    }

    internal static bool Matches(string text, string pattern)
    {
        try { return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, RegexTimeout); }
        catch (ArgumentException e) { throw new ScriptValueException($"'{pattern}' is not a regular expression: {e.Message}"); }
        catch (RegexMatchTimeoutException) { throw new ScriptValueException($"matching '{pattern}' took too long"); }
    }
}
