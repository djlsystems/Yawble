using System.ComponentModel;
using System.Reflection;
using Harness.Host;

namespace Harness.Tests.Host;

/// <summary>
/// What the Manager, the Member and the `workflow_complete` tool say about a workflow a member
/// owns - one a person started by telling that member directly. The Manager lets the owner declare
/// or declares it itself once everybody is idle, and never sends the owner back to retry a refused
/// declaration: that relay is the loop in which each refused attempt's row woke the other side
/// until a person closed the workflow.
/// </summary>
public sealed class MemberOwnedWorkflowGuidanceTests
{
    private static string Skill(string name) => BuiltInSkills.All.Single(s => s.Name == name).Body;

    [Fact]
    public void The_manager_skill_says_let_the_owner_declare_or_declare_for_it_once_everyone_is_idle()
    {
        var manager = Skill("manager");

        Assert.Contains("## A workflow a member owns", manager, StringComparison.Ordinal);
        Assert.Contains("1. Let the owner declare.", manager, StringComparison.Ordinal);
        Assert.Contains(
            "once the owner and every other\n   member are idle in it and nothing is queued in it but your own delivery.",
            manager, StringComparison.Ordinal);
        Assert.Contains("says `declaredBy` you, on behalf of the owner.", manager, StringComparison.Ordinal);
        Assert.Contains("do not declare again in the same run.", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void The_manager_skill_never_sends_the_owner_back_to_retry_a_refused_declaration()
    {
        var manager = Skill("manager");

        Assert.Contains("Never tell the owner to retry a refused declaration.", manager, StringComparison.Ordinal);
        Assert.Contains("say so with `blocked`, naming the refusal, and stop.", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void No_skill_says_only_the_manager_or_only_the_owner_may_declare()
    {
        foreach (var name in new[] { "manager", "member" })
        {
            var body = Skill(name);
            Assert.DoesNotContain("is the Manager's declaration and is refused to anyone else", body, StringComparison.Ordinal);
            Assert.DoesNotContain("only a Manager declares", body, StringComparison.Ordinal);
            Assert.Contains(
                "and of the Manager for a workflow one of its own members\nowns. It is refused to anyone else.",
                body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_workflow_complete_tool_says_a_manager_may_declare_for_its_member()
    {
        var description = typeof(PlatformMcpTools)
            .GetMethod(nameof(PlatformMcpTools.WorkflowComplete))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Contains("a Manager may also declare one its own member owns", description, StringComparison.Ordinal);
        Assert.Contains("declaredBy the Manager on behalf of the owner", description, StringComparison.Ordinal);
        Assert.Contains("Do not retry a refused declaration in the same run.", description, StringComparison.Ordinal);
    }
}
