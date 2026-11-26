using Xunit;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Tests.Workflows.Language;

public sealed class WorkflowCheckerTests
{
    private static readonly WorkflowSchema Triage = WorkflowSchema.Parse("Triage", """
        { "type": "object", "properties": {
            "emailId": { "type": "string" },
            "category": { "type": "string", "enum": ["order", "proof", "inquiry", "spam", "other"] },
            "escalate": { "type": "boolean" },
            "meta": { "type": "object", "properties": { "score": { "type": "number" } } } } }
        """);

    private static readonly WorkflowSchemaSet Schemas = new([Triage]);

    private static WorkflowCheckResult Check(WorkflowDocument doc, FakeCatalog? catalog = null) =>
        WorkflowChecker.Check(doc, Schemas, catalog ?? FakeCatalog.MailAndSms());

    private static WorkflowCheckResult CheckSteps(string steps, string variables = "[]", string settings = "[]", string requires = "[]", string extra = "") =>
        Check(WorkflowSamples.Workflow(steps, variables, settings, requires, extra: extra));

    private static void HasIssue(WorkflowCheckResult result, string code, string? stepId, string text)
    {
        Assert.False(result.Ok);
        Assert.True(result.Issues.Any(i => i.Code == code && i.StepId == stepId && i.Message.Contains(text, StringComparison.Ordinal)),
            $"expected {code} on {stepId ?? "(workflow)"} containing \"{text}\"; got:\n" + string.Join("\n", result.Issues));
    }

    [Fact]
    public void TheExampleChecksClean_AgainstAMailAndSmsCatalog()
    {
        var result = WorkflowChecker.Check(WorkflowSamples.InboxSmsAlert, WorkflowSchemaSet.Empty, FakeCatalog.MailAndSms());
        Assert.True(result.Ok, string.Join("\n", result.Issues));
    }

    [Fact]
    public void TheExampleListsTheSchemaFieldsEachStepUses()
    {
        var result = WorkflowChecker.Check(WorkflowSamples.InboxSmsAlert, WorkflowSchemaSet.Empty, FakeCatalog.MailAndSms());
        var refs = result.SchemaReferences;
        Assert.Contains(new WorkflowSchemaReference("EmailMessage", "", null), refs);
        Assert.Contains(new WorkflowSchemaReference("TriggerFired", "", null), refs);
        Assert.Contains(new WorkflowSchemaReference("EmailMessage", "subject", "text"), refs);
        Assert.Contains(new WorkflowSchemaReference("EmailMessage", "from", "text"), refs);
        Assert.Contains(new WorkflowSchemaReference("EmailAddress", "address", "text"), refs);
        Assert.Contains(new WorkflowSchemaReference("EmailMessage", "receivedAt", "mark"), refs);
        Assert.Equal(refs.Count, refs.Distinct().Count());
    }

    [Fact]
    public void WithoutTheActionsInTheCatalog_TheExampleIsADraft()
    {
        var result = WorkflowChecker.Check(WorkflowSamples.InboxSmsAlert, WorkflowSchemaSet.Empty, new FakeCatalog().Read("email", "list"));
        HasIssue(result, WorkflowCheckCodes.UnknownAction, null, "kind 'api' has no action 'POST /2010-04-01/Accounts/{AccountSid}/Messages.json'");
        Assert.Single(result.Issues);
    }

    [Fact]
    public void ADocumentThatDoesNotParse_IsOneIssue()
    {
        var result = WorkflowChecker.Check("{ nope", WorkflowSchemaSet.Empty, new FakeCatalog());
        HasIssue(result, WorkflowCheckCodes.InvalidDocument, null, "not valid JSON");
    }

