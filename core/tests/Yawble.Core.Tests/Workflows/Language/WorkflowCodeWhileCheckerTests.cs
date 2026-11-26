using Xunit;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Tests.Workflows.Language;

/// <summary>
/// The code and while steps in the checker. Code: the code parsed (a syntax error with its line and column), inputs typed from
/// their expressions, returns a type that exists and typed into into and steps.&lt;id&gt;, a body with no return. While: its
/// condition typed as if's, body and then as forEach's, max from 1 to 10,000.
/// </summary>
public sealed class WorkflowCodeWhileCheckerTests
{
    private static readonly WorkflowSchema Staleness = WorkflowSchema.Parse("Staleness", """
        { "type": "object", "properties": { "stale": { "type": "boolean" }, "age": { "type": "number" } }, "required": ["stale"] }
        """);

    private const string Vars = """
        [{ "name": "n", "type": "int", "initial": 0 }, { "name": "s", "type": "Staleness" }, { "name": "text", "type": "string", "initial": "" }]
        """;

    private static WorkflowCheckResult Check(string steps) =>
        WorkflowChecker.Check(WorkflowSamples.Workflow(steps, Vars), new WorkflowSchemaSet([Staleness]), FakeCatalog.MailAndSms());

    private static void Clean(WorkflowCheckResult result) => Assert.True(result.Ok, string.Join("\n", result.Issues));

    private static WorkflowCheckIssue HasIssue(WorkflowCheckResult result, string code, string text, string step = "a")
    {
        var issue = result.Issues.FirstOrDefault(i => i.Code == code && i.StepId == step && i.Message.Contains(text, StringComparison.Ordinal));
        Assert.True(issue is not null, $"expected {code} on {step} containing \"{text}\"; got:\n" + string.Join("\n", result.Issues));
        return issue;
    }

    private static string Code(string code, string extra = "", string next = "null") =>
        $$"""[{ "id": "a", "type": "code", "code": {{System.Text.Json.JsonSerializer.Serialize(code)}} {{extra}}, "next": {{next}} }]""";

    [Fact]
    public void CodeAndWhileAreStepTypes()
    {
        Assert.Contains("code", WorkflowChecker.StepTypes);
        Assert.Contains("while", WorkflowChecker.StepTypes);
    }

    // ---- code ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACodeStep_WithInputsReturnsAndInto_ChecksClean_AndItsResultIsTypedForLaterSteps()
    {
        var result = Check("""
            [{ "id": "a", "type": "code", "inputs": { "n": "{vars.n}", "when": "{now()}", "fixed": 3 },
               "code": "const age = inputs.n * 2;\nreturn { stale: age > 4, age };", "returns": "Staleness", "into": "s", "next": "b" },
             { "id": "b", "type": "if", "when": { "left": "{vars.s.stale}", "op": "eq", "right": true }, "then": "c", "else": null },
             { "id": "c", "type": "log", "text": "age {steps.a.age}", "next": null }]
            """);
        Clean(result);
        Assert.Contains(new WorkflowSchemaReference("Staleness", "", "a"), result.SchemaReferences);
        Assert.Contains(new WorkflowSchemaReference("Staleness", "age", "c"), result.SchemaReferences);
    }

    [Fact]
    public void ACodeBody_WithNoReturn_Parses()
    {
        Clean(Check(Code("log('nothing to give back');")));
    }

    [Fact]
    public void ASyntaxError_IsAProblemOnTheStep_WithItsLineAndColumn()
    {
        var first = HasIssue(Check(Code("const x = ;\nreturn x;")), WorkflowCheckCodes.BadExpression, "the code does not parse");
        Assert.Contains("(line 1, column 11)", first.Message);

        var third = HasIssue(Check(Code("const a = 1;\nconst b = 2;\nreturn a +* b;")), WorkflowCheckCodes.BadExpression, "the code does not parse");
        Assert.Contains("(line 3, column 11)", third.Message);

        // Unclosed: the error is at the end of the code, not in the wrapper around it.
        var open = HasIssue(Check(Code("if (inputs.x) {\n  return 1;")), WorkflowCheckCodes.BadExpression, "the code does not parse");
        Assert.Matches(@"\(line [23], column \d+\)", open.Message);
    }

    [Fact]
    public void StrictModeIsOn_AtCheckTime()
    {
        // with is a syntax error only in strict mode.
        HasIssue(Check(Code("with (inputs) { return 1; }")), WorkflowCheckCodes.BadExpression, "the code does not parse");
    }

