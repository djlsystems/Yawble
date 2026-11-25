using Xunit;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts.Language;

/// <summary>
/// The tell step in the checker: its fields and their types, timeout and reply only with wait, a reply schema that exists, the
/// 7-day limit, and into typed as TellResult (its data typed by the reply schema).
/// </summary>
public sealed class ScriptTellCheckerTests
{
    private static readonly ScriptSchema TriageResult = ScriptSchema.Parse("TriageResult", """
        { "type": "object", "properties": {
            "refund": { "type": "boolean" },
            "category": { "type": "string", "enum": ["order", "refund", "other"] } },
          "required": ["refund"] }
        """);

    private static ScriptCheckResult Check(string steps, string variables = """[{ "name": "t", "type": "TellResult" }]""") =>
        ScriptChecker.Check(ScriptSamples.Script(steps, variables), new ScriptSchemaSet([TriageResult]), FakeCatalog.MailAndSms());

    private static void Clean(ScriptCheckResult result) => Assert.True(result.Ok, string.Join("\n", result.Issues));

    private static void HasIssue(ScriptCheckResult result, string code, string text)
    {
        Assert.False(result.Ok);
        Assert.True(result.Issues.Any(i => i.Code == code && i.StepId == "a" && i.Message.Contains(text, StringComparison.Ordinal)),
            $"expected {code} on a containing \"{text}\"; got:\n" + string.Join("\n", result.Issues));
    }