    // ---- the graph ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ASuccessorThatDoesNotExist()
    {
        HasIssue(CheckSteps("""[ { "id": "a", "type": "log", "text": "x", "next": "b" } ]"""), WorkflowCheckCodes.UnknownStep, "a", "next names step 'b'");
        HasIssue(CheckSteps("""[ { "id": "z", "type": "end" } ]"""), WorkflowCheckCodes.UnknownStep, null, "start names step 'a'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "switch", "on": "x", "cases": { "x": "gone" }, "default": null } ]"""),
            WorkflowCheckCodes.UnknownStep, "a", "cases.x names step 'gone'");
    }

    [Fact]
    public void EveryStepMustBeReachable()
    {
        var result = CheckSteps("""
            [ { "id": "a", "type": "if", "when": "{true}", "then": "b", "else": null },
              { "id": "b", "type": "end" },
              { "id": "orphan", "type": "log", "text": "never", "next": "b" } ]
            """);
        HasIssue(result, WorkflowCheckCodes.Unreachable, "orphan", "cannot be reached");
        Assert.Single(result.Issues);
    }

    [Fact]
    public void ALoopBodyMustEndWithNull_NotJumpOutOrBack()
    {
        HasIssue(CheckSteps("""
            [ { "id": "a", "type": "forEach", "items": "{settings.list}", "as": "x", "body": "b", "then": "c" },
              { "id": "b", "type": "log", "text": "{x}", "next": "c" },
              { "id": "c", "type": "end" } ]
            """, settings: """[ { "name": "list", "type": "list<string>" } ]"""), WorkflowCheckCodes.LoopStructure, "c", "both inside and outside");

        HasIssue(CheckSteps("""
            [ { "id": "a", "type": "forEach", "items": "{settings.list}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "log", "text": "{x}", "next": "a" } ]
            """, settings: """[ { "name": "list", "type": "list<string>" } ]"""), WorkflowCheckCodes.LoopStructure, "b", "goes back to the loop 'a'");
    }

    [Fact]
    public void AnUnknownStepType_AndDuplicates()
    {
        HasIssue(CheckSteps("""[ { "id": "a", "type": "shell", "next": null } ]"""), WorkflowCheckCodes.UnknownStepType, "a", "'shell' is not a step type");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" }, { "id": "a", "type": "end" } ]"""), WorkflowCheckCodes.Duplicate, null, "step id 'a' is declared 2 times");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", variables: """[ { "name": "v", "type": "int" }, { "name": "v", "type": "int" } ]"""),
            WorkflowCheckCodes.Duplicate, null, "variable 'v'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", settings: """[ { "name": "s", "type": "int" }, { "name": "s", "type": "int" } ]"""),
            WorkflowCheckCodes.Duplicate, null, "setting 's'");
    }

    [Fact]
    public void AnUnknownTriggerKind()
    {
        var doc = WorkflowDocument.Parse("""
            { "format": "yawble-workflow/1", "id": "t", "trigger": { "accepts": ["every", "hourly"] }, "start": "a", "steps": [ { "id": "a", "type": "end" } ] }
            """);
        HasIssue(Check(doc), WorkflowCheckCodes.UnknownTrigger, null, "'hourly'");
    }

    // ---- declarations -------------------------------------------------------------------------------------------------

    [Fact]
    public void TypesMustExist_AndLiteralsMustFitThem()
    {
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", variables: """[ { "name": "v", "type": "Nope" } ]"""),
            WorkflowCheckCodes.UnknownType, null, "variable 'v': no schema named 'Nope'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", settings: """[ { "name": "s", "type": "list<Gone>" } ]"""),
            WorkflowCheckCodes.UnknownType, null, "setting 's'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", variables: """[ { "name": "v", "type": "int", "initial": "ten" } ]"""),
            WorkflowCheckCodes.TypeMismatch, null, "the initial value of variable 'v'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", variables: """[ { "name": "v", "type": "datetime", "initial": "tomorrow" } ]"""),
            WorkflowCheckCodes.TypeMismatch, null, "is not a datetime");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", settings: """[ { "name": "s", "type": "list<int>", "default": [1, "x"] } ]"""),
            WorkflowCheckCodes.TypeMismatch, null, "the default of setting 's'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", variables: """[ { "name": "v", "type": "int", "scope": "forever" } ]"""),
            WorkflowCheckCodes.InvalidDocument, null, "scope 'forever'");
        Assert.True(CheckSteps("""[ { "id": "a", "type": "end" } ]""",
            variables: """[ { "name": "v", "type": "enum(a, b)", "initial": "a" }, { "name": "t", "type": "Triage" } ]""").Ok);
    }

    [Fact]
    public void RequiresActionsMustBeInTheCatalog()
    {
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", requires: """[ { "name": "mail", "kind": "email", "actions": ["list", "delete"] } ]"""),
            WorkflowCheckCodes.UnknownAction, null, "kind 'email' has no action 'delete'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end" } ]""", requires: """[ { "name": "mail", "kind": "fax", "actions": ["list"] } ]"""),
            WorkflowCheckCodes.UnknownAction, null, "kind 'fax' has no action 'list'");
    }

    // ---- steps --------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnIntegrationStepUsesARequirementAndOneOfItsActions()
    {
        const string requires = """[ { "name": "mail", "kind": "email", "actions": ["list"] } ]""";
        HasIssue(CheckSteps("""[ { "id": "a", "type": "integration", "use": "post", "action": "list", "next": null } ]""", requires: requires),
            WorkflowCheckCodes.UnknownRequirement, "a", "'post' is not in requires");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "integration", "use": "mail", "action": "get", "next": null } ]""", requires: requires),
            WorkflowCheckCodes.UnknownAction, "a", "action 'get' is not listed in requires 'mail'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "integration", "use": "mail", "action": "list", "into": "nope", "next": null } ]""", requires: requires),
            WorkflowCheckCodes.UnknownReference, "a", "variable 'nope' is not declared");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "integration", "action": "list", "next": null } ]""", requires: requires),
            WorkflowCheckCodes.MissingField, "a", "needs 'use'");
    }

    [Theory]
    [InlineData("""{ "id": "a", "type": "set", "value": 1, "next": null }""", "needs 'var'")]
    [InlineData("""{ "id": "a", "type": "set", "var": "n", "next": null }""", "set needs a value")]
    [InlineData("""{ "id": "a", "type": "if", "then": null, "else": null }""", "if needs a when")]
    [InlineData("""{ "id": "a", "type": "switch", "cases": {}, "default": null }""", "switch needs an on")]
    [InlineData("""{ "id": "a", "type": "switch", "on": "x", "default": null }""", "switch needs cases")]
    [InlineData("""{ "id": "a", "type": "forEach", "as": "x", "body": null, "then": null }""", "forEach needs items")]
    [InlineData("""{ "id": "a", "type": "forEach", "items": "{settings.l}", "body": null, "then": null }""", "a forEach needs a body")]
    [InlineData("""{ "id": "a", "type": "file.writeAllText", "path": "x.txt", "next": null }""", "needs 'text'")]
    [InlineData("""{ "id": "a", "type": "file.readLines", "next": null }""", "needs 'path'")]
    [InlineData("""{ "id": "a", "type": "log", "next": null }""", "needs 'text'")]
    public void AStepLackingWhatItNeeds(string step, string text) =>
        HasIssue(CheckSteps($"[ {step} ]", variables: """[ { "name": "n", "type": "int" } ]""", settings: """[ { "name": "l", "type": "list<int>" } ]"""),
            WorkflowCheckCodes.MissingField, "a", text);

    [Theory]
    [InlineData("{vars.nope}", "variable 'nope' is not declared")]
    [InlineData("{settings.nope}", "setting 'nope' is not declared")]
    [InlineData("{steps.nope}", "step 'nope' does not exist")]
    [InlineData("{steps.a}", "step 'a' (log) has no result")]
    [InlineData("{x.y}", "'x' is not input, settings, vars, steps or the item of a loop")]
    [InlineData("{vars}", "'vars' needs a name")]
    [InlineData("{input.nope}", "TriggerFired has no field 'nope'")]
    [InlineData("{vars.t.nope}", "Triage has no field 'nope'")]
    [InlineData("{vars.t.meta.nope}", "Triage has no field 'meta.nope'")]
    [InlineData("{vars.messages[0].from.nope}", "EmailAddress has no field 'nope'")]
    [InlineData("{max(vars.messages, 'nope')}", "EmailMessage has no field 'nope'")]
    public void EveryReferenceMustResolve(string template, string text) =>
        HasIssue(CheckSteps($$"""[ { "id": "a", "type": "log", "text": "{{template}}", "next": null } ]""",
            variables: """[ { "name": "t", "type": "Triage" }, { "name": "messages", "type": "list<EmailMessage>" } ]"""),
            WorkflowCheckCodes.UnknownReference, "a", text);

    [Fact]
    public void ALoopItemIsKnownOnlyInsideItsLoop_WithTheListsItemType()
    {
        const string vars = """[ { "name": "messages", "type": "list<EmailMessage>" } ]""";
        Assert.True(CheckSteps("""
            [ { "id": "a", "type": "forEach", "items": "{vars.messages}", "as": "m", "body": "b", "then": "c" },
              { "id": "b", "type": "log", "text": "{m.subject} {m.to[0].address}", "next": null },
              { "id": "c", "type": "end" } ]
            """, variables: vars).Ok);
        HasIssue(CheckSteps("""
            [ { "id": "a", "type": "forEach", "items": "{vars.messages}", "as": "m", "body": "b", "then": "c" },
              { "id": "b", "type": "log", "text": "{m.subjec}", "next": null },
              { "id": "c", "type": "log", "text": "{m.subject}", "next": null } ]
            """, variables: vars), WorkflowCheckCodes.UnknownReference, "c", "'m' is not input");
        HasIssue(CheckSteps("""
            [ { "id": "a", "type": "forEach", "items": "{vars.messages}", "as": "m", "body": "b", "then": null },
              { "id": "b", "type": "log", "text": "{m.subjec}", "next": null } ]
            """, variables: vars), WorkflowCheckCodes.UnknownReference, "b", "EmailMessage has no field 'subjec'");
    }

    [Fact]
    public void ALoopNeedsAListAndAFreeName()
    {
        HasIssue(CheckSteps("""
            [ { "id": "a", "type": "forEach", "items": "{vars.n}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "log", "text": "{x}", "next": null } ]
            """, variables: """[ { "name": "n", "type": "int" } ]"""), WorkflowCheckCodes.TypeMismatch, "a", "items is int, not a list");
        HasIssue(CheckSteps("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "vars", "body": "b", "then": null },
              { "id": "b", "type": "log", "text": "x", "next": null } ]
            """, variables: """[ { "name": "l", "type": "list<int>" } ]"""), WorkflowCheckCodes.InvalidDocument, "a", "'vars' cannot name a loop's item");
        HasIssue(CheckSteps("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "c", "then": null },
              { "id": "c", "type": "log", "text": "{x}", "next": null } ]
            """, variables: """[ { "name": "l", "type": "list<int>" } ]"""), WorkflowCheckCodes.Duplicate, "b", "already the item of an enclosing loop");
    }

    [Theory]
    [InlineData("int", "\"text\"", "the value is string, the variable is int")]
    [InlineData("int", "\"{vars.s}\"", "the value is string, the variable is int")]
    [InlineData("int", "\"{vars.f}\"", "the value is float, the variable is int")]
    [InlineData("int", "\"{vars.l}\"", "the value is list<int>, the variable is int")]
    [InlineData("list<int>", "\"{vars.n}\"", "the value is int, the variable is list<int>")]
    [InlineData("bool", "\"{count(vars.l)}\"", "the value is int, the variable is bool")]
    [InlineData("datetime", "\"{vars.n}\"", "the value is int, the variable is datetime")]
    [InlineData("datetime", "\"soon\"", "is not a datetime")]
    [InlineData("EmailMessage", "\"{vars.t}\"", "the value is Triage, the variable is EmailMessage")]
    [InlineData("enum(a, b)", "\"c\"", "is not a enum(a, b)")]
    public void ASetMustMatchTheVariablesType(string type, string value, string text) =>
        HasIssue(CheckSteps($$"""[ { "id": "a", "type": "set", "var": "v", "value": {{value}}, "next": null } ]""",
            variables: $$"""
                [ { "name": "v", "type": "{{type}}" }, { "name": "s", "type": "string" }, { "name": "f", "type": "float" },
                  { "name": "n", "type": "int" }, { "name": "l", "type": "list<int>" }, { "name": "t", "type": "Triage" } ]
                """), WorkflowCheckCodes.TypeMismatch, "a", text);

    [Theory]
    [InlineData("int", "\"{vars.n + 1}\"")]
    [InlineData("float", "\"{vars.n / 2}\"")]
    [InlineData("string", "\"{vars.n}\"")]
    [InlineData("string", "\"n is {vars.n}\"")]
    [InlineData("datetime", "\"{now()}\"")]
    [InlineData("datetime", "\"{addHours(now(), 2)}\"")]
    [InlineData("datetime", "\"2026-11-23T10:00:00Z\"")]
    [InlineData("duration", "\"{now() - addDays(now(), -1)}\"")]
    [InlineData("date", "\"{today()}\"")]
    [InlineData("list<int>", "[1, 2]")]
    [InlineData("list<int>", "\"{vars.l}\"")]
    [InlineData("bool", "\"{vars.n > 2}\"")]
    [InlineData("bool", "\"{contains(vars.l, 2)}\"")]
    [InlineData("int", "\"{first(vars.l)}\"")]
    [InlineData("enum(order, proof, inquiry, spam, other)", "\"{vars.t.category}\"")]
    [InlineData("float", "\"{vars.t.meta.score}\"")]
    [InlineData("string", "\"{coalesce(vars.s, 'x')}\"")]
    public void ASetOfAMatchingValueChecksClean(string type, string value)
    {
        var result = CheckSteps($$"""[ { "id": "a", "type": "set", "var": "v", "value": {{value}}, "next": null } ]""",
            variables: $$"""
                [ { "name": "v", "type": "{{type}}" }, { "name": "s", "type": "string" }, { "name": "n", "type": "int" },
                  { "name": "l", "type": "list<int>" }, { "name": "t", "type": "Triage" } ]
                """);
        Assert.True(result.Ok, string.Join("\n", result.Issues));
    }

    [Theory]
    [InlineData("{vars.s * 2}", "cannot apply '*' to string and int")]
    [InlineData("{vars.l - 1}", "cannot apply '-' to list<int> and int")]
    [InlineData("{vars.l > 1}", "compares list<int> with int")]
    [InlineData("{-vars.s}", "cannot negate string")]
    [InlineData("{count(vars.n)}", "count(): argument 1 is int, not a list")]
    [InlineData("{addHours(vars.n, 1)}", "addHours(): argument 1 is int, not a datetime")]
    [InlineData("{addHours(now(), vars.s)}", "addHours(): argument 2 is string, not a number")]
    [InlineData("{join(vars.l, 1)}", "join(): argument 2 is int, not text")]
    [InlineData("{first(vars.n)}", "first(): argument 1 is int, not a list")]
    [InlineData("{max(vars.l, vars.s)}", "the field is a quoted name")]
    [InlineData("{max(vars.l, 'x')}", "the items are int, not objects with fields")]
    [InlineData("{vars.n.x}", "int has no field 'x'")]
    [InlineData("{vars.n[0]}", "int is not a list")]
    public void ExpressionsMustBeWellTyped(string template, string text) =>
        HasIssue(CheckSteps($$"""[ { "id": "a", "type": "log", "text": "{{template}}", "next": null } ]""",
            variables: """[ { "name": "s", "type": "string" }, { "name": "n", "type": "int" }, { "name": "l", "type": "list<int>" } ]"""),
            WorkflowCheckCodes.TypeMismatch, "a", text);

    [Theory]
    [InlineData("{vars.n")]
    [InlineData("{nope()}")]
    [InlineData("{matches(vars.s, '(')}")]
    public void ABadExpressionIsReported(string template) =>
        HasIssue(CheckSteps($$"""[ { "id": "a", "type": "log", "text": "{{template}}", "next": null } ]""",
            variables: """[ { "name": "s", "type": "string" }, { "name": "n", "type": "int" } ]"""),
            WorkflowCheckCodes.BadExpression, "a", "");

    [Fact]
    public void ConditionsAreChecked()
    {
        const string vars = """[ { "name": "s", "type": "string" }, { "name": "n", "type": "int" }, { "name": "l", "type": "list<int>" } ]""";
        HasIssue(CheckSteps("""[ { "id": "a", "type": "if", "when": { "left": "{vars.l}", "op": "gt", "right": 1 }, "then": null, "else": null } ]""", vars),
            WorkflowCheckCodes.TypeMismatch, "a", "'gt' compares list<int> with int");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "if", "when": { "left": "{vars.n}", "op": "contains", "right": 1 }, "then": null, "else": null } ]""", vars),
            WorkflowCheckCodes.TypeMismatch, "a", "'contains' needs text or a list");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "if", "when": { "left": "{vars.s}", "op": "matches", "right": "(" }, "then": null, "else": null } ]""", vars),
            WorkflowCheckCodes.BadExpression, "a", "is not a regular expression");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "if", "when": { "left": "{vars.s}", "op": "like", "right": 1 }, "then": null, "else": null } ]""", vars),
            WorkflowCheckCodes.BadExpression, "a", "'like' is not a condition operator");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "if", "when": { "all": [ { "not": { "left": "{vars.gone}", "op": "exists" } } ] }, "then": null, "else": null } ]""", vars),
            WorkflowCheckCodes.UnknownReference, "a", "variable 'gone'");
        Assert.True(CheckSteps("""
            [ { "id": "a", "type": "if", "when": { "any": [ { "left": "{vars.n}", "op": "ge", "right": 2.5 }, { "left": "{vars.l}", "op": "contains", "right": 3 },
                { "left": "{vars.s}", "op": "empty" }, { "left": "{now()}", "op": "gt", "right": "2026-01-01T00:00:00Z" } ] }, "then": null, "else": null } ]
            """, vars).Ok);
    }

    [Fact]
    public void ASwitchOnAnEnumMayOnlyNameItsValues()
    {
        var result = CheckSteps("""
            [ { "id": "a", "type": "switch", "on": "{vars.t.category}", "cases": { "order": "b", "refund": "b" }, "default": null },
              { "id": "b", "type": "end" } ]
            """, variables: """[ { "name": "t", "type": "Triage" } ]""");
        HasIssue(result, WorkflowCheckCodes.TypeMismatch, "a", "case 'refund' is not a value of");
        Assert.Contains(new WorkflowSchemaReference("Triage", "category", "a"), result.SchemaReferences);
    }

    [Fact]
    public void FileStepResultsHaveTheirTypes()
    {
        Assert.True(CheckSteps("""
            [ { "id": "a", "type": "file.readLines", "path": "in.txt", "into": "lines", "next": "b" },
              { "id": "b", "type": "file.exists", "path": "in.txt", "into": "there", "next": "c" },
              { "id": "c", "type": "file.readAllText", "path": "in.txt", "into": "text", "next": "d" },
              { "id": "d", "type": "log", "text": "{count(steps.a)} {steps.b} {lower(steps.c)}", "next": null } ]
            """, variables: """[ { "name": "lines", "type": "list<string>" }, { "name": "there", "type": "bool" }, { "name": "text", "type": "string" } ]""").Ok);
        HasIssue(CheckSteps("""[ { "id": "a", "type": "file.readLines", "path": "in.txt", "into": "n", "next": null } ]""",
            variables: """[ { "name": "n", "type": "int" } ]"""), WorkflowCheckCodes.TypeMismatch, "a", "the result is list<string>; variable 'n' is int");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "file.writeAllText", "path": "x", "text": "{vars.l}", "next": null } ]""",
            variables: """[ { "name": "l", "type": "list<int>" } ]"""), WorkflowCheckCodes.TypeMismatch, "a", "'text' is list<int>, not text");
    }

    [Fact]
    public void EndOutputsMustBeDeclared_WhenTheWorkflowDeclaresOutputs()
    {
        const string extra = """ "outputs": [ { "name": "count", "type": "int" } ], """;
        Assert.True(CheckSteps("""[ { "id": "a", "type": "end", "outputs": { "count": 3 } } ]""", extra: extra).Ok);
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end", "outputs": { "other": 3 } } ]""", extra: extra), WorkflowCheckCodes.UnknownReference, "a", "output 'other'");
        HasIssue(CheckSteps("""[ { "id": "a", "type": "end", "outputs": { "count": "many" } } ]""", extra: extra), WorkflowCheckCodes.TypeMismatch, "a", "output 'count'");
        Assert.True(CheckSteps("""[ { "id": "a", "type": "end", "outputs": { "anything": [1] } } ]""").Ok);
    }

    [Fact]
    public void TheInputFollowsTheTriggers_OrTheDeclaredType()
    {
        static WorkflowDocument Doc(string head) => WorkflowDocument.Parse($$"""
            { "format": "yawble-workflow/1", "id": "t", {{head}}, "start": "a",
              "steps": [ { "id": "a", "type": "log", "text": "{count(input.added)}", "next": null } ] }
            """);
        Assert.True(Check(Doc(""" "trigger": { "accepts": ["folder"] } """)).Ok);
        HasIssue(Check(Doc(""" "trigger": { "accepts": ["every"] } """)), WorkflowCheckCodes.UnknownReference, "a", "TriggerFired has no field 'added'");
        Assert.True(Check(Doc(""" "trigger": { "accepts": ["every", "folder"] } """)).Ok);
        HasIssue(Check(Doc(""" "trigger": { "accepts": ["event"] }, "input": "Triage" """)), WorkflowCheckCodes.UnknownReference, "a", "Triage has no field 'added'");
    }
}
