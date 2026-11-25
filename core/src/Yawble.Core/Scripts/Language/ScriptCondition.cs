using System.Text.Json.Nodes;

namespace Yawble.Core.Scripts.Language;

/// <summary>
/// A condition: <c>{ "left": ..., "op": ..., "right": ... }</c>, or <c>{ "all": [...] }</c>, <c>{ "any": [...] }</c>,
/// <c>{ "not": ... }</c>, or a template whose value is read as true or false. Left and right are values (templates in
/// their strings).
/// </summary>
public abstract record ScriptCondition
{
    public static readonly IReadOnlyList<string> Operators =
        ["eq", "ne", "gt", "ge", "lt", "le", "contains", "startsWith", "matches", "exists", "empty"];

    /// <summary>Operators that take no right-hand side.</summary>
    public static bool IsUnary(string op) => op is "exists" or "empty";

    public static ScriptCondition Parse(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String:
                return new TemplateCondition(v.GetValue<string>());
            case JsonValue b when b.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False:
                return new TemplateCondition(b.GetValue<bool>() ? "{true}" : "{false}");
            case JsonObject o when o["all"] is JsonArray all:
                return new AllCondition(all.Select(Parse).ToList());
            case JsonObject o when o["any"] is JsonArray any:
                return new AnyCondition(any.Select(Parse).ToList());
            case JsonObject o when o.ContainsKey("not"):
                return new NotCondition(Parse(o["not"]));
            case JsonObject o when o.ContainsKey("op"):
            {
                var op = (string?)o["op"] ?? "";
                if (!Operators.Contains(op)) throw new ScriptExpressionException($"'{op}' is not a condition operator ({string.Join(", ", Operators)})");
                if (!o.ContainsKey("left")) throw new ScriptExpressionException($"the '{op}' condition has no left");
                if (!IsUnary(op) && !o.ContainsKey("right")) throw new ScriptExpressionException($"the '{op}' condition has no right");
                return new CompareCondition(o["left"], op, o["right"]);
            }
            default:
                throw new ScriptExpressionException("a condition is {left, op, right}, {all: [...]}, {any: [...]}, {not: ...} or a template");
        }
    }

    public abstract bool Evaluate(ScriptEvaluator evaluator);
}

public sealed record TemplateCondition(string Template) : ScriptCondition
{
    public override bool Evaluate(ScriptEvaluator evaluator) => ScriptValues.Truthy(evaluator.EvaluateTemplate(Template));
}

public sealed record AllCondition(IReadOnlyList<ScriptCondition> Items) : ScriptCondition
{
    public override bool Evaluate(ScriptEvaluator evaluator) => Items.All(i => i.Evaluate(evaluator));
}

public sealed record AnyCondition(IReadOnlyList<ScriptCondition> Items) : ScriptCondition
{
    public override bool Evaluate(ScriptEvaluator evaluator) => Items.Any(i => i.Evaluate(evaluator));
}

public sealed record NotCondition(ScriptCondition Inner) : ScriptCondition
{
    public override bool Evaluate(ScriptEvaluator evaluator) => !Inner.Evaluate(evaluator);
}

public sealed record CompareCondition(JsonNode? Left, string Op, JsonNode? Right) : ScriptCondition
{
    public override bool Evaluate(ScriptEvaluator evaluator)
    {
        var left = ScriptValues.FromNode(evaluator.EvaluateValue(Left));
        if (Op == "exists") return left is not null;
        if (Op == "empty") return ScriptValues.IsEmpty(left);
        var right = ScriptValues.FromNode(evaluator.EvaluateValue(Right));
        switch (Op)
        {
            case "eq": return ScriptValues.AreEqual(left, right);
            case "ne": return !ScriptValues.AreEqual(left, right);
            case "contains":
                return left switch
                {
                    null => false,
                    JsonArray a => a.Any(i => ScriptValues.AreEqual(ScriptValues.FromNode(i), right)),
                    _ => ScriptValues.Format(left).Contains(ScriptValues.Format(right), StringComparison.Ordinal),
                };
            case "startsWith":
                return left is not null && ScriptValues.Format(left).StartsWith(ScriptValues.Format(right), StringComparison.Ordinal);
            case "matches":
                return left is not null && ScriptEvaluator.Matches(ScriptValues.Format(left), ScriptValues.Format(right));
        }
        // gt ge lt le: a null on either side is false (nothing to order).
        if (left is null || right is null) return false;
        if (!ScriptValues.TryCompare(left, right, out var c))
            throw new ScriptValueException($"cannot compare {ScriptValues.Describe(left)} with {ScriptValues.Describe(right)}");
        return Op switch { "gt" => c > 0, "ge" => c >= 0, "lt" => c < 0, _ => c <= 0 };
    }
}
