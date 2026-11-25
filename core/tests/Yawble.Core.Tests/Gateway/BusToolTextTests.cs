using System.Text.Json.Nodes;
using Yawble.Core.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// What the bus tools ask agents to write. Reports are shown formatted on the member's card and in the workflows, so the
/// long ones (a hand-back, a completion, a block) ask for a one-line summary and then Markdown with real line breaks,
/// not one sentence or one paragraph.
/// </summary>
public class BusToolTextTests
{
    private static string Param(string tool, string param)
    {
        var t = McpBusServer.ToolList().Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == tool);
        return t["inputSchema"]!["properties"]![param]!["description"]!.GetValue<string>();
    }

    [Theory]
    [InlineData("handback", "delivered")]
    [InlineData("workflow_complete", "delivered")]
    [InlineData("blocked", "reason")]
    public void LongReportsAskForASummaryLineThenMarkdownWithLineBreaks(string tool, string param)
    {
        var d = Param(tool, param);
        Assert.Contains("Markdown", d);
        Assert.Contains("summary", d);
        Assert.Contains("line break", d);
        Assert.DoesNotContain("one short sentence", d);
    }

    [Fact]
    public void ProgressStaysOneShortLine() => Assert.Contains("One short line", Param("progress", "status"));

    /// <summary>needs_decision ties a question to a tester's FAIL (card, kind, reason); workflow_complete says it is refused over one.</summary>
    [Fact]
    public void NeedsDecisionTakesCardKindAndReason_AndWorkflowCompleteSaysAFailIsRefused()
    {
        Assert.Contains("tester's FAIL", Param("needs_decision", "card"));
        Assert.Contains(Yawble.Core.Bus.DecisionActions.CloseOverFail, Param("needs_decision", "card"));
        Assert.Contains("accept-fail", Param("needs_decision", "kind"));
        Assert.Contains("wont-do", Param("needs_decision", "kind"));
        Assert.Contains("Required for wont-do", Param("needs_decision", "reason"));
        var nd = McpBusServer.ToolList().Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == "needs_decision");
        var props = nd["inputSchema"]!["properties"]!;
        Assert.Equal("integer", props["card"]!["type"]!.GetValue<string>());
        Assert.Equal(["accept-fail", "wont-do"], props["kind"]!["enum"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Empty(nd["inputSchema"]!["required"]!.AsArray());
        var wc = McpBusServer.ToolList().Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == "workflow_complete");
        Assert.Contains("tester hand-back is a FAIL unless the person said yes", wc["description"]!.GetValue<string>());
    }
}
