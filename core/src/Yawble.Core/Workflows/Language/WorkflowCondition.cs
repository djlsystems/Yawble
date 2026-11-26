using System.Text.Json.Nodes;

namespace Yawble.Core.Workflows.Language;

/// <summary>
/// A condition: <c>{ "left": ..., "op": ..., "right": ... }</c>, or <c>{ "all": [...] }</c>, <c>{ "any": [...] }</c>,
/// <c>{ "not": ... }</c>, or a template whose value is read as true or false. Left and right are values (templates in
/// their strings).
/// </summary>
public abstract record WorkflowCondition
{
    public static readonly IReadOnlyList<string> Operators =
        ["eq", "ne", "gt", "ge", "lt", "le", "contains", "startsWith", "matches", "exists", "empty"];

    /// <summary>Operators that take no right-hand side.</summary>
    public static bool IsUnary(string op) => op is "exists" or "empty";

    public static WorkflowCondition Parse(JsonNode? node)
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
                if (!Operators.Contains(op)) throw new WorkflowExpressionException($"'{op}' is not a condition operator ({string.Join(", ", Operators)})");
                if (!o.ContainsKey("left")) throw new WorkflowExpressionException($"the '{op}' condition has no left");
                if (!IsUnary(op) && !o.ContainsKey("right")) throw new WorkflowExpressionException($"the '{op}' condition has no right");
                return new CompareCondition(o["left"], op, o["right"]);
            }
            default:
                throw new WorkflowExpressionException("a condition is {left, op, right}, {all: [...]}, {any: [...]}, {not: ...} or a template");
        }
    }

    public abstract bool Evaluate(WorkflowEvaluator evaluator);
}

public sealed record TemplateCondition(string Template) : WorkflowCondition
{
    public override bool Evaluate(WorkflowEvaluator evaluator) => WorkflowValues.Truthy(evaluator.EvaluateTemplate(Template));
}

public sealed record AllCondition(IReadOnlyList<WorkflowCondition> Items) : WorkflowCondition
{
    public override bool Evaluate(WorkflowEvaluator evaluator) => Items.All(i => i.Evaluate(evaluator));
}

public sealed record AnyCondition(IReadOnlyList<WorkflowCondition> Items) : WorkflowCondition
{
    public override bool Evaluate(WorkflowEvaluator evaluator) => Items.Any(i => i.Evaluate(evaluator));
}

public sealed record NotCondition(WorkflowCondition Inner) : WorkflowCondition
{
    public override bool Evaluate(WorkflowEvaluator evaluator) => !Inner.Evaluate(evaluator);
}

public sealed record CompareCondition(JsonNode? Left, string Op, JsonNode? Right) : WorkflowCondition
{
    public override bool Evaluate(WorkflowEvaluator evaluator)
    {
        var left = WorkflowValues.FromNode(evaluator.EvaluateValue(Left));
        if (Op == "exists") return left is not null;
        if (Op == "empty") return WorkflowValues.IsEmpty(left);
        var right = WorkflowValues.FromNode(evaluator.EvaluateValue(Right));
        switch (Op)
        {
            case "eq": return WorkflowValues.AreEqual(left, right);
            case "ne": return !WorkflowValues.AreEqual(left, right);
            case "contains":
                return left switch
                {
                    null => false,
                    JsonArray a => a.Any(i => WorkflowValues.AreEqual(WorkflowValues.FromNode(i), right)),
                    _ => WorkflowValues.Format(left).Contains(WorkflowValues.Format(right), StringComparison.Ordinal),
                };
            case "startsWith":
                return left is not null && WorkflowValues.Format(left).StartsWith(WorkflowValues.Format(right), StringComparison.Ordinal);
            case "matches":
                return left is not null && WorkflowEvaluator.Matches(WorkflowValues.Format(left), WorkflowValues.Format(right));
        }
        // gt ge lt le: a null on either side is false (nothing to order).
        if (left is null || right is null) return false;
        if (!WorkflowValues.TryCompare(left, right, out var c))
            throw new WorkflowValueException($"cannot compare {WorkflowValues.Describe(left)} with {WorkflowValues.Describe(right)}");
        return Op switch { "gt" => c > 0, "ge" => c >= 0, "lt" => c < 0, _ => c <= 0 };
    }
}