    [Fact]
    public void TellIsAStepType_AndTellResultABuiltInSchema()
    {
        Assert.Contains("tell", ScriptChecker.StepTypes);
        var schema = BuiltInSchemas.Find("TellResult");
        Assert.NotNull(schema);
        Assert.True(schema.BuiltIn);
        var outcome = ScriptType.FromJsonSchema(schema.Schema["properties"]!["outcome"], _ => null);
        Assert.Equal(["sent", "done", "failed", "blocked", "timedOut", "dryRun"], outcome.EnumValues);
        Assert.Equal(["outcome", "workflow", "reply"], schema.Schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void AMinimalTell_ToTheManager_ChecksClean()
    {
        Clean(Check("""[{ "id": "a", "type": "tell", "text": "Look at the inbox.", "next": null }]"""));
    }

    [Fact]
    public void AFullTell_WithWaitTimeoutReplyAndInto_ChecksClean_AndTypesTheReplyData()
    {
        var result = Check("""
            [{ "id": "a", "type": "tell", "member": "Manager", "team": "Support", "text": "Read {input.triggerId}.",
               "wait": true, "timeout": "4h", "reply": "TriageResult", "into": "t", "next": "b" },
             { "id": "b", "type": "if", "when": { "left": "{vars.t.data.refund}", "op": "eq", "right": true }, "then": "c", "else": null },
             { "id": "c", "type": "log", "text": "{vars.t.outcome} in workflow {vars.t.workflow}: {steps.a.data.category} {vars.t.reply}", "next": null }]
            """);
        Clean(result);
        Assert.Contains(new ScriptSchemaReference("TriageResult", "", "a"), result.SchemaReferences);
        Assert.Contains(new ScriptSchemaReference("TriageResult", "refund", "b"), result.SchemaReferences);
        Assert.Contains(new ScriptSchemaReference("TriageResult", "category", "c"), result.SchemaReferences);
    }

    [Fact]
    public void AReplyField_TheSchemaDoesNotHave_IsAnUnknownReference()
    {
        var result = Check("""
            [{ "id": "a", "type": "tell", "text": "x", "wait": true, "reply": "TriageResult", "into": "t", "next": "b" },
             { "id": "b", "type": "log", "text": "{vars.t.data.colour}", "next": null }]
            """);
        Assert.Contains(result.Issues, i => i.StepId == "b" && i.Code == ScriptCheckCodes.UnknownReference && i.Message.Contains("colour", StringComparison.Ordinal));
    }

    [Fact]
    public void ATellResultFieldThatDoesNotExist_IsAnUnknownReference()
    {
        var result = Check("""
            [{ "id": "a", "type": "tell", "text": "x", "into": "t", "next": "b" },
             { "id": "b", "type": "log", "text": "{steps.a.answer}", "next": null }]
            """);
        Assert.Contains(result.Issues, i => i.StepId == "b" && i.Code == ScriptCheckCodes.UnknownReference && i.Message.Contains("answer", StringComparison.Ordinal));
    }

    [Fact]
    public void TextIsRequired_AndMustBeText()
    {
        HasIssue(Check("""[{ "id": "a", "type": "tell", "next": null }]"""), ScriptCheckCodes.MissingField, "tell needs 'text'");
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "{settings.nothing}", "next": null }]"""), ScriptCheckCodes.UnknownReference, "setting 'nothing'");
    }

    [Fact]
    public void MemberAndTeam_AreNames()
    {
        HasIssue(Check("""[{ "id": "a", "type": "tell", "member": 3, "text": "x", "next": null }]"""), ScriptCheckCodes.InvalidDocument, "'member' is a name");
        HasIssue(Check("""[{ "id": "a", "type": "tell", "team": "", "text": "x", "next": null }]"""), ScriptCheckCodes.InvalidDocument, "'team' is a name");
    }

    [Fact]
    public void WaitIsTrueOrFalse()
    {
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": "yes", "next": null }]"""), ScriptCheckCodes.InvalidDocument, "'wait' is true or false");
    }

    [Fact]
    public void ReplyOrTimeoutWithoutWait_IsAProblem()
    {
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "reply": "TriageResult", "next": null }]"""), ScriptCheckCodes.BadValue, "a reply goes with wait: true");
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": false, "timeout": "1h", "next": null }]"""), ScriptCheckCodes.BadValue, "a timeout goes with wait: true");
    }

    [Fact]
    public void AReplySchema_MustExist()
    {
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": true, "reply": "NoSuchThing", "next": null }]"""),
            ScriptCheckCodes.UnknownType, "no schema named 'NoSuchThing'");
    }

    [Fact]
    public void ATimeoutOverSevenDays_OrNotADuration_IsAProblem()
    {
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": true, "timeout": "8d", "next": null }]"""), ScriptCheckCodes.BadValue, "the most is 7 days");
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": true, "timeout": "soon", "next": null }]"""), ScriptCheckCodes.TypeMismatch, "is not a duration");
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": true, "timeout": "0m", "next": null }]"""), ScriptCheckCodes.BadValue, "more than zero");
        Clean(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": true, "timeout": "7d", "next": null }]"""));
        Clean(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": true, "timeout": "1h 30m", "next": null }]"""));
        Clean(Check("""[{ "id": "a", "type": "tell", "text": "x", "wait": true, "timeout": "PT45M", "next": null }]"""));
    }

    [Fact]
    public void IntoMustTakeATellResult()
    {
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "into": "t", "next": null }]""", """[{ "name": "t", "type": "string" }]"""),
            ScriptCheckCodes.TypeMismatch, "the result is TellResult");
        HasIssue(Check("""[{ "id": "a", "type": "tell", "text": "x", "into": "nope", "next": null }]"""), ScriptCheckCodes.UnknownReference, "variable 'nope'");
        Clean(Check("""[{ "id": "a", "type": "tell", "text": "x", "into": "t", "next": null }]""", """[{ "name": "t", "type": "any" }]"""));
    }

    [Theory]
    [InlineData("4h", 240)]
    [InlineData("30m", 30)]
    [InlineData("2d", 2880)]
    [InlineData("1h 30m", 90)]
    [InlineData("PT2H", 120)]
    [InlineData("01:15:00", 75)]
    public void TimeoutTexts(string text, int minutes)
    {
        Assert.True(ScriptTell.TryParseTimeout(System.Text.Json.Nodes.JsonValue.Create(text), out var t));
        Assert.Equal(TimeSpan.FromMinutes(minutes), t);
    }

    [Theory]
    [InlineData("Done. {\"refund\": true}", "{\"refund\":true}")]
    [InlineData("All done.\n```json\n{ \"refund\": false, \"category\": \"order\" }\n```", "{\"refund\":false,\"category\":\"order\"}")]
    [InlineData("I looked at {the order}. Result: {\"refund\": true}", "{\"refund\":true}")]
    [InlineData("No JSON here.", null)]
    [InlineData("Ends with {broken", null)]
    public void TheReplyJson_IsWhatTheSummaryEndsWith(string reply, string? json)
    {
        Assert.Equal(json, ScriptTell.ReplyJson(reply)?.ToJsonString());
    }
}