    [Theory]
    [InlineData("import fs from 'fs';\nreturn 1;")]
    [InlineData("export const x = 1;")]
    public void ModuleSyntax_DoesNotParse(string code)
    {
        HasIssue(Check(Code(code)), WorkflowCheckCodes.BadExpression, "the code does not parse");
    }

    [Fact]
    public void AMissingOrEmptyCode_IsAMissingField()
    {
        HasIssue(Check("""[{ "id": "a", "type": "code", "next": null }]"""), WorkflowCheckCodes.MissingField, "code needs 'code'");
        HasIssue(Check(Code("   ")), WorkflowCheckCodes.MissingField, "code needs 'code'");
    }

    [Fact]
    public void Inputs_AreTypedFromTheirExpressions()
    {
        HasIssue(Check(Code("return 1;", """, "inputs": { "x": "{vars.nope}" }""")), WorkflowCheckCodes.UnknownReference, "variable 'nope' is not declared");
        HasIssue(Check(Code("return 1;", """, "inputs": { "x": "{vars.s.colour}" }""")), WorkflowCheckCodes.UnknownReference, "colour");
        HasIssue(Check(Code("return 1;", """, "inputs": { "x": "{vars.n + }" }""")), WorkflowCheckCodes.BadExpression, "");
        HasIssue(Check(Code("return 1;", """, "inputs": ["x"]""")), WorkflowCheckCodes.InvalidDocument, "inputs is an object");
        HasIssue(Check(Code("return 1;", """, "inputs": { "a b": 1 }""")), WorkflowCheckCodes.InvalidDocument, "cannot name an input");
    }

    [Fact]
    public void Returns_FlowsIntoInto_AndAMismatchIsAProblem()
    {
        Clean(Check(Code("return 2;", """, "returns": "int", "into": "n" """)));
        HasIssue(Check(Code("return 2;", """, "returns": "string", "into": "n" """)), WorkflowCheckCodes.TypeMismatch, "the result is string; variable 'n' is int");
        HasIssue(Check(Code("return 2;", """, "returns": "Staleness", "into": "text" """)), WorkflowCheckCodes.TypeMismatch, "variable 'text' is string");
        // No returns: any, which goes into any variable.
        Clean(Check(Code("return 'x';", """, "into": "text" """)));
    }

    [Fact]
    public void AnUnknownReturnsTypeOrSchema_IsAProblem()
    {
        HasIssue(Check(Code("return 1;", """, "returns": "Nope" """)), WorkflowCheckCodes.UnknownType, "returns:");
        HasIssue(Check(Code("return 1;", """, "returns": "list<Nope>" """)), WorkflowCheckCodes.UnknownType, "returns:");
    }

    [Fact]
    public void StepsResult_IsTheReturnsType()
    {
        var result = Check("""
            [{ "id": "a", "type": "code", "code": "return { stale: true };", "returns": "Staleness", "next": "b" },
             { "id": "b", "type": "log", "text": "{steps.a.colour}", "next": null }]
            """);
        HasIssue(result, WorkflowCheckCodes.UnknownReference, "colour", step: "b");
    }

    // ---- while --------------------------------------------------------------------------------------------------------

    private static string While(string fields, string body = """{ "id": "b", "type": "set", "var": "n", "value": "{vars.n + 1}", "next": null }""") => $$"""
        [{ "id": "a", "type": "while", {{fields}} },
         {{body}},
         { "id": "c", "type": "log", "text": "done at {vars.n}", "next": null }]
        """;

    private const string When = """ "when": { "left": "{vars.n}", "op": "lt", "right": 5 } """;

