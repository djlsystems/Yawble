using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Workflows.Actions;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Skills;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using WorkflowCaller = Yawble.Core.Integrations.WorkflowCaller;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The built-in "workflows" skill is offered to the Concierge (skills_list / skills_get, through its MCP tools), never to a
/// team's agents; it names every step type, function, condition operator, built-in schema and email action the engine has; and its example
/// workflow checks clean with the real checker and the real email and api catalogs. Fakes only: a temp database, no mailbox, no API.
/// </summary>
public sealed class ConciergeWorkflowsSkillTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly BusEngine _bus;

    public ConciergeWorkflowsSkillTests() => _bus = new BusEngine(_db.OpenStore(), new UnavailableRunner());

    public void Dispose()
    {
        _bus.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _db.Dispose();
    }

    private static Skill WorkflowsSkill => Assert.Single(SkillCatalog.BuiltIns, s => s.Name == "workflows");

    private ConciergeTools Tools(bool byPerson = true, bool remote = false) =>
        new(_bus, new ConciergeStore(_db.ConnectionString), (_, _, _) => Task.FromResult<JsonNode?>(null), () => null,
            turn: new ConciergeTurn("c1", byPerson, remote));

    private static async Task<(string Text, bool IsError)> Call(ConciergeTools tools, string name, JsonObject? args = null)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args ?? new JsonObject() },
        }.ToJsonString());
        var result = JsonNode.Parse((await tools.HandleAsync(body, Ct)).Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    // ------------------------------------------------------------------------------------------------ offered

    [Fact]
    public void TheWorkflowsSkillIsBuiltIn_ForTheConciergeOnly()
    {
        var skill = WorkflowsSkill;
        Assert.Equal(SkillKinds.BuiltIn, skill.Kind);
        Assert.Equal(new[] { SkillCatalog.ConciergeRole }, skill.Roles);
        Assert.False(skill.OfferedTo("manager"));
        Assert.False(skill.OfferedTo("member"));

        var catalog = new SkillCatalog(_db.ConnectionString);
        Assert.Contains(catalog.OfferedToConcierge(), s => s.Name == "workflows");
        Assert.Contains(SkillCatalog.BuiltInsForConcierge(), s => s.Name == "workflows");
        // A team's agents are never offered it, nor do the team role skills reach the Concierge.
        Assert.DoesNotContain(catalog.Offered("manager", 1), s => s.Name == "workflows");
        Assert.DoesNotContain(catalog.Offered("member", 1), s => s.Name == "workflows");
        Assert.DoesNotContain(catalog.OfferedToConcierge(), s => s.Name is "member" or "manager" or "dev-tools");
    }

    [Fact]
    public async Task TheConciergeListsAndLoadsTheWorkflowsSkill_InEveryKindOfTurn()
    {
        var names = JsonNode.Parse((await Tools().HandleAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), Ct)).Json!)!
            ["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("skills_list", names);
        Assert.Contains("skills_get", names);
        Assert.Contains("skills_list", ConciergeTools.ToolNames);

        // Reads only: a review turn (no message from the person) and a Cloud Link turn may load it too.
        foreach (var tools in new[] { Tools(), Tools(byPerson: false), Tools(remote: true) })
        {
            var (list, listError) = await Call(tools, "skills_list");
            Assert.False(listError, list);
            Assert.Contains("- workflows: " + WorkflowsSkill.Description, list);

            var (text, error) = await Call(tools, "skills_get", new JsonObject { ["name"] = "Workflows" });
            Assert.False(error, text);
            Assert.Equal(WorkflowsSkill.Body, text); // whole, and nothing in it mistaken for a secret
        }

        var (refused, isError) = await Call(Tools(), "skills_get", new JsonObject { ["name"] = "member" });
        Assert.True(isError);
        Assert.Contains("no skill named 'member' is offered to you. Offered: workflows", refused);
    }

    [Fact]
    public async Task ThePersonsOwnConciergeSkill_IsOfferedToo_ButNotATeamsOrAnEveryoneSkill()
    {
        var catalog = new SkillCatalog(_db.ConnectionString);
        catalog.Save("release-notes", "How the person likes release notes.", [SkillCatalog.ConciergeRole], "Short, plain bullets.", null);
        var team = await _bus.CreateTeamAsync("alpha", ct: Ct);
        catalog.Save("team-only", "A team's own.", [SkillCatalog.ConciergeRole], "x", team);
        catalog.Save("for-everyone", "Every team agent.", ["any"], "x", null);

        var (list, _) = await Call(Tools(), "skills_list");
        Assert.Contains("- release-notes: How the person likes release notes. (the person's own)", list);
        Assert.DoesNotContain("team-only", list);
        Assert.DoesNotContain("for-everyone", list);
        Assert.Equal("Short, plain bullets.", (await Call(Tools(), "skills_get", new JsonObject { ["name"] = "release-notes" })).Text);

        // A team's agents are not offered a Concierge skill either.
        Assert.DoesNotContain(catalog.Offered("member", 1), s => s.Name == "release-notes");
    }

    [Fact]
    public void ThePromptPointsTheConciergeAtTheSkill() =>
        Assert.Contains("load 'workflows' before you build", ConciergePrompt.Role);

    // ------------------------------------------------------------------------------------------------ exact

    [Fact]
    public void TheSkillNamesEveryStepTypeFunctionOperatorSchemaActionAndTrigger_TheEngineHas()
    {
        var body = WorkflowsSkill.Body;
        foreach (var type in WorkflowChecker.StepTypes) Assert.Contains($"`{type}`", body);
        foreach (var f in WorkflowExpressionParser.Functions.Keys) Assert.Contains($"`{f}(", body);
        foreach (var op in WorkflowCondition.Operators) Assert.Matches($@"\b{op}\b", body);
        foreach (var schema in BuiltInSchemas.Names) Assert.Contains($"`{schema}", body);
        foreach (var action in EmailActions.All) Assert.Matches($@"\b{action.Name}\b", body);
        foreach (var kind in WorkflowTriggerSpec.Kinds) Assert.Matches($@"\b{kind}\b", body);
        Assert.Contains(WorkflowDocument.CurrentFormat, body);
    }

    /// <summary>The skill teaches the two tools that turn a workflow on, by their exact names and fields, and their approvals.</summary>
    [Fact]
    public void TheSkillTeachesTheTriggerAndActionAllowTools()
    {
        var body = Regex.Replace(WorkflowsSkill.Body, @"\s+", " "); // phrases may wrap across lines
        Assert.Contains("`workflow_trigger_set`", body);
        Assert.Contains("`integration_action_allow`", body);
        Assert.Contains("`integration_action_allow { team, integration, action }`", body);
        foreach (var field in new[] { "`team`", "`member`", "`name`", "`schedule`", "`enabled`", "`id`", "`only_when_idle`" })
            Assert.Contains(field, body);
        foreach (var kind in new[] { "`every`", "`cron`", "`once`", "`event`", "`folder`" })
            Assert.Contains(kind, body);

        // The offer after a member is added, the needs-approval message it answers, and no loop.
        Assert.Contains("After `workflow_member_add` succeeds, offer to turn its trigger on with `workflow_trigger_set`", body);
        Assert.Contains("`needs approval: allow <action> for <team>`", body);
        Assert.Contains("Never ask for a whole API", body);
        Assert.Contains("Never call it again in a loop", body);
        Assert.Contains("needs the person's yes in the window", body);
    }

    [Fact]
    public void TheSkillsExampleWorkflow_ChecksCleanWithTheRealCatalogs()
    {
        var json = Example(WorkflowsSkill.Body);
        var doc = WorkflowDocument.Parse(json);
        Assert.Equal("inbox-sms-alert", doc.Id);

        var check = WorkflowChecker.Check(doc, BuiltInSchemas.Lookup, Catalog().Catalog);
        Assert.True(check.Ok, string.Join("\n", check.Issues.Select(i => $"{i.StepId}: {i.Code}: {i.Message}")));
        // The email list is typed by its signature: the check reads EmailList and EmailMessage fields.
        Assert.Contains(check.SchemaReferences, r => r.Schema == "EmailList" && r.FieldPath == "messages");
        Assert.Contains(check.SchemaReferences, r => r.Schema == "EmailMessage" && r.FieldPath == "id");
    }

    [Fact]
    public void TheCheckWouldCatchTheMistakesTheSkillWarnsAbout()
    {
        var json = Example(WorkflowsSkill.Body);
        // A function's result takes no field.
        var field = json.Replace("\"{vars.last.id}\"", "\"{last(vars.res.messages).id}\"", StringComparison.Ordinal);
        Assert.NotEqual(json, field);
        Assert.Contains(WorkflowChecker.Check(field, BuiltInSchemas.Lookup, Catalog().Catalog).Issues, i => i.Code == WorkflowCheckCodes.BadExpression);
        // An api action is the operation's id, not its method and route.
        var route = json.Replace("\"CreateMessage\"", "\"POST /2010-04-01/Accounts/{AccountSid}/Messages.json\"", StringComparison.Ordinal);
        Assert.Contains(WorkflowChecker.Check(route, BuiltInSchemas.Lookup, Catalog().Catalog).Issues, i => i.Code == WorkflowCheckCodes.UnknownAction);
    }

    /// <summary>The json block under the skill's "## Example" heading.</summary>
    private static string Example(string body)
    {
        var m = Regex.Match(body, @"## Example[^\n]*\n+```json\n(?<json>.*?)\n```", RegexOptions.Singleline);
        Assert.True(m.Success, "the skill has an example workflow");
        return m.Groups["json"].Value;
    }

    /// <summary>The real kinds: the email catalog (with signatures) and an api catalog with one team's SMS operation.</summary>
    private static WorkflowActionKinds Catalog() => new(new Dictionary<string, IWorkflowActionsProvider>
    {
        [EmailActions.Kind] = new EmailWorkflowActionsProvider(new NoMail()),
        [ApiWorkflowActionsProvider.Kind] = new ApiWorkflowActionsProvider(new SmsApi()),
    });

    private sealed class NoMail : IWorkflowEmail
    {
        public Task<T> UseAsync<T>(WorkflowCaller caller, string integration, string action, JsonObject args, Func<EmailMailbox, Task<T>> work, CancellationToken ct) =>
            throw new NotSupportedException("the check calls no mailbox");
    }

    private sealed class SmsApi : IWorkflowApi
    {
        public Task<WorkflowApiResult> CallAsync(WorkflowCaller caller, string integration, string action, JsonObject args, CancellationToken ct) =>
            throw new NotSupportedException("the check calls no API");

        public IReadOnlyList<string> Teams() => ["Alpha"];

        public IReadOnlyList<WorkflowOperation> Operations(string team) =>
        [
            new("CreateMessage", "POST", "/2010-04-01/Accounts/{AccountSid}/Messages.json", "Send a message", "allow",
                [new("To", "body", true, "string"), new("From", "body", true, "string"), new("Body", "body", true, "string")],
                "application/x-www-form-urlencoded"),
        ];
    }
}
