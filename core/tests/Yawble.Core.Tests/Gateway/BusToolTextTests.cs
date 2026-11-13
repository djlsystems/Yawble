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
}