    [Fact]
    public void AWhile_WithConditionBodyThenAndMax_ChecksClean()
    {
        Clean(Check(While($"""{When}, "body": "b", "then": "c", "max": 50""")));
        Clean(Check(While($"""{When}, "body": "b", "then": "c" """)));
        Clean(Check(While(""" "when": { "all": [{ "left": "{vars.n}", "op": "lt", "right": 5 }, { "not": { "left": "{vars.text}", "op": "empty" } }] }, "body": "b", "then": "c" """)));
        // then is optional: the run (or the enclosing body) ends after the loop.
        Clean(WorkflowChecker.Check(WorkflowSamples.Workflow($$"""
            [{ "id": "a", "type": "while", {{When}}, "body": "b" },
             { "id": "b", "type": "set", "var": "n", "value": "{vars.n + 1}", "next": null }]
            """, Vars), new WorkflowSchemaSet([Staleness]), FakeCatalog.MailAndSms()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(10_001)]
    public void AMaxBelowOneOrAbove10000_IsABadValue(int max)
    {
        HasIssue(Check(While($"""{When}, "body": "b", "then": "c", "max": {max}""")), WorkflowCheckCodes.BadValue, "it is from 1 to 10,000");
    }

    [Fact]
    public void AMaxOf10000_IsAllowed_AndAMaxThatIsNotAWholeNumber_IsAProblem()
    {
        Clean(Check(While($"""{When}, "body": "b", "then": "c", "max": 10000""")));
        HasIssue(Check(While($"""{When}, "body": "b", "then": "c", "max": "50" """)), WorkflowCheckCodes.InvalidDocument, "whole number");
        HasIssue(Check(While($"""{When}, "body": "b", "then": "c", "max": 2.5""")), WorkflowCheckCodes.InvalidDocument, "whole number");
    }

    [Fact]
    public void AWhile_NeedsAWhenAndABody()
    {
        HasIssue(Check(While(""" "body": "b", "then": "c" """)), WorkflowCheckCodes.MissingField, "while needs a when");
        var noBody = Check(While($"""{When}, "then": "c" """));
        HasIssue(noBody, WorkflowCheckCodes.MissingField, "a while needs a body");
    }

    [Fact]
    public void TheCondition_IsTypedAsIfs()
    {
        HasIssue(Check(While(""" "when": { "left": "{vars.nope}", "op": "exists" }, "body": "b", "then": "c" """)), WorkflowCheckCodes.UnknownReference, "variable 'nope'");
        HasIssue(Check(While(""" "when": { "left": "{vars.s}", "op": "gt", "right": 3 }, "body": "b", "then": "c" """)), WorkflowCheckCodes.TypeMismatch, "'gt' compares");
        HasIssue(Check(While(""" "when": { "left": "{vars.n}", "op": "bogus", "right": 3 }, "body": "b", "then": "c" """)), WorkflowCheckCodes.BadExpression, "");
    }

    [Fact]
    public void ABodyThatGoesBackToItsWhile_IsALoopStructureProblem()
    {
        var result = Check(While($"""{When}, "body": "b", "then": "c" """,
            body: """{ "id": "b", "type": "set", "var": "n", "value": "{vars.n + 1}", "next": "a" }"""));
        HasIssue(result, WorkflowCheckCodes.LoopStructure, "goes back to the loop 'a'", step: "b");
    }

    [Fact]
    public void AStepReachedBothInsideAndOutsideTheBody_IsALoopStructureProblem()
    {
        var result = Check(While($"""{When}, "body": "b", "then": "c" """,
            body: """{ "id": "b", "type": "set", "var": "n", "value": "{vars.n + 1}", "next": "c" }"""));
        Assert.Contains(result.Issues, i => i.Code == WorkflowCheckCodes.LoopStructure && i.Message.Contains("the loop 'a'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownBodyOrThen_IsAnUnknownStep_AndStepsOnlyInsideAreReachable()
    {
        HasIssue(Check(While($"""{When}, "body": "zz", "then": "c" """)), WorkflowCheckCodes.UnknownStep, "body names step 'zz'");
        HasIssue(Check(While($"""{When}, "body": "b", "then": "zz" """)), WorkflowCheckCodes.UnknownStep, "then names step 'zz'");
    }

    [Fact]
    public void AWhileInsideAForEach_AndAForEachInsideAWhile_SeeTheirItems()
    {
        var doc = WorkflowSamples.Workflow("""
            [{ "id": "a", "type": "while", "when": { "left": "{vars.n}", "op": "lt", "right": 2 }, "body": "f", "then": null },
             { "id": "f", "type": "forEach", "items": [1, 2], "as": "item", "body": "g", "then": "h" },
             { "id": "g", "type": "set", "var": "text", "value": "{item}", "next": null },
             { "id": "h", "type": "set", "var": "n", "value": "{vars.n + 1}", "next": null }]
            """, Vars);
        Clean(WorkflowChecker.Check(doc, new WorkflowSchemaSet([Staleness]), FakeCatalog.MailAndSms()));
    }

    [Fact]
    public void ACodeStepInsideAWhile_ChecksClean()
    {
        Clean(Check(While($"""{When}, "body": "b", "then": "c" """,
            body: """{ "id": "b", "type": "code", "inputs": { "n": "{vars.n}" }, "code": "return inputs.n + 1;", "returns": "int", "into": "n", "next": null }""")));
    }
}
