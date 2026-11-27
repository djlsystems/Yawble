using Xunit;
using Yawble.Core.Tests.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Tests.Workflows.Language;

/// <summary>The check of file.move, file.copy and file.list: required fields, true/false options and the type of into.</summary>
public sealed class WorkflowFileMoveListCheckerTests
{
    private static readonly WorkflowSchema Photo = WorkflowSchema.Parse("Photo", """
        { "type": "object", "properties": { "path": { "type": "string" }, "name": { "type": "string" } } }
        """);

    private static WorkflowCheckResult CheckSteps(string steps, string variables = "[]") =>
        WorkflowChecker.Check(WorkflowSamples.Workflow(steps, variables), new WorkflowSchemaSet([Photo]), FakeCatalog.MailAndSms());

    private static void Clean(WorkflowCheckResult result) => Assert.True(result.Ok, string.Join("\n", result.Issues));

    private static void HasIssue(WorkflowCheckResult result, string code, string text)
    {
        Assert.False(result.Ok);
        Assert.True(result.Issues.Any(i => i.Code == code && i.StepId == "a" && i.Message.Contains(text, StringComparison.Ordinal)),
            $"expected {code} on a containing \"{text}\"; got:\n" + string.Join("\n", result.Issues));
    }

    [Fact]
    public void TheThreeAreStepTypes()
    {
        Assert.Contains(WorkflowStepTypes.FileMove, WorkflowChecker.StepTypes);
        Assert.Contains(WorkflowStepTypes.FileCopy, WorkflowChecker.StepTypes);
        Assert.Contains(WorkflowStepTypes.FileList, WorkflowChecker.StepTypes);
        Assert.Equal(("file.move", "file.copy", "file.list"), (WorkflowStepTypes.FileMove, WorkflowStepTypes.FileCopy, WorkflowStepTypes.FileList));
    }

    [Fact]
    public void AListThenAMoveOfEachFile_ChecksClean_WithTypedFieldsAndResults()
    {
        Clean(CheckSteps("""
            [ { "id": "a", "type": "file.list", "path": "photos", "pattern": "*.jpg;*.png", "recursive": true, "into": "files", "next": "b" },
              { "id": "b", "type": "forEach", "items": "{steps.a}", "as": "f", "body": "c", "then": "e" },
              { "id": "c", "type": "file.move", "path": "{f.path}", "to": "photos/sorted/", "overwrite": false, "into": "to", "next": "d" },
              { "id": "d", "type": "log", "text": "{f.name} {f.size + 1} {hoursSince(f.modifiedAt)} -> {steps.c}", "next": null },
              { "id": "e", "type": "file.copy", "path": "a.txt", "to": "b.txt", "into": "to", "next": "g" },
              { "id": "g", "type": "file.list", "path": ".", "into": "photos", "next": null } ]
            """, """[ { "name": "files", "type": "list<any>" }, { "name": "to", "type": "string" }, { "name": "photos", "type": "list<Photo>" } ]"""));
    }

    [Fact]
    public void AListsItemsHaveOnlyItsFields()
    {
        var result = CheckSteps("""
            [ { "id": "a", "type": "file.list", "path": "photos", "next": "b" },
              { "id": "b", "type": "forEach", "items": "{steps.a}", "as": "f", "body": "c", "then": null },
              { "id": "c", "type": "log", "text": "{f.width}", "next": null } ]
            """);
        Assert.Contains(result.Issues, i => i.Code == WorkflowCheckCodes.UnknownReference && i.StepId == "c" && i.Message.Contains("no field 'width'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "id": "a", "type": "file.move", "to": "out/", "next": null }""", "needs 'path'")]
    [InlineData("""{ "id": "a", "type": "file.move", "path": "a.jpg", "next": null }""", "needs 'to'")]
    [InlineData("""{ "id": "a", "type": "file.copy", "path": "a.jpg", "next": null }""", "needs 'to'")]
    [InlineData("""{ "id": "a", "type": "file.list", "pattern": "*.jpg", "next": null }""", "needs 'path'")]
    public void AMissingFieldIsAProblem(string step, string text) =>
        HasIssue(CheckSteps($"[ {step} ]"), WorkflowCheckCodes.MissingField, text);

    [Theory]
    [InlineData("""{ "id": "a", "type": "file.copy", "path": "a.jpg", "to": "b/", "overwrite": "yes", "next": null }""", "'overwrite' is string, not true or false")]
    [InlineData("""{ "id": "a", "type": "file.list", "path": "p", "recursive": 1, "next": null }""", "'recursive' is int, not true or false")]
    [InlineData("""{ "id": "a", "type": "file.list", "path": "p", "pattern": "{vars.l}", "next": null }""", "'pattern' is list<string>, not text")]
    public void AnOptionOfTheWrongTypeIsAProblem(string step, string text) =>
        HasIssue(CheckSteps($"[ {step} ]", """[ { "name": "l", "type": "list<string>" } ]"""), WorkflowCheckCodes.TypeMismatch, text);

    [Theory]
    [InlineData("file.move", "int", "the result is string; variable 'v' is int")]
    [InlineData("file.copy", "list<string>", "the result is string; variable 'v' is list<string>")]
    public void AMovesIntoIsText(string type, string varType, string text) =>
        HasIssue(CheckSteps($$"""[ { "id": "a", "type": "{{type}}", "path": "a", "to": "b", "into": "v", "next": null } ]""",
            $$"""[ { "name": "v", "type": "{{varType}}" } ]"""), WorkflowCheckCodes.TypeMismatch, text);

    [Theory]
    [InlineData("string")]
    [InlineData("list<string>")]
    public void AListsIntoIsAListOfFiles(string varType) =>
        HasIssue(CheckSteps("""[ { "id": "a", "type": "file.list", "path": "p", "into": "v", "next": null } ]""",
            $$"""[ { "name": "v", "type": "{{varType}}" } ]"""), WorkflowCheckCodes.TypeMismatch, "the result is list<object>");

    [Fact]
    public void TheSkillsPhotoExample_ChecksClean() =>
        Clean(WorkflowChecker.Check(WorkflowFileMoveListTests.PhotoExample(), WorkflowSchemaSet.Empty, FakeCatalog.MailAndSms()));
}
