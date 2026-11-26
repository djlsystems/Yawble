using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Workflows.Actions;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Tests.Bus;
using IntegrationCaller = Yawble.Core.Integrations.WorkflowCaller;

namespace Yawble.Core.Tests.Workflows;

/// <summary>
/// A workflow built only through the shell's designer (shell/tests/unit/workflow-designer.test.ts writes and compares
/// Workflows/Fixtures/designer-built.json): an integration step saving into a new variable, an if, a set, a log and an end. The real
/// checker with the real Email catalog passes it, and through the library its requires is what the steps give (no stale-requires warning).
/// </summary>
public sealed class DesignerBuiltWorkflowTests
{
    private const string Inbox = "Example Support Inbox";

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Workflows", "Fixtures", "designer-built.json"));

    private static WorkflowActionKinds Kinds() => new(new Dictionary<string, IWorkflowActionsProvider>
    {
        [EmailActions.Kind] = new EmailWorkflowActionsProvider(new NoMail()),
    });

    [Fact]
    public void TheFixtureHasEveryStepTheDesignerWasAskedFor()
    {
        var doc = WorkflowDocument.Parse(Fixture());
        Assert.Equal("designer-built", doc.Id);
        Assert.Equal(["end", "integration", "if", "set", "log"], doc.Steps.Select(s => s.Type));
        var list = doc.Steps.Single(s => s.Type == "integration");
        Assert.Equal((Inbox, "list", "res"), (list.Text("use"), list.Text("action"), list.Text("into")));
        Assert.Contains(doc.Variables, v => v.Name == "res" && v.Type == "EmailList");
    }

    [Fact]
    public void TheRealChecker_FindsNoProblem()
    {
        var check = WorkflowChecker.Check(WorkflowDocument.Parse(Fixture()), BuiltInSchemas.Lookup, Kinds().Catalog);
        Assert.True(check.Ok, string.Join("\n", check.Issues.Select(i => $"{i.StepId}: {i.Code}: {i.Message}")));
        Assert.Empty(check.Issues);
    }

    [Fact]
    public void ThroughTheLibrary_ItChecksClean_WithNoStaleRequiresWarning_WithOrWithoutTheIntegration()
    {
        using var db = new TempDb();
        var kinds = Kinds();
        var withIt = new WorkflowIntegrationSet([new WorkflowIntegrationInfo(Inbox, "email", true, [])]);
        foreach (var directory in new IWorkflowIntegrationDirectory[] { withIt, NoWorkflowIntegrations.Instance })
        {
            var library = new WorkflowLibrary(db.ConnectionString, () => kinds.Catalog, integrations: directory);
            var check = library.Check(Fixture());
            Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
            Assert.Empty(check["issues"]!.AsArray());
            Assert.DoesNotContain(check["warnings"]!.AsArray(), w => w!["code"]!.GetValue<string>() == WorkflowLibrary.StaleRequires);
            Assert.Empty(check["warnings"]!.AsArray());
        }
    }

    private sealed class NoMail : IWorkflowEmail
    {
        public Task<T> UseAsync<T>(IntegrationCaller caller, string integration, string action, JsonObject args, Func<EmailMailbox, Task<T>> work, CancellationToken ct) =>
            throw new NotSupportedException("the check calls no mailbox");
    }
}
