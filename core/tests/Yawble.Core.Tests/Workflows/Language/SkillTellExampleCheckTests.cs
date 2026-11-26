using System.Text.RegularExpressions;
using Xunit;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Skills;

namespace Yawble.Core.Tests.Workflows.Language;

/// <summary>
/// Tester helper: the tell example under the workflows skill's "## Tell" heading, taken verbatim from the skill,
/// put into a workflow that defines what it names (a forEach giving <c>m</c>, a TriageResult schema, a TellResult variable
/// <c>triage</c>, a <c>route</c> step) and checked with the real checker.
/// </summary>
public sealed class SkillTellExampleCheckTests
{
    private static readonly WorkflowSchema TriageResult = WorkflowSchema.Parse("TriageResult", """
        { "type": "object", "properties": { "refund": { "type": "boolean" } }, "required": ["refund"] }
        """);

    private static string TellExample()
    {
        var body = Assert.Single(SkillCatalog.BuiltIns, s => s.Name == "workflows").Body.Replace("\r\n", "\n", StringComparison.Ordinal);
        var m = Regex.Match(body, @"## Tell[^\n]*\n.*?```json\n(?<json>.*?)\n```", RegexOptions.Singleline);
        Assert.True(m.Success, "the skill has a tell example under ## Tell");
        return m.Groups["json"].Value;
    }

    [Fact]
    public void TheSkillsTellExample_ChecksClean_InAWorkflowThatDefinesWhatItNames()
    {
        var step = TellExample();
        Assert.Contains("\"type\": \"tell\"", step, StringComparison.Ordinal);
        var json = """
            { "format": "yawble-workflow/1", "id": "tell-example", "name": "Tell example", "description": "x",
              "trigger": { "accepts": ["every"] },
              "requires": [ { "name": "mail", "kind": "email", "actions": ["list"] } ],
              "variables": [ { "name": "res", "type": "EmailList", "scope": "run" },
                             { "name": "triage", "type": "TellResult", "scope": "run" } ],
              "start": "list",
              "steps": [
                { "id": "list", "type": "integration", "use": "mail", "action": "list", "args": { "folder": "INBOX" }, "into": "res", "next": "each" },
                { "id": "each", "type": "forEach", "items": "{vars.res.messages}", "as": "m", "body": "triage", "then": null },
            """ + step + """
            ,
                { "id": "route", "type": "log", "text": "{vars.triage.outcome} {vars.triage.deliverable} {vars.triage.data.refund}", "next": null }
              ] }
            """;
        var check = WorkflowChecker.Check(json, new WorkflowSchemaSet([TriageResult]), FakeCatalog.MailAndSms());
        Assert.True(check.Ok, string.Join("\n", check.Issues.Select(i => $"{i.StepId}: {i.Code}: {i.Message}")));
    }
}
