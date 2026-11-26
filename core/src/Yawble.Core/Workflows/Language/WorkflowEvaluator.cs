using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Workflows.Language;

/// <summary>What a path's root name stands for while a workflow runs: <c>input</c>, <c>settings</c>, <c>vars</c>,
/// <c>steps</c> and the names of the loops the step is in.</summary>
public interface IWorkflowScope
{
    bool TryGetRoot(string name, out JsonNode? value);
}

/// <summary>A scope over fixed roots (tests, and the engine's own).</summary>
public sealed class WorkflowScope(IReadOnlyDictionary<string, JsonNode?> roots) : IWorkflowScope
{
    public bool TryGetRoot(string name, out JsonNode? value) => roots.TryGetValue(name, out value);
}

/// <summary>Evaluates expressions, templates and values (JSON with templates in its strings) in a scope.</summary>
public sealed class WorkflowEvaluator(IWorkflowScope scope, TimeProvider? clock = null)
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>A JSON value with every string read as a template: a template that is one expression keeps its type.</summary>
    public JsonNode? EvaluateValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => WorkflowValues.ToNode(EvaluateTemplate(v.GetValue<string>())),
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, EvaluateValue(p.Value)))),
        JsonArray a => new JsonArray(a.Select(EvaluateValue).ToArray()),
        _ => node.DeepClone(),
    };

    public object? EvaluateTemplate(string text) => Evaluate(WorkflowTemplate.Parse(text));

    public object? Evaluate(WorkflowTemplate template)
    {
        if (template.IsSingleExpression) return Evaluate((WorkflowExpr)template.Parts[0]);
        var sb = new StringBuilder();
        foreach (var part in template.Parts)
            sb.Append(part is WorkflowExpr e ? WorkflowValues.Format(Evaluate(e)) : (string)part);
        return sb.ToString();
    }

    public object? Evaluate(WorkflowExpr expr) => expr switch
    {
        LiteralExpr l => l.Value,
        PathExpr p => Resolve(p),
        NegateExpr n => Evaluate(n.Operand) switch
        {
            long l => (object)checked(-l),
            double d => -d,
            TimeSpan t => -t,
            var v => throw new WorkflowValueException($"cannot negate {WorkflowValues.Describe(v)}"),
        },
        BinaryExpr b => Binary(b.Op, Evaluate(b.Left), Evaluate(b.Right)),
        CallExpr c => Call(c),
        _ => throw new WorkflowValueException("unknown expression"),
    };

    private object? Resolve(PathExpr path)
    {
        if (!scope.TryGetRoot(path.Root, out var node)) throw new WorkflowValueException($"'{path.Root}' is not defined");
        foreach (var segment in path.Segments)
        {
            node = (node, segment) switch
            {
                (null, _) => null,
                (JsonObject o, string field) => o[field],
                (JsonArray a, int index) => index < a.Count ? a[index] : null,
                (JsonArray, string field) => throw new WorkflowValueException($"'{path}': a list has no field '{field}'"),
                (_, var s) => throw new WorkflowValueException($"'{path}': {WorkflowValues.Describe(WorkflowValues.FromNode(node))} has no {(s is int ? "items" : $"field '{s}'")}"),
            };
        }
        return WorkflowValues.FromNode(node);
    }

    internal static object? Binary(string op, object? a, object? b)
    {
        switch (op)
        {
            case "==": return WorkflowValues.AreEqual(a, b);
            case "!=": return !WorkflowValues.AreEqual(a, b);
            case "<" or "<=" or ">" or ">=":
                if (!WorkflowValues.TryCompare(a, b, out var c))
                    throw new WorkflowValueException($"cannot compare {WorkflowValues.Describe(a)} with {WorkflowValues.Describe(b)}");
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
                _ => lb == 0 ? throw new WorkflowValueException("division by zero") : la % lb == 0 ? la / lb : (object)((double)la / lb),
            };
        }
        if (WorkflowValues.IsNumber(a) && WorkflowValues.IsNumber(b))
        {
            double x = WorkflowValues.ToDouble(a), y = WorkflowValues.ToDouble(b);
            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                _ => y == 0 ? throw new WorkflowValueException("division by zero") : x / y,
            };
        }

        // Times: datetime +/- duration, datetime - datetime, duration +/- duration, duration * or / a number.
        if (TimeArithmetic(op, a, b, out var timeResult)) return timeResult;

        if (op == "+" && (a is string || b is string)) return WorkflowValues.Format(a) + WorkflowValues.Format(b);
        throw new WorkflowValueException($"cannot apply '{op}' to {WorkflowValues.Describe(a)} and {WorkflowValues.Describe(b)}");
    }

    private static bool TimeArithmetic(string op, object? a, object? b, out object? result)
    {
        result = null;
        bool IsInstant(object? v) => v is DateTimeOffset || (v is string s && WorkflowValues.TryParseInstant(s, out _));
        bool IsDate(object? v) => v is DateOnly || (v is string s && WorkflowValues.TryParseDate(s, out _));
        bool IsDuration(object? v) => v is TimeSpan || (v is string s && s.StartsWith('P') && WorkflowValues.TryParseDuration(s, out _));

        if ((op == "+" || op == "-") && (IsInstant(a) || a is DateOnly) && IsDuration(b))
        {
            var t = WorkflowValues.ToInstant(a);
            var d = WorkflowValues.ToDuration(b);
            result = op == "+" ? t + d : t - d;
            return true;
        }
        if (op == "+" && IsDuration(a) && (IsInstant(b) || b is DateOnly)) { result = WorkflowValues.ToInstant(b) + WorkflowValues.ToDuration(a); return true; }
        if (op == "-" && (IsInstant(a) || IsDate(a)) && (IsInstant(b) || IsDate(b)))
        {
            result = WorkflowValues.ToInstant(a) - WorkflowValues.ToInstant(b);
            return true;
        }
        if ((op == "+" || op == "-") && ((a is TimeSpan && IsDuration(b)) || (IsDuration(a) && b is TimeSpan)))
        {
            var x = WorkflowValues.ToDuration(a);
            var y = WorkflowValues.ToDuration(b);
            result = op == "+" ? x + y : x - y;
            return true;
        }
        if ((op == "*" || op == "/") && a is TimeSpan da && WorkflowValues.IsNumber(b))
        {
            var n = WorkflowValues.ToDouble(b);
            if (op == "/" && n == 0) throw new WorkflowValueException("division by zero");
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
                    var v => throw new WorkflowValueException($"count() of {WorkflowValues.Describe(v)}: not a list"),
                };
            case "first":
            {
                var list = WorkflowValues.ToList(args[0]);
                return list.Count == 0 ? null : WorkflowValues.FromNode(list[0]);
            }
            case "last":
            {
                var list = WorkflowValues.ToList(args[0]);
                return list.Count == 0 ? null : WorkflowValues.FromNode(list[^1]);
            }
            case "max" or "min":
            {
                var list = WorkflowValues.ToList(args[0]);
                var field = args.Length > 1 ? args[1] as string ?? throw new WorkflowValueException($"{call.Function}(): the field is not text") : null;
                object? best = null;
                foreach (var item in list)
                {
                    var v = field is null ? WorkflowValues.FromNode(item) : WorkflowValues.FromNode((item as JsonObject)?[field]);
                    if (v is null) continue;
                    if (best is null) { best = v; continue; }
                    if (!WorkflowValues.TryCompare(v, best, out var c))
                        throw new WorkflowValueException($"{call.Function}(): cannot compare {WorkflowValues.Describe(v)} with {WorkflowValues.Describe(best)}");
                    if (call.Function == "max" ? c > 0 : c < 0) best = v;
                }
                return best;
            }
            case "addHours":
                return WorkflowValues.ToInstant(args[0]).AddHours(WorkflowValues.ToDouble(args[1]));
            case "addDays":
            {
                var n = WorkflowValues.ToDouble(args[1]);
                if (args[0] is DateOnly d || (args[0] is string s && WorkflowValues.TryParseDate(s, out d)))
                {
                    if (Math.Floor(n) != n) throw new WorkflowValueException("addDays() on a date takes a whole number of days");
                    return d.AddDays((int)n);
                }
                return WorkflowValues.ToInstant(args[0]).AddDays(n);
            }
            case "hoursSince":
                return args[0] is null ? null : (now - WorkflowValues.ToInstant(args[0])).TotalHours;
            case "formatDuration":
                return args[0] is null ? "" : WorkflowValues.FormatDuration(WorkflowValues.ToDuration(args[0]));
            case "lower": return WorkflowValues.Format(args[0]).ToLowerInvariant();
            case "upper": return WorkflowValues.Format(args[0]).ToUpperInvariant();
            case "contains":
                return args[0] switch
                {
                    null => false,
                    JsonArray a => a.Any(i => WorkflowValues.AreEqual(WorkflowValues.FromNode(i), args[1])),
                    JsonObject o => args[1] is string key && o.ContainsKey(key),
                    var v => WorkflowValues.Format(v).Contains(WorkflowValues.Format(args[1]), StringComparison.Ordinal),
                };
            case "startsWith":
                return args[0] is not null && WorkflowValues.Format(args[0]).StartsWith(WorkflowValues.Format(args[1]), StringComparison.Ordinal);
            case "matches":
                return args[0] is not null && Matches(WorkflowValues.Format(args[0]), WorkflowValues.Format(args[1]));
            case "join":
                return string.Join(WorkflowValues.Format(args[1]), WorkflowValues.ToList(args[0]).Select(i => WorkflowValues.Format(WorkflowValues.FromNode(i))));
            case "coalesce":
                return args.FirstOrDefault(a => a is not null);
        }
        throw new WorkflowValueException($"'{call.Function}' is not a function");
    }

    internal static bool Matches(string text, string pattern)
    {
        try { return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, RegexTimeout); }
        catch (ArgumentException e) { throw new WorkflowValueException($"'{pattern}' is not a regular expression: {e.Message}"); }
        catch (RegexMatchTimeoutException) { throw new WorkflowValueException($"matching '{pattern}' took too long"); }
    }
}
